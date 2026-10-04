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

from common import ROOT, collect_windows_evidence, kill_process_tree, start_detached, wait_for_json
from github_artifacts import wait_and_download


EVIDENCE = ROOT / "evidence" / "csharp-client"


def recv_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("unexpected EOF")
        data.extend(chunk)
    return bytes(data)


def socks_connect(
    proxy_host: str,
    proxy_port: int,
    target_host: str,
    target_port: int,
) -> socket.socket:
    sock = socket.create_connection((proxy_host, proxy_port), timeout=15)
    sock.sendall(b"\x05\x01\x00")
    if recv_exact(sock, 2) != b"\x05\x00":
        sock.close()
        raise RuntimeError("SOCKS5 method negotiation failed")

    try:
        raw_ip = socket.inet_pton(socket.AF_INET, target_host)
        request = b"\x05\x01\x00\x01" + raw_ip
    except OSError:
        try:
            raw_ip = socket.inet_pton(socket.AF_INET6, target_host)
            request = b"\x05\x01\x00\x04" + raw_ip
        except OSError:
            host = target_host.encode("idna")
            request = b"\x05\x01\x00\x03" + bytes([len(host)]) + host

    request += struct.pack("!H", target_port)
    sock.sendall(request)

    header = recv_exact(sock, 4)
    if header[0] != 5 or header[1] != 0:
        sock.close()
        raise RuntimeError(f"SOCKS5 CONNECT failed: {header.hex()}")

    if header[3] == 1:
        recv_exact(sock, 4)
    elif header[3] == 3:
        recv_exact(sock, recv_exact(sock, 1)[0])
    elif header[3] == 4:
        recv_exact(sock, 16)
    else:
        sock.close()
        raise RuntimeError(f"invalid SOCKS5 reply address type: {header[3]}")
    recv_exact(sock, 2)
    return sock


