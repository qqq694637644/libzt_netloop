using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal static class Program
{
    private const int MaxTcpTunnels = 128;
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

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };

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

            var libztConnector = new LibztTcpConnector(options.ConnectTimeout);
            var loopbackConnector = new LoopbackTcpConnector(options.ConnectTimeout);
            IProxyConnector egressConnector = BuildEgressConnector(options);

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

            var overlayBind = SelectOverlayBindAddress(state.ManagedAddresses);
            await using var overlayAgent = new OverlaySocksAgent(
                overlayBind.ToString(),
                options.OverlayPort,
                new Socks5ConnectionHandler(overlayRouter, HalfCloseTimeout),
                MaxTcpTunnels);

            await using var localSocks = new LocalSocksServer(
                new IPEndPoint(options.SocksAddress, options.SocksPort),
                new Socks5ConnectionHandler(localRouter, HalfCloseTimeout),
                MaxTcpTunnels);
            localSocks.Start();

            await StatusWriter.WriteAsync(
                options.StatusFile,
                new {
                    phase = "ready",
                    network_id = options.NetworkId.ToString("x16"),
                    node_id = state.NodeId.ToString("x10"),
                    managed_addresses = state.ManagedAddresses.Select(static x => x.ToString()).ToArray(),
                    overlay_host = overlayBind.ToString(),
                    overlay_port = options.OverlayPort,
                    socks_host = options.SocksAddress.ToString(),
                    socks_port = options.SocksPort,
                    peers = peers.Select(static x => x.ToString()).Order().ToArray(),
                    default_exit = options.DefaultExit?.ToString(),
                    egress = options.Egress
                },
                shutdown.Token).ConfigureAwait(false);

            JsonLog.Info("netloop_ready", new {
                network = options.NetworkId.ToString("x16"),
                node = state.NodeId.ToString("x10"),
                overlay = $"{overlayBind}:{options.OverlayPort}",
                socks = $"{options.SocksAddress}:{options.SocksPort}"
            });

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return 0;
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

    private static IPAddress SelectOverlayBindAddress(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            throw new InvalidOperationException("ZeroTier network has no Managed IP.");

        return addresses.FirstOrDefault(static x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses[0];
    }
}
