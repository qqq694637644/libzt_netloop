# PR #5: physical network handover / Peer path revalidation

> 历史实现说明：PR #7 已取代本节的 Windows 250ms 防抖与 6s/2s
> episode 时间分组。Windows 现在按每条回调即时采集到的实际出站
> IPv4/IPv6 路由生成 epoch，并对失败采集执行有限重试。
> 请以 `docs/pr7-native-debt-cleanup.md` 为当前规则。

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

### Windows Root HELLO scheduling: handover-only snapshots

After a handover, the A/B logs showed A's new public IPv6 surface was visible
to another peer long before the Root supplying B's RENDEZVOUS candidates
proposed that new address. Upstream `Node::_PingPeersThatNeedPing` uses a
**16 × 14-second (224-second) full-HELLO gate** for configured upstreams
which still have remembered paths. This is a possible explanation for delayed
Root IPv6 contact, **not proof**: existing paths, authenticated replies and
actual sent packets must be correlated before touching scheduler behavior.

Three **diagnostic-only** events are recorded for an explicit Windows
physical handover; periodic Binder refreshes and Linux/Android do not emit
them. Every event uses the existing underlay ABI and JSONL writer:

| Kind / event | Fields | Interpretation |
| --- | --- | --- |
| 34 `binder_handover_ipv6_bindings` | `flags`: bit 1 = any IPv6 binding, bit 2 = global IPv6 binding, bit 4 = IPv4 binding; `byte_count` = number of bound address/port entries; `result` = count of global IPv6 entries; `endpoint` = first bound global IPv6 | Binder has a global IPv6 address **after** copying refreshed interfaces into Core; not proof the new remote IPv6 path is routable |
| 32 `core_handover_background_ping_deadline` | `reference` = previous native background ping check time; `result` = ms until the next periodic ping check would be due; `flags` bit 1 = low-bandwidth mode | Distinguishes a delayed **background tick** from the separate per-Root full-HELLO decision |
| 33 `core_handover_root_hello_eligibility` | `peer_id` = Root/Moon ID; `reference` = previous full-HELLO timestamp; `result` = ms remaining on Root full-HELLO eligibility gate (0 = due, -1 = Peer not loaded, -2 = no configured address in that family); `endpoint` = first configured stable address per family | Snapshot of the existing Root eligibility condition, **not** a new HELLO or a guarantee of transmission |

For kind 33 `flags`: bit 1 = Root Peer loaded; bit 2 = remembered paths
present; bit 4 = full-HELLO timer gate active; bit 8 = IPv6 candidate; bit 16
= IPv4 candidate; bit 32 = Core categorized this contact as a leaf; bit 64
= configured endpoint missing for that address family. At most eight upstream
identities and **one IPv4 plus one IPv6 candidate each** are recorded per
notification; other configured endpoints might exist. A missing-family event
has `result=-2` and no endpoint. Its address sampling is for diagnostics,
not the actual randomly selected endpoint; `core_hello_sent` (10) provides
that authoritative transmitted destination.

To diagnose a slow recovery, compare kinds 34 → 32/33 with 27
(`core_upstream_probe_scheduler`, sampled), 10 (`core_hello_sent`, including
its submission result and endpoint), 13 (`core_ok_hello_authenticated`), 21
(`core_surface_observed`) on A, and 15/20 (authenticated RENDEZVOUS/candidate
source) on B. If the gate is still active despite a bound IPv6 interface,
that is direct evidence of local scheduling delay. If HELLO was submitted
immediately but Root never authenticated, examine return-path reachability.
If Root authentication precedes B's new candidate by a long time, the
remaining delay concerns candidate propagation; A/B logs cannot expose the
third-party Root's private address cache or timer state.

**No** new HELLO/ECHO/WHOIS is sent by these snapshots; Root timeout/priority,
path selection, Peer Dial and the existing `connection_probe.ps1` remain
unchanged.

### Windows handover: bounded fast Root IPv6 HELLO

The subsequent A/B trace showed the specific failure: after Binder had a
different global IPv6 address, known Roots still had memorized paths and the
ordinary 224-second Full HELLO gate had roughly 170 seconds remaining. The
normal background tick continued to run, but A did not send a new IPv6 Full
HELLO to those Roots until the gate expired. After Root IPv6 HELLO/OK,
authenticated RENDEZVOUS candidates and A→B HTTP recovered shortly afterward.
That supports accelerating this *specific* handover transition, without
changing the regular scheduler for ordinary operation.

In the Windows libzt NodeService I/O loop, every Binder refresh now retains a
deduplicated set of **bound global IPv6 IP addresses** (ignoring UDP source
ports). On an explicit physical-network notification, the native side opens
the same 6-second minimum / 2-second quiet handover episode as the managed
owner. When the bound global IPv6 IP set **actually changes**, once per
episode, libzt calls `ZT_Node_fastProbeRootIpv6AfterNetworkChange()` after
rebinding sockets and synchronizing addresses into Core. A changed address
observed up to 5 seconds before the OS callback also qualifies; if the new
address appears in a subsequent Binder refresh, the pending episode remains
eligible for up to 30 seconds after the last callback. No previous baseline,
only a port rebind, or an empty global IPv6 set does not trigger a probe.