def https_public_ip_via_socks(proxy_port: int) -> str:
    raw = socks_connect("127.0.0.1", proxy_port, "api.ipify.org", 443)
    context = ssl.create_default_context()
    with context.wrap_socket(raw, server_hostname="api.ipify.org") as tls:
        tls.settimeout(20)
        tls.sendall(
            b"GET / HTTP/1.1\r\n"
            b"Host: api.ipify.org\r\n"
            b"User-Agent: libzt-netloop-csharp-ci/1\r\n"
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
        raise RuntimeError(f"ipify returned non-200 response: {header[:200]!r}")
    return body.decode("ascii").strip()


def verify_peer_local_service(proxy_port: int, peer_ip: str, service_port: int) -> None:
    with socks_connect("127.0.0.1", proxy_port, peer_ip, service_port) as sock:
        sock.settimeout(20)
        sock.sendall(
            b"GET / HTTP/1.0\r\n"
            b"Host: netloop-peer-local\r\n"
            b"Connection: close\r\n\r\n"
        )
        payload = bytearray()
        while True:
            chunk = sock.recv(65536)
            if not chunk:
                break
            payload.extend(chunk)
    if not bytes(payload).startswith(b"HTTP/1.0 200") and not bytes(payload).startswith(b"HTTP/1.1 200"):
        raise RuntimeError(f"peer local-service request failed: {payload[:200]!r}")


def start_client(
    executable: Path,
    rendezvous: dict,
    listen_port: int,
    status_path: Path,
) -> int:
    return start_detached(
        [
            str(executable),
            "--network",
            rendezvous["network_id"],
            "--state-dir",
            str(ROOT / "state" / "csharp-ci-client"),
            "--socks-host",
            "127.0.0.1",
            "--socks-port",
            str(listen_port),
            "--overlay-port",
            str(rendezvous["overlay_port"]),
            "--default-exit",
            rendezvous["server_ip"],
            "--status-file",
            str(status_path),
            "--startup-timeout",
            "180",
            "--connect-timeout",
            "20",
        ],
        EVIDENCE / "netloop_process.log",
    )


def retry(label: str, action, attempts: int = 8, delay: float = 3.0):
    last_error = None
    for attempt in range(1, attempts + 1):
        try:
            return action()
        except Exception as exc:
            last_error = exc
            with (EVIDENCE / f"{label}_attempts.log").open("a", encoding="utf-8") as log:
                log.write(f"attempt={attempt} error={exc!r}\n")
            time.sleep(delay)
    raise RuntimeError(f"{label} failed after {attempts} attempts: {last_error}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", default="runtime-csharp")
    parser.add_argument("--rendezvous-artifact", required=True)
    parser.add_argument("--listen-port", type=int, default=19080)
    args = parser.parse_args()

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    result: dict = {
        "success": False,
        "client_joined": False,
        "peer_local_service": False,
        "egress_ip_matches": False,
        "restart_recovery": False,
        "identity_preserved": False,
    }
    client_pid = 0

    try:
        rendezvous_dir = EVIDENCE / "rendezvous"
        wait_and_download(args.rendezvous_artifact, rendezvous_dir, timeout=600)
        rendezvous_files = list(rendezvous_dir.rglob("rendezvous.json"))
        if not rendezvous_files:
            raise FileNotFoundError("rendezvous artifact did not contain rendezvous.json")
        rendezvous = json.loads(rendezvous_files[0].read_text(encoding="utf-8"))
        result["rendezvous"] = rendezvous

        runtime = Path(args.runtime).resolve()
        executable = runtime / "netloop.exe"
        if not executable.exists():
            raise FileNotFoundError(executable)

        status_path = EVIDENCE / "client_status.json"
        client_pid = start_client(executable, rendezvous, args.listen_port, status_path)
        status = wait_for_json(status_path, timeout=200)
        result["client_joined"] = True
        result["client_status"] = status
        first_node_id = status.get("node_id")

        retry(
            "peer_local_service",
            lambda: verify_peer_local_service(
                args.listen_port,
                rendezvous["server_ip"],
                int(rendezvous["local_service_port"]),
            ),
        )
        result["peer_local_service"] = True

        observed_ip = retry(
            "egress",
            lambda: https_public_ip_via_socks(args.listen_port),
        )
        expected_ip = rendezvous["expected_public_ip"]
        result["observed_public_ip"] = observed_ip
        result["expected_public_ip"] = expected_ip
        if observed_ip != expected_ip:
            raise AssertionError(
                f"egress IP mismatch: through default_exit={observed_ip}, server={expected_ip}"
            )
        result["egress_ip_matches"] = True

        stale_tunnels: list[socket.socket] = []
        try:
            for _ in range(4):
                stale_tunnels.append(
                    socks_connect("127.0.0.1", args.listen_port, "api.ipify.org", 443)
                )
            kill_process_tree(client_pid)
            client_pid = 0
            time.sleep(1)
        finally:
            for stale in stale_tunnels:
                try:
                    stale.close()
                except OSError:
                    pass

        status_path.unlink(missing_ok=True)
        client_pid = start_client(executable, rendezvous, args.listen_port, status_path)
        restarted_status = wait_for_json(status_path, timeout=200)
        result["restart_status"] = restarted_status
        result["identity_preserved"] = restarted_status.get("node_id") == first_node_id
        if not result["identity_preserved"]:
            raise AssertionError(
                f"Node ID changed across restart: {first_node_id} -> {restarted_status.get('node_id')}"
            )

        restarted_ip = retry(
            "restart_egress",
            lambda: https_public_ip_via_socks(args.listen_port),
        )
        if restarted_ip != expected_ip:
            raise AssertionError(
                f"restart egress IP mismatch: through default_exit={restarted_ip}, server={expected_ip}"
            )
        result["restart_observed_public_ip"] = restarted_ip
        result["restart_recovery"] = True
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
