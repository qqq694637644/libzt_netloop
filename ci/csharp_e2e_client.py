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
import threading
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
    try:
        sock.sendall(b"\x05\x01\x00")
        if recv_exact(sock, 2) != b"\x05\x00":
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
            raise RuntimeError(f"SOCKS5 CONNECT failed: {header.hex()}")

        if header[3] == 1:
            recv_exact(sock, 4)
        elif header[3] == 3:
            recv_exact(sock, recv_exact(sock, 1)[0])
        elif header[3] == 4:
            recv_exact(sock, 16)
        else:
            raise RuntimeError(f"invalid SOCKS5 reply address type: {header[3]}")
        recv_exact(sock, 2)
        return sock
    except Exception:
        sock.close()
        raise


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


RESET_BUDGET_SECONDS = 3.0
RESET_RETRY_INTERVAL_SECONDS = 0.05
RESET_UDP_RESPONSE_SLICE_SECONDS = 0.20
RESET_INTER_CYCLE_SETTLE_SECONDS = 1.0
RESET_PRECONDITION_TIMEOUT_SECONDS = 5.0


def nearest_rank_percentile(values: list[float], percentile: int) -> float | None:
    if not values:
        return None
    if not 1 <= percentile <= 100:
        raise ValueError(f"invalid percentile {percentile}")
    ordered = sorted(values)
    rank = ((len(ordered) * percentile) + 99) // 100
    return ordered[max(0, rank - 1)]


def format_exception(exc: BaseException) -> str:
    return f"{type(exc).__name__}: {exc}"


def open_negotiated_socks_sentinel(
    proxy_port: int,
    *,
    timeout: float = RESET_PRECONDITION_TIMEOUT_SECONDS,
) -> socket.socket:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None

    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        attempt_timeout = min(1.0, max(0.1, remaining))
        sock: socket.socket | None = None
        try:
            sock = socket.create_connection(
                ("127.0.0.1", proxy_port),
                timeout=attempt_timeout,
            )
            sock.settimeout(attempt_timeout)
            sock.sendall(b"\x05\x01\x00")
            if recv_exact(sock, 2) != b"\x05\x00":
                raise RuntimeError(
                    "stale SOCKS sentinel did not complete method negotiation"
                )
            sock.settimeout(0.05)
            return sock
        except Exception as exc:
            last_error = exc
            if sock is not None:
                sock.close()

        sleep_for = min(
            RESET_RETRY_INTERVAL_SECONDS,
            max(0.0, deadline - time.monotonic()),
        )
        if sleep_for > 0:
            time.sleep(sleep_for)

    raise TimeoutError(
        "local SOCKS precondition did not become healthy before reset"
    ) from last_error


def wait_for_old_runtime_abort(
    *,
    deadline: float,
    old_runtime_closed: threading.Event,
) -> bool:
    remaining = deadline - time.monotonic()
    return remaining > 0 and old_runtime_closed.wait(remaining)


def write_reset_timing_matrix(rows: list[dict[str, object]]) -> None:
    header = [
        "cycle",
        "old_close_ms",
        "tcp_ms",
        "udp_ms",
        "tcp_attempts",
        "udp_assoc_attempts",
        "udp_attempts",
        "status_ms",
        "result",
        "errors",
    ]
    lines = ["\t".join(header)]
    for row in rows:
        errors = row.get("errors") or []
        lines.append(
            "\t".join(
                [
                    str(row["cycle"]),
                    "" if row.get("old_close_ms") is None else f"{row['old_close_ms']:.1f}",
                    "" if row.get("tcp_ms") is None else f"{row['tcp_ms']:.1f}",
                    "" if row.get("udp_ms") is None else f"{row['udp_ms']:.1f}",
                    str(row.get("tcp_attempts", 0)),
                    str(row.get("udp_assoc_attempts", 0)),
                    str(row.get("udp_attempts", 0)),
                    "" if row.get("status_ms") is None else f"{row['status_ms']:.1f}",
                    "PASS" if row.get("passed") else "FAIL",
                    " | ".join(str(value) for value in errors),
                ]
            )
        )

    payload = "\n".join(lines) + "\n"
    (EVIDENCE / "reset_timing.tsv").write_text(payload, encoding="utf-8")
    print("Reset timing matrix:")
    print(payload, end="")


