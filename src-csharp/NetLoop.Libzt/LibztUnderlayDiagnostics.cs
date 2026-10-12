namespace NetLoop.Libzt;

/// <summary>
/// Lightweight, event-driven underlay timeline. Timestamps use monotonic
/// milliseconds; JSONL records supply wall-clock timestamps. This is not
/// an end-to-end connectivity probe or a native Peer health policy.
/// </summary>
internal sealed class LibztUnderlayDiagnostics(Func<long>? clock = null)
{
    internal static string NativeKind(uint kind) => kind switch
    {
        1 => "network_refresh_queued",
        2 => "binder_refresh_started",
        3 => "binder_refresh_completed",
        4 => "peer_path_checked",
        5 => "wire_udp_sent",
        6 => "wire_udp_received",
        7 => "node_phase",
        8 => "planet_path_snapshot",
        10 => "core_hello_sent",
        11 => "core_hello_authenticated",
        12 => "core_ok_hello_sent",
        13 => "core_ok_hello_authenticated",
        14 => "core_rendezvous_sent",
        15 => "core_rendezvous_authenticated",
        16 => "core_upstream_online_decision",
        17 => "core_peer_path_learned",
        18 => "core_echo_sent",
        19 => "core_ok_echo_authenticated",
        20 => "core_rendezvous_source",
        21 => "core_surface_observed",
        22 => "core_surface_previous",
        23 => "core_peer_probe_or_path_removed",
        24 => "core_memorized_path_lookup",
        25 => "core_unknown_path_confirmation",
        26 => "core_switch_route_selected",
        27 => "core_upstream_probe_scheduler",
        28 => "core_scope_path_invalidated",
        29 => "core_surface_expired",
        30 => "core_handover_peer_paths_revalidated",
        _ => "unknown"
    };

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
