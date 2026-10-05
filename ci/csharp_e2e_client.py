#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import sys
import time
import traceback

from common import (
    ROOT,
    build_stun_binding_request,
    collect_windows_evidence,
    kill_process_tree,
    parse_stun_public_ip,
    start_detached,
    wait_for_json,
)
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
    *,
    timeout: float = 15.0,
) -> socket.socket:
    sock = socket.create_connection((proxy_host, proxy_port), timeout=timeout)
    sock.settimeout(timeout)
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


def encode_socks_target(host: str, port: int) -> bytes:
    try:
        return b"\x01" + socket.inet_pton(socket.AF_INET, host) + struct.pack("!H", port)
    except OSError:
        pass

    try:
        return b"\x04" + socket.inet_pton(socket.AF_INET6, host) + struct.pack("!H", port)
    except OSError:
        pass

    encoded = host.encode("idna")
    if not 1 <= len(encoded) <= 255:
        raise ValueError(f"SOCKS5 domain length is invalid: {host!r}")
    return b"\x03" + bytes([len(encoded)]) + encoded + struct.pack("!H", port)


def decode_socks_endpoint(payload: bytes, offset: int = 0) -> tuple[str, int, int]:
    if offset >= len(payload):
        raise ValueError("truncated SOCKS5 address")

    address_type = payload[offset]
    offset += 1
    if address_type == 1:
        if len(payload) < offset + 4 + 2:
            raise ValueError("truncated SOCKS5 IPv4 address")
        host = socket.inet_ntop(socket.AF_INET, payload[offset : offset + 4])
        offset += 4
    elif address_type == 4:
        if len(payload) < offset + 16 + 2:
            raise ValueError("truncated SOCKS5 IPv6 address")
        host = socket.inet_ntop(socket.AF_INET6, payload[offset : offset + 16])
        offset += 16
    elif address_type == 3:
        if len(payload) < offset + 1:
            raise ValueError("truncated SOCKS5 domain length")
        length = payload[offset]
        offset += 1
        if len(payload) < offset + length + 2:
            raise ValueError("truncated SOCKS5 domain")
        host = payload[offset : offset + length].decode("ascii")
        offset += length
    else:
        raise ValueError(f"invalid SOCKS5 address type {address_type}")

    port = struct.unpack("!H", payload[offset : offset + 2])[0]
    offset += 2
    return host, port, offset


class SocksUdpAssociation:
    def __init__(
        self,
        proxy_host: str,
        proxy_port: int,
        *,
        timeout: float = 15.0,
    ):
        self.proxy_host = proxy_host
        self.proxy_port = proxy_port
        self.control = socket.create_connection(
            (proxy_host, proxy_port),
            timeout=timeout,
        )
        self.control.settimeout(timeout)
        self.udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp.bind(("127.0.0.1", 0))
        self.udp.settimeout(15)

        try:
            self.control.sendall(b"\x05\x01\x00")
            if recv_exact(self.control, 2) != b"\x05\x00":
                raise RuntimeError("SOCKS5 UDP method negotiation failed")

            # Port zero intentionally exercises the RFC 1928 first-datagram
            # source claim path used by the server-side association.
            request = b"\x05\x03\x00" + encode_socks_target("0.0.0.0", 0)
            self.control.sendall(request)

            header = recv_exact(self.control, 4)
            if header[0] != 5 or header[1] != 0:
                raise RuntimeError(f"SOCKS5 UDP ASSOCIATE failed: {header.hex()}")

            address_type = header[3]
            if address_type == 1:
                remainder = recv_exact(self.control, 6)
            elif address_type == 4:
                remainder = recv_exact(self.control, 18)
            elif address_type == 3:
                length = recv_exact(self.control, 1)[0]
                remainder = bytes([length]) + recv_exact(self.control, length + 2)
            else:
                raise RuntimeError(
                    f"invalid SOCKS5 UDP relay address type: {address_type}"
                )

            relay_host, relay_port, _ = decode_socks_endpoint(
                bytes([address_type]) + remainder
            )
            if relay_host in ("0.0.0.0", "::"):
                relay_host = proxy_host
            self.relay = (relay_host, relay_port)
        except Exception:
            self.close()
            raise

    def send(
        self,
        target_host: str,
        target_port: int,
        payload: bytes,
        *,
        frag: int = 0,
    ) -> None:
        if not 0 <= frag <= 255:
            raise ValueError("SOCKS5 UDP FRAG must fit in one byte")
        packet = b"\x00\x00" + bytes([frag]) + encode_socks_target(
            target_host, target_port
        ) + payload
        self.udp.sendto(packet, self.relay)

    def receive(self, timeout: float = 15.0) -> tuple[str, int, bytes]:
        self.udp.settimeout(timeout)
        packet, remote = self.udp.recvfrom(65535)
        if remote[0] != self.relay[0] or remote[1] != self.relay[1]:
            raise RuntimeError(
                f"SOCKS5 UDP response came from unexpected relay {remote}, "
                f"expected {self.relay}"
            )
        if len(packet) < 7 or packet[0:2] != b"\x00\x00":
            raise RuntimeError(f"invalid SOCKS5 UDP response: {packet[:20].hex()}")
        if packet[2] != 0:
            raise RuntimeError(f"unexpected SOCKS5 UDP response FRAG={packet[2]}")
        host, port, payload_offset = decode_socks_endpoint(packet, 3)
        return host, port, packet[payload_offset:]

    def roundtrip(
        self,
        target_host: str,
        target_port: int,
        payload: bytes,
        *,
        timeout: float = 15.0,
    ) -> tuple[str, int, bytes]:
        self.send(target_host, target_port, payload)
        return self.receive(timeout)

    def close_control(self) -> None:
        try:
            self.control.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.control.close()

    def close(self) -> None:
        try:
            self.control.close()
        except OSError:
            pass
        try:
            self.udp.close()
        except OSError:
            pass

    def __enter__(self) -> "SocksUdpAssociation":
        return self

    def __exit__(self, exc_type, exc_value, traceback_value) -> None:
        self.close()


