using System.Net;
using System.Threading.Channels;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class RoutingUdpTransportFactory : IProxyUdpTransportFactory
{
    private readonly RouteSelector _selector;
    private IPAddress _overlayBindAddress;
    private readonly ushort _overlayUdpPort;
    private readonly IProxyUdpTransportFactory _egressFactory;
    private readonly int _queueCapacity;
    private readonly UdpByteBudget _budget;

    internal RoutingUdpTransportFactory(
        RouteSelector selector,
        IPAddress overlayBindAddress,
        ushort overlayUdpPort,
        IProxyUdpTransportFactory egressFactory,
        int queueCapacity,
        UdpByteBudget budget)
    {
        _selector = selector;
        _overlayBindAddress = overlayBindAddress;
        _overlayUdpPort = overlayUdpPort;
        _egressFactory = egressFactory;
        _queueCapacity = queueCapacity;
        _budget = budget;
    }

    internal void UpdateBindAddress(IPAddress address)
        => Volatile.Write(ref _overlayBindAddress, address);

    public async ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bindAddress = Volatile.Read(ref _overlayBindAddress);
        var localTransport = new SystemUdpTransport(_queueCapacity, _budget);
        LibztUdpSocket? overlaySocket = null;
        try
        {
            overlaySocket = LibztUdpSocket.Bind(bindAddress, 0);
            return new RoutingUdpTransport(
                _selector,
                bindAddress,
                _overlayUdpPort,
                localTransport,
                _egressFactory,
                overlaySocket,
                _queueCapacity,
                _budget);
        }
        catch
        {
            await localTransport.DisposeAsync().ConfigureAwait(false);
            if (overlaySocket is not null)
                await overlaySocket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class RoutingUdpTransport : IProxyUdpTransport
{
    private const int ReceiveQueueCapacity = 256;

    private readonly RouteSelector _selector;
    private readonly IPAddress _primarySelfAddress;
    private readonly ushort _overlayUdpPort;
    private readonly IProxyUdpTransport _localTransport;
    private readonly IProxyUdpTransportFactory _egressFactory;
    private readonly LibztUdpSocket _overlaySocket;
    private readonly UdpResponsePeerRegistry _allowedPeers = new();
    private readonly Channel<ProxyUdpDatagram> _received;
    private readonly UdpByteBudget _budget;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _receiveLoops;
    private readonly SemaphoreSlim _egressGate = new(1, 1);
    private IProxyUdpTransport? _egressTransport;
    private Task? _egressReceiveLoop;
    private int _disposed;

    internal RoutingUdpTransport(
        RouteSelector selector,
        IPAddress primarySelfAddress,
        ushort overlayUdpPort,
        IProxyUdpTransport localTransport,
        IProxyUdpTransportFactory egressFactory,
        LibztUdpSocket overlaySocket,
        int queueCapacity,
        UdpByteBudget budget)
    {
        _selector = selector;
        _primarySelfAddress = primarySelfAddress;
        _overlayUdpPort = overlayUdpPort;
        _localTransport = localTransport;
        _egressFactory = egressFactory;
        _overlaySocket = overlaySocket;
        _budget = budget;
        _received = Channel.CreateBounded<ProxyUdpDatagram>(
            new BoundedChannelOptions(queueCapacity) {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        _receiveLoops = [
            RunGuardedAsync(
                token => PumpTransportAsync(
                    _localTransport,
                    rewriteLoopbackSource: true,
                    cancellationToken: token),
                "routing_udp_local_receive_failed"),
            RunGuardedAsync(
                PumpOverlayAsync,
                "routing_udp_overlay_receive_failed")
        ];
    }

    public async ValueTask SendAsync(
        ProxyTarget target,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var decision = _selector.Select(target, overlayIngress: false);

        switch (decision.Kind)
        {
            case RouteKind.LocalLoopback:
                await _localTransport.SendAsync(
                    MapToLoopback(target),
                    content,
                    cancellationToken).ConfigureAwait(false);
                return;

            case RouteKind.LocalEgress:
                var egressTransport = await GetEgressTransportAsync(
                    cancellationToken).ConfigureAwait(false);
                await egressTransport.SendAsync(
                    target,
                    content,
                    cancellationToken).ConfigureAwait(false);
                return;

            case RouteKind.OverlayPeer:
            case RouteKind.DefaultExit:
            {
                var peer = decision.PeerAddress
                    ?? throw new InvalidOperationException("UDP overlay route has no peer address.");
                var agent = new IPEndPoint(peer, _overlayUdpPort);
                _allowedPeers.Allow(agent);

                var packet = Socks5UdpPacket.Build(target, content.Span);
                await _overlaySocket.SendToAsync(
                    packet,
                    agent,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            case RouteKind.Reject:
                throw new IOException($"UDP route rejected for {target}: {decision.Reason}");

            default:
                throw new InvalidOperationException($"Unknown UDP route kind {decision.Kind}.");
        }
    }

    private async ValueTask<IProxyUdpTransport> GetEgressTransportAsync(
        CancellationToken cancellationToken)
    {
        if (_egressTransport is { } existing)
            return existing;

        await _egressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_egressTransport is { } current)
                return current;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stop.Token);
            var created = await _egressFactory
                .CreateAsync(linked.Token)
                .ConfigureAwait(false);

            if (Volatile.Read(ref _disposed) != 0)
            {
                await created.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(
                    nameof(RoutingUdpTransport));
            }

            _egressTransport = created;
            _egressReceiveLoop = RunGuardedAsync(
                token => PumpTransportAsync(
                    created,
                    rewriteLoopbackSource: false,
                    cancellationToken: token),
                "routing_udp_egress_receive_failed");
            return created;
        }
        finally
        {
            _egressGate.Release();
        }
    }

    public async ValueTask<ProxyUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var datagram = await _received.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        _budget.Release(datagram.Content.Length);
        return datagram;
    }

    private async Task PumpTransportAsync(
        IProxyUdpTransport transport,
        bool rewriteLoopbackSource,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var response = rewriteLoopbackSource
                ? RewriteLocalResponse(datagram)
                : datagram;
            if (!_budget.TryReserve(response.Content.Length))
            {
                UdpQueueMetrics.RoutingReceiveDropped();
                continue;
            }
            if (!_received.Writer.TryWrite(response))
            {
                _budget.Release(response.Content.Length);
                UdpQueueMetrics.RoutingReceiveDropped();
            }
        }
    }

    private ProxyUdpDatagram RewriteLocalResponse(ProxyUdpDatagram datagram)
    {
        if (!datagram.Source.TryGetIPAddress(out var address)
            || !IPAddress.IsLoopback(address))
        {
            return datagram;
        }

        return datagram with {
            Source = new ProxyTarget(
                _primarySelfAddress.ToString(),
                datagram.Source.Port)
        };
    }

    private async Task PumpOverlayAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await _overlaySocket.ReceiveFromAsync(cancellationToken).ConfigureAwait(false);
            if (!_allowedPeers.IsAllowed(datagram.RemoteEndPoint))
            {
                JsonLog.Info("udp_overlay_unexpected_peer_dropped", new {
                    source = datagram.RemoteEndPoint.ToString()
                });
                continue;
            }

            if (!Socks5UdpPacket.TryParse(datagram.Content, out var packet, out var error))
            {
                JsonLog.Info("udp_overlay_response_dropped", new {
                    source = datagram.RemoteEndPoint.ToString(),
                    error
                });
                continue;
            }

            if (!_budget.TryReserve(packet.Content.Length))
            {
                UdpQueueMetrics.RoutingReceiveDropped();
                continue;
            }
            var response = new ProxyUdpDatagram(
                packet.Target, packet.Content.ToArray());
            if (!_received.Writer.TryWrite(response))
            {
                _budget.Release(response.Content.Length);
                UdpQueueMetrics.RoutingReceiveDropped();
            }
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
            _received.Writer.TryComplete(ex);
            JsonLog.Error(eventName, new {
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);
        await _overlaySocket.DisposeAsync().ConfigureAwait(false);
        await _localTransport.DisposeAsync().ConfigureAwait(false);

        IProxyUdpTransport? egressTransport;
        Task? egressReceiveLoop;
        await _egressGate.WaitAsync().ConfigureAwait(false);
        try
        {
            egressTransport = _egressTransport;
            _egressTransport = null;
            egressReceiveLoop = _egressReceiveLoop;
            _egressReceiveLoop = null;
        }
        finally
        {
            _egressGate.Release();
        }

        if (egressTransport is not null)
            await egressTransport.DisposeAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_receiveLoops).ConfigureAwait(false);
            if (egressReceiveLoop is not null)
                await egressReceiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _received.Writer.TryComplete();
        while (_received.Reader.TryRead(out var queued))
            _budget.Release(queued.Content.Length);
        _egressGate.Dispose();
        _stop.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RoutingUdpTransport));
    }
}

