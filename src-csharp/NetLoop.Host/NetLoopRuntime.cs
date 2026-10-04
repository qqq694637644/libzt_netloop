using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class NetLoopRuntime : IAsyncDisposable
{
    private const int MaxTcpTunnels = 128;
    private const int MaxUdpAssociations = 64;
    private static readonly TimeSpan HalfCloseTimeout = TimeSpan.FromSeconds(15);

    private readonly Socks5UdpAssociationFactory _udpAssociationFactory;
    private readonly OverlaySocksAgent _overlayAgent;
    private readonly OverlayUdpAgent _overlayUdpAgent;
    private readonly LocalSocksServer _localSocks;
    private int _disposed;

    private NetLoopRuntime(
        LibztNetworkState state,
        HashSet<IPAddress> peers,
        IPAddress overlayBindAddress,
        Socks5UdpAssociationFactory udpAssociationFactory,
        OverlaySocksAgent overlayAgent,
        OverlayUdpAgent overlayUdpAgent,
        LocalSocksServer localSocks)
    {
        State = state;
        Peers = peers;
        OverlayBindAddress = overlayBindAddress;
        _udpAssociationFactory = udpAssociationFactory;
        _overlayAgent = overlayAgent;
        _overlayUdpAgent = overlayUdpAgent;
        _localSocks = localSocks;
    }

    internal LibztNetworkState State { get; }

    internal IReadOnlyCollection<IPAddress> Peers { get; }

    internal IPAddress OverlayBindAddress { get; }

    internal static async Task<NetLoopRuntime> CreateAsync(
        HostOptions options,
        LibztNetworkState state)
    {
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
        var egressConnector = BuildEgressConnector(options);
        var overlayBind = SelectOverlayBindAddress(state.ManagedAddresses);
        var udpEgressFactory = BuildUdpEgressFactory(options);
        var routingUdpFactory = new RoutingUdpTransportFactory(
            selector,
            overlayBind,
            options.OverlayUdpPort,
            udpEgressFactory);

        Socks5UdpAssociationFactory? udpAssociations = null;
        OverlaySocksAgent? overlayAgent = null;
        OverlayUdpAgent? overlayUdpAgent = null;
        LocalSocksServer? localSocks = null;
        try
        {
            udpAssociations = new Socks5UdpAssociationFactory(
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

            overlayAgent = new OverlaySocksAgent(
                overlayBind.ToString(),
                options.OverlayPort,
                new Socks5ConnectionHandler(overlayRouter, HalfCloseTimeout),
                MaxTcpTunnels);
            overlayUdpAgent = new OverlayUdpAgent(
                overlayBind,
                options.OverlayUdpPort,
                selector,
                udpEgressFactory,
                MaxUdpAssociations,
                options.UdpIdleTimeout);

            localSocks = new LocalSocksServer(
                new IPEndPoint(options.SocksAddress, options.SocksPort),
                new Socks5ConnectionHandler(
                    localRouter,
                    HalfCloseTimeout,
                    udpAssociations),
                MaxTcpTunnels);
            localSocks.Start();

            return new NetLoopRuntime(
                state,
                peers,
                overlayBind,
                udpAssociations,
                overlayAgent,
                overlayUdpAgent,
                localSocks);
        }
        catch
        {
            if (localSocks is not null)
                await localSocks.DisposeAsync().ConfigureAwait(false);
            if (overlayUdpAgent is not null)
                await overlayUdpAgent.DisposeAsync().ConfigureAwait(false);
            if (overlayAgent is not null)
                await overlayAgent.DisposeAsync().ConfigureAwait(false);
            if (udpAssociations is not null)
                await udpAssociations.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Stop ingress first so no new sessions can appear while the old
        // runtime is being discarded.
        await _localSocks.DisposeAsync().ConfigureAwait(false);
        await _overlayAgent.DisposeAsync().ConfigureAwait(false);
        await _overlayUdpAgent.DisposeAsync().ConfigureAwait(false);
        await _udpAssociationFactory.DisposeAsync().ConfigureAwait(false);

        JsonLog.Info("runtime_sessions_disposed", new {
            node = State.NodeId.ToString("x10")
        });
    }

    private static IProxyConnector BuildEgressConnector(HostOptions options)
    {
        var direct = new DirectTcpConnector(options.ConnectTimeout);
        if (options.Egress == "direct")
            return direct;

        return new Socks5ProxyConnector(
            direct,
            new ProxyTarget(
                options.UpstreamHost
                ?? throw new InvalidOperationException("Missing upstream SOCKS5 host."),
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
                options.UpstreamHost
                ?? throw new InvalidOperationException("Missing upstream SOCKS5 host."),
                options.UpstreamPort),
            options.UpstreamUsername,
            options.UpstreamPassword);
    }

    private static IPAddress SelectOverlayBindAddress(
        IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            throw new InvalidOperationException("ZeroTier network has no Managed IP.");

        return addresses.FirstOrDefault(
                   static address =>
                       address.AddressFamily
                       == System.Net.Sockets.AddressFamily.InterNetwork)
               ?? addresses[0];
    }
}