def verify_peer_local_udp(proxy_port: int, peer_ip: str, service_port: int) -> None:
    payload = b"netloop-peer-udp-echo"
    with SocksUdpAssociation("127.0.0.1", proxy_port) as association:
        _, _, response = association.roundtrip(peer_ip, service_port, payload)
        if response != payload:
            raise AssertionError(
                f"peer UDP echo mismatch: expected={payload!r}, observed={response!r}"
            )


def verify_udp_frag_rejected(proxy_port: int, peer_ip: str, service_port: int) -> None:
    payload = b"netloop-frag-must-drop"
    with SocksUdpAssociation("127.0.0.1", proxy_port) as association:
        association.send(peer_ip, service_port, payload, frag=1)
        try:
            association.receive(timeout=1.0)
        except (TimeoutError, socket.timeout, ConnectionResetError, OSError):
            pass
        else:
            raise AssertionError("SOCKS5 UDP FRAG != 0 unexpectedly produced a response")

        # A malformed datagram must not poison the live association.
        _, _, response = association.roundtrip(peer_ip, service_port, payload)
        if response != payload:
            raise AssertionError("UDP association did not recover after FRAG rejection")


def udp_public_ip_via_socks(
    proxy_port: int,
    stun_host: str,
    stun_port: int,
) -> str:
    request, transaction_id = build_stun_binding_request()
    with SocksUdpAssociation("127.0.0.1", proxy_port) as association:
        _, _, response = association.roundtrip(
            stun_host,
            stun_port,
            request,
            timeout=20,
        )
    return parse_stun_public_ip(response, transaction_id)


def verify_udp_control_close_cleanup(
    proxy_port: int,
    peer_ip: str,
    service_port: int,
) -> None:
    association = SocksUdpAssociation("127.0.0.1", proxy_port)
    try:
        payload = b"netloop-control-lifetime"
        _, _, response = association.roundtrip(peer_ip, service_port, payload)
        if response != payload:
            raise AssertionError("UDP association did not work before control close")

        association.close_control()
        time.sleep(1.0)
        association.send(peer_ip, service_port, payload)
        try:
            association.receive(timeout=1.5)
        except (TimeoutError, socket.timeout, ConnectionResetError, OSError):
            return
        raise AssertionError("UDP association still relayed traffic after control TCP closed")
    finally:
        association.close()


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


