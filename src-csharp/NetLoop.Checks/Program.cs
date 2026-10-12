using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NetLoop.Core;
using NetLoop.Host;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Checks;

/// <summary>Deterministic offline checks: never start libzt or use a real identity.</summary>
internal static class Program
{
    private static int _passed;
    private static readonly IPAddress A = IPAddress.Parse("10.70.0.1");
    private static readonly IPAddress B = IPAddress.Parse("10.70.0.2");
    private static readonly IPAddress C = IPAddress.Parse("10.70.0.3");

    private static async Task<int> Main()
    {
        try
        {
            Check("Controller direct routing and B fail-closed", RouteDecisions);
            Check("Atomic route generation and invalidation", RouteGenerations);
            Check("Strict single Managed IPv4", SingleAddress);
            Check("Managed IP change requires exactly one rebuild", ManagedIpTransition);
            Check("UDP association removal matches key and object", UdpConditionalRemove);
            Check("Native bind errors are classified without blanket retries", NativeBindClassification);
            Check("UDP response peer set is bounded and expires entries", UdpResponsePeers);
            Check("TCP capacity occupancy classified by phase and source", TcpSessionPhases);
            await CheckAsync("TCP permit shutdown and concurrent exits reconcile", TcpSessionPermitShutdown);
            Check("Underlay port and Peer path timeline uses observed events", UnderlayTimeline);
            Check("Native underlay trace ABI and event kinds are fixed", NativeUnderlayTraceAbi);
            Check("SOCKS5 UDP datagram logical source", UdpPacketRoundTrip);
            Check("Shared UDP byte budget", UdpBudget);
            await CheckAsync("C Managed IP to 127.0.0.1 TCP", LoopbackTcp);
            await CheckAsync("C Managed IP to local UDP with source rewrite", LoopbackUdp);
            await CheckAsync("UDP Factory concurrent disposal", UdpFactoryDisposal);
            await CheckAsync("Startup processes physical events while Node waits", StartupPhysicalRefresh);
            Check("Rapid physical events retain the final network notification", RapidPhysicalEventDelivery);
            await CheckAsync("Peer dialing is bounded across simultaneous SOCKS requests", PeerDialAdmission);
            await CheckAsync("Peer dialing releases canceled waiters and isolates different peers", PeerDialCancellation);
            await CheckAsync("Peer failure backoff does not hold established TCP", PeerDialCooldown);
            await CheckAsync("SOCKS target refused by B/C does not cool down healthy Peer", TargetRefusalDoesNotCooldownPeer);
            await CheckAsync("Two slow target CONNECT replies do not block a healthy target", SlowTargetsDoNotOccupyPeerDials);
            await CheckAsync("Slow target reply timeout does not degrade healthy B Peer", TargetReplyTimeoutDoesNotCooldownPeer);
            await CheckAsync("Actual SOCKS Peer transport failure still triggers backoff", PeerTransportFailureStillBacksOff);
            Check("Ready Runtime retries a failed native refresh", ReadyRefreshRetry);
            await CheckAsync("UDP cleanup errors never leak association capacity", UdpFaultyCleanup);
            await CheckAsync("Overlay UDP returns queued bytes after failed Dispose", OverlayFaultyCleanup);
            Check("Removed --status-file CLI is rejected", RemovedStatusOption);
            await CheckAsync("JSONL consumer survives an output failure", JsonLogConsumerSurvives);
            Console.WriteLine($"PASS {_passed} offline tests");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL after {_passed} checks: {ex}");
            return 1;
        }
    }

    private static OverlayNetworkSnapshot Snapshot(
        IPAddress self, bool connected = true, bool via = false)
    {
        return new OverlayNetworkSnapshot(
            self,
            [self],
            connected ? [OverlayPrefix.Parse("10.70.0.0/24")] : [],
            via
                ? [new ManagedOverlayRoute(
                    OverlayPrefix.Parse("10.70.0.2/32"),
                    IPAddress.Parse("10.70.0.254"), 0, 0)]
                : []);
    }

