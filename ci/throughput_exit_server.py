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
)
from github_artifacts import wait_and_download
from throughput_probe import count_route_decisions


EVIDENCE = ROOT / "evidence" / "throughput-server"
PIDS = EVIDENCE / "pids.json"


def adhoc_network_id(port: int) -> str:
    return f"ff{port:04x}{port:04x}000000"


def prepare(args: argparse.Namespace) -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    runtime = Path(args.runtime).resolve()
    executable = runtime / ("netloop.exe" if os.name == "nt" else "netloop")
    if not executable.exists():
        raise FileNotFoundError(executable)

    network_id = adhoc_network_id(args.overlay_port)
    status_path = EVIDENCE / "server_status.json"
    state_dir = ROOT / "state" / "throughput-server"
    command = [
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
        "--egress",
        "direct",
    ]

    netloop_pid = start_detached(command, EVIDENCE / "netloop_process.log")
    PIDS.write_text(
        json.dumps({"netloop_pid": netloop_pid}, indent=2),
        encoding="utf-8",
    )
    try:
        status = wait_for_json(status_path, timeout=200)
        server_ip = status.get("primary_overlay_address")
        if not server_ip:
            raise RuntimeError(
                "throughput exit did not publish primary_overlay_address: "
                f"{status}"
            )
        expected_ip = public_ip()
        rendezvous = {
            "network_id": network_id,
            "server_ip": server_ip,
            "server_node_id": status.get("node_id"),
            "overlay_port": args.overlay_port,
            "overlay_udp_port": args.overlay_udp_port,
            "expected_public_ip": expected_ip,
            "github_run_id": os.environ.get("GITHUB_RUN_ID", ""),
            "github_run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT", ""),
        }
        (EVIDENCE / "rendezvous.json").write_text(
            json.dumps(rendezvous, indent=2),
            encoding="utf-8",
        )
        print(json.dumps(rendezvous, indent=2))
        return 0
    except Exception:
        kill_process_tree(netloop_pid)
        raise


def hold(args: argparse.Namespace) -> int:
    result_dir = EVIDENCE / "client_result"
    exit_code = 1
    try:
        wait_and_download(
            args.client_artifact,
            result_dir,
            timeout=args.timeout,
        )
        files = list(result_dir.rglob("result.json"))
        if not files:
            raise FileNotFoundError(
                "throughput client artifact did not contain result.json"
            )
        result = json.loads(files[0].read_text(encoding="utf-8"))
        expected_routes = int(result.get("expected_speed_routes", 0))
        observed_routes = count_route_decisions(
            EVIDENCE / "netloop_process.log",
            ingress="overlay",
            route="DirectEgress",
        )
        route_verification = {
            "expected_speed_routes": expected_routes,
            "server_overlay_egress_routes": observed_routes,
            "success": (
                expected_routes > 0
                and observed_routes >= expected_routes
            ),
        }
        (EVIDENCE / "route_verification.json").write_text(
            json.dumps(route_verification, indent=2),
            encoding="utf-8",
        )
        (EVIDENCE / "observed_client_result.json").write_text(
            json.dumps(result, indent=2),
            encoding="utf-8",
        )
        print(json.dumps(result, indent=2))
        print(json.dumps(route_verification, indent=2))
        exit_code = (
            0
            if result.get("success")
            and route_verification["success"]
            else 1
        )
    finally:
        cleanup_processes()
    return exit_code


def cleanup_processes() -> None:
    if os.name == "nt":
        collect_windows_evidence(EVIDENCE / "network")
    if PIDS.exists():
        pids = json.loads(PIDS.read_text(encoding="utf-8"))
        kill_process_tree(int(pids.get("netloop_pid", 0)))


def cleanup(_args: argparse.Namespace) -> int:
    cleanup_processes()
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    prep = subparsers.add_parser("prepare")
    prep.add_argument("--runtime", default="runtime-csharp")
    prep.add_argument("--overlay-port", type=int, required=True)
    prep.add_argument("--overlay-udp-port", type=int, required=True)
    prep.add_argument("--socks-port", type=int, default=18080)

    wait = subparsers.add_parser("hold")
    wait.add_argument("--client-artifact", required=True)
    wait.add_argument("--timeout", type=float, default=1800)
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
            traceback.format_exc(),
            encoding="utf-8",
        )
        if args.command == "prepare":
            cleanup_processes()
        print(traceback.format_exc(), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
