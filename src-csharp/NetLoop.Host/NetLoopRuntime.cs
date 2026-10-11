using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class NetLoopRuntime : IAsyncDisposable
{
    private const int MaxTcpTunnels = 128;
    private const int MaxUdpAssociations = 64;
    private static readonly TimeSpan PeerConnectTimeout =
        TimeSpan.FromMilliseconds(2_500);

    private readonly Socks5UdpAssociationFactory _udpAssociationFactory;
    private readonly OverlaySocksAgent _overlayAgent;
    private readonly OverlayUdpAgent _overlayUdpAgent;
    private readonly LocalSocksServer _localSocks;
    private readonly RouteSelector _selector;
    private readonly object _routeUpdateSync = new();
    private LibztNetworkState _state;
    private int _aborted;
    private int _disposed;

    private NetLoopRuntime(
        LibztNetworkState state,
        IPAddress overlayBindAddress,
        RouteSelector selector,
        Socks5UdpAssociationFactory udpAssociationFactory,
        OverlaySocksAgent overlayAgent,
        OverlayUdpAgent overlayUdpAgent,
        LocalSocksServer localSocks)
    {
        _state = state;
        OverlayBindAddress = overlayBindAddress;
        _selector = selector;
        _udpAssociationFactory = udpAssociationFactory;
        _overlayAgent = overlayAgent;
        _overlayUdpAgent = overlayUdpAgent;
        _localSocks = localSocks;
    }

    internal LibztNetworkState State => Volatile.Read(ref _state);

    internal IPAddress OverlayBindAddress { get; }

    /// <summary>
    /// A critical listener task must remain alive until the runtime is
    /// deliberately stopped. Its unexpected completion marks this runtime
    /// unhealthy even if the process and status file still say READY.
    /// </summary>
    internal async Task WaitForCriticalServiceFailureAsync(CancellationToken cancellationToken)
    {
        var listeners = new[] {
            _localSocks.Completion,
            _overlayAgent.Completion,
            _overlayUdpAgent.Completion
        };
        var stopped = await Task.WhenAny(listeners).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var listenerName = ReferenceEquals(stopped, listeners[0])
            ? "local_socks" : ReferenceEquals(stopped, listeners[1])
                ? "overlay_tcp" : "overlay_udp";
        try
        {
            await stopped.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException($"Critical {listenerName} listener failed.", ex);
        }

        throw new IOException($"Critical {listenerName} listener stopped unexpectedly.");
    }

    internal static async Task<NetLoopRuntime> CreateAsync(
        HostOptions options,
        LibztNetworkState state)
    {
        var overlayBind = SelectOverlayBindAddress(state.ManagedAddresses);
        ValidateConfiguredOverlayAddresses(options, state.ManagedAddresses, overlayBind);
        var defaultExit = options.DefaultExit
            ?? throw new InvalidOperationException("Default exit is required.");

        var snapshot = BuildNetworkSnapshot(state, overlayBind);
        var selector = new RouteSelector(snapshot, defaultExit);

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
                egressConnector,
                options.SocksConnectReplyTimeout);
            var overlayRouter = new RoutingConnector(
                selector,
                overlayIngress: true,
                options.OverlayPort,
                libztConnector,
                loopbackConnector,
                egressConnector,
                options.SocksConnectReplyTimeout);

            overlayAgent = new OverlaySocksAgent(
                overlayBind.ToString(),
                options.OverlayPort,
                new Socks5ConnectionHandler(
                    overlayRouter,
                    handshakeTimeout: options.SocksHandshakeTimeout),
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
                    udpAssociations,
                    options.SocksHandshakeTimeout),
                MaxTcpTunnels);
            localSocks.Start();

            return new NetLoopRuntime(
                state,
                overlayBind,
                selector,
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

    /// <summary>
    /// Replaces only the routing table. Existing TCP handlers retain their
    /// connected streams and no native listener or ZeroTier Node is restarted.
    /// </summary>
    internal void UpdateNetworkState(LibztNetworkState updated)
    {
        lock (_routeUpdateSync)
        {
            var previous = State;
            if (Volatile.Read(ref _disposed) != 0
                || updated.Generation <= previous.Generation)
                return;

            if (updated.NetworkId != previous.NetworkId || updated.NodeId != previous.NodeId)
                throw new InvalidOperationException("ZeroTier config update belongs to a different node/network.");

            // Agent listeners are bound to the startup primary address. Do not
            // tear them down (which would abort established connections) on a
            // controller-driven address change; surface the limitation.
            if (!updated.ManagedAddresses.Contains(OverlayBindAddress))
            {
                JsonLog.Error("overlay_bind_address_no_longer_assigned", new {
                    bound_address = OverlayBindAddress.ToString(),
                    assigned_addresses = updated.ManagedAddresses.Select(
                        static x => x.ToString()).ToArray(),
                    generation = updated.Generation
                });
            }

            _selector.UpdateNetwork(
                BuildNetworkSnapshot(updated, OverlayBindAddress));
            Volatile.Write(ref _state, updated);

            JsonLog.Info("overlay_routes_updated", new {
                generation = updated.Generation,
                assigned_prefixes = updated.AssignedPrefixes.Select(
                    static x => x.ToString()).ToArray(),
                managed_routes = updated.ManagedRoutes.Select(static x => new {
                    target = x.Target.ToString(),
                    via = x.Via?.ToString(),
                    flags = x.Flags,
                    metric = x.Metric
                }).ToArray()
            });
        }
    }

    private static OverlayNetworkSnapshot BuildNetworkSnapshot(
        LibztNetworkState state,
        IPAddress overlayBind)
        => new(
            overlayBind,
            state.ManagedAddresses,
            state.AssignedPrefixes,
            state.ManagedRoutes);

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
            options.UpstreamPassword,
            connectReplyTimeout: options.SocksConnectReplyTimeout);
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
            options.UpstreamPassword,
            setupTimeout: options.SocksConnectReplyTimeout);
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

        if (options.DefaultExit is not null
            && secondarySelfAddresses.Contains(options.DefaultExit))
        {
            throw new InvalidOperationException(
                $"Configured default exit {options.DefaultExit} is a secondary local Managed IP; "
                + $"NetLoop primary address is {primaryAddress}.");
        }
    }
}
