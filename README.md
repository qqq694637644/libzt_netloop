# libzt_netloop

NetLoop is a C# cross-platform SOCKS5 mesh/exit-node application built on top of ZeroTier Sockets (`libzt`).

The repository no longer contains the original NetLoop C++ client/server implementation. The only native C/C++ code built by this repository is the third-party `external/libzt` dependency and the small patches required to expose the P/Invoke runtime used by the C# applications.

## Projects

- `src-csharp/NetLoop.Core` — routing, relay, and shared runtime abstractions.
- `src-csharp/NetLoop.Socks` — SOCKS5 TCP/UDP protocol handling.
- `src-csharp/NetLoop.Libzt` — P/Invoke bindings and libzt socket wrappers.
- `src-csharp/NetLoop.Host` — Windows/Linux NetLoop host.
- `src-csharp/NetLoop.Android` — Android foreground-service application.

## Supported targets

- Windows x64
- Linux x64
- Linux arm64
- Android arm64-v8a

Android CI builds the arm64-v8a application. Automated Android emulator E2E
jobs are currently disabled.

## Overlay addresses

Each runtime chooses one **primary overlay address** for its peer TCP/UDP listeners: the numerically lowest Managed IPv4 address is preferred; if no IPv4 address exists, the numerically lowest Managed IPv6 address is used. The selected value is published as `primary_overlay_address` in the readiness status.

`--default-exit` is required on every node and selects the exit used for non-overlay destinations; it **does not override ZeroTier's direct-peer routing**. Every direct ZeroTier peer is identified exclusively by matching the Controller-assigned address prefixes and Managed Routes, never by hardcoding a network such as `172.26.0.0/24` or maintaining a manually entered peer list. **The `--peer` CLI argument has been removed: old command lines that include it now fail to start.** The exit node may use its own primary address as `--default-exit`; in that case NetLoop uses local egress instead of connecting to itself.

Controller **direct** routes (no `via` gateway) allow direct peer Agent forwarding. More-specific routes with a `via` gateway are **rejected explicitly**: the NetLoop Agent SOCKS5 protocol does not implement ZeroTier gateway/multihop routing. ZeroTier `/0` default routes are not treated as Overlay Peer networks; ordinary internet traffic still uses the configured NetLoop default exit. Missing Controller prefixes are never guessed. This behavior includes IPv6. Desktop SOCKS always binds to loopback; `--socks-host` is intentionally unsupported.

Only the current primary Managed IP is bound for new NetLoop TCP/UDP Agent connections. If the Controller revokes that address and grants a replacement, the runtime binds replacement Agent listeners first, retires the old TCP acceptor and **drains its established TCP sessions** without restarting libzt. Existing UDP associations on the revoked address are closed; old TCP sessions can be aborted when the 16-retired-listener safety limit is exceeded. **Route-only** configuration updates do not interrupt existing TCP. See [dynamic ZeroTier routing](docs/zerotier-network-routes.md).

## Peer P2P / relay diagnostics

NetLoop writes a `libzt_peer_status` JSON log entry whenever libzt reports a
peer transport/path event (codes 240–244). The entry includes:

- `peer_id`: the remote **ZeroTier node ID** (not its Managed IP)
- `transport`: `DIRECT`, `RELAY`, `UNREACHABLE`, or `UNKNOWN`
- `path_count`: number of known physical paths at the event
- `physical_paths`: IP:port endpoints, preferred/expired flags and latency
- `change`: the underlying libzt event, e.g. `PEER_PATH_DEAD`

To identify the A→B link, compare `peer_id` in A's log with `node_id` in
B's `server-status.json` (or B's `libzt_network_ready` log). **Do not** infer
the physical P2P state from `route: "OverlayPeer"`: that is the logical
NetLoop route, not ZeroTier's direct/relay underlay.

Example filter on Windows PowerShell:

```powershell
Select-String -Path .\client.log -Pattern '"event":"libzt_peer_status"' |
  ForEach-Object { ($_.Line | ConvertFrom-Json).data } |
  Select-Object peer_id,transport,path_count,physical_paths
```

These are **event-time snapshots**, not continuous probes of all peers; an old
`DIRECT` event does not prove a connection is *still* direct after a network
change. ZeroTier root/controller peers can also appear in the log, so always
match the node ID. The peer diagnostics require the updated `libzt.dll` (or
`libzt.so`) built with the NetLoop peer-event ABI patch, not just an updated
 managed DLL.

