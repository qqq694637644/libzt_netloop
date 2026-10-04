using System.Diagnostics;
using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            var options = HostOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(HostOptions.Usage);
                return 0;
            }

            if (options.ShowVersion)
            {
                Console.WriteLine("netloop 0.1.0");
                return 0;
            }

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                return await RunAsync(
                    options,
                    shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (Exception ex)
        {
            JsonLog.Error("fatal", new {
                error_type = ex.GetType().FullName,
                error = ex.Message,
                stack = ex.StackTrace
            });
            return 1;
        }
    }

    private static async Task<int> RunAsync(
        HostOptions options,
        CancellationToken shutdownToken)
    {
        await using var node = new LibztNode(
            options.NetworkId,
            options.StateDirectory,
            options.StartupTimeout);
        var state = await node.StartAsync(shutdownToken).ConfigureAwait(false);

        var resetCount = 0;
        long? resetStarted = null;
        string? lastResetReason = null;

        while (!shutdownToken.IsCancellationRequested)
        {
            var runtime = await NetLoopRuntime.CreateAsync(
                options,
                state).ConfigureAwait(false);
            var resetRequested = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resetMonitor = new RuntimeResetMonitor(
                options.ResetCommandFile,
                options.ResetEventDebounce,
                reason => resetRequested.TrySetResult(reason));

            try
            {
                resetMonitor.Start();
                double? resetElapsedMs = resetStarted is null
                    ? null
                    : Stopwatch.GetElapsedTime(resetStarted.Value).TotalMilliseconds;

                await WriteReadyStatusAsync(
                    options,
                    runtime,
                    resetCount,
                    resetElapsedMs,
                    lastResetReason,
                    shutdownToken).ConfigureAwait(false);

                JsonLog.Info("netloop_ready", new {
                    network = options.NetworkId.ToString("x16"),
                    node = runtime.State.NodeId.ToString("x10"),
                    overlay = $"{runtime.OverlayBindAddress}:{options.OverlayPort}",
                    overlay_udp = $"{runtime.OverlayBindAddress}:{options.OverlayUdpPort}",
                    socks = $"{options.SocksAddress}:{options.SocksPort}",
                    reset_count = resetCount,
                    reset_elapsed_ms = resetElapsedMs,
                    process_id = Environment.ProcessId
                });

                resetStarted = null;
                lastResetReason = null;

                var shutdownTask = Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    shutdownToken);
                var completed = await Task.WhenAny(
                    shutdownTask,
                    resetRequested.Task).ConfigureAwait(false);

                if (ReferenceEquals(completed, shutdownTask))
                {
                    try
                    {
                        await shutdownTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (shutdownToken.IsCancellationRequested)
                    {
                    }
                    return 0;
                }

                lastResetReason = await resetRequested.Task.ConfigureAwait(false);
                var nextResetCount = checked(resetCount + 1);
                resetStarted = Stopwatch.GetTimestamp();

                await StatusWriter.WriteAsync(
                    options.StatusFile,
                    new {
                        phase = "resetting",
                        network_id = options.NetworkId.ToString("x16"),
                        node_id = runtime.State.NodeId.ToString("x10"),
                        reset_count = resetCount,
                        next_reset_count = nextResetCount,
                        process_id = Environment.ProcessId,
                        reason = lastResetReason
                    },
                    CancellationToken.None).ConfigureAwait(false);

                JsonLog.Info("runtime_reset_start", new {
                    reason = lastResetReason,
                    reset_count = resetCount,
                    next_reset_count = nextResetCount,
                    process_id = Environment.ProcessId
                });

                // Stop accepting traffic and wait until every active managed
                // session from the old runtime is gone.
                await resetMonitor.DisposeAsync().ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);

                // Keep the ZeroTier node/identity/network alive. Tell the
                // service that the host physical network changed so it
                // immediately refreshes UDP binds and local-interface state.
                node.NotifyPhysicalNetworkChanged();
                state = node.RefreshNetworkState();
                resetCount = nextResetCount;

                JsonLog.Info("runtime_reset_transport_refresh", new {
                    reason = lastResetReason,
                    reset_count = resetCount,
                    node = state.NodeId.ToString("x10")
                });
            }
            finally
            {
                await resetMonitor.DisposeAsync().ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static Task WriteReadyStatusAsync(
        HostOptions options,
        NetLoopRuntime runtime,
        int resetCount,
        double? resetElapsedMs,
        string? resetReason,
        CancellationToken cancellationToken)
        => StatusWriter.WriteAsync(
            options.StatusFile,
            new {
                phase = "ready",
                network_id = options.NetworkId.ToString("x16"),
                node_id = runtime.State.NodeId.ToString("x10"),
                managed_addresses = runtime.State.ManagedAddresses
                    .Select(static address => address.ToString())
                    .ToArray(),
                overlay_host = runtime.OverlayBindAddress.ToString(),
                overlay_port = options.OverlayPort,
                overlay_udp_port = options.OverlayUdpPort,
                socks_host = options.SocksAddress.ToString(),
                socks_port = options.SocksPort,
                peers = runtime.Peers
                    .Select(static address => address.ToString())
                    .Order()
                    .ToArray(),
                default_exit = options.DefaultExit?.ToString(),
                egress = options.Egress,
                reset_count = resetCount,
                reset_elapsed_ms = resetElapsedMs,
                reset_reason = resetReason,
                process_id = Environment.ProcessId
            },
            cancellationToken);
}
