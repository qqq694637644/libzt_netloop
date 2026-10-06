#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import socket
import sys
import time
import traceback

from common import (
    ROOT,
    collect_windows_evidence,
    kill_process_tree,
    public_ip,
    start_detached,
    stun_public_ip,
    wait_for_json,
    wait_for_tcp,
)
from github_artifacts import wait_and_download


EVIDENCE = ROOT / "evidence" / "csharp-server"
PIDS = EVIDENCE / "pids.json"


def adhoc_network_id(port: int) -> str:
    return f"ff{port:04x}{port:04x}000000"


def wait_for_udp_echo(host: str, port: int, timeout: float = 20.0) -> None:
    family = socket.AF_INET6 if ":" in host else socket.AF_INET
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            with socket.socket(family, socket.SOCK_DGRAM) as udp:
                udp.settimeout(1.0)
                token = b"netloop-udp-ready"
                udp.sendto(token, (host, port))
                content, _ = udp.recvfrom(1024)
                if content == token:
                    return
        except Exception as exc:
            last_error = exc
            time.sleep(0.2)
    raise TimeoutError(
        f"timed out waiting for UDP echo {host}:{port}; last_error={last_error}"
    )


def prepare(args: argparse.Namespace) -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    runtime = Path(args.runtime).resolve()
    executable = runtime / ("netloop.exe" if os.name == "nt" else "netloop")
    if not executable.exists():
        raise FileNotFoundError(executable)

    network_id = adhoc_network_id(args.overlay_port)
    status_path = EVIDENCE / "server_status.json"
    state_dir = ROOT / "state" / "csharp-ci-server"

    http_pid = start_detached(
        [
            sys.executable,
            "-m",
            "http.server",
            str(args.local_service_port),
            "--bind",
            "::1",
        ],
        EVIDENCE / "local_service.log",
    )
    udp_pid = start_detached(
        [
            sys.executable,
            str(ROOT / "ci" / "udp_echo_server.py"),
            "--host",
            "::1",
            "--port",
            str(args.local_udp_service_port),
        ],
        EVIDENCE / "local_udp_service.log",
    )

    netloop_pid = 0
    upstream_pid = 0
    try:
        wait_for_tcp("::1", args.local_service_port, timeout=20)
        wait_for_udp_echo("::1", args.local_udp_service_port, timeout=20)
        expected_ip = public_ip()
        expected_udp_ip = stun_public_ip()
        (EVIDENCE / "expected_public_ip.txt").write_text(
            expected_ip + "\n", encoding="utf-8"
        )
        (EVIDENCE / "expected_udp_public_ip.txt").write_text(
            expected_udp_ip + "\n", encoding="utf-8"
        )

        netloop_command = [
            str(executable),
            "--network",
            network_id,
            "--state-dir",
            str(state_dir),
            "--socks-host",
            "127.0.0.1",
            "--socks-port",
            str(args.socks_port),
            "--overlay-port",
            str(args.overlay_port),
            "--overlay-udp-port",
            str(args.overlay_udp_port),
            "--status-file",
            str(status_path),
            "--startup-timeout",
            "180",
            "--connect-timeout",
            "20",
        ]
        if args.upstream_socks_port:
            upstream_pid = start_detached(
                [
                    sys.executable,
                    str(ROOT / "ci" / "socks5_test_proxy.py"),
                    "--host",
                    "127.0.0.1",
                    "--port",
                    str(args.upstream_socks_port),
                    "--username",
                    args.upstream_username,
                    "--password",
                    args.upstream_password,
                ],
                EVIDENCE / "upstream_socks.log",
            )
            wait_for_tcp("127.0.0.1", args.upstream_socks_port, timeout=20)
            netloop_command.extend(
                [
                    "--egress",
                    "upstream-socks5",
                    "--upstream-host",
                    "127.0.0.1",
                    "--upstream-port",
                    str(args.upstream_socks_port),
                    "--upstream-user",
                    args.upstream_username,
                    "--upstream-password",
                    args.upstream_password,
                ]
            )
        else:
            netloop_command.extend(["--egress", "direct"])

        netloop_pid = start_detached(
            netloop_command,
            EVIDENCE / "netloop_process.log",
        )

        PIDS.write_text(
            json.dumps(
                {
                    "netloop_pid": netloop_pid,
                    "http_pid": http_pid,
                    "udp_pid": udp_pid,
                    "upstream_pid": upstream_pid,
                },
                indent=2,
            ),
            encoding="utf-8",
        )

        status = wait_for_json(status_path, timeout=200)
        server_ip = status.get("primary_overlay_address")
        if not server_ip:
            raise RuntimeError(
                "server did not publish primary_overlay_address: "
                f"{status}"
            )

        rendezvous = {
            "network_id": network_id,
            "server_ip": server_ip,
            "server_node_id": status.get("node_id"),
            "overlay_port": args.overlay_port,
            "overlay_udp_port": args.overlay_udp_port,
            "local_service_port": args.local_service_port,
            "local_udp_service_port": args.local_udp_service_port,
            "expected_public_ip": expected_ip,
            "expected_udp_public_ip": expected_udp_ip,
            "stun_host": "stun.l.google.com",
            "stun_port": 19302,
            "server_egress": (
                "upstream-socks5" if args.upstream_socks_port else "direct"
            ),
            "github_run_id": os.environ.get("GITHUB_RUN_ID", ""),
            "github_run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT", ""),
        }
        rendezvous_path = EVIDENCE / "rendezvous.json"
        rendezvous_path.write_text(
            json.dumps(rendezvous, indent=2), encoding="utf-8"
        )
        print(json.dumps(rendezvous, indent=2))
        return 0
    except Exception:
        kill_process_tree(netloop_pid)
        kill_process_tree(http_pid)
        kill_process_tree(udp_pid)
        kill_process_tree(upstream_pid)
        raise


