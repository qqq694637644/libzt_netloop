using System.Net;
using System.Threading.Channels;
using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

internal enum RuntimePhase
{
    Starting,
    Ready,
    Degraded,
    Resetting,
    Stopped
}

internal sealed record RuntimeStatus(
    RuntimePhase Phase,
    string? Reason,
    NetLoopRuntime? Runtime,
    LibztNetworkState? Network,
    int ResetCount);

/// <summary>
/// The one owner of business Runtime creation and teardown. Native callbacks,
/// OS network callbacks and listener tasks only publish notifications. This is
/// an in-process lifetime loop, not a service, watcher or process supervisor.
/// </summary>
internal sealed class RuntimeOwner(LibztNode node, HostOptions options)
    : IAsyncDisposable
{
    internal enum EventKind { Config, Unavailable, Physical, Manual }
    internal sealed record Event(
        EventKind Kind, string Reason, LibztNetworkState? Network = null);

    private readonly Channel<Event> _events = Channel.CreateBounded<Event>(
        new BoundedChannelOptions(64) {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            // Config is re-queried from the native source of truth before
            // publishing. Drop old notifications while a Node waits for IP.
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _finished = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private NetLoopRuntime? _active;
    private RuntimeStatus _status = new(RuntimePhase.Starting, null, null, null, 0);
    private int _resetCount;
    private int _running;
    private bool _physicalRefreshPending;
    private Event? _deferredManualReset;

    internal event Action<RuntimeStatus>? StatusChanged;

    internal RuntimeStatus Snapshot => Volatile.Read(ref _status);
    internal NetLoopRuntime? Current => Volatile.Read(ref _active);
    internal int ResetCount => _resetCount;

    internal void RequestPhysicalNetworkRefresh(string reason)
        => _events.Writer.TryWrite(new Event(EventKind.Physical, reason));

    internal void RequestManualReset(string reason)
        => _events.Writer.TryWrite(new Event(EventKind.Manual, reason));

    private void Publish(RuntimePhase phase, string? reason)
    {
        var status = new RuntimeStatus(
            phase, reason, Current, node.State, _resetCount);
        Volatile.Write(ref _status, status);
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            // A status observer must never take down the data plane.
            JsonLog.Error("runtime_status_observer_failed", new {
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
    }

    private static bool MatchesBindAddress(
        NetLoopRuntime runtime,
        LibztNetworkState state)
        => !RequiresManagedIpRebind(runtime.OverlayBindAddress, state.ManagedAddresses);

    internal static bool RequiresManagedIpRebind(
        IPAddress boundAddress,
        IReadOnlyList<IPAddress> latest)
        => latest.Count != 1 || !latest[0].Equals(boundAddress);

    /// <summary>
    /// The sole lifecycle owner must also handle physical path notifications
    /// while StartAsync/RuntimeRecovery await Controller configuration.
    /// Config/unavailable events need no replay: runtime creation re-queries
    /// the native source. Preserve a manual reset until a runtime exists.
    /// </summary>
    private void DrainEventsWhileWaiting()
        => DrainPendingWaitingEvents(
            _events.Reader,
            node.TryNotifyPhysicalNetworkChanged,
            ref _physicalRefreshPending,
            ref _deferredManualReset);

    internal static void DrainPendingWaitingEvents(
        ChannelReader<Event> events,
        Func<bool> tryNotifyPhysicalNetworkChanged,
        ref bool physicalRefreshPending,
        ref Event? deferredManualReset)
    {
        while (events.TryRead(out var notification))
        {
            switch (notification.Kind)
            {
                case EventKind.Physical:
                    physicalRefreshPending = true;
                    break;
                case EventKind.Manual:
                    deferredManualReset = notification;
                    break;
                // A new runtime always reads the newest Controller state.
                case EventKind.Config:
                case EventKind.Unavailable:
                    break;
            }
        }

        // The native Node might not have started yet. Retain one coalesced
        // refresh request and retry it on the next waiting iteration.
        if (physicalRefreshPending && tryNotifyPhysicalNetworkChanged())
            physicalRefreshPending = false;
    }

    private void RestoreDeferredManualReset()
    {
        if (_deferredManualReset is not { } reset)
            return;
        _deferredManualReset = null;
        _events.Writer.TryWrite(reset);
    }

    internal static async Task AwaitStartupAsync(
        Task startup,
        Action processWaitingEvents,
        Action reportDegraded,
        CancellationToken cancellationToken)
    {
        var startedAt = Environment.TickCount64;
        var degradedReported = false;
        while (!startup.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processWaitingEvents();
            if (!degradedReported
                && Environment.TickCount64 - startedAt >= 3_000)
            {
                degradedReported = true;
                reportDegraded();
            }

            if (!startup.IsCompleted)
                await Task.WhenAny(
                    startup, Task.Delay(250, cancellationToken))
                    .ConfigureAwait(false);
        }
        processWaitingEvents();
        await startup.ConfigureAwait(false);
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("RuntimeOwner can only run once.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _stop.Token);
        var token = linked.Token;

        void OnConfiguration(LibztNetworkState snapshot)
            => _events.Writer.TryWrite(
                new Event(EventKind.Config, "controller_refresh", snapshot));

        void OnUnavailable(string reason)
            => _events.Writer.TryWrite(
                new Event(EventKind.Unavailable, reason));

        node.NetworkStateChanged += OnConfiguration;
        node.NetworkConfigurationUnavailable += OnUnavailable;

        try
        {
            Publish(RuntimePhase.Starting, "native_node_starting");
            var startup = node.StartAsync(token);
            await AwaitStartupAsync(
                startup,
                DrainEventsWhileWaiting,
                () => Publish(RuntimePhase.Degraded, "waiting_for_controller"),
                token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                var runtime = Current;
                if (runtime is null)
                {
                    Publish(RuntimePhase.Degraded, "waiting_for_runtime_configuration");
                    runtime = await RuntimeRecovery.CreateAsync(
                            node, options, _resetCount, DrainEventsWhileWaiting, token)
                        .ConfigureAwait(false);

                    // Runtime creation includes several native binds. Never
                    // publish a replacement made for a revoked/older address.
                    LibztNetworkState? latest;
                    try
                    {
                        latest = node.RefreshNetworkState();
                    }
                    catch (LibztException ex) when (ex.IsNetworkUnavailable)
                    {
                        latest = null;
                    }

                    if (latest is null
                        || !node.NetworkConfigurationAvailable
                        || !MatchesBindAddress(runtime, latest))
                    {
                        runtime.Abort();
                        await runtime.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    runtime.UpdateNetworkState(latest);
                    Volatile.Write(ref _active, runtime);
                    Publish(RuntimePhase.Ready, null);
                    RestoreDeferredManualReset();
                }

                string? restart = null;
                using (var supervision =
                       CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var nextEvent = _events.Reader.ReadAsync(supervision.Token).AsTask();
                    var listenerFailure = runtime.WaitForCriticalServiceFailureAsync(
                        supervision.Token);
                    try
                    {
                        var completed = await Task.WhenAny(nextEvent, listenerFailure)
                            .ConfigureAwait(false);
                        if (ReferenceEquals(completed, listenerFailure))
                        {
                            try
                            {
                                await listenerFailure.ConfigureAwait(false);
                            }
                            catch (IOException ex) when (!token.IsCancellationRequested)
                            {
                                JsonLog.Error("runtime_listener_failed", new {
                                    error = ex.Message
                                });
                            }
                            restart = "critical_listener_failure";
                        }
                        else
                        {
                            var currentEvent = await nextEvent.ConfigureAwait(false);
                            switch (currentEvent.Kind)
                            {
                                case EventKind.Physical:
                                    node.TryNotifyPhysicalNetworkChanged();
                                    // Physical handover alone never aborts a
                                    // potentially healthy long-lived TCP/RDP.
                                    break;
                                case EventKind.Unavailable:
                                    if (!node.NetworkConfigurationAvailable)
                                    {
                                        runtime.MarkNetworkConfigurationUnavailable(
                                            currentEvent.Reason);
                                        Publish(RuntimePhase.Degraded,
                                            "controller_configuration_unavailable");
                                    }
                                    break;
                                case EventKind.Config:
                                    var current = currentEvent.Network!;
                                    if (current.Generation <= runtime.State.Generation)
                                        break;

                                    if (current.ManagedAddresses.Count > 1
                                        || (current.ManagedAddresses.Count == 1
                                            && current.ManagedAddresses[0].AddressFamily
                                            != System.Net.Sockets.AddressFamily.InterNetwork))
                                    {
                                        throw new InvalidOperationException(
                                            "Controller must assign exactly one IPv4 Managed IP.");
                                    }

                                    if (!MatchesBindAddress(runtime, current))
                                    {
                                        runtime.MarkNetworkConfigurationUnavailable(
                                            "managed_ip_revoked");
                                        Publish(RuntimePhase.Degraded,
                                            "managed_ip_revoked");
                                        restart = "managed_ip_changed";
                                    }
                                    else
                                    {
                                        runtime.UpdateNetworkState(current);
                                        Publish(RuntimePhase.Ready, null);
                                    }
                                    break;
                                case EventKind.Manual:
                                    restart = currentEvent.Reason;
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        await supervision.CancelAsync().ConfigureAwait(false);
                        try
                        {
                            await Task.WhenAll(nextEvent, listenerFailure)
                                .ConfigureAwait(false);
                        }
                        catch (Exception) when (supervision.IsCancellationRequested)
                        {
                            // Both competing wait tasks are observed.
                        }
                    }
                }

                if (restart is not null)
                {
                    Publish(RuntimePhase.Resetting, restart);
                    Volatile.Write(ref _active, null);
                    runtime.Abort();
                    await runtime.DisposeAsync().ConfigureAwait(false);
                    _resetCount++;
                    JsonLog.Info("runtime_replaced", new {
                        restart,
                        reset_count = _resetCount,
                        node = node.State?.NodeId.ToString("x10")
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            node.NetworkStateChanged -= OnConfiguration;
            node.NetworkConfigurationUnavailable -= OnUnavailable;
            _events.Writer.TryComplete();
            try
            {
                var current = Interlocked.Exchange(ref _active, null);
                if (current is not null)
                {
                    current.Abort();
                    await current.DisposeAsync().ConfigureAwait(false);
                }
                Publish(RuntimePhase.Stopped, "stopped");
            }
            finally
            {
                _finished.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _running) != 0)
            await _finished.Task.ConfigureAwait(false);
        _stop.Dispose();
    }
}
