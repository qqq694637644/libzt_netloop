using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class NetLoopRuntime : IAsyncDisposable
{
    private const int MaxRetiredTcpListeners = 16;
    private static readonly TimeSpan PeerConnectTimeout =
        TimeSpan.FromMilliseconds(2_500);

    private readonly Socks5UdpAssociationFactory _udpAssociationFactory;
    private OverlaySocksAgent _overlayAgent;
    private OverlayUdpAgent _overlayUdpAgent;
    private readonly LocalSocksServer _localSocks;
    private readonly RouteSelector _selector;
    private readonly AgentAccessPolicy _accessPolicy;
    private readonly HostOptions _options;
    private readonly Socks5ConnectionHandler _overlayHandler;
    private readonly IProxyUdpTransportFactory _udpEgressFactory;
    private readonly RoutingUdpTransportFactory _routingUdpFactory;
    private readonly List<OverlaySocksAgent> _retiredTcpAgents = [];
    private readonly List<Task> _retiredCleanup = [];
    private readonly TaskCompletionSource<string> _criticalListenerStopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _routeUpdateSync = new();
    private LibztNetworkState _state;
    private IPAddress _overlayBindAddress;
    private int _aborted;
    private int _disposed;

    private NetLoopRuntime(
        LibztNetworkState state,
        IPAddress overlayBindAddress,
        HostOptions options,
        RouteSelector selector,
        AgentAccessPolicy accessPolicy,
        Socks5ConnectionHandler overlayHandler,
        IProxyUdpTransportFactory udpEgressFactory,
        RoutingUdpTransportFactory routingUdpFactory,
        Socks5UdpAssociationFactory udpAssociationFactory,
        OverlaySocksAgent overlayAgent,
        OverlayUdpAgent overlayUdpAgent,
        LocalSocksServer localSocks)
    {
        _state = state;
        _overlayBindAddress = overlayBindAddress;
        _options = options;
        _selector = selector;
        _accessPolicy = accessPolicy;
        _overlayHandler = overlayHandler;
        _udpEgressFactory = udpEgressFactory;
        _routingUdpFactory = routingUdpFactory;
        _udpAssociationFactory = udpAssociationFactory;
        _overlayAgent = overlayAgent;
        _overlayUdpAgent = overlayUdpAgent;
        _localSocks = localSocks;
        ObserveCriticalListener(_localSocks.Completion, "local_socks",
            () => true);
        ObserveCriticalListener(_overlayAgent.Completion, "overlay_tcp",
            () => ReferenceEquals(Volatile.Read(ref _overlayAgent), overlayAgent));
        ObserveCriticalListener(_overlayUdpAgent.Completion, "overlay_udp",
            () => ReferenceEquals(Volatile.Read(ref _overlayUdpAgent), overlayUdpAgent));
    }

    internal LibztNetworkState State => Volatile.Read(ref _state);

    internal IPAddress OverlayBindAddress => Volatile.Read(ref _overlayBindAddress);

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
        var overlayBind = SelectOverlayBindAddress(state.ManagedAddresses);
        ValidateConfiguredOverlayAddresses(options, state.ManagedAddresses, overlayBind);
        var defaultExit = options.DefaultExit
            ?? throw new InvalidOperationException("Default exit is required.");

        var snapshot = BuildNetworkSnapshot(state, overlayBind);
        var selector = new RouteSelector(snapshot, defaultExit);
        var accessPolicy = new AgentAccessPolicy(
            options.AgentAllowedSources,
            options.AgentAllowedTargetPorts);

        var libztConnector = new LibztTcpConnector(PeerConnectTimeout);
        var loopbackConnector = new LoopbackTcpConnector(options.ConnectTimeout);
        var egressConnector = BuildEgressConnector(options);
        var udpEgressFactory = BuildUdpEgressFactory(options);
        var routingUdpFactory = new RoutingUdpTransportFactory(
            selector,
            overlayBind,
            options.OverlayUdpPort,
            udpEgressFactory,
            options.UdpQueueCapacity);

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
                options.UdpSendTimeout);

            localSocks = new LocalSocksServer(
                new IPEndPoint(options.SocksAddress, options.SocksPort),
                new Socks5ConnectionHandler(
                    localRouter,
                    udpAssociations,
                    options.SocksHandshakeTimeout,
                    relayBufferBytes: options.RelayBufferKiB * 1024),
                options.MaxTcpTunnels);
            localSocks.Start();

            return new NetLoopRuntime(
                state,
                overlayBind,
                options,
                selector,
                accessPolicy,
                overlayHandler,
                udpEgressFactory,
                routingUdpFactory,
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

            if (!updated.ManagedAddresses.Contains(OverlayBindAddress))
            {
                JsonLog.Error("overlay_bind_address_no_longer_assigned", new {
                    bound_address = OverlayBindAddress.ToString(),
                    assigned_addresses = updated.ManagedAddresses.Select(
                        static x => x.ToString()).ToArray(),
                    generation = updated.Generation
                });

                if (updated.ManagedAddresses.Count != 0)
                    TryMigrateAgentListeners(SelectOverlayBindAddress(updated.ManagedAddresses));
            }

            _selector.UpdateNetwork(
                BuildNetworkSnapshot(updated, OverlayBindAddress));
            if (updated.ManagedAddresses.Count == 0)
                _selector.MarkNetworkUnavailable();
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

    /// <summary>
    /// Create replacement TCP/UDP Agent listeners before removing the old
    /// listeners. Accepted TCP streams retain the original Agent lifetime
    /// and drain naturally; UDP bound to a revoked IP cannot be preserved.
    /// Route-only updates never invoke this path.
    /// Must be called under _routeUpdateSync.
    /// </summary>
    private void TryMigrateAgentListeners(IPAddress newBindAddress)
    {
        if (newBindAddress.Equals(OverlayBindAddress))
            return;

        OverlaySocksAgent? replacementTcp = null;
        OverlayUdpAgent? replacementUdp = null;
        try
        {
            replacementTcp = new OverlaySocksAgent(
                newBindAddress.ToString(),
                _options.OverlayPort,
                _overlayHandler,
                _options.MaxTcpTunnels,
                _accessPolicy);
            replacementUdp = new OverlayUdpAgent(
                newBindAddress,
                _options.OverlayUdpPort,
                _selector,
                _udpEgressFactory,
                _options.MaxUdpAssociations,
                _options.UdpIdleTimeout,
                _accessPolicy,
                _options.UdpQueueCapacity,
                _options.UdpSendTimeout);
        }
        catch (Exception ex)
        {
            // Any partially started replacement must be disposed before the
            // next periodic configuration refresh retries the migration.
            replacementTcp?.Abort();
            replacementUdp?.Abort();
            if (replacementTcp is not null)
                TrackRetiredTask(DisposeDiscardedTcpAsync(replacementTcp));
            if (replacementUdp is not null)
                TrackRetiredTask(DisposeDiscardedUdpAsync(replacementUdp));
            JsonLog.Error("overlay_bind_migration_failed", new {
                old_address = OverlayBindAddress.ToString(),
                new_address = newBindAddress.ToString(),
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            return;
        }

        var previousTcp = _overlayAgent;
        var previousUdp = _overlayUdpAgent;
        var previousAddress = OverlayBindAddress;
        // Replacement is fully bound before switching the active listeners.
        Volatile.Write(ref _overlayAgent, replacementTcp);
        Volatile.Write(ref _overlayUdpAgent, replacementUdp);
        Volatile.Write(ref _overlayBindAddress, newBindAddress);
        _routingUdpFactory.UpdateBindAddress(newBindAddress);
        ObserveCriticalListener(replacementTcp.Completion, "overlay_tcp",
            () => ReferenceEquals(Volatile.Read(ref _overlayAgent), replacementTcp));
        ObserveCriticalListener(replacementUdp.Completion, "overlay_udp",
            () => ReferenceEquals(Volatile.Read(ref _overlayUdpAgent), replacementUdp));

        previousTcp.StopAccepting();
        _retiredTcpAgents.Add(previousTcp);
        TrackRetiredTask(DrainRetiredTcpAsync(previousTcp));

        previousUdp.Abort();
        TrackRetiredTask(DisposeDiscardedUdpAsync(previousUdp));

        // Bound the number of retained old sockets under extreme controller
        // address churn. Most will disappear as sessions close naturally.
        if (_retiredTcpAgents.Count > MaxRetiredTcpListeners)
        {
            var oldest = _retiredTcpAgents[0];
            JsonLog.Error("overlay_retired_tcp_limit_exceeded", new {
                limit = MaxRetiredTcpListeners,
                behavior = "oldest_sessions_aborted"
            });
            oldest.Abort();
        }

        JsonLog.Info("overlay_bind_migrated", new {
            previous_address = previousAddress.ToString(),
            new_address = newBindAddress.ToString(),
            existing_tcp_sessions = "draining",
            old_udp_associations = "closed"
        });
    }

    private void TrackRetiredTask(Task task)
    {
        _retiredCleanup.RemoveAll(static item => item.IsCompleted);
        _retiredCleanup.Add(task);
    }

    private async Task DrainRetiredTcpAsync(OverlaySocksAgent agent)
    {
        try
        {
            await agent.DrainAndDisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            JsonLog.Error("overlay_retired_tcp_cleanup_failed", new {
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
        finally
        {
            lock (_routeUpdateSync)
                _retiredTcpAgents.Remove(agent);
        }
    }

    private static async Task DisposeDiscardedTcpAsync(OverlaySocksAgent agent)
    {
        try
        {
            await agent.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            JsonLog.Error("overlay_tcp_cleanup_failed", new { error = ex.Message });
        }
    }

    private static async Task DisposeDiscardedUdpAsync(OverlayUdpAgent agent)
    {
        try
        {
            await agent.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            JsonLog.Error("overlay_udp_cleanup_failed", new { error = ex.Message });
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
        lock (_routeUpdateSync)
        {
            _overlayAgent.Abort();
            _overlayUdpAgent.Abort();
            foreach (var retired in _retiredTcpAgents)
                retired.Abort();
        }
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
        OverlaySocksAgent tcp;
        OverlayUdpAgent udp;
        Task[] cleanup;
        lock (_routeUpdateSync)
        {
            tcp = _overlayAgent;
            udp = _overlayUdpAgent;
            cleanup = [.. _retiredCleanup];
        }
        await tcp.DisposeAsync().ConfigureAwait(false);
        await udp.DisposeAsync().ConfigureAwait(false);
        if (cleanup.Length != 0)
            await Task.WhenAll(cleanup).ConfigureAwait(false);
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
            return new DirectUdpTransportFactory(options.UdpQueueCapacity);

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