def hold(args: argparse.Namespace) -> int:
    result_dir = EVIDENCE / "client_result"
    exit_code = 1
    try:
        wait_and_download(args.client_artifact, result_dir, timeout=args.timeout)
        result_files = list(result_dir.rglob("result.json"))
        if not result_files:
            raise FileNotFoundError("client result artifact did not contain result.json")
        result = json.loads(result_files[0].read_text(encoding="utf-8"))
        (EVIDENCE / "observed_client_result.json").write_text(
            json.dumps(result, indent=2), encoding="utf-8"
        )
        exit_code = 0 if result.get("success") else 1
        print(json.dumps(result, indent=2))
    finally:
        collect_windows_evidence(EVIDENCE / "network")
        if PIDS.exists():
            pids = json.loads(PIDS.read_text(encoding="utf-8"))
            kill_process_tree(int(pids.get("netloop_pid", 0)))
            kill_process_tree(int(pids.get("http_pid", 0)))
            kill_process_tree(int(pids.get("udp_pid", 0)))
            kill_process_tree(int(pids.get("upstream_pid", 0)))
    return exit_code


def cleanup(_args: argparse.Namespace) -> int:
    collect_windows_evidence(EVIDENCE / "network")
    if PIDS.exists():
        pids = json.loads(PIDS.read_text(encoding="utf-8"))
        kill_process_tree(int(pids.get("netloop_pid", 0)))
        kill_process_tree(int(pids.get("http_pid", 0)))
        kill_process_tree(int(pids.get("udp_pid", 0)))
        kill_process_tree(int(pids.get("upstream_pid", 0)))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    prep = subparsers.add_parser("prepare")
    prep.add_argument("--runtime", default="runtime-csharp")
    prep.add_argument("--overlay-port", type=int, default=42042)
    prep.add_argument("--overlay-udp-port", type=int, default=42043)
    prep.add_argument("--socks-port", type=int, default=18080)
    prep.add_argument("--local-service-port", type=int, default=18181)
    prep.add_argument("--local-udp-service-port", type=int, default=18182)
    prep.add_argument("--upstream-socks-port", type=int)
    prep.add_argument("--upstream-username", default="netloop-ci")
    prep.add_argument("--upstream-password", default="netloop-ci-password")

    wait = subparsers.add_parser("hold")
    wait.add_argument("--client-artifact", required=True)
    wait.add_argument("--timeout", type=float, default=600)
    subparsers.add_parser("cleanup")

    args = parser.parse_args()
    try:
        if args.command == "prepare":
            return prepare(args)
        if args.command == "hold":
            return hold(args)
        return cleanup(args)
    except Exception:
        EVIDENCE.mkdir(parents=True, exist_ok=True)
        (EVIDENCE / f"{args.command}_failure.txt").write_text(
            traceback.format_exc(), encoding="utf-8"
        )
        collect_windows_evidence(EVIDENCE / "network_failure")
        if args.command == "prepare" and PIDS.exists():
            pids = json.loads(PIDS.read_text(encoding="utf-8"))
            kill_process_tree(int(pids.get("netloop_pid", 0)))
            kill_process_tree(int(pids.get("http_pid", 0)))
            kill_process_tree(int(pids.get("udp_pid", 0)))
            kill_process_tree(int(pids.get("upstream_pid", 0)))
        print(traceback.format_exc(), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
