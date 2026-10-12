# PR #5: physical network handover / Peer path revalidation

## Scope

A confirmed OS physical network change should abort old SOCKS/overlay business
sessions without restarting ZeroTier identity or rejoining a virtual network.
`RuntimeOwner.RequestPhysicalNetworkRefresh` no longer silently drops the
second notification in 500 ms: OS monitors already debounce, and the owner
coalesces events while startup/native configuration is not ready.

On Windows, an OS network handover is treated as an **episode**, not one
destructive reset per callback. The first physical event resets the existing
business Runtime immediately (after the monitor's existing debounce); later
address/availability callbacks in the same episode still request native Binder
and Peer path refresh, without aborting the replacement Runtime. Events arriving
during Runtime creation extend that episode and still request native refresh.
An episode can start again only after **at least 6 seconds since the first
event and 2 seconds without a further event**, assessed when the next physical
event is consumed with a ready Runtime. This adds no waiting before the first
reset. Controller route hot updates, actual Managed IP changes and manual
resets retain their independent lifecycle behavior.

The private native `zts_node_network_changed()` handler now revalidates peer
paths **after the Binder rebinding is complete**. It calls
`ZT_Node_revalidatePeerPathsAfterNetworkChange()` only on explicit physical
network change, not on the periodic binder refresh. Up to 32 leaf peers with
remembered paths (most recent first) receive scope-limited ECHO/HELLO probing,
followed by existing rate-limited WHOIS. A previous path is no longer
preferred until authenticated direct traffic revalidates it. Planet/root/
upstream paths are retained as potential relay and rendezvous fallback. This
neither disables relay nor guarantees a direct connection after strict NAT.

Native trace event `core_handover_peer_paths_revalidated` (kind 30) records:
`reference` = revalidation timestamp (native monotonic ms), `flags=3` =
post-binder leaf-only pass, `byte_count` = local Binder address count,
`result` = number of leaf peers selected. It is **not** a successful peer
handshake or overlay reachability assertion. Existing
`core_scope_path_invalidated` (kind 28) and authenticated HELLO/ECHO traces
supply the more detailed per-path evidence.

### Single-Flight regression and targeted native forensic trace

The Windows Single-Flight experiment in commit `63f8490` was **reverted** after
an A/B handover with the browser load and probe showed no post-handover A→B
success during the observed failure window. Local admissions stayed at
`InFlight=1, Waiting=0, Rejected=0, Recovering=true`; `peer_queue_full` was
not the failure mode. Single-Flight cannot manufacture a reachable ZeroTier
Peer path, and its one-dial gate could only reduce connection attempt capacity
while the underlying route was broken. This observation does **not** prove
that Single-Flight caused the underlying Peer path failure: it is an unsafe
recovery policy until a controlled A/B comparison shows a benefit.

Native evidence in that run (2026-10-12 UTC): A observed a new IPv6 surface
(`240e:b8f:…`) at 23:02:44, but B was still receiving authenticated
Root/Planet RENDEZVOUS suggestions for A's **previous** `2408:840d:…`
IPv6 and `116.231.169.84:24746` IPv4 at 23:03:03 and 23:03:34. B kept
probing these obsolete endpoints while A fell back to upstream relay routing.
The missing piece is *why* those Root-supplied suggestions remained stale:
the available leaf logs do not expose Root's internal address record or
prove if/when Root learned A's new physical endpoint.

To bound the next investigation without increasing wire traffic, native
`core_handover_whois_decision` **kind 31** logs only WHOIS decisions directly
requested by the existing post-Binder leaf revalidation call (maximum 32 peers
per network-change callback). Fields:

| Field | Meaning |
| --- | --- |
| `peer_id` | Leaf whose identity WHOIS was requested |
| `reason_flags=1` | Explicit physical-handover caller |
| `result=1` | WHOIS packet submitted to Switch for upstream transmission (not proof it arrived) |
| `result=0` | Suppressed by `ZT_WHOIS_RETRY_DELAY` |
| `result=-1` | No upstream available |
| `result=-2` | Self-address (skipped) |
| `reference` | Native monotonic timestamp of the decision |

WHOIS requests the Peer **identity**, not an immediate physical endpoint
update or a guaranteed RENDEZVOUS. Interpret kind 31 together with existing
`core_rendezvous_authenticated` (15, candidate/acceptance),
`core_rendezvous_source` (20, upstream sender), Root HELLO/OK (10/13),
`core_surface_observed` (21) and `core_peer_path_learned` (17) on **both** A
and B. There are **no** new background probes, packet payloads, raw secrets,
changed timer constants, additional sockets, or modified selection algorithms.
The underlay ABI remains version 2, 240 bytes.

## Verify with the existing probe

Use the **existing** `tools/connection_probe.ps1` from the project. Do not
install a scheduled task, create a second probe, or alter the traffic protocol.
For the A client, run the same 2-second-interval SOCKS5 URL probe used in
previous testing, and a separate public-Internet control probe. Save both
JSONL files and A/B Runtime JSONL with UTC timestamps. Test the same Wi-Fi /
hotspot handovers with both baseline and candidate builds and note the actual
moment of manual switching independently when possible.

Example for new A→B measurements:

```powershell
pwsh -File tools/connection_probe.ps1 -Node A -TargetUrl 'http://172.26.0.254:8080/' -IntervalSeconds 2 -TimeoutSeconds 5 -SocksPort 1080
```

Use your **actual existing** A→B endpoint, proxy port and target instead of
the example values. Run the public control probe independently; `curl` through
A's SOCKS5 exit is not a direct physical-public-network check.

After finishing both runs, compare the *existing* probe output with:

```bash
python tools/analyze_handover_mttr.py \
  --baseline-runtime logs/pr5-before-A.jsonl \
  --baseline-probe logs/pr5-before-probe-A.jsonl \
  --candidate-runtime logs/pr5-after-A.jsonl \
  --candidate-probe logs/pr5-after-probe-A.jsonl \
  --target-url 'http://172.26.0.254:8080/' \
  --json-out logs/pr5-new-connection-comparison.json
```

The analyzer only accepts strictly matching target URLs, requires successful
new requests before the change, a failed request after the marker and at least
two consecutive successful new requests to confirm sampled recovery. It
refuses an unsupported comparison (different marker types, unequal episode
counts, missing probe coverage). The reported marker-to-success duration is
**not** physical-network MTTR: network notifications can lag the outage, and
2-second sampling plus HTTP completion introduces measurement uncertainty.
Without A/B real devices and the experiment logs, **no MTTR improvement has
been demonstrated**.

## Regression / build boundaries

User A/B logs later confirmed that the Windows episode update suppressed
repeated destructive Runtime resets; other native link failure remains
possible. The Single-Flight experiment was reverted. Existing
`connection_probe.ps1` is unchanged, and this diagnostic addition neither
changes Core forwarding/path policy nor promises faster real-world recovery.

Run C# offline checks, Python offline checks and the **Windows x64 CI build**
from the updated patches. Linux and Android workflows are not part of this PR #5
recovery verification. Trace ABI stays at version 2 (240 bytes); no
application restart, credentials reset, identity replacement or new native
P/Invoke entrypoint is introduced. `ZT_Node_revalidatePeerPathsAfterNetworkChange`
is internal to the libzt↔ZeroTierOne build.
