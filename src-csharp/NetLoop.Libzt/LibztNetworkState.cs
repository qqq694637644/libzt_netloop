using System.Net;

namespace NetLoop.Libzt;

public sealed record LibztNetworkState(
    ulong NetworkId,
    ulong NodeId,
    IReadOnlyList<IPAddress> ManagedAddresses);
