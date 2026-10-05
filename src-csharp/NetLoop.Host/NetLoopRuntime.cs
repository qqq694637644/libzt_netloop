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
    private static readonly TimeSpan PeerConnectTimeout =
        TimeSpan.FromMilliseconds(2_500);

    private readonly Socks5UdpAssociationFactory _udpAssociationFactory;
    private readonly OverlaySocksAgent _overlayAgent;
    private readonly OverlayUdpAgent _overlayUdpAgent;
    private readonly LocalSocksServer _localSocks;
    private int _aborted;
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
        var overlayBind = SelectOverlayBindAddress(state.ManagedAddresses);
        ValidateConfiguredOverlayAddresses(options, state.ManagedAddresses, overlayBind);
        var peers = options.Peers
            .Where(address => !overlayBind.Equals(address))
            .ToHashSet();
        if (options.DefaultExit is not null
            && !overlayBind.Equals(options.DefaultExit))
        {
            peers.Add(options.DefaultExit);
        }

        var snapshot = new OverlayNetworkSnapshot(
            overlayBind,
            peers);
        var selector = new RouteSelector(snapshot, options.DefaultExit);

        var libztConnector = new LibztTcpConnector(PeerConnectTimeout);
        var loopbackConnector = new LoopbackTcpConnector(options.ConnectTimeout);
        var egressConnector = BuildEgressConnector(options);
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

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        _localSocks.Abort();
        _overlayAgent.Abort();
        _overlayUdpAgent.Abort();
        _udpAssociationFactory.Abort();

        JsonLog.Info("runtime_sessions_aborted", new {
            node = State.NodeId.ToString("x10")
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Abort();
        await _localSocks.DisposeAsync().ConfigureAwait(false);
        await _overlayAgent.DisposeAsync().ConfigureAwait(false);
        await _overlayUdpAgent.DisposeAsync().ConfigureAwait(false);
        await _udpAssociationFactory.DisposeAsync().ConfigureAwait(false);
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

        static string SortKey(IPAddress address)
            => Convert.ToHexString(address.GetAddressBytes());

        return addresses
                   .Where(
                       static address =>
                           address.AddressFamily
                           == System.Net.Sockets.AddressFamily.InterNetwork)
                   .OrderBy(SortKey, StringComparer.Ordinal)
                   .FirstOrDefault()
               ?? addresses
                   .OrderBy(SortKey, StringComparer.Ordinal)
                   .First();
    }

    private static void ValidateConfiguredOverlayAddresses(
        HostOptions options,
        IReadOnlyList<IPAddress> managedAddresses,
        IPAddress primaryAddress)
    {
        var secondarySelfAddresses = managedAddresses
            .Where(address => !primaryAddress.Equals(address))
            .ToHashSet();

        var secondaryPeer = options.Peers.FirstOrDefault(
            secondarySelfAddresses.Contains);
        if (secondaryPeer is not null)
        {
            throw new InvalidOperationException(
                $"Configured peer {secondaryPeer} is a secondary local Managed IP; "
                + $"NetLoop primary address is {primaryAddress}.");
        }

        if (options.DefaultExit is not null
            && secondarySelfAddresses.Contains(options.DefaultExit))
        {
            throw new InvalidOperationException(
                $"Configured default exit {options.DefaultExit} is a secondary local Managed IP; "
                + $"NetLoop primary address is {primaryAddress}.");
        }
    }
}
