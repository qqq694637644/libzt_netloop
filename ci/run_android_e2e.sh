#!/usr/bin/env bash
set -euo pipefail

cleanup() {
  python ci/csharp_e2e_server.py cleanup || true
}
trap cleanup EXIT

python ci/csharp_e2e_server.py prepare \
  --runtime runtime-csharp \
  --overlay-port 42242 \
  --overlay-udp-port 42243

python ci/android_e2e_client.py \
  --apk package/netloop-android-x64-ci.apk \
  --rendezvous-file evidence/csharp-server/rendezvous.json \
  --reset-cycles 3
