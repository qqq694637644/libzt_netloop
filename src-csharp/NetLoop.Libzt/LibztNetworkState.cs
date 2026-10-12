using System.Net;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed record LibztNetworkState(
    ulong NetworkId,
    ulong NodeId,
    IReadOnlyList<IPAddress> ManagedAddresses,
    IReadOnlyList<OverlayPrefix> AssignedPrefixes,
    IReadOnlyList<ManagedOverlayRoute> ManagedRoutes,
    long Generation)
{
    // Observed when native core returned a complete config snapshot. A
    // missing native network is never published as an empty route table.
    public DateTimeOffset QueriedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
