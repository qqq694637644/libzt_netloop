#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
from pathlib import Path
import socket
import ssl
import struct
import sys
import time
import traceback

from common import (
    ROOT,
    collect_windows_evidence,
    kill_process_tree,
    start_detached,
    wait_for_json,
)
from github_artifacts import wait_and_download


EVIDENCE = ROOT / "evidence" / "client"


def recv_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("unexpected EOF")
        data.extend(chunk)
    return bytes(data)


def socks_connect(
    proxy_host: str, proxy_port: int, target_host: str, target_port: int
) -> socket.socket:
    sock = socket.create_connection((proxy_host, proxy_port), timeout=15)
    sock.sendall(b"\x05\x01\x00")
    if recv_exact(sock, 2) != b"\x05\x00":
        raise RuntimeError("SOCKS5 method negotiation failed")

    host = target_host.encode("idna")
    request = (
        b"\x05\x01\x00\x03"
        + bytes([len(host)])
        + host
        + struct.pack("!H", target_port)
    )
    sock.sendall(request)
    header = recv_exact(sock, 4)
    if header[0] != 5 or header[1] != 0:
        raise RuntimeError(f"SOCKS5 CONNECT failed: {header.hex()}")

    address_type = header[3]
    if address_type == 1:
        recv_exact(sock, 4)
    elif address_type == 3:
        recv_exact(sock, recv_exact(sock, 1)[0])
    elif address_type == 4:
        recv_exact(sock, 16)
    else:
        raise RuntimeError(
            f"invalid SOCKS5 reply address type: {address_type}"
        )
    recv_exact(sock, 2)
    return sock


def https_public_ip_via_socks(proxy_host: str, proxy_port: int) -> str:
    raw = socks_connect(proxy_host, proxy_port, "api.ipify.org", 443)
    context = ssl.create_default_context()
    with context.wrap_socket(raw, server_hostname="api.ipify.org") as tls:
        tls.settimeout(20)
        tls.sendall(
            b"GET / HTTP/1.1\r\n"
            b"Host: api.ipify.org\r\n"
            b"User-Agent: libzt-netloop-ci/1\r\n"
            b"Connection: close\r\n\r\n"
        )
        payload = bytearray()
        while True:
            chunk = tls.recv(65536)
            if not chunk:
                break
            payload.extend(chunk)

    header, body = bytes(payload).split(b"\r\n\r\n", 1)
    if b" 200 " not in header.split(b"\r\n", 1)[0]:
        raise RuntimeError(
            f"ipify returned non-200 response: {header[:200]!r}"
        )
    return body.decode("ascii").strip()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", default="runtime")
    parser.add_argument("--rendezvous-artifact", required=True)
    parser.add_argument("--listen-port", type=int, default=19080)
    args = parser.parse_args()

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    result: dict = {
        "success": False,
        "client_joined": False,
        "socks_handshake": False,
        "egress_ip_matches": False,
    }
    client_pid = 0

    try:
        rendezvous_dir = EVIDENCE / "rendezvous"
        wait_and_download(
            args.rendezvous_artifact, rendezvous_dir, timeout=600
        )
        rendezvous_files = list(rendezvous_dir.rglob("rendezvous.json"))
        if not rendezvous_files:
            raise FileNotFoundError(
                "rendezvous artifact did not contain rendezvous.json"
            )
        rendezvous = json.loads(
            rendezvous_files[0].read_text(encoding="utf-8")
        )
        result["rendezvous"] = rendezvous

        runtime = Path(args.runtime).resolve()
        client_exe = runtime / "zt_netloop_client.exe"
        if not client_exe.exists():
            raise FileNotFoundError(client_exe)

        status_path = EVIDENCE / "client_status.json"
        client_pid = start_detached(
            [
                str(client_exe),
                "--network",
                rendezvous["network_id"],
                "--state-dir",
                str(ROOT / "state" / "ci-client"),
                "--remote-host",
                rendezvous["server_ip"],
                "--remote-port",
                str(rendezvous["zt_port"]),
                "--listen-host",
                "127.0.0.1",
                "--listen-port",
                str(args.listen_port),
                "--status-file",
                str(status_path),
                "--log-file",
                str(EVIDENCE / "client.log"),
                "--timeout",
                "180",
            ],
            EVIDENCE / "client_process.log",
        )

        status = wait_for_json(status_path, timeout=200)
        result["client_joined"] = True
        result["client_status"] = status

        observed_ip: str | None = None
        last_error: Exception | None = None
        for attempt in range(1, 9):
            try:
                observed_ip = https_public_ip_via_socks(
                    "127.0.0.1", args.listen_port
                )
                result["socks_handshake"] = True
                break
            except Exception as exc:
                last_error = exc
                with (EVIDENCE / "connection_attempts.log").open(
                    "a", encoding="utf-8"
                ) as log:
                    log.write(f"attempt={attempt} error={exc!r}\n")
                time.sleep(3)

        if observed_ip is None:
            raise RuntimeError(
                f"tunnel/SOCKS test did not succeed: {last_error}"
            )

        expected_ip = rendezvous["expected_public_ip"]
        result["observed_public_ip"] = observed_ip
        result["expected_public_ip"] = expected_ip
        result["egress_ip_matches"] = observed_ip == expected_ip

        if observed_ip != expected_ip:
            raise AssertionError(
                f"egress IP mismatch: through tunnel={observed_ip}, "
                f"server={expected_ip}"
            )

        result["success"] = True
        print(json.dumps(result, indent=2))
        return 0
    except Exception as exc:
        result["error_type"] = type(exc).__name__
        result["error"] = str(exc)
        result["traceback"] = traceback.format_exc()
        print(traceback.format_exc(), file=sys.stderr)
        return 1
    finally:
        if client_pid:
            kill_process_tree(client_pid)
        collect_windows_evidence(EVIDENCE / "network")
        (EVIDENCE / "result.json").write_text(
            json.dumps(result, indent=2), encoding="utf-8"
        )


if __name__ == "__main__":
    raise SystemExit(main())
