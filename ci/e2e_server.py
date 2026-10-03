#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import traceback

from common import (
    ROOT,
    collect_windows_evidence,
    kill_process_tree,
    public_ip,
    start_detached,
    wait_for_json,
    wait_for_tcp,
)
from github_artifacts import wait_and_download


EVIDENCE = ROOT / "evidence" / "server"
PIDS = EVIDENCE / "pids.json"


def adhoc_network_id(port: int) -> str:
    return f"ff{port:04x}{port:04x}000000"


def prepare(args: argparse.Namespace) -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    runtime = Path(args.runtime).resolve()
    server_exe = runtime / "zt_netloop_server.exe"
    if not server_exe.exists():
        raise FileNotFoundError(server_exe)

    zt_port = args.zt_port
    network_id = adhoc_network_id(zt_port)
    status_path = EVIDENCE / "server_status.json"
    state_dir = ROOT / "state" / "ci-server"

    mock_pid = start_detached(
        [
            sys.executable,
            str(ROOT / "ci" / "mock_socks5.py"),
            "--host",
            "127.0.0.1",
            "--port",
            str(args.socks_port),
            "--log",
            str(EVIDENCE / "mock_socks5.log"),
        ],
        EVIDENCE / "mock_socks5_process.log",
    )

    server_pid = 0
    try:
        wait_for_tcp("127.0.0.1", args.socks_port, timeout=20)
        expected_ip = public_ip()
        (EVIDENCE / "expected_public_ip.txt").write_text(
            expected_ip + "\n", encoding="utf-8"
        )

        server_pid = start_detached(
            [
                str(server_exe),
                "--network",
                network_id,
                "--state-dir",
                str(state_dir),
                "--zt-port",
                str(zt_port),
                "--forward-host",
                "127.0.0.1",
                "--forward-port",
                str(args.socks_port),
                "--status-file",
                str(status_path),
                "--log-file",
                str(EVIDENCE / "server.log"),
                "--timeout",
                "180",
            ],
            EVIDENCE / "server_process.log",
        )

        PIDS.write_text(
            json.dumps(
                {"server_pid": server_pid, "mock_socks_pid": mock_pid}, indent=2
            ),
            encoding="utf-8",
        )

        status = wait_for_json(status_path, timeout=200)
        server_ip = status.get("zt_listen_host")
        if not server_ip:
            raise RuntimeError(
                f"server did not publish ZeroTier listen IP: {status}"
            )

        rendezvous = {
            "network_id": network_id,
            "zt_port": zt_port,
            "server_ip": server_ip,
            "server_node_id": status.get("node_id"),
            "server_ipv4": status.get("ipv4"),
            "server_ipv6": status.get("ipv6"),
            "expected_public_ip": expected_ip,
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
        kill_process_tree(server_pid)
        kill_process_tree(mock_pid)
        raise


def hold(args: argparse.Namespace) -> int:
    result_dir = EVIDENCE / "client_result"
    exit_code = 1
    try:
        wait_and_download(args.client_artifact, result_dir, timeout=args.timeout)
        result_files = list(result_dir.rglob("result.json"))
        if not result_files:
            raise FileNotFoundError(
                "client result artifact did not contain result.json"
            )
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
            kill_process_tree(int(pids.get("server_pid", 0)))
            kill_process_tree(int(pids.get("mock_socks_pid", 0)))
    return exit_code


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    prep = subparsers.add_parser("prepare")
    prep.add_argument("--runtime", default="runtime")
    prep.add_argument("--zt-port", type=int, default=42042)
    prep.add_argument("--socks-port", type=int, default=18080)

    wait = subparsers.add_parser("hold")
    wait.add_argument("--client-artifact", required=True)
    wait.add_argument("--timeout", type=float, default=600)

    args = parser.parse_args()
    try:
        return prepare(args) if args.command == "prepare" else hold(args)
    except Exception:
        EVIDENCE.mkdir(parents=True, exist_ok=True)
        (EVIDENCE / f"{args.command}_failure.txt").write_text(
            traceback.format_exc(), encoding="utf-8"
        )
        collect_windows_evidence(EVIDENCE / "network_failure")
        if args.command == "prepare" and PIDS.exists():
            pids = json.loads(PIDS.read_text(encoding="utf-8"))
            kill_process_tree(int(pids.get("server_pid", 0)))
            kill_process_tree(int(pids.get("mock_socks_pid", 0)))
        print(traceback.format_exc(), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