/// <summary>
/// An Association only accepts UDP responses from endpoints it contacted.
/// A small expiring set replaces the previous grow-only endpoint dictionary.
/// Evicting an old peer can drop a late response, but never widens access.
/// </summary>
internal sealed class UdpResponsePeerRegistry
{
    internal const int MaxPeers = 64;
    internal const long MaxIdleMilliseconds = 120_000;

    private readonly Dictionary<IPEndPoint, long> _lastSent = new();
    private readonly object _gate = new();
    private readonly Func<long> _now;

    internal UdpResponsePeerRegistry(Func<long>? clock = null)
        => _now = clock ?? (() => Environment.TickCount64);

    internal int Count
    {
        get
        {
            lock (_gate)
                return _lastSent.Count;
        }
    }

    internal void Allow(IPEndPoint peer)
    {
        lock (_gate)
        {
            var now = _now();
            if (!_lastSent.ContainsKey(peer))
            {
                if (_lastSent.Count >= MaxPeers)
                {
                    // Remove stale peers first; with many active destinations
                    // retain the most recently contacted ones.
                    foreach (var stale in _lastSent
                                 .Where(item => now - item.Value > MaxIdleMilliseconds)
                                 .Select(static item => item.Key).ToArray())
                        _lastSent.Remove(stale);
                }
                if (_lastSent.Count >= MaxPeers)
                {
                    var oldest = _lastSent.MinBy(static item => item.Value);
                    _lastSent.Remove(oldest.Key);
                }
            }
            _lastSent[peer] = now;
        }
    }

    internal bool IsAllowed(IPEndPoint peer)
    {
        lock (_gate)
        {
            if (!_lastSent.TryGetValue(peer, out var lastSent))
                return false;
            if (_now() - lastSent <= MaxIdleMilliseconds)
                return true;
            _lastSent.Remove(peer);
            return false;
        }
    }
}
