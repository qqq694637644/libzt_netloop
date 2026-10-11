using System.Net;

namespace NetLoop.Core;

public enum RouteKind
{
    LocalLoopback,
    OverlayPeer,
    DefaultExit,
    LocalEgress,
    Reject
}

public sealed record RouteDecision(RouteKind Kind, IPAddress? PeerAddress, string Reason);

/// <summary>Network prefix supplied by a ZeroTier controller, not inferred from an IP.</summary>
public readonly record struct OverlayPrefix
{
    public OverlayPrefix(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > bytes.Length * 8)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));

        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits != 0)
            bytes[wholeBytes] &= (byte)(0xff << (8 - remainingBits));
        for (var index = wholeBytes + (remainingBits != 0 ? 1 : 0);
             index < bytes.Length; index++)
            bytes[index] = 0;

        NetworkAddress = new IPAddress(bytes);
        PrefixLength = prefixLength;
    }

    public IPAddress NetworkAddress { get; }
    public int PrefixLength { get; }

    public bool Contains(IPAddress address)
    {
        var candidate = address.GetAddressBytes();
        var network = NetworkAddress.GetAddressBytes();
        if (candidate.Length != network.Length)
            return false;

        var wholeBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (candidate[index] != network[index])
                return false;
        }

        return remainingBits == 0
            || (candidate[wholeBytes] & (0xff << (8 - remainingBits)))
            == network[wholeBytes];
    }

    public static OverlayPrefix Parse(string value)
    {
        var separator = value.LastIndexOf('/');
        if (separator <= 0
            || !IPAddress.TryParse(value[..separator], out var address)
            || !int.TryParse(value[(separator + 1)..], out var bits))
            throw new FormatException($"Invalid ZeroTier CIDR prefix: {value}");

        return new OverlayPrefix(address, bits);
    }

    public override string ToString() => $"{NetworkAddress}/{PrefixLength}";
}

public sealed record ManagedOverlayRoute(
    OverlayPrefix Target,
    IPAddress? Via,
    ushort Flags,
    ushort Metric);

public sealed class OverlayNetworkSnapshot
{
    private readonly HashSet<IPAddress> _selfAddresses;
    private readonly OverlayPrefix[] _assignedPrefixes;
    private readonly ManagedOverlayRoute[] _managedRoutes;

    public OverlayNetworkSnapshot(
        IPAddress primarySelfAddress,
        IEnumerable<IPAddress> selfAddresses,
        IEnumerable<OverlayPrefix> assignedPrefixes,
        IEnumerable<ManagedOverlayRoute> managedRoutes)
    {
        PrimarySelfAddress = primarySelfAddress;
        _selfAddresses = new HashSet<IPAddress>(selfAddresses) { primarySelfAddress };
        _assignedPrefixes = assignedPrefixes.Distinct().ToArray();
        _managedRoutes = managedRoutes.ToArray();
    }

    public IPAddress PrimarySelfAddress { get; }

    public bool IsSelf(IPAddress address)
        => _selfAddresses.Contains(address);

    /// <summary>
    /// Longest-prefix match, including gateway routes that must not be mistaken
    /// for a peer Agent. On ties the controller's explicit route wins over
    /// an address-derived connected prefix.
    /// Default (/0) routes do not turn the entire internet into Overlay Peers.
    /// </summary>
    public (bool Matches, IPAddress? Via, string? Prefix) FindOverlayRoute(IPAddress address)
    {
        var bestLength = -1;
        IPAddress? bestVia = null;
        string? bestPrefix = null;

        foreach (var prefix in _assignedPrefixes)
        {
            if (prefix.PrefixLength <= bestLength
                || prefix.PrefixLength == 0
                || !prefix.Contains(address))
                continue;

            bestLength = prefix.PrefixLength;
            bestPrefix = prefix.ToString();
            bestVia = null;
        }

        foreach (var route in _managedRoutes)
        {
            if (route.Target.PrefixLength < bestLength
                || route.Target.PrefixLength == 0
                || !route.Target.Contains(address))
                continue;

            // If duplicate controller routes have conflicting gateway states,
            // prefer rejecting transit over guessing a direct Agent.
            if (route.Target.PrefixLength == bestLength
                && bestVia is not null && route.Via is null)
                continue;

            // Equal-length controller route takes precedence over the implied
            // assigned-address subnet.
            bestLength = route.Target.PrefixLength;
            bestPrefix = route.Target.ToString();
            bestVia = route.Via;
        }

        return (bestLength >= 0, bestVia, bestPrefix);
    }
}

public sealed class RouteSelector
{
    private OverlayNetworkSnapshot _network;
    private readonly IPAddress _defaultExit;

    public RouteSelector(OverlayNetworkSnapshot network, IPAddress defaultExit)
    {
        _network = network;
        _defaultExit = defaultExit;
    }

    public void UpdateNetwork(OverlayNetworkSnapshot network)
        => Volatile.Write(ref _network, network);

    public RouteDecision Select(ProxyTarget target, bool overlayIngress)
    {
        // One atomic snapshot per new decision; no in-place mutation of the
        // route table and no disruption to already-established TCP tunnels.
        var network = Volatile.Read(ref _network);
        if (target.TryGetIPAddress(out var address))
        {
            if (network.IsSelf(address))
                return new RouteDecision(RouteKind.LocalLoopback, null, "target is a local ZeroTier managed IP");

            var match = network.FindOverlayRoute(address);
            if (match.Matches)
            {
                if (match.Via is not null)
                    return new RouteDecision(
                        RouteKind.Reject,
                        null,
                        $"ZeroTier route {match.Prefix} via {match.Via} requires gateway transit unsupported by NetLoop");

                if (overlayIngress)
                    return new RouteDecision(RouteKind.Reject, null, "overlay ingress cannot transit to another overlay node");

                return new RouteDecision(RouteKind.OverlayPeer, address, $"target matches ZeroTier direct route {match.Prefix}");
            }

            // The default-exit setting is not a substitute for the
            // Controller's routing data when addressing that peer itself.
            if (address.Equals(_defaultExit))
                return new RouteDecision(
                    RouteKind.Reject,
                    null,
                    "configured default exit has no matching ZeroTier direct route");
        }

        if (overlayIngress)
            return new RouteDecision(RouteKind.LocalEgress, null, "overlay peer selected this node as final egress");

        if (network.IsSelf(_defaultExit))
            return new RouteDecision(RouteKind.LocalEgress, null, "this node is the configured default exit");

        return new RouteDecision(RouteKind.DefaultExit, _defaultExit, "non-overlay target uses configured default exit");
    }
}
