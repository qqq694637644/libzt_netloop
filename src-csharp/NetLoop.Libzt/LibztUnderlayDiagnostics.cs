namespace NetLoop.Libzt;

// Diagnostic event labels only. No peer/port/node cycle state is retained
// when the optional Native trace is disabled.
internal static class LibztUnderlayDiagnostics
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
        31 => "core_handover_whois_decision",
        32 => "core_handover_background_ping_deadline",
        33 => "core_handover_root_hello_eligibility",
        34 => "binder_handover_ipv6_bindings",
        35 => "core_handover_root_ipv6_hello_requested",
        36 => "binder_handover_root_ipv6_hello_batch",
        37 => "binder_handover_ipv6_dad_state",
        38 => "core_handover_root_ipv6_hello_authenticated",
        _ => "unknown"
    };
}