The Core call sends **one standard authenticated Full HELLO** to the first
configured global IPv6 endpoint of each **already known** non-leaf Root/Moon
in the `getUpstreamsToContact()` set, capped at **8 Root identities / 8
HELLOs per handover episode**. It bypasses only the *one-time eligibility
decision* for this handover; it does not clear or reprioritize memorized Root
paths, change the normal 224-second timer, send ongoing background probes,
alter Peer Dial, or disable relay/rendezvous fallback. Root identities without
an existing Peer entry are skipped rather than implicitly created.

The existing `core_hello_sent` (kind 10) and authenticated `core_ok_hello_*`
events report actual send submission/response. Two small markers distinguish
the new fast-path batch:

| Kind | Fields and interpretation |
| --- | --- |
| 35 `core_handover_root_ipv6_hello_requested` | One event per known Root for which Core requested a one-time IPv6 HELLO; `peer_id` = Root; `flags=8` = IPv6; `result=1` = requested (not proof of delivery); `endpoint` = chosen Root IPv6 address |
| 36 `binder_handover_root_ipv6_hello_batch` | Emitted once when a changed Binder global IPv6 IP triggers the batch; `byte_count` = distinct bound global IPv6 IPs; `result` = Roots for which Core requested HELLO (can be 0); `flags=8` = IPv6 |

**This is an actual, bounded network-behavior change**, unlike the previous
diagnostic-only snapshots. Its benefit and safety require fresh A/B field
measurements: compare Binder change → Root IPv6 HELLO → authenticated OK →
B new RENDEZVOUS → successful A→B HTTP using the unchanged probe. Do not
declare 2-second recovery from offline tests or successful CI alone.

### PR #6 follow-up: Preferred IPv6 and per-Root bounded retry

The first real-world PR #5 fast-HELLO batch attempted four Roots but received
four `putPacket()` rejections, yet consumed the episode's single fast-probe
opportunity. Requesting HELLO, accepting a wire send, and **authenticating its
specific OK(HELLO)** are different milestones. PR #6 fixes that boundary:

- Only **newly bound** global IPv6 IPs (not old unchanged addresses) are
  eligible. Windows `GetUnicastIpAddressTable(AF_INET6)` must report a matching
  `IpDadStatePreferred` and `SkipAsSource == false` before the first fast
  HELLO. A Tentative / Duplicate / Deprecated address, or an IP Helper query
  failure, defers the fast path rather than guessing the socket is ready.
  Short-lived local checks (up to 30 seconds after the last OS callback) also
  catch DAD becoming Preferred without another Binder refresh.
- `Peer::sendHELLO` now exposes the real `putPacket` acceptance result and
  outgoing packet ID. Core remembers up to 3 IDs per Root and stops sending
  to that Root once a **matching, authenticated, direct IPv6 OK(HELLO)**
  arrives. An accepted send alone cannot establish remote reachability.
- Per Root (maximum 8 known upstream identities), retry on the initial
  attempt and at most two bounded opportunities at +500 ms and +1500 ms
  after the first, stopping at the first matching authentication. An OS
  address change during the attempts cancels further fast sends; no global
  HELLO timer, old Root path, Node identity, Relay fallback, or Peer Dial
  capacity is changed.

| Event | Meaning |
| --- | --- |
| `binder_handover_ipv6_dad_state` (37) | `result=4`: preferred and usable source, `1`: Tentative, `2`: Duplicate, `3`: Deprecated, `5`: SkipAsSource; negative values indicate Windows IP Helper error. Only status transitions are logged |
| `core_handover_root_ipv6_hello_requested` (35) | `result=1/0`: the **actual wire callback** accepted/rejected HELLO; `reference`: packet ID; high `flags` bits encode 1/2/3 attempt |
| `binder_handover_root_ipv6_hello_batch` (36) | `result`: number of wire-accepted requests in this attempt, `flags` high bits: attempt number, not proof of receipt |
| `core_handover_root_ipv6_hello_authenticated` (38) | An authenticated direct IPv6 `OK(HELLO)` matched a sent fast-request packet ID and Root identity; this Root is no longer retried |

The existing `core_hello_sent`, surface awareness, RENDEZVOUS and HTTP probe
complete the evidence chain. This update is **Windows-specific**, keeps the
underlay trace ABI at 240 bytes, and still requires real A/B validation; the
root-level Chinese lessons report is `排障经验.md`.

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
`connection_probe.ps1` is unchanged. The Root IPv6 fast HELLO is bounded to
actual bound-address changes during Windows handover and has **not yet been
validated** for real-world recovery time or regressions.

Run C# offline checks, Python offline checks and the **Windows x64 CI build**
from the updated patches. Linux and Android workflows are not part of this PR #5
recovery verification. Trace ABI stays at version 2 (240 bytes); no
application restart, credentials reset, identity replacement or new C#
P/Invoke entrypoint is introduced. Both
`ZT_Node_revalidatePeerPathsAfterNetworkChange` and
`ZT_Node_fastProbeRootIpv6AfterNetworkChange` are internal to the
libzt↔ZeroTierOne build.
