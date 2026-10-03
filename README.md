# libzt_netloop

Windows-only lightweight TCP port bridge over [ZeroTier Sockets (\`libzt\`)](https://github.com/zerotier/libzt).

The intended topology is deliberately small:

\`\`\`text
laptop application
    |
    | SOCKS5 bytes (not parsed by netloop)
    v
127.0.0.1:1080
    |
zt_netloop_client.exe
    |
    | libzt / ZeroTier P2P
    v
zt_netloop_server.exe
    |
    v
127.0.0.1:10808
    |
v2rayN SOCKS5 server
\`\`\`

\`zt_netloop\` does not implement SOCKS5. It transports the TCP byte stream unchanged.
It does not install a TUN/TAP adapter, modify the Windows routing table, or run a system-wide VPN.

## Build

Requirements:

- Windows x64
- Visual Studio C++ toolchain supported by the installed CMake
- CMake
- Python 3.11+
- Git

All build orchestration is Python:

\`\`\`console
python tools/build.py --config Release
\`\`\`

The executables are staged in \`dist/\`.

## Server

Example with a normal ZeroTier network where the server has address \`10.10.10.2\`:

\`\`\`console
zt_netloop_server.exe ^
  --network 0123456789abcdef ^
  --state-dir state\\server ^
  --bind-host 10.10.10.2 ^
  --zt-port 42042 ^
  --forward-host 127.0.0.1 ^
  --forward-port 10808 ^
  --status-file status-server.json ^
  --log-file server.log
\`\`\`

If \`--bind-host\` is omitted, the server uses its assigned IPv4 address first and IPv6 otherwise.

## Client

\`\`\`console
zt_netloop_client.exe ^
  --network 0123456789abcdef ^
  --state-dir state\\client ^
  --remote-host 10.10.10.2 ^
  --remote-port 42042 ^
  --listen-host 127.0.0.1 ^
  --listen-port 1080 ^
  --status-file status-client.json ^
  --log-file client.log
\`\`\`

Point the laptop application at \`127.0.0.1:1080\` as SOCKS5. The SOCKS5 handshake and
subsequent traffic are transported to the v2rayN SOCKS5 listener unchanged.

During ZeroTier network join, the process logs the actual libzt network status every five seconds. Failures such as `ACCESS_DENIED`, `NOT_FOUND`, `PORT_ERROR`, and `CLIENT_TOO_OLD` fail immediately. A timeout includes the network status plus whether IPv4/IPv6 addresses were actually assigned inside libzt.

## CI

\`.github/workflows/windows-ci.yml\` has three Windows jobs:

1. Build and smoke-test both executables.
2. Machine A starts the server and a Python SOCKS5 fixture.
3. Machine B starts the client and performs a real SOCKS5 HTTPS request through the tunnel.

The two E2E jobs are separate GitHub-hosted Windows machines. For CI only, they use a
controller-less ZeroTier ad-hoc IPv6 network, so no ZeroTier Central API token is required.

The egress test compares the public IP observed through:

\`\`\`text
machine B -> client -> libzt -> machine A -> Python SOCKS5 -> api.ipify.org
\`\`\`

with machine A's directly observed public IP.

Build logs, process logs, status JSON, test results, \`ipconfig\`, route table, \`netstat\`,
and IPv6-interface information are uploaded as evidence artifacts. Evidence is uploaded
with \`if: always()\` so a failed join/tunnel/egress test keeps the material needed to diagnose it.

## Release

Run the `Release Windows EXEs` workflow manually and provide:

- `branch`: the branch or ref to build, for example `main`.
- `version`: the release version, for example `0.2.0` or `v0.2.0`.

The workflow builds Windows x64 and publishes exactly two release assets:

```text
zt_netloop_client.exe
zt_netloop_server.exe
```

No ZIP archive is created. The version is compiled into both executables, so `--version` reports the selected release version.

## Dependency

\`external/libzt\` is pinned as a Git submodule. This project currently tracks libzt commit
\`a707ea6ae0910efdc1125d04758c411e2e9ea4f9\`.
