using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
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
                Console.WriteLine($"netloop {GetVersion()}");
                return 0;
            }

            if (options.LogFile is not null)
                JsonLog.ConfigureFile(
                    options.LogFile,
                    options.LogMaxMegabytes,
                    options.LogRetainedFiles);

            LibztPolling.ConfigureShared(options.LibztPollMode == "shared");

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
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return 0;
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
        finally
        {
            using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await JsonLog.FlushAsync(flushTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A blocked output destination must not hang process shutdown.
            }
            catch (Exception)
            {
                // Logging failures must not mask the process exit code.
            }
        }
    }

    private static string GetVersion()
        => typeof(Program).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? typeof(Program).Assembly.GetName().Version?.ToString()
           ?? "unknown";

    private static async Task<int> RunAsync(
        HostOptions options,
        CancellationToken shutdownToken)
    {
        await using var node = new LibztNode(
            options.NetworkId,
            options.StateDirectory,
            options.StartupTimeout);

        NetLoopRuntime? activeRuntime = null;
        node.NetworkStateChanged += updated =>
        {
            // This is a config-only update, not a physical network reset.
            // Existing sessions remain attached to the same runtime.
            Volatile.Read(ref activeRuntime)?.UpdateNetworkState(updated);
        };
        node.NetworkConfigurationUnavailable += reason =>
            Volatile.Read(ref activeRuntime)?.MarkNetworkConfigurationUnavailable(reason);

        var resetCount = 0;
        long? resetStarted = null;
        string? lastResetReason = null;
        var resetRequests = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1) {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        var resetRuntimeOnNetworkChange = 0;

        void RequestReset(string reason)
        {
            var physicalNetworkEvent = reason.StartsWith(
                "network_",
                StringComparison.Ordinal);
            if (physicalNetworkEvent
                && Volatile.Read(ref resetRuntimeOnNetworkChange) == 0)
            {
                node.TryNotifyPhysicalNetworkChanged();
                return;
            }

            resetRequests.Writer.TryWrite(reason);
        }

        await using var resetMonitor = new RuntimeResetMonitor(
            options.ResetCommandFile,
            options.ResetEventDebounce,
            RequestReset);
        resetMonitor.Start();

        await node.StartAsync(shutdownToken).ConfigureAwait(false);
        Volatile.Write(ref resetRuntimeOnNetworkChange, 1);

        while (!shutdownToken.IsCancellationRequested)
        {
            var runtime = await RuntimeRecovery.CreateAsync(
                node, options, resetCount, shutdownToken).ConfigureAwait(false);
            using var statusStop = CancellationTokenSource
                .CreateLinkedTokenSource(shutdownToken);
            var statusHeartbeat = RunStatusHeartbeatAsync(
                options, runtime, resetCount, statusStop.Token);

            try
            {
                Volatile.Write(ref activeRuntime, runtime);
                runtime.UpdateNetworkState(node.State ?? runtime.State);
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
                    route_generation = runtime.State.Generation,
                    process_id = Environment.ProcessId
                });

                resetStarted = null;
                lastResetReason = null;

                using (var supervision = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
                {
                    var resetRequested = resetRequests.Reader.ReadAsync(supervision.Token).AsTask();
                    var criticalFailure = runtime.WaitForCriticalServiceFailureAsync(supervision.Token);
                    try
                    {
                        var completed = await Task.WhenAny(resetRequested, criticalFailure)
                            .ConfigureAwait(false);
                        if (ReferenceEquals(completed, criticalFailure))
                        {
                            try
                            {
                                await criticalFailure.ConfigureAwait(false);
                            }
                            catch (IOException ex) when (!shutdownToken.IsCancellationRequested)
                            {
                                JsonLog.Error("runtime_critical_service_failed", new {
                                    error_type = ex.GetType().Name,
                                    error = ex.Message,
                                    inner_error = ex.InnerException?.Message
                                });
                            }
                            lastResetReason = "critical_service_failure";
                        }
                        else
                        {
                            lastResetReason = await resetRequested.ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                        when (shutdownToken.IsCancellationRequested)
                    {
                        return 0;
                    }
                    finally
                    {
                        await supervision.CancelAsync().ConfigureAwait(false);
                        try
                        {
                            await Task.WhenAll(resetRequested, criticalFailure).ConfigureAwait(false);
                        }
                        catch (Exception) when (supervision.IsCancellationRequested)
                        {
                            // Both tasks are observed; a failing listener
                            // was already recorded before triggering reset.
                        }
                    }
                }

                await statusStop.CancelAsync().ConfigureAwait(false);
                await statusHeartbeat.ConfigureAwait(false);

                var nextResetCount = checked(resetCount + 1);
                resetStarted = Stopwatch.GetTimestamp();
                Volatile.Write(ref resetRuntimeOnNetworkChange, 0);
                Volatile.Write(ref activeRuntime, null);

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

                // Network reset is an abort, not a graceful drain. Abort first
                // so stale traffic stops immediately, then wait until every old
                // socket/task has released its fd before the new runtime starts.
                runtime.Abort();
                await runtime.DisposeAsync().ConfigureAwait(false);

                // Keep the ZeroTier node/identity/network alive. Tell the
                // service that the host physical network changed so it
                // immediately refreshes UDP binds and local-interface state.
                if (lastResetReason != "critical_service_failure")
                    node.TryNotifyPhysicalNetworkChanged();
                // The latest ZeroTier state is re-queried on each runtime
                // creation attempt, including after a transient empty config.
                Volatile.Write(ref resetRuntimeOnNetworkChange, 1);
                resetCount = nextResetCount;

                JsonLog.Info("runtime_reset_transport_refresh", new {
                    reason = lastResetReason,
                    reset_count = resetCount,
                    node = runtime.State.NodeId.ToString("x10")
                });
            }
            finally
            {
                await statusStop.CancelAsync().ConfigureAwait(false);
                await statusHeartbeat.ConfigureAwait(false);
                Volatile.Write(ref activeRuntime, null);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task RunStatusHeartbeatAsync(
        HostOptions options,
        NetLoopRuntime runtime,
        int resetCount,
        CancellationToken cancellationToken)
    {
        if (options.StatusFile is null)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                try
                {
                    await WriteReadyStatusAsync(
                        options, runtime, resetCount,
                        null, null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                    when (ex is IOException or UnauthorizedAccessException)
                {
                    JsonLog.Error("status_heartbeat_write_failed", new {
                        error_type = ex.GetType().Name,
                        error = ex.Message
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
                phase = runtime.IsHealthy ? "ready" : "degraded",
                reason = runtime.DegradedReason,
                updated_at_utc = DateTimeOffset.UtcNow,
                last_network_query_utc = runtime.State.QueriedAtUtc,
                network_config_available = runtime.IsNetworkConfigAvailable,
                overlay_bind_assigned = runtime.IsAgentBindAddressAssigned,
                network_id = options.NetworkId.ToString("x16"),
                node_id = runtime.State.NodeId.ToString("x10"),
                primary_overlay_address =
                    runtime.OverlayBindAddress.ToString(),
                overlay_port = options.OverlayPort,
                overlay_udp_port = options.OverlayUdpPort,
                socks_host = options.SocksAddress.ToString(),
                socks_port = options.SocksPort,
                assigned_prefixes = runtime.State.AssignedPrefixes
                    .Select(static prefix => prefix.ToString()).ToArray(),
                managed_routes = runtime.State.ManagedRoutes
                    .Select(static route => new {
                        target = route.Target.ToString(),
                        via = route.Via?.ToString(),
                        flags = route.Flags,
                        metric = route.Metric
                    }).ToArray(),
                route_generation = runtime.State.Generation,
                agent_source_acl_enabled = runtime.AccessPolicy.RestrictSources,
                agent_target_acl_enabled = runtime.AccessPolicy.RestrictTargets,
                agent_source_denied = runtime.AccessPolicy.RejectedSources,
                agent_target_denied = runtime.AccessPolicy.RejectedTargets,
                libzt_poller = LibztPolling.SnapshotMetrics(),
                udp_queue_dropped = UdpQueueMetrics.Snapshot(),
                tcp_tunnel_capacity_per_listener = options.MaxTcpTunnels,
                udp_association_capacity_per_entry = options.MaxUdpAssociations,
                udp_queue_capacity_per_transport = options.UdpQueueCapacity,
                relay_buffer_kib = options.RelayBufferKiB,
                default_exit = options.DefaultExit?.ToString(),
                egress = options.Egress,
                reset_count = resetCount,
                reset_elapsed_ms = resetElapsedMs,
                reset_reason = resetReason,
                process_id = Environment.ProcessId
            },
            cancellationToken);
}