def run_reset_stress(
    proxy_port: int,
    rendezvous: dict,
    status_path: Path,
    reset_command_path: Path,
    initial_status: dict,
    cycles: int,
) -> dict:
    node_id = str(initial_status["node_id"])
    process_id = int(initial_status["process_id"])
    initial_reset_count = int(initial_status.get("reset_count", 0))
    last_ready_status = initial_status
    rows: list[dict[str, object]] = []

    for iteration in range(cycles):
        cycle = iteration + 1
        expected_reset_count = initial_reset_count + cycle
        row: dict[str, object] = {
            "cycle": cycle,
            "expected_reset_count": expected_reset_count,
            "old_close_ms": None,
            "tcp_ms": None,
            "udp_ms": None,
            "tcp_attempts": 0,
            "udp_assoc_attempts": 0,
            "udp_attempts": 0,
            "status_ms": None,
            "status_ok": False,
            "passed": False,
            "errors": [],
        }

        stale: socket.socket | None = None
        try:
            # This is a precondition, not part of the recovery SLO. Do not
            # start the reset clock until the local SOCKS runtime is known-good
            # and the sentinel has entered an active handler.
            stale = open_negotiated_socks_sentinel(proxy_port)
        except Exception as exc:
            if stale is not None:
                stale.close()
                stale = None
            row["errors"].append(
                f"stale precondition failed: {format_exception(exc)}"
            )

        started = time.monotonic()
        deadline = started + RESET_BUDGET_SECONDS
        stale_closed = threading.Event()
        stale_state: dict[str, object] = {}
        tcp_state: dict[str, object] = {}
        udp_state: dict[str, object] = {}

        def watch_stale_connection() -> None:
            if stale is None:
                stale_state["error"] = "no stale connection was established"
                return

            while time.monotonic() < deadline:
                try:
                    payload = stale.recv(1)
                    if payload:
                        stale_state["error"] = (
                            "stale local SOCKS connection produced data during reset"
                        )
                        return

                    closed_at = time.monotonic()
                    stale_state["closed_at"] = closed_at
                    row["old_close_ms"] = (closed_at - started) * 1000.0
                    stale_closed.set()
                    return
                except socket.timeout:
                    continue
                except (
                    ConnectionResetError,
                    ConnectionAbortedError,
                    BrokenPipeError,
                    OSError,
                ):
                    closed_at = time.monotonic()
                    stale_state["closed_at"] = closed_at
                    row["old_close_ms"] = (closed_at - started) * 1000.0
                    stale_closed.set()
                    return

            stale_state["error"] = (
                "stale local SOCKS connection was not discarded within recovery window"
            )

        def probe_tcp() -> None:
            if not wait_for_old_runtime_abort(
                deadline=deadline,
                old_runtime_closed=stale_closed,
            ):
                tcp_state.update(
                    {
                        "ok": False,
                        "ms": None,
                        "attempts": 0,
                        "last_error": "old runtime was not discarded before deadline",
                    }
                )
                return

            attempts = 0
            last_error: str | None = None
            while time.monotonic() < deadline:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                attempts += 1
                try:
                    verify_peer_local_service(
                        proxy_port,
                        rendezvous["server_ip"],
                        int(rendezvous["local_service_port"]),
                        timeout=remaining,
                    )
                    tcp_state.update(
                        {
                            "ok": True,
                            "ms": (time.monotonic() - started) * 1000.0,
                            "attempts": attempts,
                            "last_error": last_error,
                        }
                    )
                    return
                except Exception as exc:
                    last_error = format_exception(exc)

                sleep_for = min(
                    RESET_RETRY_INTERVAL_SECONDS,
                    max(0.0, deadline - time.monotonic()),
                )
                if sleep_for > 0:
                    time.sleep(sleep_for)

            tcp_state.update(
                {
                    "ok": False,
                    "ms": None,
                    "attempts": attempts,
                    "last_error": last_error or "fresh TCP did not recover before deadline",
                }
            )

        def probe_udp() -> None:
            payload = f"netloop-reset-{cycle}".encode("ascii")
            if not wait_for_old_runtime_abort(
                deadline=deadline,
                old_runtime_closed=stale_closed,
            ):
                udp_state.update(
                    {
                        "ok": False,
                        "ms": None,
                        "association_attempts": 0,
                        "attempts": 0,
                        "last_error": "old runtime was not discarded before deadline",
                    }
                )
                return

            association_attempts = 0
            datagram_attempts = 0
            last_error: str | None = None
            while time.monotonic() < deadline:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                association_attempts += 1
                try:
                    with SocksUdpAssociation(
                        "127.0.0.1",
                        proxy_port,
                        timeout=min(0.5, remaining),
                    ) as association:
                        while time.monotonic() < deadline:
                            datagram_attempts += 1
                            association.send(
                                rendezvous["server_ip"],
                                int(rendezvous["local_udp_service_port"]),
                                payload,
                            )
                            remaining = deadline - time.monotonic()
                            if remaining <= 0:
                                break
                            try:
                                _, _, response = association.receive(
                                    timeout=min(
                                        RESET_UDP_RESPONSE_SLICE_SECONDS,
                                        remaining,
                                    )
                                )
                            except (
                                TimeoutError,
                                socket.timeout,
                                ConnectionResetError,
                                OSError,
                            ) as exc:
                                last_error = format_exception(exc)
                                sleep_for = min(
                                    RESET_RETRY_INTERVAL_SECONDS,
                                    max(0.0, deadline - time.monotonic()),
                                )
                                if sleep_for > 0:
                                    time.sleep(sleep_for)
                                continue

                            if response != payload:
                                last_error = (
                                    "fresh UDP mismatch: "
                                    f"expected={payload!r}, observed={response!r}"
                                )
                                continue

                            udp_state.update(
                                {
                                    "ok": True,
                                    "ms": (time.monotonic() - started) * 1000.0,
                                    "association_attempts": association_attempts,
                                    "attempts": datagram_attempts,
                                    "last_error": last_error,
                                }
                            )
                            return
                except Exception as exc:
                    last_error = format_exception(exc)

                sleep_for = min(
                    RESET_RETRY_INTERVAL_SECONDS,
                    max(0.0, deadline - time.monotonic()),
                )
                if sleep_for > 0:
                    time.sleep(sleep_for)

            udp_state.update(
                {
                    "ok": False,
                    "ms": None,
                    "association_attempts": association_attempts,
                    "attempts": datagram_attempts,
                    "last_error": last_error or "fresh UDP did not recover before deadline",
                }
            )

        threads: list[threading.Thread] = []
        try:
            try:
                write_json_atomic(
                    reset_command_path,
                    {"Id": expected_reset_count, "Command": "reset"},
                )
            except Exception as exc:
                row["errors"].append(
                    f"reset command write failed: {format_exception(exc)}"
                )

            threads = [
                threading.Thread(
                    target=watch_stale_connection,
                    name=f"netloop-stale-{cycle}",
                    daemon=True,
                ),
                threading.Thread(
                    target=probe_tcp,
                    name=f"netloop-tcp-probe-{cycle}",
                    daemon=True,
                ),
                threading.Thread(
                    target=probe_udp,
                    name=f"netloop-udp-probe-{cycle}",
                    daemon=True,
                ),
            ]
            for thread in threads:
                thread.start()

            join_deadline = deadline + 0.5
            for thread in threads:
                thread.join(max(0.0, join_deadline - time.monotonic()))

            if stale_state.get("error") is not None:
                row["errors"].append(f"old-close: {stale_state['error']}")
            if row.get("old_close_ms") is None:
                row["errors"].append("old-close: no close/reset observed")

            row["tcp_ms"] = tcp_state.get("ms")
            row["tcp_attempts"] = tcp_state.get("attempts", 0)
            if not tcp_state.get("ok"):
                row["errors"].append(
                    f"tcp: {tcp_state.get('last_error') or 'probe failed'}"
                )

            row["udp_ms"] = udp_state.get("ms")
            row["udp_assoc_attempts"] = udp_state.get(
                "association_attempts",
                0,
            )
            row["udp_attempts"] = udp_state.get("attempts", 0)
            if not udp_state.get("ok"):
                row["errors"].append(
                    f"udp: {udp_state.get('last_error') or 'probe failed'}"
                )

            status_started = time.monotonic()
            try:
                ready_status = wait_for_reset_ready(
                    status_path,
                    expected_process_id=process_id,
                    expected_reset_count=expected_reset_count,
                    expected_node_id=node_id,
                    timeout=5.0,
                )
                last_ready_status = ready_status
                row["status_ms"] = (
                    time.monotonic() - status_started
                ) * 1000.0
                row["status_ok"] = True
                row["observed_reset_count"] = int(
                    ready_status.get("reset_count", -1)
                )
            except Exception as exc:
                row["errors"].append(
                    f"status: {format_exception(exc)}"
                )

            old_close_ok = (
                isinstance(row.get("old_close_ms"), float)
                and row["old_close_ms"] <= RESET_BUDGET_SECONDS * 1000.0
            )
            tcp_ok = (
                tcp_state.get("ok") is True
                and isinstance(row.get("tcp_ms"), float)
                and row["tcp_ms"] <= RESET_BUDGET_SECONDS * 1000.0
            )
            udp_ok = (
                udp_state.get("ok") is True
                and isinstance(row.get("udp_ms"), float)
                and row["udp_ms"] <= RESET_BUDGET_SECONDS * 1000.0
            )
            row["passed"] = (
                old_close_ok
                and tcp_ok
                and udp_ok
                and row.get("status_ok") is True
                and not row["errors"]
            )
        finally:
            if stale is not None:
                stale.close()
            for thread in threads:
                thread.join(timeout=0.05)
            rows.append(row)

        if cycle < cycles:
            # A stress cycle represents one completed physical-network change,
            # not continuous sub-second interface flapping. Keep this delay
            # outside the measured recovery budget so the next synthetic
            # network-change event starts from a steady state.
            time.sleep(RESET_INTER_CYCLE_SETTLE_SECONDS)

    write_reset_timing_matrix(rows)

    old_close_samples = [
        float(row["old_close_ms"])
        for row in rows
        if isinstance(row.get("old_close_ms"), float)
    ]
    tcp_samples = [
        float(row["tcp_ms"])
        for row in rows
        if isinstance(row.get("tcp_ms"), float)
    ]
    udp_samples = [
        float(row["udp_ms"])
        for row in rows
        if isinstance(row.get("udp_ms"), float)
    ]
    passed = sum(1 for row in rows if row.get("passed") is True)
    metrics = {
        "cycles": cycles,
        "passed": passed,
        "failed": cycles - passed,
        "success": passed == cycles,
        "identity_preserved": all(
            row.get("status_ok") is True for row in rows
        ),
        "rows": rows,
        "old_close_p95_ms": nearest_rank_percentile(old_close_samples, 95),
        "old_close_max_ms": max(old_close_samples) if old_close_samples else None,
        "tcp_p95_ms": nearest_rank_percentile(tcp_samples, 95),
        "tcp_max_ms": max(tcp_samples) if tcp_samples else None,
        "udp_p95_ms": nearest_rank_percentile(udp_samples, 95),
        "udp_max_ms": max(udp_samples) if udp_samples else None,
        "expected_final_reset_count": initial_reset_count + cycles,
        "observed_final_reset_count": int(
            last_ready_status.get("reset_count", initial_reset_count)
        ),
        "final_process_id": process_id,
    }
    (EVIDENCE / "reset_stress.json").write_text(
        json.dumps(metrics, indent=2),
        encoding="utf-8",
    )
    return metrics


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
        executable = runtime / ("netloop.exe" if os.name == "nt" else "netloop")
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
            reset_metrics = run_reset_stress(
                args.listen_port,
                rendezvous,
                status_path,
                reset_command_path,
                status,
                args.reset_cycles,
            )
            result["reset_metrics"] = reset_metrics
            result["reset_stress"] = bool(reset_metrics["success"])
            result["identity_preserved"] = bool(
                reset_metrics["identity_preserved"]
            )
            result["restart_recovery"] = bool(reset_metrics["success"])
            if not reset_metrics["success"]:
                raise AssertionError(
                    "runtime reset stress failed: "
                    f"{reset_metrics['passed']}/{reset_metrics['cycles']} cycles passed; "
                    f"see reset_timing.tsv and reset_stress.json"
                )
            result["success"] = True
            print(json.dumps(result, indent=2))
            return 0

        stale_tunnels: list[socket.socket] = []
        try:
            for index in range(4):
                stale_tunnels.append(
                    retry(
                        f"restart_stale_tunnel_{index + 1}",
                        lambda: socks_connect(
                            "127.0.0.1",
                            args.listen_port,
                            "api.ipify.org",
                            443,
                            timeout=3.0,
                        ),
                        attempts=4,
                        delay=0.25,
                    )
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
