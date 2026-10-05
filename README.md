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

The workflows build the C# runtime and exercise TCP, UDP, peer-local routing, exit-node behavior, and runtime-reset recovery on their supported test platforms.

## Native dependency

`external/libzt` is pinned as a Git submodule. NetLoop applies repository patches under `patches/libzt` and `patches/zerotierone` before building the native P/Invoke library.

The former standalone Asio submodule and the original C++ NetLoop application have been removed.
