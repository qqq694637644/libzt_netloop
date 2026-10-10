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

Android CI also builds an x86_64-only test APK for the GitHub-hosted Android emulator. This x86_64 artifact is for CI only and is not a release target.

## Overlay addresses

Each runtime chooses one **primary overlay address** for its peer TCP/UDP listeners: the numerically lowest Managed IPv4 address is preferred; if no IPv4 address exists, the numerically lowest Managed IPv6 address is used. The selected value is published as `primary_overlay_address` in the readiness status.

`--default-exit` is required on every node and automatically becomes an effective peer when it is remote, so it must not be duplicated in `--peer`. Use repeatable `--peer` only for additional NetLoop nodes whose `primary_overlay_address` must be reachable directly. The exit node may use its own primary address as `--default-exit`; in that case NetLoop uses local egress instead of connecting to itself. Desktop SOCKS always binds to loopback; `--socks-host` is intentionally unsupported. A node may have additional Managed IPs, but NetLoop v1 ignores them for routing and only exposes services on the primary address; configuring a secondary local Managed IP as a peer or default exit is rejected at startup.

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

## Build

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

The platform CI workflows build the C# runtime and exercise TCP, UDP, peer-local routing, exit-node behavior, and runtime-reset recovery:

| Platform | Build | Runtime E2E | Reset stress |
| --- | --- | --- | --- |
| Windows x64 | yes | two GitHub-hosted runners | 10 cycles |
| Linux x64 | yes | two GitHub-hosted runners | 10 cycles |
| Linux arm64 | yes | two native arm64 GitHub-hosted runners | 10 cycles |
| Android x86_64 | CI-only APK | x86_64 emulator + Linux peer | 3 cycles |
| Android arm64-v8a | release APK | not available on hosted emulator | not run |

The reset tests use deterministic fault simulation. They prove that once a reset is triggered, the old runtime is discarded and fresh TCP/UDP traffic can recover inside the configured budget. They do **not** replace physical-device testing of Wi-Fi -> hotspot/cellular handover, where the host interface, NAT mapping, and ZeroTier physical path actually change.

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
