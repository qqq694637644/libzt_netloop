namespace NetLoop.Host;

/// <summary>
/// Sole owner of a Windows physical-network handover epoch. Distinct physical
/// egress paths always get distinct epochs, however close together in time.
/// No timing heuristic can merge a Wi-Fi -> hotspot -> Wi-Fi round trip.
/// </summary>
internal sealed class PhysicalHandoverEpisode
{
    private bool _hasEpisode;
    private bool _lastObservationHasRoute;
    private PhysicalEgressIdentity? _lastKnownRoute;

    internal ulong Epoch { get; private set; }

    internal void Seed(PhysicalEgressIdentity? initialRoute)
    {
        if (_hasEpisode || _lastKnownRoute is not null)
            throw new InvalidOperationException("Physical egress can only be seeded before monitoring.");
        _lastKnownRoute = initialRoute;
        _lastObservationHasRoute = initialRoute is not null;
    }

    /// <summary>True only for a new physical disconnect or egress.</summary>
    internal bool Observe(PhysicalEgressIdentity? route, bool reportedUnavailable = false)
    {
        if (!reportedUnavailable && route?.IsCaptureFailed == true)
            return false;
        // The availability event is authoritative even if Windows still has
        // a stale route entry at the instant the notification arrives.
        var hasRoute = !reportedUnavailable && route is not null;
        var previouslyKnown = _lastObservationHasRoute;
        var changed = false;
        if (!hasRoute)
        {
            // Repeated events during the same offline phase are duplicates.
            changed = !_hasEpisode || previouslyKnown;
            _lastObservationHasRoute = false;
        }
        else
        {
            // A new path after the offline phase belongs to the handover
            // already opened by the disconnect; a different *online* path
            // always begins a new epoch, even a few milliseconds later.
            changed = !_hasEpisode && _lastKnownRoute is null
                || (previouslyKnown && _lastKnownRoute is not null
                    && route!.DiffersFrom(_lastKnownRoute));
            _lastKnownRoute = route;
            _lastObservationHasRoute = true;
        }
        if (!changed)
            return false;
        _hasEpisode = true;
        ++Epoch;
        return true;
    }
}