Dynamic ZeroTier routing additionally requires the matching native
`0004-network-cidr-queries.patch`: **deploy both** the new NetLoop .NET
assemblies and the rebuilt native libzt library on every node.

Peer TCP connects are initiated with nonblocking `zts_bsd_connect` and
completed through `poll(POLLOUT)` plus socket `SO_ERROR`. NetLoop cancels and
retries each pending socket within its bounded recovery window, rather than
waiting in libzt's blocking `zts_connect` helper. To diagnose failures,
inspect `libzt_connect_retry` (including `elapsed_ms`),
`libzt_connect_cancelled`, and `socks_peer_negotiation_retry` in the A-node
log. When troubleshooting identity mismatches, compare both nodes'
`status-file` `node_id` values to the actual authorized members in the
ZeroTier controller; Managed IP alone does not identify a peer.

### TCP connect / accept correlation

The host logs one `tcp_connect_start` per native outgoing socket attempt,
followed by `tcp_connect_completed`, `tcp_connect_timeout`, or
`tcp_connect_failed`. Each has a **unique** `attempt_id`, plus:

- `connect_id`: one invocation of the libzt TCP connector, possibly with
  multiple native socket attempts
- `peer_attempt_id`: the enclosing SOCKS5 peer-negotiation retry iteration
- `connection_id`: the local incoming SOCKS5 client's request; this also
  appears on `socks_connect_request`, `socks_connect_failed`, and
  `socks_peer_negotiation_retry`
- `fd`: the temporary native socket descriptor, **not** a unique identifier
- `local`: the outgoing ZeroTier IP:port when libzt can report it
- `socket_error` and `socket_error_source`: SO_ERROR observed during polling
  or sampled only **after** an attempt is abandoned; zero at cancellation
  does not prove a TCP handshake completed

On the exit agent, `tcp_accept_completed` is emitted immediately after
`zts_accept` returns. Its `connection_id` is reused in the later
`socks_connect_request` once the SOCKS5 request is fully parsed; failures
before parsing appear as `overlay_socks_client_failed`, and connection
capacity rejection as `tcp_accept_rejected`.

