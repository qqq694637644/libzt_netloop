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

Each runtime chooses one **primary overlay address** for its peer TCP/UDP listeners: IPv4 is preferred when the ZeroTier network assigns one; otherwise the first Managed IPv6 address is used. The selected value is published as `primary_overlay_address` in the readiness status.

Configure `--peer` and `--default-exit` with the other node's `primary_overlay_address`. A node may have additional Managed IPs, but NetLoop v1 ignores them for routing and only exposes services on the primary address.

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
- `.github/workflows/release.yml` (tag/manual release builds)

The workflows build the C# runtime and exercise TCP, UDP, peer-local routing, exit-node behavior, and runtime-reset recovery:

| Platform | Build | Runtime E2E | Reset stress |
| --- | --- | --- | --- |
| Windows x64 | yes | two GitHub-hosted runners | 10 cycles |
| Linux x64 | yes | two GitHub-hosted runners | 10 cycles |
| Linux arm64 | yes | two native arm64 GitHub-hosted runners | 10 cycles |
| Android x86_64 | CI-only APK | x86_64 emulator + Linux peer | 3 cycles |
| Android arm64-v8a | release APK | not available on hosted emulator | not run |

The reset tests are deterministic fault injection. They prove that once a reset is triggered, the old runtime is discarded and fresh TCP/UDP traffic can recover inside the configured budget. They do **not** replace physical-device testing of Wi-Fi -> hotspot/cellular handover, where the host interface, NAT mapping, and ZeroTier physical path actually change.

## Releases

`.github/workflows/release.yml` builds these release artifacts:

- `netloop-win-x64.zip`
- `netloop-linux-x64.tar.gz`
- `netloop-linux-arm64.tar.gz`
- `netloop-android-arm64.apk`

Tags matching `vMAJOR.MINOR.PATCH` create/update the corresponding GitHub Release. Manual workflow dispatch builds the same artifacts without publishing a release.

Desktop release archives are self-contained and do not require a separately
installed .NET runtime. A release tag such as `v0.3.1` is the single version
source: Desktop `netloop --version` and the Android display version both report
`0.3.1`. Manual release builds use a development version derived from the
commit SHA.

## Native dependency

`external/libzt` is pinned as a Git submodule. NetLoop applies repository patches under `patches/libzt` and `patches/zerotierone` before building the native P/Invoke library.

The former standalone Asio submodule and the original C++ NetLoop application have been removed.