    private static void RouteDecisions()
    {
        var router = new RouteSelector(Snapshot(A), B, 1);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.OverlayPeer, "A must route C directly");
        Require(router.Select(new ProxyTarget("example.org", 443), false).Kind
            == RouteKind.DefaultExit, "Internet must use B when directly reachable");
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), true).Kind
            == RouteKind.Reject, "Overlay ingress must never transit to C");

        var onC = new RouteSelector(Snapshot(C), B, 1);
        Require(onC.Select(new ProxyTarget(C.ToString(), 3389), true).Kind
            == RouteKind.LocalLoopback, "C must classify its own Managed IP as loopback");

        var withoutB = new RouteSelector(Snapshot(A, connected: false), B, 1);
        Require(withoutB.Select(new ProxyTarget("example.org", 80), false).Kind
            == RouteKind.Reject, "B without Controller route must reject Internet traffic");

        var withGateway = new RouteSelector(Snapshot(A, via: true), B, 1);
        Require(withGateway.Select(new ProxyTarget("example.org", 80), false).Kind
            == RouteKind.Reject, "Gateway via route must not be treated as peer Agent");
    }

    private static void RouteGenerations()
    {
        var router = new RouteSelector(Snapshot(A), B, 10);
        router.MarkNetworkUnavailable("controller_offline");
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.Reject, "Unavailable config must fail closed");

        router.UpdateNetwork(Snapshot(A), 10);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.Reject, "Same generation must not undo invalidation");
        router.UpdateNetwork(Snapshot(A), 9);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.Reject, "Old generation must not resurrect CIDR");
        router.UpdateNetwork(Snapshot(A), 11);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.OverlayPeer, "New Controller snapshot must restore routing");
        Require(router.Generation == 11, "Generation must be 11");
    }

    private static void SingleAddress()
    {
        Require(NetLoopRuntime.ValidateSingleManagedAddress([C]).Equals(C),
            "Single IPv4 was rejected");
        Throws<InvalidOperationException>(
            () => NetLoopRuntime.ValidateSingleManagedAddress([]));
        Throws<InvalidOperationException>(
            () => NetLoopRuntime.ValidateSingleManagedAddress([A, C]));
        Throws<InvalidOperationException>(
            () => NetLoopRuntime.ValidateSingleManagedAddress([IPAddress.IPv6Loopback]));
    }

    private static void ManagedIpTransition()
    {
        Require(!RuntimeOwner.RequiresManagedIpRebind(C, [C]),
            "Same IPv4 address must not restart Runtime");
        Require(RuntimeOwner.RequiresManagedIpRebind(C, [B]),
            "Revoked IP must request one Runtime replacement");
        Require(RuntimeOwner.RequiresManagedIpRebind(C, []),
            "Missing IP must withdraw active Runtime");
    }

    private static void NativeUnderlayTraceAbi()
    {
        Require(Marshal.SizeOf<LibztNative.UnderlayTraceEvent>() == 240,
            "Native trace C# ABI must match 240-byte native struct");
        Require(Marshal.OffsetOf<LibztNative.UnderlayTraceEvent>(
                nameof(LibztNative.UnderlayTraceEvent.Endpoint)).ToInt64() == 48,
            "Native trace endpoint must start at offset 48");
        Require(Marshal.OffsetOf<LibztNative.UnderlayTraceEvent>(
                nameof(LibztNative.UnderlayTraceEvent.LocalEndpoint)).ToInt64() == 112,
            "Native trace local endpoint must start at offset 112");
        Require(Marshal.OffsetOf<LibztNative.UnderlayTraceEvent>(
                nameof(LibztNative.UnderlayTraceEvent.InterfaceName)).ToInt64() == 176,
            "Native trace interface name must start at offset 176");
        Require(LibztUnderlayDiagnostics.NativeKind(1) == "network_refresh_queued"
                && LibztUnderlayDiagnostics.NativeKind(3) == "binder_refresh_completed"
                && LibztUnderlayDiagnostics.NativeKind(5) == "wire_udp_sent"
                && LibztUnderlayDiagnostics.NativeKind(6) == "wire_udp_received"
                && LibztUnderlayDiagnostics.NativeKind(8) == "planet_path_snapshot"
                && LibztUnderlayDiagnostics.NativeKind(10) == "core_hello_sent"
                && LibztUnderlayDiagnostics.NativeKind(11) == "core_hello_authenticated"
                && LibztUnderlayDiagnostics.NativeKind(12) == "core_ok_hello_sent"
                && LibztUnderlayDiagnostics.NativeKind(13) == "core_ok_hello_authenticated"
                && LibztUnderlayDiagnostics.NativeKind(14) == "core_rendezvous_sent"
                && LibztUnderlayDiagnostics.NativeKind(15) == "core_rendezvous_authenticated"
                && LibztUnderlayDiagnostics.NativeKind(16) == "core_upstream_online_decision"
                && LibztUnderlayDiagnostics.NativeKind(17) == "core_peer_path_learned"
                && LibztUnderlayDiagnostics.NativeKind(18) == "core_echo_sent"
                && LibztUnderlayDiagnostics.NativeKind(19) == "core_ok_echo_authenticated"
                && LibztUnderlayDiagnostics.NativeKind(20) == "core_rendezvous_source"
                && LibztUnderlayDiagnostics.NativeKind(21) == "core_surface_observed"
                && LibztUnderlayDiagnostics.NativeKind(22) == "core_surface_previous"
                && LibztUnderlayDiagnostics.NativeKind(23) == "core_peer_probe_or_path_removed"
                && LibztUnderlayDiagnostics.NativeKind(24) == "core_memorized_path_lookup"
                && LibztUnderlayDiagnostics.NativeKind(25) == "core_unknown_path_confirmation"
                && LibztUnderlayDiagnostics.NativeKind(26) == "core_switch_route_selected"
                && LibztUnderlayDiagnostics.NativeKind(27) == "core_upstream_probe_scheduler"
                && LibztUnderlayDiagnostics.NativeKind(28) == "core_scope_path_invalidated"
                && LibztUnderlayDiagnostics.NativeKind(29) == "core_surface_expired"
                && LibztUnderlayDiagnostics.NativeKind(30) == "core_handover_peer_paths_revalidated"
                && LibztUnderlayDiagnostics.NativeKind(31) == "core_handover_whois_decision",
            "Native trace kinds must remain compatible across builds");
        Require(LibztUnderlayDiagnostics.NativeKind(200) == "unknown",
            "Unknown native trace kind must remain safe");
    }

    private static void UdpConditionalRemove()
    {
        var dictionary = new ConcurrentDictionary<string, object>();
        var old = new object();
        var replacement = new object();
        dictionary.TryAdd("peer", old);
        dictionary["peer"] = replacement;

        Require(!OverlayUdpAgent.RemoveMatching(dictionary, "peer", old),
            "Old association accidentally removed newer association");
        Require(dictionary.TryGetValue("peer", out var current)
                && ReferenceEquals(current, replacement),
            "Unexpected change to newer association");
        Require(OverlayUdpAgent.RemoveMatching(dictionary, "peer", replacement),
            "Current association could not be removed");
    }

    private static void NativeBindClassification()
    {
        foreach (var errno in new[] { 99, 100, 101, 113 })
        {
            Require(new LibztException("zts_bsd_bind_easy", -1, errno)
                .IsTransientRuntimeBindFailure, $"Expected transient native bind errno {errno}");
            Require(new LibztException("zts_bsd_socket(udp)", -1, errno)
                .IsTransientRuntimeBindFailure, $"Expected transient UDP socket errno {errno}");
        }

        Require(!new LibztException("zts_bsd_bind_easy", -1, 98)
            .IsTransientRuntimeBindFailure, "EADDRINUSE must fail immediately");
        Require(!new LibztException("zts_bsd_bind_easy", -2, 100)
            .IsTransientRuntimeBindFailure, "Native service failure is not a bind retry");
        Require(!new LibztException("zts_bsd_write", -1, 100)
            .IsTransientRuntimeBindFailure, "Established TCP I/O must not trigger startup retries");
        Require(!new LibztException("zts_bsd_bind_easy", -1, 0)
            .IsTransientRuntimeBindFailure, "Unknown errno must not retry");
        Require(new LibztException("zts_core_query_route_count", -4)
            .IsNetworkUnavailable, "Temporarily missing Controller data must be retryable");
    }

    private static void UdpResponsePeers()
    {
        long now = 1000;
        var peers = new UdpResponsePeerRegistry(() => now);
        var first = new IPEndPoint(IPAddress.Parse("10.70.1.1"), 42043);
        Require(!peers.IsAllowed(first), "Unsolicited peer response was accepted");
        for (var i = 1; i <= UdpResponsePeerRegistry.MaxPeers; i++)
        {
            peers.Allow(new IPEndPoint(IPAddress.Parse($"10.70.1.{i}"), 42043));
            now++;
        }
        Require(peers.Count == UdpResponsePeerRegistry.MaxPeers,
            "Peer set did not reach expected cap");
        var newer = new IPEndPoint(IPAddress.Parse("10.70.1.65"), 42043);
        peers.Allow(newer);
        Require(peers.Count == UdpResponsePeerRegistry.MaxPeers,
            "Peer set exceeded fixed maximum");
        Require(!peers.IsAllowed(first), "Oldest peer was not evicted");
        Require(peers.IsAllowed(newer), "Most recent contacted peer missing");
        now += UdpResponsePeerRegistry.MaxIdleMilliseconds + 1;
        Require(!peers.IsAllowed(newer), "Expired peer remains authorized");
        peers.Allow(first);
        Require(peers.IsAllowed(first), "A subsequent outbound send should reauthorize peer");
    }

    private static void TcpSessionPhases()
    {
        var sessions = new TcpSessionTracker();
        using var capacity = new SemaphoreSlim(3, 3);
        Require(capacity.Wait(0) && capacity.Wait(0) && capacity.Wait(0),
            "Could not acquire TCP permits for phase test");
        using var overlayA = sessions.Track("overlay", A.ToString(), capacity);
        using var overlayC = sessions.Track("overlay", C.ToString(), capacity);
        using var local = sessions.Track("local", "127.0.0.1", capacity);
        overlayA.SetPhase(TcpSessionPhase.Relay);
        overlayC.SetPhase(TcpSessionPhase.Connecting);
        local.SetPhase(TcpSessionPhase.UdpAssociation);
        var usage = sessions.Snapshot();
        Require(usage.Total == 3
                && usage.Handshake == 0 && usage.Connecting == 1
                && usage.Relay == 1 && usage.UdpAssociation == 1
                && usage.Overlay == 2 && usage.Local == 1,
            "TCP permit phases do not match the three active sessions");
        Require(usage.Sources.Any(source =>
                source.Ingress == "overlay" && source.Source == A.ToString()
                && source.Relay == 1),
            "A's established RDP/TCP permit not attributed correctly");
        sessions.Reject("overlay", C.ToString(), 3);
        Require(sessions.Snapshot().RejectedOverlay == 1,
            "Source rejection did not increment the overlay counter");
        overlayA.Dispose();
        overlayC.Dispose();
        local.Dispose();
        Require(sessions.Snapshot().Total == 0
                && sessions.Snapshot().Sources.Count == 0
                && capacity.CurrentCount == 3,
            "Closed TCP sessions were not removed and permitted slots returned");
        local.Dispose();
        overlayA.Dispose();
        Require(capacity.CurrentCount == 3,
            "Duplicate lease disposal over-released the shared TCP capacity");
    }

    private static async Task TcpSessionPermitShutdown()
    {
        const int maxConnections = 4;
        using var capacity = new SemaphoreSlim(maxConnections, maxConnections);
        var sessions = new TcpSessionTracker();

        // Only the lease releases the accepted client's capacity permit.
        // A connection in asynchronous cleanup remains counted/occupied.
        Require(capacity.Wait(0), "Failed to acquire test TCP permit");
        var first = sessions.Track("overlay", C.ToString(), capacity);
        var closeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = Task.Run(async () => {
            using (first)
            {
                await using (var slowClose =
                    new DelayedDisposalConnection(closeStarted, releaseClose))
                {
                    await Task.Yield();
                }
            }
        });

        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(sessions.Snapshot().Total == 1
                && capacity.CurrentCount == maxConnections - 1,
            "A TCP slot was returned before asynchronous connection cleanup finished");
        releaseClose.TrySetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Require(sessions.Snapshot().Total == 0
                && capacity.CurrentCount == maxConnections,
            "A TCP slot was not returned after connection cleanup finished");

        var workers = Enumerable.Range(0, 160).Select(async i => {
            await capacity.WaitAsync();
            using var lease = sessions.Track(
                i % 2 == 0 ? "local" : "overlay",
                i % 2 == 0 ? "127.0.0.1" : B.ToString(),
                capacity);
            lease.SetPhase(i % 3 == 0 ? TcpSessionPhase.Relay
                : TcpSessionPhase.Connecting);
            if (i % 3 == 0)
                await Task.Yield();
            // Simulate error exits as well as normal completion.
            if (i % 7 == 0)
            {
                try
                {
                    throw new IOException("synthetic client shutdown");
                }
                catch (IOException)
                {
                }
            }
            lease.Dispose();
            lease.Dispose();
        }).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(3));
        var usage = sessions.Snapshot();
        Require(usage.Total == 0 && usage.Sources.Count == 0
                && capacity.CurrentCount == maxConnections,
            "Concurrent exits leaked or over-released TCP slots or left stale session metrics");
    }

    private static void UnderlayTimeline()
    {
        long now = 1_000;
        var observations = new LibztUnderlayDiagnostics(() => now);
        var firstPorts = observations.ObservePorts(9993, 34000);
        Require(firstPorts is { PreviousSecondary: null },
            "Initial native port snapshot was not accepted");
        Require(observations.ObservePorts(9993, 34000) is null,
            "Unchanged ports should not produce a new log record");
        observations.RecordPhysicalRefresh();
        now += 250;
        var changed = observations.ObservePorts(9993, 34100);
        Require(changed is {
            PreviousSecondary: 34000,
            Secondary: 34100,
            SinceRefreshMs: 250
        }, "Observed native secondary port rotation was not recorded");

        observations.ObservePeer(LibztNative.EventPeerRelay, 0x12345);
        now += 91_000;
        var peer = observations.ObservePeer(
            LibztNative.EventPeerDirect, 0x12345);
        Require(peer is { ElapsedMs: 91_000, Previous: "RELAY" },
            "Native RELAY to DIRECT observation was not timed");
        Require(observations.ObservePeer(
            LibztNative.EventPeerDirect, 0x12345) is null,
            "Repeated DIRECT is not another recovery");

        observations.ObserveNode(LibztNative.EventNodeOnline);
        now += 156_000;
        var offline = observations.ObserveNode(
            LibztNative.EventNodeOffline);
        Require(offline is { LastPhaseDurationMs: 156_000 },
            "NODE_OFFLINE did not capture prior online duration");
        now += 70_000;
        var online = observations.ObserveNode(
            LibztNative.EventNodeOnline);
        Require(online is { LastPhaseDurationMs: 70_000 },
            "NODE_ONLINE did not capture offline interval");
        now += 156_000;
        offline = observations.ObserveNode(
            LibztNative.EventNodeOffline);
        Require(offline is { TimeSincePreviousOfflineMs: 226_000 },
            "226-second NODE_OFFLINE cycle cannot be measured");
    }

    private static async Task PeerDialAdmission()
    {
        var gate = new PeerDialCoordinator();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maxActive = 0;

        async ValueTask<IProxyConnection> Dial(CancellationToken token)
        {
            Interlocked.Increment(ref started);
            var current = Interlocked.Increment(ref active);
            if (current > maxActive)
                maxActive = current;
            try
            {
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                return new FakeControl();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var calls = Enumerable.Range(
                0,
                PeerDialCoordinator.MaxInFlightPerPeer
                + PeerDialCoordinator.MaxWaitingPerPeer)
            .Select(_ => gate.ConnectAsync(B, Dial, CancellationToken.None).AsTask())
            .ToArray();
        var usage = gate.Snapshot().Single();
        Require(usage.InFlight == 2 && usage.Waiting == 4
                && started == 2,
            "Concurrent calls did not stop at two outgoing peer negotiations");
        await ThrowsAsync<IOException>(async () =>
            _ = await gate.ConnectAsync(B, Dial, CancellationToken.None));
        Require(gate.Snapshot().Single().Rejected == 1,
            "Overflow request was not rejected with a bounded queue");

        release.TrySetResult();
        var established = await Task.WhenAll(calls)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Require(maxActive <= PeerDialCoordinator.MaxInFlightPerPeer
                && gate.Snapshot().Single().InFlight == 0,
            "The gate exceeded the outgoing dial budget");
        foreach (var connection in established)
            await connection.DisposeAsync();
    }

    private static async Task PeerDialCooldown()
    {
        long now = 10_000;
        var gate = new PeerDialCoordinator(() => now);
        await ThrowsAsync<IOException>(async () =>
            _ = await gate.ConnectAsync(
                B,
                _ => ValueTask.FromException<IProxyConnection>(
                    new IOException("synthetic peer outage")),
                CancellationToken.None));
        var failed = gate.Snapshot().Single();
        Require(failed.ConsecutiveFailures == 1
                && failed.RemainingCooldownMs == 250,
            "First failed Peer attempt did not set bounded cooldown");
        now += 250;

        // Simulate an already-open RDP relay: after ConnectAsync returns the
        // dial permit is free even while the caller retains that connection.
        var connections = new List<IProxyConnection>();
        for (var i = 0; i < 7; i++)
            connections.Add(await gate.ConnectAsync(
                B,
                _ => ValueTask.FromResult<IProxyConnection>(new FakeControl()),
                CancellationToken.None));
        Require(gate.Snapshot().Single().InFlight == 0
                && gate.Snapshot().Single().ConsecutiveFailures == 0,
            "Established RDP connection was incorrectly counted as an in-flight dial");
        foreach (var connection in connections)
            await connection.DisposeAsync();
    }

    private static async Task PeerDialCancellation()
    {
        var gate = new PeerDialCoordinator();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<IProxyConnection> Blocked(CancellationToken token)
        {
            await release.Task.WaitAsync(token);
            return new FakeControl();
        }

        var first = gate.ConnectAsync(B, Blocked, CancellationToken.None).AsTask();
        var second = gate.ConnectAsync(B, Blocked, CancellationToken.None).AsTask();
        using var canceled = new CancellationTokenSource();
        var waiting = gate.ConnectAsync(B, Blocked, canceled.Token).AsTask();
        Require(gate.Snapshot().Single().Waiting == 1,
            "Cancelable peer request was not queued");

        // C has a separate dial budget despite B being fully occupied.
        var directC = await gate.ConnectAsync(
            C,
            _ => ValueTask.FromResult<IProxyConnection>(new FakeControl()),
            CancellationToken.None);
        await directC.DisposeAsync();
        canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => waiting);
        var bStatus = gate.Snapshot().Single(entry => entry.Peer == B.ToString());
        Require(bStatus.Waiting == 0 && bStatus.InFlight == 2,
            "Canceled waiter failed to release B's admission queue slot");
        release.TrySetResult();
        var opened = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(2));
        foreach (var connection in opened)
            await connection.DisposeAsync();
        Require(gate.Snapshot().All(entry =>
                entry.Waiting == 0 && entry.InFlight == 0),
            "Peer gates did not return their permits after cancellation");
    }

    private static RoutingConnector ScriptedRouter(
        ScriptedSocksConnector remote,
        PeerDialCoordinator dials,
        TimeSpan? targetReplyTimeout = null)
        => new(
            new RouteSelector(Snapshot(A), B, 1),
            overlayIngress: false,
            overlayPort: 42042,
            remote,
            new LoopbackTcpConnector(TimeSpan.FromSeconds(1)),
            new DirectTcpConnector(TimeSpan.FromSeconds(1)),
            dials,
            targetReplyTimeout ?? TimeSpan.FromSeconds(2));

    private static async Task TargetRefusalDoesNotCooldownPeer()
    {
        var remote = new ScriptedSocksConnector(port =>
            port is 9001 or 3389 ? (byte)5 : (byte)0);
        var dials = new PeerDialCoordinator();
        var router = ScriptedRouter(remote, dials);

        // Internet via B: B's greeting works; B legitimately rejects target.
        await ThrowsAsync<IOException>(async () =>
            _ = await router.ConnectAsync(
                new ProxyTarget("198.51.100.10", 9001), CancellationToken.None));
        var b = dials.Snapshot().Single(status => status.Peer == B.ToString());
        Require(b.ConsecutiveFailures == 0 && b.RemainingCooldownMs == 0,
            "A rejected destination incorrectly marked the healthy B Peer failed");

        await using (var healthy = await router.ConnectAsync(
            new ProxyTarget("198.51.100.10", 443), CancellationToken.None))
        {
            Require(dials.Snapshot().Single(status => status.Peer == B.ToString())
                    .InFlight == 0,
                "An established B relay retained a dial permit");
        }

        // Direct A -> C Managed IP -> C local RDP: a closed RDP port must
        // not disable another CONNECT to C's still-healthy SOCKS Agent.
        await ThrowsAsync<IOException>(async () =>
            _ = await router.ConnectAsync(
                new ProxyTarget(C.ToString(), 3389), CancellationToken.None));
        var c = dials.Snapshot().Single(status => status.Peer == C.ToString());
        Require(c.ConsecutiveFailures == 0 && c.RemainingCooldownMs == 0,
            "C's rejected RDP target incorrectly cooled down C's Peer transport");
        await using var alternateC = await router.ConnectAsync(
            new ProxyTarget(C.ToString(), 3390), CancellationToken.None);
        Require(remote.Peers.Contains(B.ToString())
                && remote.Peers.Contains(C.ToString()),
            "The test did not actually use B/C SOCKS Agents");
        Require(remote.Connections.All(conn =>
                !conn.TargetFailed || conn.IsDisposed),
            "Rejected destination leaked its negotiated SOCKS Peer transport");
    }

    private static async Task SlowTargetsDoNotOccupyPeerDials()
    {
        // A slow destination never sends its CONNECT reply. Both A->B TCP
        // connections and SOCKS greetings are still immediately healthy.
        var remote = new ScriptedSocksConnector(port =>
            port is 9001 or 9002 ? null : (byte)0);
        var dials = new PeerDialCoordinator();
        var router = ScriptedRouter(remote, dials);
        using var stop = new CancellationTokenSource();
        try
        {
            var slow1 = router.ConnectAsync(
                new ProxyTarget("198.51.100.11", 9001), stop.Token).AsTask();
            var slow2 = router.ConnectAsync(
                new ProxyTarget("198.51.100.12", 9002), stop.Token).AsTask();
            await Task.WhenAll(
                remote.WaitForTargetAsync(9001),
                remote.WaitForTargetAsync(9002))
                .WaitAsync(TimeSpan.FromSeconds(2));

            var status = dials.Snapshot().Single();
            Require(status.InFlight == 0 && status.Waiting == 0,
                "Slow destination CONNECT response is still occupying Peer dial slots");

            // Before the fix the third healthy target waits on the same 2
            // slots for 3 seconds and fails, despite a healthy B greeting.
            await using var fast = await router.ConnectAsync(
                    new ProxyTarget("198.51.100.13", 443), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Require(dials.Snapshot().Single().Rejected == 0,
                "Healthy destination was rejected due to unrelated slow targets");
            stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(
                () => Task.WhenAll(slow1, slow2));
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static async Task TargetReplyTimeoutDoesNotCooldownPeer()
    {
        var remote = new ScriptedSocksConnector(port =>
            port == 9004 ? null : (byte)0);
        var dials = new PeerDialCoordinator();
        var router = ScriptedRouter(remote, dials, TimeSpan.FromMilliseconds(120));
        await ThrowsAsync<TimeoutException>(async () =>
            _ = await router.ConnectAsync(
                new ProxyTarget("198.51.100.21", 9004), CancellationToken.None));
        Require(remote.WaitForTargetAsync(9004).IsCompleted,
            "The test timeout occurred before sending target CONNECT to B");
        var afterTimeout = dials.Snapshot().Single();
        Require(afterTimeout.ConsecutiveFailures == 0
                && afterTimeout.RemainingCooldownMs == 0
                && afterTimeout.InFlight == 0,
            "Only a slow destination timed out, but healthy B was cooled down");
        Require(remote.Connections.All(conn => conn.IsDisposed),
            "A timed-out target CONNECT leaked its negotiated SOCKS connection");

        await using var healthy = await router.ConnectAsync(
            new ProxyTarget("198.51.100.22", 443), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Require(dials.Snapshot().Single().Rejected == 0,
            "Target response timeout blocked another healthy destination");
    }

    private static async Task PeerTransportFailureStillBacksOff()
    {
        var dials = new PeerDialCoordinator();
        var proxy = new Socks5ProxyConnector(
            new FailingPeerTransport(),
            new ProxyTarget(B.ToString(), 42042),
            negotiationRetryWindow: TimeSpan.FromMilliseconds(100),
            negotiationAttemptTimeout: TimeSpan.FromMilliseconds(35));
        await ThrowsAsync<TimeoutException>(async () =>
            _ = await dials.ConnectAsync(
                B, proxy.ConnectAndNegotiateAsync, CancellationToken.None));
        var status = dials.Snapshot().Single();
        Require(status.ConsecutiveFailures == 1
                && status.RemainingCooldownMs > 0
                && status.InFlight == 0,
            "A broken B Peer TCP transport did not activate bounded dial backoff");
    }

    private static void ReadyRefreshRetry()
    {
        var pending = true;
        var tries = 0;
        Require(!RuntimeOwner.TryDeliverPendingPhysicalRefresh(
                    ref pending, () => { tries++; return false; })
                && pending && tries == 1,
            "Native refresh failure was dropped instead of marked pending");
        Require(RuntimeOwner.TryDeliverPendingPhysicalRefresh(
                    ref pending, () => { tries++; return true; })
                && !pending && tries == 2,
            "Pending native refresh was not retried successfully");
        Require(RuntimeOwner.TryDeliverPendingPhysicalRefresh(
                    ref pending, () => { tries++; return false; })
                && tries == 2,
            "A successful native refresh unexpectedly triggered another retry");
    }

    private static void RapidPhysicalEventDelivery()
    {
        var events = Channel.CreateBounded<RuntimeOwner.Event>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

        // Both platform callbacks may arrive within 500 ms; neither is dropped
        // here just because the previous notification was recent.
        RuntimeOwner.QueuePhysicalNetworkRefresh(events.Writer, "network_unavailable");
        RuntimeOwner.QueuePhysicalNetworkRefresh(events.Writer, "network_available");
        Require(events.Reader.TryRead(out var first)
                && first.Kind == RuntimeOwner.EventKind.Physical
                && first.Reason == "network_unavailable",
            "First network transition was not queued");
        Require(events.Reader.TryRead(out var final)
                && final.Kind == RuntimeOwner.EventKind.Physical
                && final.Reason == "network_available",
            "Final usable-network notification was swallowed by the owner");

        // During startup/runtime creation, notifications can be combined into
        // one native refresh, but that refresh must still be issued.
        RuntimeOwner.QueuePhysicalNetworkRefresh(events.Writer, "network_unavailable");
        RuntimeOwner.QueuePhysicalNetworkRefresh(events.Writer, "network_available");
        var pending = false;
        RuntimeOwner.Event? deferred = null;
        var refreshes = 0;
        RuntimeOwner.DrainPendingWaitingEvents(events.Reader,
            () => { refreshes++; return true; }, ref pending, ref deferred);
        Require(refreshes == 1 && !pending && deferred is null,
            "Startup should coalesce rapid notifications without losing the refresh");
    }

    private static async Task StartupPhysicalRefresh()
    {
        var notifications = Channel.CreateUnbounded<RuntimeOwner.Event>();
        var startup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeReady = false;
        var attempts = 0;
        var pending = false;
        RuntimeOwner.Event? deferredReset = null;

        notifications.Writer.TryWrite(
            new RuntimeOwner.Event(RuntimeOwner.EventKind.Physical, "wifi_changed"));
        notifications.Writer.TryWrite(
            new RuntimeOwner.Event(RuntimeOwner.EventKind.Manual, "manual_reset"));

        var wait = RuntimeOwner.AwaitStartupAsync(
            startup.Task,
            () => RuntimeOwner.DrainPendingWaitingEvents(
                notifications.Reader,
                () => {
                    if (Interlocked.Increment(ref attempts) == 1)
                        firstAttempt.TrySetResult();
                    else
                        secondAttempt.TrySetResult();
                    return nativeReady;
                },
                ref pending,
                ref deferredReset),
            () => { },
            CancellationToken.None);
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!startup.Task.IsCompleted,
            "Physical notification was not processed before startup finished");
        Require(pending, "Physical refresh was lost while native Node was unavailable");
        nativeReady = true;
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!pending, "Pending refresh was not sent once native Node was ready");
        Require(deferredReset?.Kind == RuntimeOwner.EventKind.Manual,
            "Manual reset must survive startup waiting");
        startup.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void UdpPacketRoundTrip()
    {
        var original = new ProxyTarget(C.ToString(), 3389);
        var raw = Socks5UdpPacket.Build(original, new byte[] { 12, 34, 56 });
        Require(Socks5UdpPacket.TryParse(raw, out var parsed, out var error),
            $"Cannot parse UDP packet: {error}");
        Require(parsed.Target == original, "UDP packet target changed");
        Require(parsed.Content.Span.SequenceEqual(new byte[] { 12, 34, 56 }),
            "UDP packet body changed");
    }

    private static void UdpBudget()
    {
        var budget = new UdpByteBudget(100);
        Require(budget.TryReserve(90), "Initial reservation failed");
        Require(!budget.TryReserve(11), "Exceeded byte limit");
        budget.Release(90);
        Require(budget.TryReserve(100), "Budget was not returned");
        budget.Release(100);
        Require(budget.UsedBytes == 0 && budget.PeakBytes == 100,
            "Budget counters inaccurate");

        var shared = new UdpByteBudget(1024);
        Parallel.For(0, 1000, _ => {
            if (shared.TryReserve(128))
                shared.Release(128);
        });
        Require(shared.UsedBytes == 0 && shared.PeakBytes <= 1024,
            "Concurrent reserve exceeded capacity or leaked bytes");
    }

    private static async Task LoopbackTcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = checked((ushort)((IPEndPoint)listener.LocalEndpoint).Port);
            var target = new ProxyTarget(C.ToString(), port);
            var route = new RouteSelector(Snapshot(C), B, 1);
            Require(route.Select(target, true).Kind == RouteKind.LocalLoopback,
                "C local TCP route was not loopback");
            var connector = new LoopbackTcpConnector(TimeSpan.FromSeconds(2));
            var accept = listener.AcceptTcpClientAsync();
            await using var connected = await connector.ConnectAsync(
                target, CancellationToken.None);
            using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(2));
            var actual = (IPEndPoint)accepted.Client.LocalEndPoint!;
            Require(actual.Address.Equals(IPAddress.Loopback)
                && actual.Port == port, "TCP destination was not 127.0.0.1:original port");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task LoopbackUdp()
    {
        var transport = new EchoUdpTransport();
        var reply = new TaskCompletionSource<ProxyUdpDatagram>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var association = new OverlayUdpAssociation(
            new IPEndPoint(A, 54321),
            C,
            new RouteSelector(Snapshot(C), B, 1),
            transport,
            new EchoUdpFactory(),
            TimeSpan.FromMinutes(1),
            32,
            TimeSpan.FromSeconds(2),
            new UdpByteBudget(1024 * 1024),
            (_, datagram, _) => {
                reply.TrySetResult(datagram);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await association.ForwardAsync(
            new ProxyTarget(C.ToString(), 3389),
            new byte[] { 99, 88 }, CancellationToken.None);
        var actual = await reply.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(transport.LastTarget == new ProxyTarget("127.0.0.1", 3389),
            "UDP was not forwarded to local loopback with original port");
        Require(actual.Source == new ProxyTarget(C.ToString(), 3389),
            "UDP reply logical source not rewritten to C Managed IP");
        var wire = Socks5UdpPacket.Build(actual.Source, actual.Content);
        Require(Socks5UdpPacket.TryParse(wire, out var parsed, out _)
            && parsed.Target == new ProxyTarget(C.ToString(), 3389),
            "SOCKS5 reply header does not identify C:original port");
    }

    private static async Task UdpFactoryDisposal()
    {
        using var control = new FakeControl();
        var blocking = new BlockingUdpFactory();
        var factory = new Socks5UdpAssociationFactory(
            blocking, 1, TimeSpan.FromMinutes(1));

        var creation = factory.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None).AsTask();
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstDispose = factory.DisposeAsync().AsTask();
        var secondDispose = factory.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Require(creation.IsCompleted, "Pending creation did not stop");
        try
        {
            await creation.ConfigureAwait(false);
            throw new Exception("Creation should have canceled");
        }
        catch (OperationCanceledException)
        {
        }

        var activeFactory = new Socks5UdpAssociationFactory(
            new EchoUdpFactory(), 2, TimeSpan.FromMinutes(1));
        var association = await activeFactory.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None);
        await Task.WhenAll(
            activeFactory.DisposeAsync().AsTask(),
            activeFactory.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(3));
        await association.DisposeAsync();
    }

    private static async Task UdpFaultyCleanup()
    {
        using var control = new FakeControl();
        var factory = new Socks5UdpAssociationFactory(
            new FaultyDisposeUdpFactory(), 1, TimeSpan.FromMinutes(1));

        // Invalid declared client address fails AFTER transport creation.
        // Its DisposeAsync also throws; CreateAsync must preserve the original
        // protocol error and return the only capacity permit.
        await ThrowsAsync<ProtocolViolationException>(async () => {
            _ = await factory.CreateAsync(
                control, new ProxyTarget("invalid-client-name", 5000),
                CancellationToken.None);
        });

        var association = await factory.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None);
        await ThrowsAsync<IOException>(
            () => association.DisposeAsync().AsTask());

        // Association DisposeAsync was faulty too, but the next admission
        // must still succeed under the same maxAssociations=1 cap.
        var next = await factory.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None);
        try
        {
            await factory.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (IOException)
        {
            // Factory reports the transport cleanup failure, but must
            // finish releasing every Association and shared semaphore.
        }
        await next.DisposeAsync();
        await factory.DisposeAsync();
        Require(factory.ActiveAssociations == 0,
            "Factory still tracks associations after failed transport disposal");

        // A failure in the first of several active Associations must not
        // skip the others or release shared capacity while they still run.
        var multi = new Socks5UdpAssociationFactory(
            new FaultyDisposeUdpFactory(), 2, TimeSpan.FromMinutes(1));
        var first = await multi.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None);
        var second = await multi.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None);
        try
        {
            await multi.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (IOException)
        {
        }
        await Task.WhenAll(first.DisposeAsync().AsTask(), second.DisposeAsync().AsTask());
        Require(multi.ActiveAssociations == 0,
            "A failed association cleanup prevented the other from closing");
    }

    private static async Task OverlayFaultyCleanup()
    {
        var budget = new UdpByteBudget(1024 * 1024);
        var transport = new FaultyDisposeUdpTransport(blockSends: true);
        var association = new OverlayUdpAssociation(
            new IPEndPoint(A, 54321),
            C,
            new RouteSelector(Snapshot(C), B, 1),
            transport,
            new EchoUdpFactory(),
            TimeSpan.FromMinutes(1),
            32,
            TimeSpan.FromSeconds(2),
            budget,
            (_, _, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        for (var i = 0; i < 16; i++)
            Require(association.TryEnqueue(
                new ProxyTarget(C.ToString(), 3389),
                new byte[1024]), "Failed to enqueue test UDP datagram");
        Require(budget.UsedBytes > 0, "No UDP data was held in the pending queue");

        await association.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Require(budget.UsedBytes == 0,
            "Failed UDP transport disposal left queued bytes reserved");
        await association.DisposeAsync();
    }

    private static void RemovedStatusOption()
    {
        Throws<ArgumentException>(() => HostOptions.Parse([
            "--network", "123456789abcdef0",
            "--state-dir", "state",
            "--default-exit", "10.70.0.2",
            "--status-file", "client-status.json"
        ]));
        var options = HostOptions.Parse([
            "--network", "123456789abcdef0",
            "--state-dir", "state",
            "--default-exit", "10.70.0.2"
        ]);
        Require(options.DefaultExit?.Equals(B) == true,
            "The supported configuration did not parse after status-file removal");
        Require(!HostOptions.Usage.Contains("--status-file", StringComparison.Ordinal),
            "Removed --status-file is still advertised in --help");
    }

    private static async Task JsonLogConsumerSurvives()
    {
        var original = Console.Out;
        try
        {
            Console.SetOut(new FailOnceWriter(original));
            JsonLog.Info("synthetic_json_output_error");
            JsonLog.Info("synthetic_after_output_error");
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(3));
            await JsonLog.FlushAsync(deadline.Token);
        }
        finally
        {
            Console.SetOut(original);
        }

        JsonLog.Info("synthetic_consumer_still_alive");
        using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await JsonLog.FlushAsync(flush.Token);
    }

    private sealed class FailOnceWriter(TextWriter next) : TextWriter
    {
        private int _failed;
        public override System.Text.Encoding Encoding => next.Encoding;
        public override void WriteLine(string? value)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
                throw new IOException("synthetic stdout failure");
            next.WriteLine(value);
        }
        public override void Flush() => next.Flush();
    }

    private static void Check(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static async Task CheckAsync(string name, Func<Task> action)
    {
        await action().WaitAsync(TimeSpan.FromSeconds(8));
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private sealed class EchoUdpFactory : IProxyUdpTransportFactory
    {
        public ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IProxyUdpTransport>(new EchoUdpTransport());
        }
    }

    private sealed class EchoUdpTransport : IProxyUdpTransport
    {
        private readonly Channel<ProxyUdpDatagram> _replies =
            Channel.CreateUnbounded<ProxyUdpDatagram>();
        public ProxyTarget? LastTarget { get; private set; }

        public ValueTask SendAsync(
            ProxyTarget target,
            ReadOnlyMemory<byte> content,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastTarget = target;
            _replies.Writer.TryWrite(
                new ProxyUdpDatagram(target, content.ToArray()));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ProxyUdpDatagram> ReceiveAsync(CancellationToken token)
            => await _replies.Reader.ReadAsync(token);

        public ValueTask DisposeAsync()
        {
            _replies.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingUdpFactory : IProxyUdpTransportFactory
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unexpected completion");
        }
    }

    private sealed class FaultyDisposeUdpFactory : IProxyUdpTransportFactory
    {
        public ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IProxyUdpTransport>(
                new FaultyDisposeUdpTransport());
        }
    }

    private sealed class FaultyDisposeUdpTransport(bool blockSends = false)
        : IProxyUdpTransport
    {
        public async ValueTask SendAsync(
            ProxyTarget target,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            if (blockSends)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async ValueTask<ProxyUdpDatagram> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Synthetic receive unexpectedly resumed");
        }

        public ValueTask DisposeAsync()
            => ValueTask.FromException(
                new IOException("synthetic transport cleanup failure"));
    }

    private sealed class FakeControl : IProxyConnection, IDisposable
    {
        public string Description => "synthetic-control";
        public EndPoint LocalEndPoint =>
            new IPEndPoint(IPAddress.Loopback, 45678);
        public EndPoint RemoteEndPoint =>
            new IPEndPoint(IPAddress.Loopback, 45679);

        public ValueTask<int> ReadAsync(byte[] b, int c, CancellationToken t) =>
            ValueTask.FromResult(0);
        public ValueTask WriteAsync(byte[] b, int c, CancellationToken t) =>
            ValueTask.CompletedTask;
        public ValueTask ShutdownWriteAsync(CancellationToken t) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private sealed class ScriptedSocksConnector(Func<ushort, byte?> response)
        : IProxyConnector
    {
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource> _requested = [];
        internal readonly ConcurrentBag<ScriptedSocksConnection> Connections = [];
        internal readonly ConcurrentBag<string> Peers = [];

        public ValueTask<IProxyConnection> ConnectAsync(
            ProxyTarget endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(endpoint.Port == 42042,
                "Unexpected destination for the synthetic Peer Agent");
            Peers.Add(endpoint.Host);
            var connection = new ScriptedSocksConnection((port, peer) => {
                _requested.GetOrAdd(port, static _ => new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();
                var reply = response(port);
                if (reply.HasValue)
                    peer.SendReply(reply.Value);
            });
            Connections.Add(connection);
            return ValueTask.FromResult<IProxyConnection>(connection);
        }

        internal Task WaitForTargetAsync(ushort port) =>
            _requested.GetOrAdd(port, static _ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    private sealed class FailingPeerTransport : IProxyConnector
    {
        public ValueTask<IProxyConnection> ConnectAsync(
            ProxyTarget target, CancellationToken cancellationToken)
            => ValueTask.FromException<IProxyConnection>(
                new IOException("synthetic native Peer TCP transport unavailable"));
    }

    private sealed class ScriptedSocksConnection(
        Action<ushort, ScriptedSocksConnection> onTarget)
        : IProxyConnection
    {
        private readonly Channel<byte> _responses = Channel.CreateUnbounded<byte>();
        private bool _greeted;
        private bool _disposed;
        public string Description => "scripted-socks-peer";
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        internal bool TargetFailed { get; private set; }
        internal bool IsDisposed => _disposed;

        public async ValueTask<int> ReadAsync(
            byte[] buffer, int count, CancellationToken cancellationToken)
        {
            byte first;
            try
            {
                first = await _responses.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
            buffer[0] = first;
            var read = 1;
            while (read < count && _responses.Reader.TryRead(out var next))
                buffer[read++] = next;
            return read;
        }

        public ValueTask WriteAsync(
            byte[] buffer, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_greeted && count == 3
                && buffer[0] == 5 && buffer[1] == 1 && buffer[2] == 0)
            {
                _greeted = true;
                _responses.Writer.TryWrite(5);
                _responses.Writer.TryWrite(0);
                return ValueTask.CompletedTask;
            }
            if (_greeted && count >= 10
                && buffer[0] == 5 && buffer[1] == 1 && buffer[2] == 0)
            {
                var port = checked((ushort)((buffer[count - 2] << 8) | buffer[count - 1]));
                onTarget(port, this);
                return ValueTask.CompletedTask;
            }
            throw new ProtocolViolationException("Unexpected synthetic SOCKS packet");
        }

        internal void SendReply(byte code)
        {
            TargetFailed = code != 0;
            foreach (var b in new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 })
                _responses.Writer.TryWrite(b);
        }

        public ValueTask ShutdownWriteAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            _responses.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedDisposalConnection(
        TaskCompletionSource started,
        TaskCompletionSource resume) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            started.TrySetResult();
            await resume.Task;
        }
    }
}
