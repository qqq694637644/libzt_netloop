namespace NetLoop.Libzt;

/// <summary>
/// Lightweight, event-driven underlay timeline. Timestamps use monotonic
/// milliseconds; JSONL records supply wall-clock timestamps. This is not
/// an end-to-end connectivity probe or a native Peer health policy.
/// </summary>
internal sealed class LibztUnderlayDiagnostics(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly object _gate = new();
    private readonly Dictionary<ulong, (short Change, long When)> _peers = [];
    private long? _lastRefresh;
    private long? _lastNodeOnline;
    private long? _lastNodeOffline;
    private bool? _isNodeOnline;
    private (ushort Primary, ushort Secondary)? _lastPorts;

    internal sealed record PortChange(
        ushort Primary,
        ushort Secondary,
        ushort? PreviousPrimary,
        ushort? PreviousSecondary,
        long? SinceRefreshMs);

    internal sealed record PeerRecovery(
        ulong PeerId,
        string Previous,
        long ElapsedMs,
        long? SinceRefreshMs);

    internal sealed record NodeChange(
        string Phase,
        long? LastPhaseDurationMs,
        long? TimeSincePreviousOfflineMs,
        long? SinceRefreshMs);

    internal void RecordPhysicalRefresh()
    {
        lock (_gate)
            _lastRefresh = _clock();
    }

    internal PortChange? ObservePorts(ushort primary, ushort secondary)
    {
        lock (_gate)
        {
            if (_lastPorts is { } previous
                && previous.Primary == primary && previous.Secondary == secondary)
                return null;

            var observation = new PortChange(
                primary, secondary,
                _lastPorts?.Primary, _lastPorts?.Secondary,
                _lastRefresh is { } refresh
                    ? Math.Max(0, _clock() - refresh) : null);
            _lastPorts = (primary, secondary);
            return observation;
        }
    }

    internal PeerRecovery? ObservePeer(short code, ulong peer)
    {
        if (peer == 0)
            return null;

        if (code is not (LibztNative.EventPeerRelay
            or LibztNative.EventPeerUnreachable
            or LibztNative.EventPeerDirect))
            return null;

        lock (_gate)
        {
            var now = _clock();
            _peers.TryGetValue(peer, out var previous);
            var recovery = code == LibztNative.EventPeerDirect
                           && previous.Change is (
                               LibztNative.EventPeerRelay
                               or LibztNative.EventPeerUnreachable)
                ? new PeerRecovery(
                    peer,
                    previous.Change == LibztNative.EventPeerRelay
                        ? "RELAY" : "UNREACHABLE",
                    Math.Max(0, now - previous.When),
                    _lastRefresh is { } refresh
                        ? Math.Max(0, now - refresh) : null)
                : null;

            if (_peers.Count >= 64 && !_peers.ContainsKey(peer))
            {
                var oldest = _peers.MinBy(static kv => kv.Value.When);
                _peers.Remove(oldest.Key);
            }
            // Only the initial transition into RELAY/UNREACHABLE starts
            // a recovery observation. Repeated events must not reset it.
            if (code == LibztNative.EventPeerDirect
                || previous.Change != code)
                _peers[peer] = (code, now);
            return recovery;
        }
    }

    internal NodeChange? ObserveNode(short code)
    {
        if (code is not (LibztNative.EventNodeOnline or LibztNative.EventNodeOffline))
            return null;
        lock (_gate)
        {
            var now = _clock();
            if (_isNodeOnline == (code == LibztNative.EventNodeOnline))
                return null;
            _isNodeOnline = code == LibztNative.EventNodeOnline;
            var sinceRefresh = _lastRefresh is { } refresh
                ? Math.Max(0, now - refresh) : (long?)null;
            if (code == LibztNative.EventNodeOffline)
            {
                var duration = _lastNodeOnline is { } online
                    ? Math.Max(0, now - online) : (long?)null;
                var period = _lastNodeOffline is { } offline
                    ? Math.Max(0, now - offline) : (long?)null;
                _lastNodeOffline = now;
                _lastNodeOnline = null;
                return new NodeChange("OFFLINE", duration, period, sinceRefresh);
            }
            else
            {
                var duration = _lastNodeOffline is { } offline
                    ? Math.Max(0, now - offline) : (long?)null;
                _lastNodeOnline = now;
                return new NodeChange("ONLINE", duration, null, sinceRefresh);
            }
        }
    }
}
