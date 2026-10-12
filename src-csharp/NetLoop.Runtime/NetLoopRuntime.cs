using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class NetLoopRuntime : IAsyncDisposable
{
    private static readonly TimeSpan PeerConnectTimeout =
        TimeSpan.FromMilliseconds(2_500);

    private readonly Socks5UdpAssociationFactory _udpAssociationFactory;
    private readonly OverlaySocksAgent _overlayAgent;
    private readonly OverlayUdpAgent _overlayUdpAgent;
    private readonly LocalSocksServer _localSocks;
    private readonly RouteSelector _selector;
    private readonly AgentAccessPolicy _accessPolicy;
    private readonly SemaphoreSlim _tcpCapacity;
    private readonly int _maxTcpTunnels;
    private readonly UdpByteBudget _udpByteBudget;
    private readonly TaskCompletionSource<string> _criticalListenerStopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _routeUpdateSync = new();
    private LibztNetworkState _state;
    private readonly IPAddress _overlayBindAddress;
    private int _aborted;
    private int _disposed;

    private NetLoopRuntime(
        LibztNetworkState state,
        IPAddress overlayBindAddress,
        RouteSelector selector,
        AgentAccessPolicy accessPolicy,
        SemaphoreSlim tcpCapacity,
        int maxTcpTunnels,
        UdpByteBudget udpByteBudget,
        Socks5UdpAssociationFactory udpAssociationFactory,
        OverlaySocksAgent overlayAgent,
        OverlayUdpAgent overlayUdpAgent,
        LocalSocksServer localSocks)
    {
        _state = state;
        _overlayBindAddress = overlayBindAddress;
        _selector = selector;
        _accessPolicy = accessPolicy;
        _tcpCapacity = tcpCapacity;
        _maxTcpTunnels = maxTcpTunnels;
        _udpByteBudget = udpByteBudget;
        _udpAssociationFactory = udpAssociationFactory;
        _overlayAgent = overlayAgent;
        _overlayUdpAgent = overlayUdpAgent;
        _localSocks = localSocks;
        ObserveCriticalListener(_localSocks.Completion, "local_socks",
            () => true);
        ObserveCriticalListener(_overlayAgent.Completion, "overlay_tcp",
            () => true);
        ObserveCriticalListener(_overlayUdpAgent.Completion, "overlay_udp",
            () => true);
    }

    internal LibztNetworkState State => Volatile.Read(ref _state);

    internal IPAddress OverlayBindAddress => _overlayBindAddress;
    internal int ActiveTcpTunnels => _maxTcpTunnels - _tcpCapacity.CurrentCount;
    internal int UdpQueuedBytes => _udpByteBudget.UsedBytes;
    internal int UdpPeakQueuedBytes => _udpByteBudget.PeakBytes;
    internal int UdpQueueByteLimit => _udpByteBudget.MaxBytes;
    internal int ActiveLocalUdpAssociations => _udpAssociationFactory.ActiveAssociations;
    internal int ActiveOverlayUdpAssociations => _overlayUdpAgent.ActiveAssociations;

    internal AgentAccessPolicy AccessPolicy => _accessPolicy;

    internal bool IsNetworkConfigAvailable => _selector.NetworkAvailable;

    internal bool IsAgentBindAddressAssigned
        => State.ManagedAddresses.Contains(OverlayBindAddress);

    internal bool IsHealthy
        => IsNetworkConfigAvailable
           && State.ManagedAddresses.Count != 0
           && IsAgentBindAddressAssigned;

    internal string? DegradedReason
        => !IsNetworkConfigAvailable
            ? "network_configuration_unavailable"
            : !IsAgentBindAddressAssigned
                ? "overlay_bind_address_revoked"
                : null;

    internal void MarkNetworkConfigurationUnavailable(string reason)
    {
        _selector.MarkNetworkUnavailable();
        JsonLog.Error("overlay_routing_disabled", new {
            reason,
            last_verified_generation = State.Generation,
            last_verified_at_utc = State.QueriedAtUtc
        });
    }

    /// <summary>
    /// A critical listener task must remain alive until the runtime is
    /// deliberately stopped. Its unexpected completion marks this runtime
    /// unhealthy even if the process and status file still say READY.
    /// </summary>
    internal async Task WaitForCriticalServiceFailureAsync(CancellationToken cancellationToken)
    {
        var failure = await _criticalListenerStopped.Task
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        throw new IOException(failure);
    }

    private void ObserveCriticalListener(
        Task completion, string name, Func<bool> stillCurrent)
    {
        _ = completion.ContinueWith(task => {
            var cause = task.Exception?.GetBaseException().Message;
            if (Volatile.Read(ref _aborted) == 0 && stillCurrent())
            {
                _criticalListenerStopped.TrySetResult(
                    $"Critical {name} listener stopped: {cause ?? "task completed"}");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static async Task<NetLoopRuntime> CreateAsync(
        HostOptions options,
        LibztNetworkState state)
    {
        var overlayBind = ValidateSingleManagedAddress(state.ManagedAddresses);
        var defaultExit = options.DefaultExit
            ?? throw new InvalidOperationException("Default exit is required.");

        var snapshot = BuildNetworkSnapshot(state, overlayBind);
        var selector = new RouteSelector(snapshot, defaultExit, state.Generation);
        var accessPolicy = new AgentAccessPolicy(
            options.AgentAllowedSources,
            options.AgentAllowedTargetPorts);

        var libztConnector = new LibztTcpConnector(PeerConnectTimeout);
        var loopbackConnector = new LoopbackTcpConnector(options.ConnectTimeout);
        var egressConnector = BuildEgressConnector(options);
        var udpByteBudget = new UdpByteBudget(16 * 1024 * 1024);
        var udpEgressFactory = BuildUdpEgressFactory(options, udpByteBudget);
        var routingUdpFactory = new RoutingUdpTransportFactory(
            selector,
            overlayBind,
            options.OverlayUdpPort,
            udpEgressFactory,
            options.UdpQueueCapacity,
            udpByteBudget);

        var tcpCapacity = new SemaphoreSlim(
            options.MaxTcpTunnels, options.MaxTcpTunnels);
        Socks5UdpAssociationFactory? udpAssociations = null;
        OverlaySocksAgent? overlayAgent = null;
        OverlayUdpAgent? overlayUdpAgent = null;
        LocalSocksServer? localSocks = null;
        try
        {
            udpAssociations = new Socks5UdpAssociationFactory(
                routingUdpFactory,
                options.MaxUdpAssociations,
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
            var overlayHandler = new Socks5ConnectionHandler(
                overlayRouter,
                handshakeTimeout: options.SocksHandshakeTimeout,
                targetAllowed: accessPolicy.AllowsTarget,
                relayBufferBytes: options.RelayBufferKiB * 1024);

            overlayAgent = new OverlaySocksAgent(
                overlayBind.ToString(),
                options.OverlayPort,
                overlayHandler,
                options.MaxTcpTunnels,
                tcpCapacity,
                accessPolicy);
            overlayUdpAgent = new OverlayUdpAgent(
                overlayBind,
                options.OverlayUdpPort,
                selector,
                udpEgressFactory,
                options.MaxUdpAssociations,
                options.UdpIdleTimeout,
                accessPolicy,
                options.UdpQueueCapacity,
                options.UdpSendTimeout,
                udpByteBudget);

            localSocks = new LocalSocksServer(
                new IPEndPoint(options.SocksAddress, options.SocksPort),
                new Socks5ConnectionHandler(
                    localRouter,
                    udpAssociations,
                    options.SocksHandshakeTimeout,
                    relayBufferBytes: options.RelayBufferKiB * 1024),
                tcpCapacity);
            localSocks.Start();

            return new NetLoopRuntime(
                state,
                overlayBind,
                selector,
                accessPolicy,
                tcpCapacity,
                options.MaxTcpTunnels,
                udpByteBudget,
                udpAssociations,
                overlayAgent,
                overlayUdpAgent,
                localSocks);
        }
        catch
        {
            // Keep the original native bind error available to the recovery
            // classifier. A secondary cleanup exception must neither hide it
            // nor prevent the other partially-created listeners from closing.
            await DisposeResourceNoThrowAsync(localSocks, "local_socks");
            await DisposeResourceNoThrowAsync(overlayUdpAgent, "overlay_udp");
            await DisposeResourceNoThrowAsync(overlayAgent, "overlay_tcp");
            await DisposeResourceNoThrowAsync(udpAssociations, "udp_factory");
            tcpCapacity.Dispose();
            throw;
        }
    }

    private static async Task DisposeResourceNoThrowAsync(
        IAsyncDisposable? resource,
        string component)
    {
        if (resource is null)
            return;
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            JsonLog.Error("runtime_resource_dispose_failed", new {
                component,
                error_type = ex.GetType().Name,
                error = ex.Message
            });
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

            if (updated.ManagedAddresses.Count != 1
                || !updated.ManagedAddresses[0].Equals(OverlayBindAddress))
            {
                // No attempt to keep old and new Agents alive together:
                // the single Runtime owner will close and replace us.
                _selector.MarkNetworkUnavailable("managed_ip_changed");
                Volatile.Write(ref _state, updated);
                JsonLog.Error("overlay_bind_address_no_longer_assigned", new {
                    bound_address = OverlayBindAddress.ToString(),
                    assigned_addresses = updated.ManagedAddresses.Select(
                        static x => x.ToString()).ToArray(),
                    generation = updated.Generation
                });
                return;
            }

            _selector.UpdateNetwork(
                BuildNetworkSnapshot(updated, OverlayBindAddress),
                updated.Generation);
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
        await DisposeResourceNoThrowAsync(_localSocks, "local_socks");
        await DisposeResourceNoThrowAsync(_overlayAgent, "overlay_tcp");
        await DisposeResourceNoThrowAsync(_overlayUdpAgent, "overlay_udp");
        await DisposeResourceNoThrowAsync(_udpAssociationFactory, "udp_factory");
        _tcpCapacity.Dispose();
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

    private static IProxyUdpTransportFactory BuildUdpEgressFactory(
        HostOptions options, UdpByteBudget budget)
    {
        if (options.Egress == "direct")
            return new DirectUdpTransportFactory(options.UdpQueueCapacity, budget);

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

    internal static IPAddress ValidateSingleManagedAddress(
        IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            throw new InvalidOperationException("ZeroTier network has no Managed IP.");
        if (addresses.Count != 1
            || addresses[0].AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException(
                "NetLoop requires exactly one IPv4 Managed IP per node.");
        return addresses[0];
    }
}