def verify_peer_local_service(
    proxy_port: int,
    peer_ip: str,
    service_port: int,
    *,
    timeout: float = 20.0,
) -> None:
    with socks_connect(
        "127.0.0.1",
        proxy_port,
        peer_ip,
        service_port,
        timeout=timeout,
    ) as sock:
        sock.settimeout(timeout)
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
    reset_command_path: Path | None = None,
) -> int:
    command = [
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
        "--overlay-udp-port",
        str(rendezvous["overlay_udp_port"]),
        "--default-exit",
        rendezvous["server_ip"],
        "--status-file",
        str(status_path),
        "--startup-timeout",
        "180",
        "--connect-timeout",
        "20",
    ]
    if reset_command_path is not None:
        command.extend(
            [
                "--reset-command-file",
                str(reset_command_path),
                "--reset-debounce-ms",
                "250",
            ]
        )

    return start_detached(command, EVIDENCE / "netloop_process.log")


def write_json_atomic(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(payload), encoding="utf-8")
    last_error: PermissionError | None = None
    for _ in range(50):
        try:
            os.replace(temp, path)
            return
        except PermissionError as exc:
            last_error = exc
            time.sleep(0.02)
    raise last_error or PermissionError(f"unable to replace {path}")


def wait_for_reset_ready(
    status_path: Path,
    *,
    expected_process_id: int,
    expected_reset_count: int,
    expected_node_id: str,
    timeout: float,
) -> dict:
    deadline = time.monotonic() + timeout
    last: dict | None = None
    while time.monotonic() < deadline:
        try:
            if status_path.exists():
                last = json.loads(status_path.read_text(encoding="utf-8"))
                if (
                    last.get("phase") == "ready"
                    and int(last.get("reset_count", -1)) == expected_reset_count
                    and int(last.get("process_id", 0)) == expected_process_id
                ):
                    if last.get("node_id") != expected_node_id:
                        raise AssertionError(
                            "Node ID changed across runtime reset: "
                            f"{expected_node_id} -> {last.get('node_id')}"
                        )
                    return last
        except (json.JSONDecodeError, PermissionError, OSError, ValueError, TypeError):
            pass
        time.sleep(0.02)

    raise TimeoutError(
        "runtime reset did not become ready in time; "
        f"expected_reset_count={expected_reset_count}, last={last}"
    )


