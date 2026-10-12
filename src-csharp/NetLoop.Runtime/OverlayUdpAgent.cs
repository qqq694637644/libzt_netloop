using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class OverlayUdpAgent : IAsyncDisposable
{
    private readonly LibztUdpSocket _socket;
    private readonly IPAddress _primarySelfAddress;
    private readonly RouteSelector _selector;
    private readonly AgentAccessPolicy _accessPolicy;
    private readonly IProxyUdpTransportFactory _egressFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxAssociations;
    private readonly int _queueCapacity;
    private readonly TimeSpan _sendTimeout;
    private readonly UdpByteBudget _budget;
    private readonly ConcurrentDictionary<IPEndPoint, OverlayUdpAssociation> _associations = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiveLoop;
    private int _aborted;
    private int _disposed;
    private readonly TaskCompletionSource _disposedFinished = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Completion => _receiveLoop;
    internal int ActiveAssociations => _associations.Count;

    internal OverlayUdpAgent(
        IPAddress bindAddress,
        ushort port,
        RouteSelector selector,
        IProxyUdpTransportFactory egressFactory,
        int maxAssociations,
        TimeSpan idleTimeout,
        AgentAccessPolicy accessPolicy,
        int queueCapacity,
        TimeSpan sendTimeout,
        UdpByteBudget budget)
    {
        if (maxAssociations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAssociations));

        _primarySelfAddress = bindAddress;
        _selector = selector;
        _accessPolicy = accessPolicy;
        _egressFactory = egressFactory;
        _maxAssociations = maxAssociations;
        _queueCapacity = queueCapacity;
        _sendTimeout = sendTimeout;
        _budget = budget;
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
                if (!_accessPolicy.AllowsSource(datagram.RemoteEndPoint.Address))
                {
                    JsonLog.Info("agent_source_denied", new {
                        protocol = "udp",
                        source = datagram.RemoteEndPoint.ToString()
                    });
                    continue;
                }
                if (!Socks5UdpPacket.TryParse(datagram.Content, out var packet, out var error))
                {
                    JsonLog.Info("overlay_udp_packet_dropped", new {
                        source = datagram.RemoteEndPoint.ToString(),
                        error
                    });
                    continue;
                }

                if (!_accessPolicy.AllowsTarget(packet.Target))
                {
                    JsonLog.Info("agent_target_denied", new {
                        protocol = "udp",
                        source = datagram.RemoteEndPoint.ToString(),
                        target = packet.Target.ToString()
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

                if (!association.TryEnqueue(packet.Target, packet.Content))
                {
                    UdpQueueMetrics.OverlayForwardDropped();
                    JsonLog.Info("overlay_udp_queue_drop", new {
                        source = datagram.RemoteEndPoint.ToString(),
                        target = packet.Target.ToString(),
                        queue_capacity = _queueCapacity,
                        max_queued_bytes = OverlayUdpAssociation.MaxQueuedBytes
                    });
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

        if (_associations.Count >= _maxAssociations)
            return null;

        var created = new OverlayUdpAssociation(
            peer,
            _primarySelfAddress,
            _selector,
            new SystemUdpTransport(_queueCapacity, _budget),
            _egressFactory,
            _idleTimeout,
            _queueCapacity,
            _sendTimeout,
            _budget,
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
            var removed = TryRemoveAssociation(peer, association);
            try
            {
                await association.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                JsonLog.Error("overlay_udp_association_dispose_failed", new {
                    peer = peer.ToString(),
                    error_type = ex.GetType().Name,
                    error = ex.Message
                });
            }
            if (removed)
            {
                JsonLog.Info("overlay_udp_association_removed", new {
                    peer = peer.ToString(),
                    active = _associations.Count
                });
            }
        }
    }

    private bool TryRemoveAssociation(
        IPEndPoint peer,
        OverlayUdpAssociation expected)
        => RemoveMatching(_associations, peer, expected);

    internal static bool RemoveMatching<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dictionary,
        TKey key,
        TValue expected)
        where TKey : notnull
        where TValue : class
    {
        // ICollection.Remove on ConcurrentDictionary compares key AND value
        // atomically. Never remove a newer association for a reused endpoint.
        return ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(
            new KeyValuePair<TKey, TValue>(key, expected));
    }

    private async ValueTask SendResponseAsync(
        IPEndPoint peer,
        ProxyUdpDatagram datagram,
        CancellationToken cancellationToken)
    {
        var packet = Socks5UdpPacket.Build(datagram.Source, datagram.Content);
        await _socket.SendToAsync(packet, peer, cancellationToken).ConfigureAwait(false);
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        _stop.Cancel();
        _ = _socket.DisposeAsync();

        foreach (var association in _associations.Values)
            association.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _disposedFinished.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            Abort();
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
        }
        finally
        {
            _stop.Dispose();
            _disposedFinished.TrySetResult();
        }
    }
}

internal sealed class OverlayUdpAssociation : IAsyncDisposable
{
    internal const int MaxQueuedBytes = 1024 * 1024;
    private sealed record ForwardItem(ProxyTarget Target, byte[] Content);
    private readonly IPEndPoint _peer;
    private readonly IPAddress _primarySelfAddress;
    private readonly RouteSelector _selector;
    private readonly IProxyUdpTransport _localTransport;
    private readonly IProxyUdpTransportFactory _egressFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _sendTimeout;
    private readonly UdpByteBudget _budget;
    private readonly Channel<ForwardItem> _forwardQueue;
    private readonly Func<IPEndPoint, ProxyUdpDatagram, CancellationToken, ValueTask> _responseSender;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _egressCreateLock = new(1, 1);
    private readonly Task _localReceiveLoop;
    private readonly Task _idleMonitor;
    private readonly Task _forwardLoop;
    private IProxyUdpTransport? _egressTransport;
    private Task? _egressReceiveLoop;
    private long _lastActivity;
    private int _queuedBytes;
    private int _aborted;
    private int _disposed;
    private readonly TaskCompletionSource _disposedFinished = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal OverlayUdpAssociation(
        IPEndPoint peer,
        IPAddress primarySelfAddress,
        RouteSelector selector,
        IProxyUdpTransport localTransport,
        IProxyUdpTransportFactory egressFactory,
        TimeSpan idleTimeout,
        int queueCapacity,
        TimeSpan sendTimeout,
        UdpByteBudget budget,
        Func<IPEndPoint, ProxyUdpDatagram, CancellationToken, ValueTask> responseSender,
        CancellationToken cancellationToken)
    {
        _peer = peer;
        _primarySelfAddress = primarySelfAddress;
        _selector = selector;
        _localTransport = localTransport;
        _egressFactory = egressFactory;
        _idleTimeout = idleTimeout;
        _sendTimeout = sendTimeout;
        _budget = budget;
        _forwardQueue = Channel.CreateBounded<ForwardItem>(
            new BoundedChannelOptions(queueCapacity) {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        _responseSender = responseSender;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastActivity = Environment.TickCount64;

        _localReceiveLoop = RunGuardedAsync(
            token => PumpResponsesAsync(_localTransport, "local", token),
            "overlay_udp_local_response_failed");
        _idleMonitor = RunGuardedAsync(
            IdleMonitorAsync,
            "overlay_udp_idle_monitor_failed");
        _forwardLoop = RunGuardedAsync(
            ForwardQueuedAsync,
            "overlay_udp_forward_failed");
        Completion = Task.WhenAll(_localReceiveLoop, _idleMonitor, _forwardLoop);
    }

    internal Task Completion { get; }

    internal bool TryEnqueue(ProxyTarget target, ReadOnlyMemory<byte> content)
    {
        if (_stop.IsCancellationRequested || content.Length > MaxQueuedBytes)
            return false;

        var bytes = content.Length;
        if (!_budget.TryReserve(bytes))
            return false;
        while (true)
        {
            var queued = Volatile.Read(ref _queuedBytes);
            if (queued + bytes > MaxQueuedBytes)
            {
                _budget.Release(bytes);
                return false;
            }
            if (Interlocked.CompareExchange(
                    ref _queuedBytes, queued + bytes, queued) == queued)
                break;
        }

        if (!_forwardQueue.Writer.TryWrite(new ForwardItem(target, content.ToArray())))
        {
            Interlocked.Add(ref _queuedBytes, -bytes);
            _budget.Release(bytes);
            return false;
        }

        Touch();
        return true;
    }

    private async Task ForwardQueuedAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in _forwardQueue.Reader
                           .ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Add(ref _queuedBytes, -item.Content.Length);
            _budget.Release(item.Content.Length);
            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_sendTimeout);
            try
            {
                await ForwardAsync(item.Target, item.Content, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested
                      && deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Overlay UDP forwarding to {item.Target} exceeded "
                    + $"{_sendTimeout.TotalSeconds:0.#} seconds.");
            }
        }
    }

    internal async ValueTask ForwardAsync(
        ProxyTarget target,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var decision = _selector.Select(target, overlayIngress: true);

        switch (decision.Kind)
        {
            case RouteKind.LocalLoopback:
                await _localTransport.SendAsync(
                    MapToLoopback(target),
                    content,
                    cancellationToken).ConfigureAwait(false);
                break;

            case RouteKind.LocalEgress:
                var egressTransport = await GetEgressTransportAsync(
                    cancellationToken).ConfigureAwait(false);
                await egressTransport.SendAsync(
                    target,
                    content,
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
            var response = source == "local"
                ? RewriteLocalResponse(datagram)
                : datagram;
            await _responseSender(_peer, response, cancellationToken).ConfigureAwait(false);
            Touch();
        }
    }

    private ProxyUdpDatagram RewriteLocalResponse(ProxyUdpDatagram datagram)
    {
        if (datagram.Source.TryGetIPAddress(out var address)
            && IPAddress.IsLoopback(address))
        {
            return datagram with {
                Source = new ProxyTarget(
                    _primarySelfAddress.ToString(),
                    datagram.Source.Port)
            };
        }

        return datagram;
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

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        _stop.Cancel();
        _forwardQueue.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _disposedFinished.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            Abort();
            await DisposeTransportNoThrowAsync(_localTransport, "loopback");

            await _egressCreateLock.WaitAsync().ConfigureAwait(false);
            var egressTransport = _egressTransport;
            var egressReceiveLoop = _egressReceiveLoop;
            _egressTransport = null;
            _egressReceiveLoop = null;
            _egressCreateLock.Release();

            if (egressTransport is not null)
                await DisposeTransportNoThrowAsync(egressTransport, "egress");

            try
            {
                await Completion.ConfigureAwait(false);
                if (egressReceiveLoop is not null)
                    await egressReceiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            // Reclaim any queued datagrams even if a downstream transport
            // refuses disposal or a worker fails while shutting down.
            while (_forwardQueue.Reader.TryRead(out var queued))
            {
                Interlocked.Add(ref _queuedBytes, -queued.Content.Length);
                _budget.Release(queued.Content.Length);
            }
            _egressCreateLock.Dispose();
            _stop.Dispose();
            _disposedFinished.TrySetResult();
        }
    }

    private static async Task DisposeTransportNoThrowAsync(
        IProxyUdpTransport transport,
        string kind)
    {
        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            JsonLog.Error("overlay_udp_transport_dispose_failed", new {
                kind,
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
    }
}
