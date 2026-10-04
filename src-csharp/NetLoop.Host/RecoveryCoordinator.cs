using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

internal sealed class RecoveryCoordinator : IOverlayRecoveryObserver, IAsyncDisposable
{
    private readonly HostOptions _options;
    private readonly LibztNode _node;
    private readonly OverlayGenerationManager _generations;
    private readonly OverlayRuntimeController _overlayRuntime;
    private readonly IPAddress[] _probePeers;
    private readonly Action<string, Exception> _requestProcessRestart;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _scheduleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _debounce;
    private long _pendingSoftEpoch = -1;
    private long _pendingSoftStartedTimestamp;
    private long _pendingSoftSequence;
    private long? _pendingSoftCommandId;
    private string? _pendingSoftReason;
    private long _sequence;
    private DateTimeOffset _ignoreNetworkEventsUntil;
    private int _started;
    private int _disposed;

    internal RecoveryCoordinator(
        HostOptions options,
        LibztNode node,
        OverlayGenerationManager generations,
        OverlayRuntimeController overlayRuntime,
        IEnumerable<IPAddress> probePeers,
        Action<string, Exception> requestProcessRestart)
    {
        _options = options;
        _node = node;
        _generations = generations;
        _overlayRuntime = overlayRuntime;
        _probePeers = probePeers.Distinct().ToArray();
        _requestProcessRestart = requestProcessRestart;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Recovery coordinator is already started.");

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        await PublishStatusAsync(
            phase: "ready",
            kind: "startup",
            reason: "initial",
            sequence: 0,
            epoch: _generations.CurrentEpoch,
            commandId: null,
            elapsedMilliseconds: 0,
            error: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        JsonLog.Info("recovery_monitor_ready", new {
            epoch = _generations.CurrentEpoch,
            soft_window_ms = _options.RecoverySoftWindow.TotalMilliseconds,
            debounce_ms = _options.RecoveryEventDebounce.TotalMilliseconds,
            hard_timeout_ms = _options.RecoveryHardTimeout.TotalMilliseconds,
            probe_peers = _probePeers.Select(static value => value.ToString()).ToArray()
        });
    }

    internal async Task<long> TriggerSoftRecoveryAsync(
        string reason,
        long? commandId,
        CancellationToken cancellationToken)
    {
        var gateStarted = Stopwatch.GetTimestamp();
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateWait = Stopwatch.GetElapsedTime(gateStarted);
        if (gateWait > TimeSpan.FromMilliseconds(25))
        {
            JsonLog.Info("recovery_soft_gate_wait", new {
                reason,
                command_id = commandId,
                elapsed_ms = gateWait.TotalMilliseconds
            });
        }
        OverlayGeneration generation;
        long sequence;
        Exception? runtimeReplaceFailure = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var state = _node.State
                ?? throw new InvalidOperationException("libzt network state is not available.");

            generation = _generations.BeginSoft();
            sequence = Interlocked.Increment(ref _sequence);
            _pendingSoftEpoch = generation.Epoch.Value;
            _pendingSoftStartedTimestamp = Stopwatch.GetTimestamp();
            _pendingSoftSequence = sequence;
            _pendingSoftCommandId = commandId;
            _pendingSoftReason = reason;

            JsonLog.Info("recovery_soft_start", new {
                sequence,
                epoch = generation.Epoch.Value,
                reason,
                command_id = commandId
            });

            await PublishStatusAsync(
                phase: "soft",
                kind: "soft",
                reason: reason,
                sequence: sequence,
                epoch: generation.Epoch.Value,
                commandId: commandId,
                elapsedMilliseconds: 0,
                error: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Soft Recovery keeps the libzt node/identity alive, but replaces
            // all overlay listeners and their native sockets immediately.
            try
            {
                await _overlayRuntime.ReplaceAsync(
                    state,
                    cancellationToken).ConfigureAwait(false);
                _generations.CompleteSoft(generation, state);
            }
            catch (Exception ex)
            {
                runtimeReplaceFailure = ex;
                JsonLog.Error("recovery_soft_runtime_replace_failed", new {
                    sequence,
                    epoch = generation.Epoch.Value,
                    reason,
                    command_id = commandId,
                    error_type = ex.GetType().Name,
                    error = ex.Message
                });
            }
        }
        finally
        {
            _recoveryGate.Release();
        }

        if (runtimeReplaceFailure is not null)
        {
            return await TriggerHardRecoveryCoreAsync(
                reason: "soft_runtime_replace_failed",
                commandId: commandId,
                expectedSoftEpoch: generation.Epoch.Value,
                force: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        _ = ValidateSoftRecoveryAsync(
            generation.Epoch.Value,
            sequence,
            reason,
            commandId,
            _lifetime.Token);

        return generation.Epoch.Value;
    }

    internal async Task<long> TriggerHardRecoveryAsync(
        string reason,
        long? commandId,
        CancellationToken cancellationToken)
        => await TriggerHardRecoveryCoreAsync(
            reason: reason,
            commandId: commandId,
            expectedSoftEpoch: null,
            force: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public void ReportOverlaySuccess(long epoch)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _ = MarkSoftRecoveredAsync(
            epoch: epoch,
            reason: "overlay_activity",
            cancellationToken: _lifetime.Token);
    }

    public void ReportOverlayFailure(long epoch, Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        JsonLog.Info("recovery_overlay_failure", new {
            epoch,
            error_type = exception.GetType().Name,
            error = exception.Message
        });

        if (Volatile.Read(ref _pendingSoftEpoch) != epoch)
            return;

        var elapsed = Stopwatch.GetElapsedTime(
            Interlocked.Read(ref _pendingSoftStartedTimestamp));
        if (elapsed < _options.RecoverySoftWindow)
            return;

        _ = TriggerHardRecoveryCoreAsync(
            reason: "overlay_failure_after_soft_window",
            commandId: null,
            expectedSoftEpoch: epoch,
            force: false,
            cancellationToken: _lifetime.Token);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs eventArgs)
        => ScheduleNetworkChange("network_address_changed");

    private void OnNetworkAvailabilityChanged(
        object? sender,
        NetworkAvailabilityEventArgs eventArgs)
        => ScheduleNetworkChange(
            eventArgs.IsAvailable
                ? "network_available"
                : "network_unavailable");

    private void ScheduleNetworkChange(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (DateTimeOffset.UtcNow < _ignoreNetworkEventsUntil)
        {
            JsonLog.Info("recovery_network_event_ignored", new {
                reason,
                ignore_until = _ignoreNetworkEventsUntil
            });
            return;
        }

        CancellationTokenSource current;
        lock (_scheduleGate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            current = _debounce;
        }

        _ = DebounceNetworkChangeAsync(reason, current);
    }

    private async Task DebounceNetworkChangeAsync(
        string reason,
        CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(
                _options.RecoveryEventDebounce,
                debounce.Token).ConfigureAwait(false);

            if (!ReferenceEquals(_debounce, debounce))
                return;

            await TriggerSoftRecoveryAsync(
                reason: reason,
                commandId: null,
                cancellationToken: debounce.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error("recovery_network_event_failed", new {
                reason,
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
    }

    private async Task ValidateSoftRecoveryAsync(
        long epoch,
        long sequence,
        string reason,
        long? commandId,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                _options.RecoverySoftWindow,
                cancellationToken).ConfigureAwait(false);

            if (Volatile.Read(ref _pendingSoftEpoch) != epoch)
                return;

            var probeSucceeded = await ProbeCurrentGenerationAsync(
                epoch,
                cancellationToken).ConfigureAwait(false);
            if (probeSucceeded)
            {
                await MarkSoftRecoveredAsync(
                    epoch: epoch,
                    reason: "soft_probe",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            JsonLog.Info("recovery_soft_probe_failed", new {
                sequence,
                epoch,
                reason,
                command_id = commandId
            });

            await TriggerHardRecoveryCoreAsync(
                reason: "soft_probe_failed",
                commandId: commandId,
                expectedSoftEpoch: epoch,
                force: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error("recovery_soft_validation_failed", new {
                sequence,
                epoch,
                error_type = ex.GetType().Name,
                error = ex.Message
            });

            if (Volatile.Read(ref _pendingSoftEpoch) == epoch)
            {
                await TriggerHardRecoveryCoreAsync(
                    reason: "soft_validation_exception",
                    commandId: commandId,
                    expectedSoftEpoch: epoch,
                    force: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> ProbeCurrentGenerationAsync(
        long expectedEpoch,
        CancellationToken cancellationToken)
    {
        if (_generations.CurrentEpoch != expectedEpoch)
            return false;

        if (_probePeers.Length == 0)
        {
            var ready = _node.IsTransportReady;
            JsonLog.Info("recovery_soft_probe_transport", new {
                epoch = expectedEpoch,
                ready
            });
            return ready;
        }

        var generation = _generations.Capture();
        if (generation.Epoch.Value != expectedEpoch)
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generation.Epoch.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1_500));

        try
        {
            _ = await generation.Ready.WaitAsync(timeout.Token).ConfigureAwait(false);
            var connector = new LibztTcpConnector(TimeSpan.FromMilliseconds(1_250));
            var target = new ProxyTarget(
                _probePeers[0].ToString(),
                _options.OverlayPort);
            await using var connection = await connector.ConnectForGenerationAsync(
                target,
                generation.Epoch.CancellationToken,
                timeout.Token).ConfigureAwait(false);
            await ProbeSocksAgentAsync(
                connection,
                timeout.Token).ConfigureAwait(false);

            JsonLog.Info("recovery_soft_probe_success", new {
                epoch = expectedEpoch,
                peer = _probePeers[0].ToString(),
                layer = "socks5"
            });
            return true;
        }
        catch (Exception ex)
            when (!cancellationToken.IsCancellationRequested
                  && !generation.Epoch.CancellationToken.IsCancellationRequested)
        {
            JsonLog.Info("recovery_soft_probe_connect_failed", new {
                epoch = expectedEpoch,
                peer = _probePeers[0].ToString(),
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            return false;
        }
    }

    private static async Task ProbeSocksAgentAsync(
        LibztTcpConnection connection,
        CancellationToken cancellationToken)
    {
        // A raw TCP connect is not enough after a libzt restart: lwIP can
        // briefly accept a connection before bidirectional application I/O is
        // stable. Complete a tiny SOCKS5 request/response exchange instead.
        var greeting = new byte[] { 0x05, 0x01, 0x00 };
        await connection.WriteAsync(
            greeting,
            greeting.Length,
            cancellationToken).ConfigureAwait(false);

        var methodReply = new byte[2];
        await ReadProbeExactlyAsync(
            connection,
            methodReply,
            cancellationToken).ConfigureAwait(false);
        if (methodReply[0] != 0x05 || methodReply[1] != 0x00)
        {
            throw new IOException(
                $"Recovery SOCKS5 probe method reply was {Convert.ToHexString(methodReply)}.");
        }

        // BIND (0x02) is intentionally unsupported by NetLoop. The server
        // replies deterministically without opening a downstream connection,
        // which makes this a side-effect-free full-duplex health check.
        var unsupportedRequestHeader = new byte[] { 0x05, 0x02, 0x00, 0x01 };
        await connection.WriteAsync(
            unsupportedRequestHeader,
            unsupportedRequestHeader.Length,
            cancellationToken).ConfigureAwait(false);

        var commandReply = new byte[10];
        await ReadProbeExactlyAsync(
            connection,
            commandReply,
            cancellationToken).ConfigureAwait(false);
        if (commandReply[0] != 0x05 || commandReply[1] != 0x07)
        {
            throw new IOException(
                $"Recovery SOCKS5 probe command reply was {Convert.ToHexString(commandReply)}.");
        }
    }

    private static async Task ReadProbeExactlyAsync(
        LibztTcpConnection connection,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var scratch = offset == 0
                ? buffer
                : new byte[buffer.Length - offset];
            var read = await connection.ReadAsync(
                scratch,
                buffer.Length - offset,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Recovery SOCKS5 probe reached EOF.");

            if (offset != 0)
                Buffer.BlockCopy(scratch, 0, buffer, offset, read);
            offset += read;
        }
    }

    private async Task MarkSoftRecoveredAsync(
        long epoch,
        string reason,
        CancellationToken cancellationToken)
    {
        // Overlay success is reported for every successful peer TCP connect.
        // Do not queue those normal data-plane events behind the recovery gate
        // unless this exact epoch actually has a pending soft recovery.
        if (Volatile.Read(ref _pendingSoftEpoch) != epoch)
            return;

        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingSoftEpoch != epoch)
                return;

            var elapsed = Stopwatch.GetElapsedTime(_pendingSoftStartedTimestamp);
            var sequence = _pendingSoftSequence;
            var commandId = _pendingSoftCommandId;
            var triggerReason = _pendingSoftReason;
            _pendingSoftEpoch = -1;
            _pendingSoftSequence = 0;
            _pendingSoftCommandId = null;
            _pendingSoftReason = null;

            JsonLog.Info("recovery_soft_ready", new {
                epoch,
                reason,
                trigger_reason = triggerReason,
                command_id = commandId,
                elapsed_ms = elapsed.TotalMilliseconds
            });

            await PublishStatusAsync(
                phase: "ready",
                kind: "soft",
                reason: triggerReason ?? reason,
                sequence: sequence,
                epoch: epoch,
                commandId: commandId,
                elapsedMilliseconds: elapsed.TotalMilliseconds,
                error: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task<long> TriggerHardRecoveryCoreAsync(
        string reason,
        long? commandId,
        long? expectedSoftEpoch,
        bool force,
        CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        OverlayGeneration? hardGeneration = null;
        long sequence = 0;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (!force)
            {
                if (expectedSoftEpoch is null || _pendingSoftEpoch != expectedSoftEpoch.Value)
                    return _generations.CurrentEpoch;

                if (DateTimeOffset.UtcNow < _ignoreNetworkEventsUntil)
                    return _generations.CurrentEpoch;
            }

            var previousState = _node.State
                ?? throw new InvalidOperationException("libzt network state is not available.");
            var previousNodeId = previousState.NodeId;
            var previousAddresses = previousState.ManagedAddresses
                .Select(static value => value.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray();

            hardGeneration = _generations.BeginHard();
            _pendingSoftEpoch = -1;
            _pendingSoftSequence = 0;
            _pendingSoftCommandId = null;
            _pendingSoftReason = null;
            sequence = Interlocked.Increment(ref _sequence);
            _ignoreNetworkEventsUntil =
                DateTimeOffset.UtcNow + _options.RecoveryHardTimeout + _options.RecoveryCooldown;

            JsonLog.Info("recovery_hard_start", new {
                sequence,
                epoch = hardGeneration.Epoch.Value,
                reason,
                command_id = commandId
            });

            await PublishStatusAsync(
                phase: "hard",
                kind: "hard",
                reason: reason,
                sequence: sequence,
                epoch: hardGeneration.Epoch.Value,
                commandId: commandId,
                elapsedMilliseconds: 0,
                error: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            timeout.CancelAfter(_options.RecoveryHardTimeout);

            await _overlayRuntime.StopAsync(timeout.Token).ConfigureAwait(false);
            var state = await _node.RestartAsync(timeout.Token).ConfigureAwait(false);

            if (state.NodeId != previousNodeId)
            {
                throw new InvalidOperationException(
                    $"Hard Recovery changed Node ID from {previousNodeId:x10} to {state.NodeId:x10}.");
            }

            var currentAddresses = state.ManagedAddresses
                .Select(static value => value.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!previousAddresses.SequenceEqual(currentAddresses, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Hard Recovery changed Managed IP set; routing snapshot is no longer valid.");
            }

            await _overlayRuntime.StartAsync(state, timeout.Token).ConfigureAwait(false);
            _generations.CompleteHard(hardGeneration, state);

            // NetTransportIsReady can become true before a fresh peer TCP path
            // is actually usable. Do not publish Hard Recovery ready until the
            // real overlay Agent port can be connected. This makes the hard
            // recovery metric represent business readiness rather than an
            // internal libzt state flag.
            while (!await ProbeCurrentGenerationAsync(
                       hardGeneration.Epoch.Value,
                       timeout.Token).ConfigureAwait(false))
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    timeout.Token).ConfigureAwait(false);
            }

            JsonLog.Info("recovery_hard_probe_ready", new {
                sequence,
                epoch = hardGeneration.Epoch.Value,
                peer = _probePeers.FirstOrDefault()?.ToString()
            });

            var elapsed = Stopwatch.GetElapsedTime(started);
            _ignoreNetworkEventsUntil =
                DateTimeOffset.UtcNow + _options.RecoveryCooldown;

            JsonLog.Info("recovery_hard_ready", new {
                sequence,
                epoch = hardGeneration.Epoch.Value,
                reason,
                elapsed_ms = elapsed.TotalMilliseconds,
                node = state.NodeId.ToString("x10"),
                addresses = currentAddresses
            });

            await PublishStatusAsync(
                phase: "ready",
                kind: "hard",
                reason: reason,
                sequence: sequence,
                epoch: hardGeneration.Epoch.Value,
                commandId: commandId,
                elapsedMilliseconds: elapsed.TotalMilliseconds,
                error: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return hardGeneration.Epoch.Value;
        }
        catch (Exception ex)
        {
            if (hardGeneration is not null)
                _generations.FailHard(hardGeneration, ex);

            var elapsed = Stopwatch.GetElapsedTime(started);
            JsonLog.Error("recovery_hard_failed", new {
                sequence,
                epoch = hardGeneration?.Epoch.Value,
                reason,
                elapsed_ms = elapsed.TotalMilliseconds,
                error_type = ex.GetType().Name,
                error = ex.Message
            });

            try
            {
                await PublishStatusAsync(
                    phase: "failed",
                    kind: "hard",
                    reason: reason,
                    sequence: sequence,
                    epoch: hardGeneration?.Epoch.Value ?? _generations.CurrentEpoch,
                    commandId: commandId,
                    elapsedMilliseconds: elapsed.TotalMilliseconds,
                    error: ex.Message,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception statusError)
            {
                JsonLog.Error("recovery_status_write_failed", new {
                    error = statusError.Message
                });
            }

            _requestProcessRestart(reason, ex);
            return hardGeneration?.Epoch.Value ?? _generations.CurrentEpoch;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task PublishStatusAsync(
        string phase,
        string kind,
        string reason,
        long sequence,
        long epoch,
        long? commandId,
        double elapsedMilliseconds,
        string? error,
        CancellationToken cancellationToken)
    {
        await StatusWriter.WriteAsync(
            _options.RecoveryStatusFile,
            new {
                phase,
                kind,
                reason,
                sequence,
                epoch,
                command_id = commandId,
                elapsed_ms = elapsedMilliseconds,
                node_id = _node.State?.NodeId.ToString("x10"),
                managed_addresses = _node.State?.ManagedAddresses
                    .Select(static value => value.ToString())
                    .ToArray(),
                process_id = Environment.ProcessId,
                error
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

        lock (_scheduleGate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        await _recoveryGate.WaitAsync().ConfigureAwait(false);
        _recoveryGate.Release();
        _recoveryGate.Dispose();
        _lifetime.Dispose();
    }
}
