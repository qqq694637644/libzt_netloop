from __future__ import annotations

import json
import os
from pathlib import Path
import signal
import socket
import struct
import subprocess
import time
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
STUN_MAGIC_COOKIE = 0x2112A442


def wait_for_json(path: Path, timeout: float, phase: str = "ready") -> dict:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        if path.exists():
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                if data.get("phase") == phase:
                    return data
            except Exception as exc:
                last_error = exc
        time.sleep(0.25)
    raise TimeoutError(f"timed out waiting for {path}; last_error={last_error}")


def wait_for_tcp(host: str, port: int, timeout: float = 20.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with socket.create_connection((host, port), timeout=1.0):
                return
        except OSError:
            time.sleep(0.2)
    raise TimeoutError(f"timed out waiting for TCP {host}:{port}")


def public_ip() -> str:
    request = urllib.request.Request(
        "https://api.ipify.org",
        headers={"User-Agent": "libzt-netloop-ci/1"},
    )
    last_error: Exception | None = None
    for _ in range(4):
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                return response.read().decode("ascii").strip()
        except Exception as exc:
            last_error = exc
            time.sleep(2)
    raise RuntimeError(f"unable to query public IP: {last_error}")


def build_stun_binding_request(transaction_id: bytes | None = None) -> tuple[bytes, bytes]:
    if transaction_id is None:
        transaction_id = os.urandom(12)
    if len(transaction_id) != 12:
        raise ValueError("STUN transaction ID must be 12 bytes")
    return (
        struct.pack("!HHI", 0x0001, 0, STUN_MAGIC_COOKIE) + transaction_id,
        transaction_id,
    )


def parse_stun_public_ip(payload: bytes, transaction_id: bytes) -> str:
    if len(payload) < 20:
        raise ValueError("truncated STUN response")

    message_type, message_length, cookie = struct.unpack("!HHI", payload[:8])
    response_transaction_id = payload[8:20]
    if message_type != 0x0101:
        raise ValueError(f"unexpected STUN message type 0x{message_type:04x}")
    if cookie != STUN_MAGIC_COOKIE:
        raise ValueError(f"unexpected STUN magic cookie 0x{cookie:08x}")
    if response_transaction_id != transaction_id:
        raise ValueError("STUN transaction ID mismatch")

    end = min(len(payload), 20 + message_length)
    offset = 20
    while offset + 4 <= end:
        attribute_type, attribute_length = struct.unpack(
            "!HH", payload[offset : offset + 4]
        )
        value_start = offset + 4
        value_end = value_start + attribute_length
        if value_end > end:
            raise ValueError("truncated STUN attribute")
        value = payload[value_start:value_end]

        if attribute_type in (0x0020, 0x0001) and len(value) >= 8:
            family = value[1]
            address = value[4:]
            if family == 0x01 and len(address) >= 4:
                raw = address[:4]
                if attribute_type == 0x0020:
                    mask = struct.pack("!I", STUN_MAGIC_COOKIE)
                    raw = bytes(left ^ right for left, right in zip(raw, mask))
                return socket.inet_ntop(socket.AF_INET, raw)

            if family == 0x02 and len(address) >= 16:
                raw = address[:16]
                if attribute_type == 0x0020:
                    mask = struct.pack("!I", STUN_MAGIC_COOKIE) + transaction_id
                    raw = bytes(left ^ right for left, right in zip(raw, mask))
                return socket.inet_ntop(socket.AF_INET6, raw)

        offset = value_start + ((attribute_length + 3) & ~3)

    raise ValueError("STUN response did not contain a mapped address")


def stun_public_ip(
    host: str = "stun.l.google.com",
    port: int = 19302,
    timeout: float = 5.0,
) -> str:
    request, transaction_id = build_stun_binding_request()
    last_error: Exception | None = None
    addresses = socket.getaddrinfo(
        host,
        port,
        type=socket.SOCK_DGRAM,
        proto=socket.IPPROTO_UDP,
    )

    for family, sock_type, protocol, _, sockaddr in addresses:
        for _ in range(2):
            try:
                with socket.socket(family, sock_type, protocol) as udp:
                    udp.settimeout(timeout)
                    udp.sendto(request, sockaddr)
                    payload, _ = udp.recvfrom(4096)
                    return parse_stun_public_ip(payload, transaction_id)
            except Exception as exc:
                last_error = exc

    raise RuntimeError(f"unable to query STUN public IP: {last_error}")


def start_detached(
    command: list[str], stdout_path: Path, stderr_path: Path | None = None
) -> int:
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    if stderr_path is None:
        stderr_path = stdout_path
    stdout = stdout_path.open("ab", buffering=0)
    stderr = stdout if stderr_path == stdout_path else stderr_path.open("ab", buffering=0)
    flags = 0
    if os.name == "nt":
        flags = subprocess.CREATE_NEW_PROCESS_GROUP
    process = subprocess.Popen(
        command,
        cwd=ROOT,
        stdout=stdout,
        stderr=stderr,
        stdin=subprocess.DEVNULL,
        creationflags=flags,
        close_fds=False,
    )
    stdout.close()
    if stderr is not stdout:
        stderr.close()
    return process.pid


def kill_process_tree(pid: int) -> None:
    if pid <= 0:
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    else:
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass


def collect_windows_evidence(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    commands = {
        "ipconfig.txt": ["ipconfig", "/all"],
        "route.txt": ["route", "print"],
        "netstat.txt": ["netstat", "-ano"],
        "ipv6_interfaces.txt": ["netsh", "interface", "ipv6", "show", "interfaces"],
    }
    for filename, command in commands.items():
        try:
            result = subprocess.run(
                command,
                cwd=ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=30,
                check=False,
            )
            (directory / filename).write_text(
                f"$ {subprocess.list2cmdline(command)}\n"
                f"exit={result.returncode}\n\n"
                f"STDOUT\n{result.stdout}\n\nSTDERR\n{result.stderr}\n",
                encoding="utf-8",
            )
        except Exception as exc:
            (directory / filename).write_text(repr(exc), encoding="utf-8")
