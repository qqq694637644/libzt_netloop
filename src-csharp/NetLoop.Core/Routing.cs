using System.Net;

namespace NetLoop.Core;

public enum RouteKind
{
    LocalLoopback,
    OverlayPeer,
    DefaultExit,
    DirectEgress,
    Reject
}

public sealed record RouteDecision(RouteKind Kind, IPAddress? PeerAddress, string Reason);

public sealed class OverlayNetworkSnapshot
{
    private readonly HashSet<IPAddress> _selfAddresses;
    private readonly HashSet<IPAddress> _peerAddresses;
    private readonly ManagedRoute[] _directOverlayRoutes;

    public OverlayNetworkSnapshot(
        IEnumerable<IPAddress> selfAddresses,
        IEnumerable<IPAddress> peerAddresses,
        IEnumerable<ManagedRoute> routes)
    {
        _selfAddresses = new HashSet<IPAddress>(selfAddresses);
        _peerAddresses = new HashSet<IPAddress>(peerAddresses);
        _directOverlayRoutes = routes
            .Where(route => route.IsDirect)
            .Where(route => _selfAddresses.Any(route.Target.Contains))
            .ToArray();
    }

    public IReadOnlyCollection<IPAddress> SelfAddresses => _selfAddresses;

    public bool IsSelf(IPAddress address) => _selfAddresses.Contains(address);

    public bool IsOverlayManagedAddress(IPAddress address)
        => _peerAddresses.Contains(address);

    public IReadOnlyList<ManagedRoute> DirectOverlayRoutes => _directOverlayRoutes;

    public IReadOnlyCollection<IPAddress> PeerAddresses => _peerAddresses;
}

public sealed class RouteSelector
{
    private readonly OverlayNetworkSnapshot _network;
    private readonly IPAddress? _defaultExit;

    public RouteSelector(OverlayNetworkSnapshot network, IPAddress? defaultExit)
    {
        _network = network;
        _defaultExit = defaultExit;
    }

    public RouteDecision Select(ProxyTarget target, bool overlayIngress)
    {
        if (target.TryGetIPAddress(out var address))
        {
            if (_network.IsSelf(address))
                return new RouteDecision(RouteKind.LocalLoopback, null, "target is this node managed IP");

            if (_network.IsOverlayManagedAddress(address))
            {
                if (overlayIngress)
                    return new RouteDecision(RouteKind.Reject, null, "overlay ingress cannot transit to another overlay node");

                return new RouteDecision(RouteKind.OverlayPeer, address, "target is direct overlay managed IP");
            }
        }

        if (overlayIngress)
            return new RouteDecision(RouteKind.DirectEgress, null, "overlay peer selected this node as final egress");

        if (_defaultExit is not null)
            return new RouteDecision(RouteKind.DefaultExit, _defaultExit, "non-overlay target uses configured default exit");

        return new RouteDecision(RouteKind.DirectEgress, null, "no default exit configured; this node is local egress");
    }
}
