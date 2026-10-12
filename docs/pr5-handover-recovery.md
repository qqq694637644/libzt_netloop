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

The episode-only update was requested for Windows PR #5 with **no tests run**;
the real recovery effect and repeated-reset suppression are not yet measured
on A/B machines. The existing `connection_probe.ps1` is unchanged, and this
step does not add Peer Dial single-flight or change ZeroTierOne path policy.

Run C# offline checks, Python offline checks and the **Windows x64 CI build**
from the updated patches. Linux and Android workflows are not part of this PR #5
recovery verification. Trace ABI stays at version 2 (240 bytes); no
application restart, credentials reset, identity replacement or new native
P/Invoke entrypoint is introduced. `ZT_Node_revalidatePeerPathsAfterNetworkChange`
is internal to the libzt↔ZeroTierOne build.
