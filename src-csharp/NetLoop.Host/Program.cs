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
            options.NetworkId, options.StateDirectory, options.StartupTimeout);
        await using var owner = new RuntimeOwner(node, options);

        var statusChanges = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        owner.StatusChanged += status => {
            statusChanges.Writer.TryWrite(true);
            JsonLog.Info("runtime_phase", new {
                phase = status.Phase.ToString().ToLowerInvariant(),
                reason = status.Reason,
                reset_count = status.ResetCount,
                route_generation = status.Network?.Generation,
                node = status.Network?.NodeId.ToString("x10"),
                bind_address = status.Runtime?.OverlayBindAddress.ToString()
            });
        };

        void OnMonitorEvent(string reason)
        {
            if (reason.StartsWith("network_", StringComparison.Ordinal))
                owner.RequestPhysicalNetworkRefresh(reason);
            else
                owner.RequestManualReset(reason);
        }

        await using var resetMonitor = new RuntimeResetMonitor(
            options.ResetCommandFile,
            options.ResetEventDebounce,
            OnMonitorEvent);
        resetMonitor.Start();

        using var statusStop = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownToken);
        var statusLoop = RunStatusLoopAsync(
            options, owner, statusChanges.Reader, statusStop.Token);

        try
        {
            await owner.RunAsync(shutdownToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await statusStop.CancelAsync().ConfigureAwait(false);
            await statusLoop.ConfigureAwait(false);
        }
    }

    private static async Task RunStatusLoopAsync(
        HostOptions options,
        RuntimeOwner owner,
        ChannelReader<bool> changes,
        CancellationToken cancellationToken)
    {
        if (options.StatusFile is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            await WriteStatusAsync(options, owner.Snapshot, cancellationToken)
                .ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                _ = await changes.ReadAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
            }
        }
    }

    private static Task WriteStatusAsync(
        HostOptions options,
        RuntimeStatus status,
        CancellationToken cancellationToken)
    {
        var runtime = status.Runtime;
        var state = runtime?.State ?? status.Network;
        return StatusWriter.TryWriteAsync(
            options.StatusFile,
            new {
                phase = status.Phase.ToString().ToLowerInvariant(),
                reason = status.Reason ?? runtime?.DegradedReason,
                updated_at_utc = DateTimeOffset.UtcNow,
                network_id = options.NetworkId.ToString("x16"),
                node_id = state?.NodeId.ToString("x10"),
                network_config_available = runtime?.IsNetworkConfigAvailable ?? false,
                last_network_query_utc = state?.QueriedAtUtc,
                active_listener_address = runtime?.OverlayBindAddress.ToString(),
                overlay_port = options.OverlayPort,
                overlay_udp_port = options.OverlayUdpPort,
                socks_host = options.SocksAddress.ToString(),
                socks_port = options.SocksPort,
                assigned_prefixes = state?.AssignedPrefixes.Select(
                    static prefix => prefix.ToString()).ToArray(),
                managed_routes = state?.ManagedRoutes.Select(
                    static route => new {
                        target = route.Target.ToString(),
                        via = route.Via?.ToString(),
                        flags = route.Flags,
                        metric = route.Metric
                    }).ToArray(),
                route_generation = state?.Generation,
                agent_source_acl_enabled = runtime?.AccessPolicy.RestrictSources,
                agent_target_acl_enabled = runtime?.AccessPolicy.RestrictTargets,
                agent_source_denied = runtime?.AccessPolicy.RejectedSources,
                agent_target_denied = runtime?.AccessPolicy.RejectedTargets,
                active_tcp_tunnels = runtime?.ActiveTcpTunnels,
                tcp_tunnel_capacity = options.MaxTcpTunnels,
                active_local_udp_associations = runtime?.ActiveLocalUdpAssociations,
                active_overlay_udp_associations = runtime?.ActiveOverlayUdpAssociations,
                udp_queued_bytes = runtime?.UdpQueuedBytes,
                udp_peak_queued_bytes = runtime?.UdpPeakQueuedBytes,
                udp_queue_byte_limit = runtime?.UdpQueueByteLimit,
                udp_queue_dropped = UdpQueueMetrics.Snapshot(),
                libzt_poller = LibztPolling.SnapshotMetrics(),
                default_exit = options.DefaultExit?.ToString(),
                egress = options.Egress,
                reset_count = status.ResetCount,
                process_id = Environment.ProcessId
            },
            cancellationToken);
    }
}
