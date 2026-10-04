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
    private const string ResetCountEnvironment = "NETLOOP_RESET_COUNT";
    private static readonly TimeSpan HalfCloseTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<int> Main(string[] args)
    {
        try
        {
            var waitForPid = TryGetOptionInt(args, "--wait-for-pid");
            if (waitForPid is > 0)
                await WaitForParentExitAsync(waitForPid.Value).ConfigureAwait(false);

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
                    args,
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
        string[] originalArgs,
        CancellationToken shutdownToken)
    {
        var resetRequested = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var node = new LibztNode(
            options.NetworkId,
            options.StateDirectory,
            options.StartupTimeout);
        var state = await node.StartAsync(shutdownToken).ConfigureAwait(false);

        var peers = options.Peers.ToHashSet();
        if (options.DefaultExit is not null)
            peers.Add(options.DefaultExit);

        var snapshot = new OverlayNetworkSnapshot(
            state.ManagedAddresses,
            peers,
            Array.Empty<ManagedRoute>());
        var selector = new RouteSelector(snapshot, options.DefaultExit);

        var libztConnector = new LibztTcpConnector(options.ConnectTimeout);
        var loopbackConnector = new LoopbackTcpConnector(options.ConnectTimeout);
        IProxyConnector egressConnector = BuildEgressConnector(options);
        var overlayBind = SelectOverlayBindAddress(state.ManagedAddresses);
        var udpEgressFactory = BuildUdpEgressFactory(options);
        var routingUdpFactory = new RoutingUdpTransportFactory(
            selector,
            overlayBind,
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
        var overlayRouter = new RoutingConnector(
            selector,
            overlayIngress: true,
            options.OverlayPort,
            libztConnector,
            loopbackConnector,
            egressConnector);

        await using var overlayAgent = new OverlaySocksAgent(
            overlayBind.ToString(),
            options.OverlayPort,
            new Socks5ConnectionHandler(overlayRouter, HalfCloseTimeout),
            MaxTcpTunnels);
        await using var overlayUdpAgent = new OverlayUdpAgent(
            overlayBind,
            options.OverlayUdpPort,
            selector,
            udpEgressFactory,
            MaxUdpAssociations,
            options.UdpIdleTimeout);

        await using var localSocks = new LocalSocksServer(
            new IPEndPoint(options.SocksAddress, options.SocksPort),
            new Socks5ConnectionHandler(
                localRouter,
                HalfCloseTimeout,
                udpAssociationFactory),
            MaxTcpTunnels);
        localSocks.Start();

        await using var resetMonitor = new RuntimeResetMonitor(
            options.ResetCommandFile,
            options.ResetEventDebounce,
            reason => resetRequested.TrySetResult(reason));
        resetMonitor.Start();

        var resetCount = GetResetCount();
        await WriteReadyStatusAsync(
            options,
            state,
            peers,
            overlayBind,
            resetCount,
            shutdownToken).ConfigureAwait(false);

        JsonLog.Info("netloop_ready", new {
            network = options.NetworkId.ToString("x16"),
            node = state.NodeId.ToString("x10"),
            overlay = $"{overlayBind}:{options.OverlayPort}",
            overlay_udp = $"{overlayBind}:{options.OverlayUdpPort}",
            socks = $"{options.SocksAddress}:{options.SocksPort}",
            reset_count = resetCount,
            process_id = Environment.ProcessId
        });

        var shutdownTask = Task.Delay(
            Timeout.InfiniteTimeSpan,
            shutdownToken);
        var completed = await Task.WhenAny(
            shutdownTask,
            resetRequested.Task).ConfigureAwait(false);

        if (ReferenceEquals(completed, resetRequested.Task))
        {
            var reason = await resetRequested.Task.ConfigureAwait(false);
            var nextResetCount = checked(resetCount + 1);

            await StatusWriter.WriteAsync(
                options.StatusFile,
                new {
                    phase = "resetting",
                    network_id = options.NetworkId.ToString("x16"),
                    node_id = state.NodeId.ToString("x10"),
                    reset_count = resetCount,
                    next_reset_count = nextResetCount,
                    process_id = Environment.ProcessId,
                    reason
                },
                CancellationToken.None).ConfigureAwait(false);

            var replacementPid = StartReplacementProcess(
                originalArgs,
                nextResetCount);

            JsonLog.Info("runtime_reset_spawned", new {
                reason,
                old_process_id = Environment.ProcessId,
                new_process_id = replacementPid,
                next_reset_count = nextResetCount
            });

            // This is intentional. A network reset means all old sessions are
            // disposable. Let the OS tear down every TCP/UDP/native handle at
            // once instead of trying to migrate or gracefully drain them.
            Environment.Exit(0);
            return 0;
        }

        try
        {
            await shutdownTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
        }

        return 0;
    }

    private static async Task WriteReadyStatusAsync(
        HostOptions options,
        LibztNetworkState state,
        IReadOnlyCollection<IPAddress> peers,
        IPAddress overlayBind,
        int resetCount,
        CancellationToken cancellationToken)
    {
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
                reset_count = resetCount,
                process_id = Environment.ProcessId
            },
            cancellationToken).ConfigureAwait(false);
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

    private static IPAddress SelectOverlayBindAddress(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            throw new InvalidOperationException("ZeroTier network has no Managed IP.");

        return addresses.FirstOrDefault(static x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses[0];
    }

    private static int GetResetCount()
        => int.TryParse(
            Environment.GetEnvironmentVariable(ResetCountEnvironment),
            out var value)
            ? Math.Max(0, value)
            : 0;

    private static int StartReplacementProcess(
        string[] originalArgs,
        int nextResetCount)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Unable to resolve current executable for runtime reset.");

        var startInfo = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var argument in WithoutWaitForPid(originalArgs))
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--wait-for-pid");
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment[ResetCountEnvironment] =
            nextResetCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start replacement NetLoop process.");
        var pid = process.Id;
        process.Dispose();
        return pid;
    }

    private static IEnumerable<string> WithoutWaitForPid(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(
                    args[index],
                    "--wait-for-pid",
                    StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            yield return args[index];
        }
    }

    private static int? TryGetOptionInt(
        string[] args,
        string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(
                    args[index],
                    option,
                    StringComparison.Ordinal))
                continue;

            return int.TryParse(
                args[index + 1],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
                ? value
                : throw new ArgumentException(
                    $"Invalid {option}: {args[index + 1]}");
        }

        return null;
    }

    private static async Task WaitForParentExitAsync(int parentPid)
    {
        if (parentPid == Environment.ProcessId)
            return;

        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Parent already exited.
        }
        catch (InvalidOperationException)
        {
            // Parent already exited.
        }
    }
}
