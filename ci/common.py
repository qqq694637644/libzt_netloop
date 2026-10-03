from __future__ import annotations

import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import time
import urllib.request


ROOT = Path(__file__).resolve().parents[1]


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
