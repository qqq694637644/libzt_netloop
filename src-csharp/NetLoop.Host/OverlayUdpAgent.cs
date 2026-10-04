using System.Collections.Concurrent;
using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class OverlayUdpAgent : IAsyncDisposable
{
    private readonly LibztUdpSocket _socket;
    private readonly RouteSelector _selector;
    private readonly IProxyUdpTransportFactory _egressFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxAssociations;
    private readonly ConcurrentDictionary<IPEndPoint, OverlayUdpAssociation> _associations = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiveLoop;
    private int _disposed;

    internal OverlayUdpAgent(
        IPAddress bindAddress,
        ushort port,
        RouteSelector selector,
        IProxyUdpTransportFactory egressFactory,
        int maxAssociations,
        TimeSpan idleTimeout)
    {
        if (maxAssociations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAssociations));

        _selector = selector;
        _egressFactory = egressFactory;
        _maxAssociations = maxAssociations;
        _idleTimeout = idleTimeout;
        _socket = LibztUdpSocket.Bind(bindAddress, port);
        _receiveLoop = ReceiveLoopAsync(_stop.Token);

        JsonLog.Info("overlay_udp_agent_ready", new {
            address = bindAddress.ToString(),
            port,
            max_associations = maxAssociations,
            idle_timeout_ms = idleTimeout.TotalMilliseconds
        });
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await _socket.ReceiveFromAsync(cancellationToken).ConfigureAwait(false);
                if (!Socks5UdpPacket.TryParse(datagram.Payload, out var packet, out var error))
                {
                    JsonLog.Info("overlay_udp_packet_dropped", new {
                        source = datagram.RemoteEndPoint.ToString(),
                        error
                    });
                    continue;
                }

                var association = await GetOrCreateAssociationAsync(
                    datagram.RemoteEndPoint,
                    cancellationToken).ConfigureAwait(false);
                if (association is null)
                {
                    JsonLog.Info("overlay_udp_capacity_drop", new {
                        source = datagram.RemoteEndPoint.ToString(),
                        active = _associations.Count,
                        capacity = _maxAssociations
                    });
                    continue;
                }

                try
                {
                    await association.ForwardAsync(
                        packet.Target,
                        packet.Payload,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    JsonLog.Error("overlay_udp_forward_failed", new {
                        peer = datagram.RemoteEndPoint.ToString(),
                        target = packet.Target.ToString(),
                        error_type = ex.GetType().Name,
                        error = ex.Message
                    });

                    if (TryRemoveAssociation(datagram.RemoteEndPoint, association))
                        await association.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error("overlay_udp_agent_failed", new {
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<OverlayUdpAssociation?> GetOrCreateAssociationAsync(
        IPEndPoint peer,
        CancellationToken cancellationToken)
    {
        if (_associations.TryGetValue(peer, out var existing))
            return existing;

        PruneCompletedAssociations();
        if (_associations.Count >= _maxAssociations)
            return null;

        var created = new OverlayUdpAssociation(
            peer,
            _selector,
            new SystemUdpTransport(),
            _egressFactory,
            _idleTimeout,
            SendResponseAsync,
            _stop.Token);

        if (!_associations.TryAdd(peer, created))
        {
            await created.DisposeAsync().ConfigureAwait(false);
            return _associations.TryGetValue(peer, out var winner) ? winner : null;
        }

        _ = ObserveAssociationAsync(peer, created);
        JsonLog.Info("overlay_udp_association_created", new {
            peer = peer.ToString(),
            active = _associations.Count
        });
        return created;
    }

    private async Task ObserveAssociationAsync(
        IPEndPoint peer,
        OverlayUdpAssociation association)
    {
        try
        {
            await association.Completion.ConfigureAwait(false);
        }
        finally
        {
            if (TryRemoveAssociation(peer, association))
            {
                await association.DisposeAsync().ConfigureAwait(false);
                JsonLog.Info("overlay_udp_association_removed", new {
                    peer = peer.ToString(),
                    active = _associations.Count
                });
            }
        }
    }

    private void PruneCompletedAssociations()
    {
        foreach (var entry in _associations)
        {
            if (!entry.Value.Completion.IsCompleted)
                continue;

            if (TryRemoveAssociation(entry.Key, entry.Value))
            {
                _ = entry.Value.DisposeAsync();
            }
        }
    }

    private bool TryRemoveAssociation(
        IPEndPoint peer,
        OverlayUdpAssociation expected)
    {
        if (!_associations.TryGetValue(peer, out var current)
            || !ReferenceEquals(current, expected))
            return false;

        return _associations.TryRemove(peer, out var removed)
            && ReferenceEquals(removed, expected);
    }

    private async ValueTask SendResponseAsync(
        IPEndPoint peer,
        ProxyUdpDatagram datagram,
        CancellationToken cancellationToken)
    {
        var packet = Socks5UdpPacket.Build(datagram.Source, datagram.Payload);
        await _socket.SendToAsync(packet, peer, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);
        await _socket.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        var associations = _associations.ToArray();
        _associations.Clear();
        foreach (var entry in associations)
            await entry.Value.DisposeAsync().ConfigureAwait(false);

        _stop.Dispose();
    }
}

internal sealed class OverlayUdpAssociation : IAsyncDisposable
{
    private readonly IPEndPoint _peer;
    private readonly RouteSelector _selector;
    private readonly IProxyUdpTransport _localTransport;
    private readonly IProxyUdpTransportFactory _egressFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<IPEndPoint, ProxyUdpDatagram, CancellationToken, ValueTask> _responseSender;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _egressCreateLock = new(1, 1);
    private readonly Task _localReceiveLoop;
    private readonly Task _idleMonitor;
    private IProxyUdpTransport? _egressTransport;
    private Task? _egressReceiveLoop;
    private long _lastActivity;
    private int _disposed;

    internal OverlayUdpAssociation(
        IPEndPoint peer,
        RouteSelector selector,
        IProxyUdpTransport localTransport,
        IProxyUdpTransportFactory egressFactory,
        TimeSpan idleTimeout,
        Func<IPEndPoint, ProxyUdpDatagram, CancellationToken, ValueTask> responseSender,
        CancellationToken cancellationToken)
    {
        _peer = peer;
        _selector = selector;
        _localTransport = localTransport;
        _egressFactory = egressFactory;
        _idleTimeout = idleTimeout;
        _responseSender = responseSender;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastActivity = Environment.TickCount64;

        _localReceiveLoop = RunGuardedAsync(
            token => PumpResponsesAsync(_localTransport, "local", token),
            "overlay_udp_local_response_failed");
        _idleMonitor = RunGuardedAsync(
            IdleMonitorAsync,
            "overlay_udp_idle_monitor_failed");
        Completion = Task.WhenAll(_localReceiveLoop, _idleMonitor);
    }

    internal Task Completion { get; }

    internal async ValueTask ForwardAsync(
        ProxyTarget target,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var decision = _selector.Select(target, overlayIngress: true);
        JsonLog.Info("udp_route_decision", new {
            target = target.ToString(),
            ingress = "overlay",
            route = decision.Kind.ToString(),
            peer = _peer.ToString(),
            decision.Reason
        });

        switch (decision.Kind)
        {
            case RouteKind.LocalLoopback:
                await _localTransport.SendAsync(
                    MapToLoopback(target),
                    payload,
                    cancellationToken).ConfigureAwait(false);
                break;

            case RouteKind.DirectEgress:
                var egressTransport = await GetEgressTransportAsync(
                    cancellationToken).ConfigureAwait(false);
                await egressTransport.SendAsync(
                    target,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                break;

            case RouteKind.Reject:
                JsonLog.Info("overlay_udp_transit_rejected", new {
                    peer = _peer.ToString(),
                    target = target.ToString()
                });
                return;

            case RouteKind.OverlayPeer:
            case RouteKind.DefaultExit:
                throw new InvalidOperationException(
                    $"Overlay UDP ingress unexpectedly selected {decision.Kind} for {target}.");

            default:
                throw new InvalidOperationException($"Unknown UDP route kind {decision.Kind}.");
        }

        Touch();
    }

    private async ValueTask<IProxyUdpTransport> GetEgressTransportAsync(
        CancellationToken cancellationToken)
    {
        if (_egressTransport is { } existing)
            return existing;

        await _egressCreateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_egressTransport is { } current)
                return current;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stop.Token);
            var created = await _egressFactory.CreateAsync(
                linked.Token).ConfigureAwait(false);
            _egressTransport = created;
            _egressReceiveLoop = RunGuardedAsync(
                token => PumpResponsesAsync(created, "egress", token),
                "overlay_udp_egress_response_failed");

            JsonLog.Info("overlay_udp_egress_created", new {
                peer = _peer.ToString()
            });
            return created;
        }
        finally
        {
            _egressCreateLock.Release();
        }
    }

    private async Task PumpResponsesAsync(
        IProxyUdpTransport transport,
        string source,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            await _responseSender(_peer, datagram, cancellationToken).ConfigureAwait(false);
            Touch();

            JsonLog.Info("overlay_udp_response_sent", new {
                peer = _peer.ToString(),
                transport = source,
                source_endpoint = datagram.Source.ToString(),
                bytes = datagram.Payload.Length
            });
        }
    }

    private async Task IdleMonitorAsync(CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromSeconds(
            Math.Clamp(_idleTimeout.TotalSeconds / 4, 1, 5));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            var idleFor = Environment.TickCount64 - Interlocked.Read(ref _lastActivity);
            if (idleFor < _idleTimeout.TotalMilliseconds)
                continue;

            JsonLog.Info("overlay_udp_association_idle_timeout", new {
                peer = _peer.ToString(),
                idle_ms = idleFor
            });
            await _stop.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    private async Task RunGuardedAsync(
        Func<CancellationToken, Task> action,
        string eventName)
    {
        try
        {
            await action(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error(eventName, new {
                peer = _peer.ToString(),
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private static ProxyTarget MapToLoopback(ProxyTarget target)
    {
        var loopback = target.TryGetIPAddress(out var address)
                       && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Loopback
            : IPAddress.Loopback;
        return new ProxyTarget(loopback.ToString(), target.Port);
    }

    private void Touch()
        => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);
        await _localTransport.DisposeAsync().ConfigureAwait(false);

        await _egressCreateLock.WaitAsync().ConfigureAwait(false);
        var egressTransport = _egressTransport;
        var egressReceiveLoop = _egressReceiveLoop;
        _egressTransport = null;
        _egressReceiveLoop = null;
        _egressCreateLock.Release();

        if (egressTransport is not null)
            await egressTransport.DisposeAsync().ConfigureAwait(false);

        try
        {
            await Completion.ConfigureAwait(false);
            if (egressReceiveLoop is not null)
                await egressReceiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _egressCreateLock.Dispose();
        _stop.Dispose();
    }
}