IDs are process-local and **not sent on the wire**. To compare the two nodes,
match A's `local` endpoint with B's `tcp_accept_completed.data.remote`
(and A's `target` with B's listening address) in the same UTC interval;
account for clock skew and port reuse. A timeout with no matching B accept
does not by itself prove whether ZeroTier TCP SYN packets arrived.
See [correlation examples](docs/connection-probe.md#tcp-%E5%BB%BA%E8%BF%9E%E4%B8%8E-agent-accept-%E6%97%A5%E5%BF%97%E5%85%B3%E8%81%94).

## Continuous A/C-to-B connection probe (Windows)

Run the independent PowerShell probe **on A and/or C**, with NetLoop left
running, to record actual SOCKS5 -> B:8080 connectivity in UTC. It does not
modify the ZeroTier node, reset the runtime, or depend on ZeroTier's
`NODE_ONLINE`/`NODE_OFFLINE` status:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\connection_probe.ps1 -Node A
# Run on C, in its own PowerShell window:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\connection_probe.ps1 -Node C
```

By default the tool uses `127.0.0.1:1080`, tests
`http://172.26.0.254:8080/`, limits each curl request to 12 seconds, waits
20 seconds between requests, and writes a *new* UTC-stamped
`evidence/connection-probe-<node>-<timestamp>.jsonl` in the current
directory. Use `-IntervalSeconds 2` for finer-grained handover observation,
`-Count 3` for a short run, or `-LogPath` for an explicit output file.
Ctrl+C stops the probe. Any HTTP response (including 404/500) means the
SOCKS5 transport reached an HTTP server; exit code 97 or HTTP 000 indicates
failure. Keep the NetLoop logs for correlation and **do not delete identity
or state directories**. See [the Chinese probe guide](docs/connection-probe.md).

## Build

### Long-running reliability (PR #3, based on PR #2)

The runtime now limits the incoming SOCKS5 handshake (default 15 seconds)
and the upstream CONNECT reply / UDP ASSOCIATE setup (default 25 seconds).
Unexpected termination of local SOCKS, Overlay TCP, or Overlay UDP listener
tasks triggers business-runtime recovery. Transient runtime initialization
failures are retried at most 12 times with capped backoff; the ZeroTier Node
and identity remain intact.

Windows/Linux hosts can optionally rotate JSONL logs instead of relying on
unbounded shell redirection. For example:

```powershell
netloop.exe --network <network-id> --state-dir .\state `
  --default-exit <zt-managed-ip> `
  --status-file .\netloop-status.json `
  --log-file .\logs\netloop.jsonl --log-max-mb 16 --log-retain 5
```

Logging uses a bounded asynchronous queue. If overloaded, look for
`json_log_records_dropped`. See the
[long-running reliability guide](docs/reliability-hardening.md) for
recovery limitations, event names, and parameter details.

Further PR #3 hardening includes 30-second native Controller config
revalidation (new routing rejects requests while configuration is missing),
15-second health/status-file updates, opt-in inbound Agent source-IP/CIDR and
destination-port ACLs, and resource-budget/UDP queue-drop counters.
`--libzt-poll-mode shared` is an **experimental opt-in** batched socket
poller; the original `per-socket` mode remains the default until real-device
connection-lifetime testing is available.

For **manual**, offline Windows application upgrades/rollback,
DPAPI-encrypted identity backups and JSONL probe statistics, see the
[operations guide](docs/long-term-operations.md). This project intentionally
does **not** install a watchdog, Windows service, process auto-restart or
scheduled background task.

Desktop builds are orchestrated by:

```console
python tools/build_csharp.py --help
```

Android native libzt builds are orchestrated by:

```console
python tools/build_android_native.py --help
```

The top-level `CMakeLists.txt` exists only to build the shared libzt P/Invoke runtime required by the C# code. It does not build a NetLoop C++ executable.

## CI

Active GitHub Actions workflows:

- `.github/workflows/csharp-windows-ci.yml`
- `.github/workflows/csharp-linux-ci.yml`
- `.github/workflows/csharp-android-ci.yml`
- `.github/workflows/release.yml` (manual Windows/Android release publishing)

The platform CI workflows currently **build only**. They do not run smoke
tests, network E2E, or reset-stress jobs:

| Platform | Build | Runtime E2E |
| --- | --- | --- |
| Windows x64 | yes | not run |
| Linux x64 | yes | not run |
| Linux arm64 | yes | not run |
| Android arm64-v8a | yes | not run |

Connection recovery on physical-device Wi-Fi/hotspot/cellular handovers is
currently observed with the independent
[connection probe](docs/connection-probe.md), not asserted by CI.

## Releases

Run `.github/workflows/release.yml` from **Actions -> C# Windows and Android Release -> Run workflow**.
Provide a `version` (for example, `1.2.3` or `v1.2.3`) and choose `publish_windows`
and/or `publish_android`. At least one platform must be selected. The workflow
creates or updates the `vMAJOR.MINOR.PATCH` GitHub Release and uploads only the
selected assets:

- `netloop-win-x64.zip`
- `netloop-dotnet-runtime-win-x64.zip`
- `netloop-android-arm64.apk`

The Windows release uses **one self-contained .NET publish**, split into two zips:

- `netloop-win-x64.zip`: NetLoop's own EXE and DLL files only
- `netloop-dotnet-runtime-win-x64.zip`: .NET runtime files and the required
  `netloop.deps.json` / `netloop.runtimeconfig.json` files

Extract **both archives into the same directory**. No separate .NET installation,
runtime folder, or environment variable is required:

```powershell
Expand-Archive .\netloop-dotnet-runtime-win-x64.zip .\netloop
Expand-Archive .\netloop-win-x64.zip .\netloop -Force
.\netloop\netloop.exe --version
```

For code-only updates, overwrite the EXE/DLL files from the app zip. Update the
runtime zip too when the .NET runtime or application dependencies change.

A release version such as `1.2.3` is shown by Windows `netloop --version`
and as the Android display version. Android `versionCode` is derived from the
numeric version and must fit Android's supported range.

## Native dependency

`external/libzt` is pinned as a Git submodule. NetLoop applies repository patches under `patches/libzt` and `patches/zerotierone` before building the native P/Invoke library.

The former standalone Asio submodule and the original C++ NetLoop application have been removed.
