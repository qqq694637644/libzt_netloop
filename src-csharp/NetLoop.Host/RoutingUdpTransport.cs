using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class RoutingUdpTransportFactory : IProxyUdpTransportFactory
{
    private readonly RouteSelector _selector;
    private readonly IPAddress _overlayBindAddress;
    private readonly ushort _overlayUdpPort;
    private readonly IProxyUdpTransportFactory _egressFactory;

    internal RoutingUdpTransportFactory(
        RouteSelector selector,
        IPAddress overlayBindAddress,
        ushort overlayUdpPort,
        IProxyUdpTransportFactory egressFactory)
    {
        _selector = selector;
        _overlayBindAddress = overlayBindAddress;
        _overlayUdpPort = overlayUdpPort;
        _egressFactory = egressFactory;
    }

    public async ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var localTransport = new SystemUdpTransport();
        IProxyUdpTransport? egressTransport = null;
        LibztUdpSocket? overlaySocket = null;
        try
        {
            egressTransport = await _egressFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
            overlaySocket = LibztUdpSocket.Bind(_overlayBindAddress, 0);
            return new RoutingUdpTransport(
                _selector,
                _overlayUdpPort,
                localTransport,
                egressTransport,
                overlaySocket);
        }
        catch
        {
            await localTransport.DisposeAsync().ConfigureAwait(false);
            if (egressTransport is not null)
                await egressTransport.DisposeAsync().ConfigureAwait(false);
            if (overlaySocket is not null)
                await overlaySocket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class RoutingUdpTransport : IProxyUdpTransport
{
    private readonly RouteSelector _selector;
    private readonly ushort _overlayUdpPort;
    private readonly IProxyUdpTransport _localTransport;
    private readonly IProxyUdpTransport _egressTransport;
    private readonly LibztUdpSocket _overlaySocket;
    private readonly ConcurrentDictionary<IPEndPoint, byte> _allowedPeers = new();
    private readonly Channel<ProxyUdpDatagram> _received = Channel.CreateUnbounded<ProxyUdpDatagram>(
        new UnboundedChannelOptions {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _receiveLoops;
    private int _disposed;

    internal RoutingUdpTransport(
        RouteSelector selector,
        ushort overlayUdpPort,
        IProxyUdpTransport localTransport,
        IProxyUdpTransport egressTransport,
        LibztUdpSocket overlaySocket)
    {
        _selector = selector;
        _overlayUdpPort = overlayUdpPort;
        _localTransport = localTransport;
        _egressTransport = egressTransport;
        _overlaySocket = overlaySocket;

        _receiveLoops = [
            RunGuardedAsync(
                token => PumpTransportAsync(_localTransport, "local", token),
                "routing_udp_local_receive_failed"),
            RunGuardedAsync(
                token => PumpTransportAsync(_egressTransport, "egress", token),
                "routing_udp_egress_receive_failed"),
            RunGuardedAsync(
                PumpOverlayAsync,
                "routing_udp_overlay_receive_failed")
        ];
    }

    public async ValueTask SendAsync(
        ProxyTarget target,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var decision = _selector.Select(target, overlayIngress: false);

        JsonLog.Info("udp_route_decision", new {
            target = target.ToString(),
            ingress = "local",
            route = decision.Kind.ToString(),
            peer = decision.PeerAddress?.ToString(),
            decision.Reason
        });

        switch (decision.Kind)
        {
            case RouteKind.LocalLoopback:
                await _localTransport.SendAsync(
                    MapToLoopback(target),
                    payload,
                    cancellationToken).ConfigureAwait(false);
                return;

            case RouteKind.DirectEgress:
                await _egressTransport.SendAsync(
                    target,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                return;

            case RouteKind.OverlayPeer:
            case RouteKind.DefaultExit:
            {
                var peer = decision.PeerAddress
                    ?? throw new InvalidOperationException("UDP overlay route has no peer address.");
                var agent = new IPEndPoint(peer, _overlayUdpPort);
                _allowedPeers.TryAdd(agent, 0);

                var packet = Socks5UdpPacket.Build(target, payload.Span);
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

    public async ValueTask<ProxyUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _received.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpTransportAsync(
        IProxyUdpTransport transport,
        string source,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            await _received.Writer.WriteAsync(datagram, cancellationToken).ConfigureAwait(false);
            JsonLog.Info("udp_response_received", new {
                transport = source,
                source_endpoint = datagram.Source.ToString(),
                bytes = datagram.Payload.Length
            });
        }
    }

    private async Task PumpOverlayAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await _overlaySocket.ReceiveFromAsync(cancellationToken).ConfigureAwait(false);
            if (!_allowedPeers.ContainsKey(datagram.RemoteEndPoint))
            {
                JsonLog.Info("udp_overlay_unexpected_peer_dropped", new {
                    source = datagram.RemoteEndPoint.ToString()
                });
                continue;
            }

            if (!Socks5UdpPacket.TryParse(datagram.Payload, out var packet, out var error))
            {
                JsonLog.Info("udp_overlay_response_dropped", new {
                    source = datagram.RemoteEndPoint.ToString(),
                    error
                });
                continue;
            }

            await _received.Writer.WriteAsync(
                new ProxyUdpDatagram(packet.Target, packet.Payload.ToArray()),
                cancellationToken).ConfigureAwait(false);
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
        await _egressTransport.DisposeAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_receiveLoops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _received.Writer.TryComplete();
        _stop.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RoutingUdpTransport));
    }
}
