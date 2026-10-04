using System.Diagnostics;
using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal static class Program
{
    private const int MaxTcpTunnels = 128;
    private const int MaxUdpAssociations = 64;
    private const int MaxAutomaticProcessRestarts = 3;
    private const string ProcessRestartCountEnvironment = "NETLOOP_PROCESS_RESTART_COUNT";
    private static readonly TimeSpan HalfCloseTimeout = TimeSpan.FromSeconds(15);

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

            var restart = await RunNodeAsync(options).ConfigureAwait(false);
            return restart is null ? 0 : StartReplacementProcess(args, restart);
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

    private static async Task<ProcessRestartRequest?> RunNodeAsync(HostOptions options)
    {
        using var shutdown = new CancellationTokenSource();
        var restartRequested = new TaskCompletionSource<ProcessRestartRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await using var node = new LibztNode(
                options.NetworkId,
                options.StateDirectory,
                options.StartupTimeout);
            var state = await node.StartAsync(shutdown.Token).ConfigureAwait(false);

            var peers = options.Peers.ToHashSet();
            if (options.DefaultExit is not null)
                peers.Add(options.DefaultExit);

            var snapshot = new OverlayNetworkSnapshot(
                state.ManagedAddresses,
                peers,
                Array.Empty<ManagedRoute>());
            var selector = new RouteSelector(snapshot, options.DefaultExit);

            using var generations = new OverlayGenerationManager(state);
            var libztConnector = new GenerationLibztTcpConnector(
                generations,
                options.ConnectTimeout);
            var loopbackConnector = new LoopbackTcpConnector(options.ConnectTimeout);
            IProxyConnector egressConnector = BuildEgressConnector(options);
            var udpEgressFactory = BuildUdpEgressFactory(options);
            var routingUdpFactory = new RoutingUdpTransportFactory(
                selector,
                generations,
                options.OverlayUdpPort,
                udpEgressFactory);
            await using var udpAssociationFactory = new Socks5UdpAssociationFactory(
                routingUdpFactory,
                MaxUdpAssociations,
                options.UdpIdleTimeout);

            var localRouter = new RoutingConnector(
                selector,
                overlayIngress: false,
                options.OverlayPort,
                libztConnector,
                loopbackConnector,
                egressConnector);

            await using var overlayRuntime = new OverlayRuntimeController(
                options,
                selector,
                libztConnector,
                loopbackConnector,
                egressConnector,
                udpEgressFactory,
                generations,
                HalfCloseTimeout,
                MaxTcpTunnels,
                MaxUdpAssociations);
            await overlayRuntime.StartAsync(
                state,
                shutdown.Token).ConfigureAwait(false);

            await using var localSocks = new LocalSocksServer(
                new IPEndPoint(options.SocksAddress, options.SocksPort),
                new Socks5ConnectionHandler(
                    localRouter,
                    HalfCloseTimeout,
                    udpAssociationFactory),
                MaxTcpTunnels);
            localSocks.Start();

            await using var recovery = new RecoveryCoordinator(
                options,
                node,
                generations,
                overlayRuntime,
                peers,
                (reason, exception) => restartRequested.TrySetResult(
                    new ProcessRestartRequest(reason, exception.Message)));
            libztConnector.RecoveryObserver = recovery;
            await recovery.StartAsync(shutdown.Token).ConfigureAwait(false);
            await using var recoveryCommands = new RecoveryCommandWatcher(
                options.RecoveryCommandFile,
                recovery);

            var overlayBind = OverlayGenerationManager.SelectBindAddress(state);
            await StatusWriter.WriteAsync(
                options.StatusFile,
                new {
                    phase = "ready",
                    network_id = options.NetworkId.ToString("x16"),
                    node_id = state.NodeId.ToString("x10"),
                    managed_addresses = state.ManagedAddresses.Select(static x => x.ToString()).ToArray(),
                    overlay_host = overlayBind.ToString(),
                    overlay_port = options.OverlayPort,
                    overlay_udp_port = options.OverlayUdpPort,
                    socks_host = options.SocksAddress.ToString(),
                    socks_port = options.SocksPort,
                    peers = peers.Select(static x => x.ToString()).Order().ToArray(),
                    default_exit = options.DefaultExit?.ToString(),
                    egress = options.Egress,
                    epoch = generations.CurrentEpoch,
                    process_id = Environment.ProcessId
                },
                shutdown.Token).ConfigureAwait(false);

            JsonLog.Info("netloop_ready", new {
                network = options.NetworkId.ToString("x16"),
                node = state.NodeId.ToString("x10"),
                overlay = $"{overlayBind}:{options.OverlayPort}",
                overlay_udp = $"{overlayBind}:{options.OverlayUdpPort}",
                socks = $"{options.SocksAddress}:{options.SocksPort}",
                epoch = generations.CurrentEpoch,
                process_id = Environment.ProcessId
            });

            var shutdownTask = WaitForCancellationAsync(shutdown.Token);
            var completed = await Task.WhenAny(
                shutdownTask,
                restartRequested.Task).ConfigureAwait(false);
            if (ReferenceEquals(completed, restartRequested.Task))
            {
                var restart = await restartRequested.Task.ConfigureAwait(false);
                JsonLog.Error("process_restart_requested", new {
                    restart.Reason,
                    restart.Error,
                    process_id = Environment.ProcessId
                });
                return restart;
            }

            try
            {
                await shutdownTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            return null;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static IProxyConnector BuildEgressConnector(HostOptions options)
    {
        var direct = new DirectTcpConnector(options.ConnectTimeout);
        if (options.Egress == "direct")
            return direct;

        return new Socks5ProxyConnector(
            direct,
            new ProxyTarget(
                options.UpstreamHost ?? throw new InvalidOperationException("Missing upstream SOCKS5 host."),
                options.UpstreamPort),
            options.UpstreamUsername,
            options.UpstreamPassword);
    }

    private static IProxyUdpTransportFactory BuildUdpEgressFactory(HostOptions options)
    {
        if (options.Egress == "direct")
            return new DirectUdpTransportFactory();

        var direct = new DirectTcpConnector(options.ConnectTimeout);
        return new Socks5UdpProxyTransportFactory(
            direct,
            new ProxyTarget(
                options.UpstreamHost ?? throw new InvalidOperationException("Missing upstream SOCKS5 host."),
                options.UpstreamPort),
            options.UpstreamUsername,
            options.UpstreamPassword);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(
            Timeout.InfiniteTimeSpan,
            cancellationToken).ConfigureAwait(false);
    }

    private static int StartReplacementProcess(
        string[] args,
        ProcessRestartRequest restart)
    {
        var currentCount = int.TryParse(
            Environment.GetEnvironmentVariable(ProcessRestartCountEnvironment),
            out var parsed)
            ? parsed
            : 0;
        if (currentCount >= MaxAutomaticProcessRestarts)
        {
            JsonLog.Error("process_restart_limit_reached", new {
                restart.Reason,
                restart.Error,
                restart_count = currentCount,
                limit = MaxAutomaticProcessRestarts
            });
            return 1;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Unable to resolve current executable for automatic restart.");
        var startInfo = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment[ProcessRestartCountEnvironment] =
            (currentCount + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start replacement NetLoop process.");
        JsonLog.Info("process_restart_spawned", new {
            old_process_id = Environment.ProcessId,
            new_process_id = process.Id,
            restart_count = currentCount + 1,
            restart.Reason
        });
        process.Dispose();
        return 0;
    }

    private sealed record ProcessRestartRequest(string Reason, string Error);

}