def run_reset_stress(
    proxy_port: int,
    rendezvous: dict,
    status_path: Path,
    reset_command_path: Path,
    initial_status: dict,
    cycles: int,
) -> dict:
    current = initial_status
    node_id = str(current["node_id"])
    samples_ms: list[float] = []

    for iteration in range(cycles):
        process_id = int(current["process_id"])
        expected_reset_count = int(current.get("reset_count", 0)) + 1

        stale = socket.create_connection(
            ("127.0.0.1", proxy_port),
            timeout=1.0,
        )
        stale.settimeout(1.0)
        started = time.monotonic()
        deadline = started + 3.0

        try:
            write_json_atomic(
                reset_command_path,
                {"Id": expected_reset_count, "Command": "reset"},
            )

            # The old runtime is disposable by design. Its active local TCP
            # connection must be force-closed before we count any new traffic
            # as recovered.
            try:
                closed = stale.recv(1)
                if closed:
                    raise AssertionError(
                        "stale local SOCKS connection produced data during reset"
                    )
            except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
                pass
            except socket.timeout as exc:
                raise AssertionError(
                    "stale local SOCKS connection was not discarded within 1s"
                ) from exc
        finally:
            stale.close()

        last_tcp_error: Exception | None = None
        while time.monotonic() < deadline:
            remaining = deadline - time.monotonic()
            try:
                verify_peer_local_service(
                    proxy_port,
                    rendezvous["server_ip"],
                    int(rendezvous["local_service_port"]),
                    timeout=max(0.1, remaining),
                )
                last_tcp_error = None
                break
            except Exception as exc:
                last_tcp_error = exc
                if time.monotonic() >= deadline:
                    break
                time.sleep(min(0.05, max(0.0, deadline - time.monotonic())))
        if last_tcp_error is not None:
            raise AssertionError(
                f"fresh TCP did not recover within 3s: {last_tcp_error}"
            ) from last_tcp_error

        payload = f"netloop-reset-{iteration}".encode("ascii")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise AssertionError(
                f"runtime reset {iteration} exhausted 3s before fresh UDP"
            )
        with SocksUdpAssociation(
            "127.0.0.1",
            proxy_port,
            timeout=max(0.1, remaining),
        ) as association:
            _, _, response = association.roundtrip(
                rendezvous["server_ip"],
                int(rendezvous["local_udp_service_port"]),
                payload,
                timeout=max(0.1, deadline - time.monotonic()),
            )
        if response != payload:
            raise AssertionError(
                f"fresh UDP after reset {iteration} mismatched: {response!r}"
            )

        elapsed_ms = (time.monotonic() - started) * 1000.0
        samples_ms.append(elapsed_ms)
        if elapsed_ms > 3000:
            raise AssertionError(
                f"runtime reset {iteration} exceeded 3000ms: {elapsed_ms:.1f}ms"
            )

        # Status is diagnostic/control-plane evidence only. It must confirm
        # identity and reset_count, but file polling latency is intentionally
        # excluded from the user-visible recovery budget above.
        current = wait_for_reset_ready(
            status_path,
            expected_process_id=process_id,
            expected_reset_count=expected_reset_count,
            expected_node_id=node_id,
            timeout=5.0,
        )

    return {
        "cycles": cycles,
        "samples_ms": samples_ms,
        "max_ms": max(samples_ms) if samples_ms else 0.0,
        "final_reset_count": int(current.get("reset_count", 0)),
        "final_process_id": int(current.get("process_id", 0)),
    }


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
    parser.add_argument("--reset-stress", action="store_true")
    parser.add_argument("--reset-cycles", type=int, default=10)
    args = parser.parse_args()

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    result: dict = {
        "success": False,
        "client_joined": False,
        "peer_local_service": False,
        "peer_local_udp": False,
        "udp_frag_rejected": False,
        "udp_default_exit_matches": False,
        "udp_control_close_cleanup": False,
        "egress_ip_matches": False,
        "restart_recovery": False,
        "identity_preserved": False,
        "reset_stress": False,
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
        reset_command_path = EVIDENCE / "reset_command.json"
        reset_command_path.unlink(missing_ok=True)
        client_pid = start_client(
            executable,
            rendezvous,
            args.listen_port,
            status_path,
            reset_command_path if args.reset_stress else None,
        )
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

        retry(
            "peer_local_udp",
            lambda: verify_peer_local_udp(
                args.listen_port,
                rendezvous["server_ip"],
                int(rendezvous["local_udp_service_port"]),
            ),
        )
        result["peer_local_udp"] = True

        retry(
            "udp_frag_rejected",
            lambda: verify_udp_frag_rejected(
                args.listen_port,
                rendezvous["server_ip"],
                int(rendezvous["local_udp_service_port"]),
            ),
            attempts=3,
            delay=1.0,
        )
        result["udp_frag_rejected"] = True

        observed_udp_ip = retry(
            "udp_default_exit",
            lambda: udp_public_ip_via_socks(
                args.listen_port,
                rendezvous["stun_host"],
                int(rendezvous["stun_port"]),
            ),
            attempts=4,
            delay=2.0,
        )
        expected_udp_ip = rendezvous["expected_udp_public_ip"]
        result["observed_udp_public_ip"] = observed_udp_ip
        result["expected_udp_public_ip"] = expected_udp_ip
        if observed_udp_ip != expected_udp_ip:
            raise AssertionError(
                "UDP default_exit public IP mismatch: "
                f"through default_exit={observed_udp_ip}, server={expected_udp_ip}"
            )
        result["udp_default_exit_matches"] = True

        retry(
            "udp_control_close_cleanup",
            lambda: verify_udp_control_close_cleanup(
                args.listen_port,
                rendezvous["server_ip"],
                int(rendezvous["local_udp_service_port"]),
            ),
            attempts=3,
            delay=1.0,
        )
        result["udp_control_close_cleanup"] = True

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

        if args.reset_stress:
            result["reset_metrics"] = run_reset_stress(
                args.listen_port,
                rendezvous,
                status_path,
                reset_command_path,
                status,
                args.reset_cycles,
            )
            result["reset_stress"] = True
            result["identity_preserved"] = True
            result["restart_recovery"] = True
            result["success"] = True
            print(json.dumps(result, indent=2))
            return 0

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
        try:
            if (EVIDENCE / "client_status.json").exists():
                current_status = json.loads(
                    (EVIDENCE / "client_status.json").read_text(encoding="utf-8")
                )
                current_pid = int(current_status.get("process_id", 0))
                if current_pid and current_pid != client_pid:
                    kill_process_tree(current_pid)
        except (json.JSONDecodeError, OSError, ValueError, TypeError):
            pass
        collect_windows_evidence(EVIDENCE / "network")
        (EVIDENCE / "result.json").write_text(
            json.dumps(result, indent=2), encoding="utf-8"
        )


if __name__ == "__main__":
    raise SystemExit(main())
