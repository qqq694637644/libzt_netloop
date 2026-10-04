using NetLoop.Core;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class RoutingConnector : IProxyConnector
{
    private readonly RouteSelector _selector;
    private readonly bool _overlayIngress;
    private readonly ushort _overlayPort;
    private readonly IProxyConnector _libztConnector;
    private readonly IProxyConnector _loopbackConnector;
    private readonly IProxyConnector _egressConnector;

    internal RoutingConnector(
        RouteSelector selector,
        bool overlayIngress,
        ushort overlayPort,
        IProxyConnector libztConnector,
        IProxyConnector loopbackConnector,
        IProxyConnector egressConnector)
    {
        _selector = selector;
        _overlayIngress = overlayIngress;
        _overlayPort = overlayPort;
        _libztConnector = libztConnector;
        _loopbackConnector = loopbackConnector;
        _egressConnector = egressConnector;
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
            RouteKind.DirectEgress => await _egressConnector.ConnectAsync(target, cancellationToken).ConfigureAwait(false),
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
            new ProxyTarget(peer.ToString(), _overlayPort));
        return proxy.ConnectAsync(target, cancellationToken);
    }
}
