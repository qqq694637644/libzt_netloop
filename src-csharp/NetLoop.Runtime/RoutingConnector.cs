using NetLoop.Core;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class RoutingConnector : IProxyConnector
{
    private static readonly TimeSpan PeerNegotiationRecoveryWindow =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeerNegotiationAttemptTimeout =
        TimeSpan.FromMilliseconds(750);

    private readonly RouteSelector _selector;
    private readonly bool _overlayIngress;
    private readonly ushort _overlayPort;
    private readonly IProxyConnector _libztConnector;
    private readonly IProxyConnector _loopbackConnector;
    private readonly IProxyConnector _egressConnector;
    private readonly PeerDialCoordinator _peerDials;
    private readonly TimeSpan _connectReplyTimeout;

    internal RoutingConnector(
        RouteSelector selector,
        bool overlayIngress,
        ushort overlayPort,
        IProxyConnector libztConnector,
        IProxyConnector loopbackConnector,
        IProxyConnector egressConnector,
        PeerDialCoordinator peerDials,
        TimeSpan connectReplyTimeout)
    {
        _selector = selector;
        _overlayIngress = overlayIngress;
        _overlayPort = overlayPort;
        _libztConnector = libztConnector;
        _loopbackConnector = loopbackConnector;
        _egressConnector = egressConnector;
        _peerDials = peerDials;
        _connectReplyTimeout = connectReplyTimeout;
    }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        var decision = _selector.Select(target, _overlayIngress);
        JsonLog.Info("route_decision", new {
            target = target.ToString(),
            ingress = _overlayIngress ? "overlay" : "local",
            route = decision.Kind.ToString(),
            peer = decision.PeerAddress?.ToString(),
            decision.Reason
        });

        return decision.Kind switch {
            RouteKind.LocalLoopback => await _loopbackConnector.ConnectAsync(target, cancellationToken).ConfigureAwait(false),
            RouteKind.LocalEgress => await _egressConnector.ConnectAsync(target, cancellationToken).ConfigureAwait(false),
            RouteKind.OverlayPeer or RouteKind.DefaultExit =>
                await ConnectViaPeerAsync(decision, target, cancellationToken).ConfigureAwait(false),
            RouteKind.Reject => throw new IOException($"Route rejected for {target}: {decision.Reason}"),
            _ => throw new InvalidOperationException($"Unknown route kind {decision.Kind}.")
        };
    }

    private ValueTask<IProxyConnection> ConnectViaPeerAsync(
        RouteDecision decision,
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        var peer = decision.PeerAddress
            ?? throw new InvalidOperationException("Overlay route has no peer address.");

        var proxy = new Socks5ProxyConnector(
            _libztConnector,
            new ProxyTarget(peer.ToString(), _overlayPort),
            negotiationRetryWindow: PeerNegotiationRecoveryWindow,
            negotiationAttemptTimeout: PeerNegotiationAttemptTimeout,
            connectReplyTimeout: _connectReplyTimeout);
        return _peerDials.ConnectAsync(
            peer,
            token => proxy.ConnectAsync(target, token),
            cancellationToken);
    }
}
