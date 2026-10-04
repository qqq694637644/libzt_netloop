using System.Net;

namespace NetLoop.Libzt;

public sealed record LibztRouteInfo(string Target, string Via, ushort Flags, ushort Metric)
{
    public bool IsDirect
        => string.IsNullOrWhiteSpace(Via)
           || Via == "0.0.0.0"
           || Via == "::";
}

public sealed record LibztNetworkState(
    ulong NetworkId,
    ulong NodeId,
    IReadOnlyList<IPAddress> ManagedAddresses,
    IReadOnlyList<LibztRouteInfo> Routes);
