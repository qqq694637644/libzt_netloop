using System.Net;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed record LibztNetworkState(
    ulong NetworkId,
    ulong NodeId,
    IReadOnlyList<IPAddress> ManagedAddresses,
    IReadOnlyList<OverlayPrefix> AssignedPrefixes,
    IReadOnlyList<ManagedOverlayRoute> ManagedRoutes,
    long Generation);
