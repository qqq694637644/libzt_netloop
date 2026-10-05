#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import time
import traceback

import csharp_e2e_client as desktop_e2e
from common import ROOT, kill_process_tree, start_detached, wait_for_json
from github_artifacts import wait_and_download
from throughput_probe import count_route_decisions, run_matrix, write_tsv


EVIDENCE = ROOT / "evidence" / "throughput-client"


def parse_concurrencies(value: str) -> list[int]:
    result = [int(item.strip()) for item in value.split(",") if item.strip()]
    if not result or any(item < 1 for item in result):
        raise ValueError(f"invalid concurrency list: {value!r}")
    return result


def retry_public_ip(proxy_port: int) -> str:
    last_error: Exception | None = None
    for _ in range(8):
        try:
            return desktop_e2e.https_public_ip_via_socks(proxy_port)
        except Exception as exc:
            last_error = exc
            time.sleep(2)
    raise RuntimeError(f"unable to query public IP through default exit: {last_error}")


def start_client(
    executable: Path,
    rendezvous: dict,
    listen_port: int,
    status_path: Path,
) -> int:
    command = [
        str(executable),
        "--network",
        str(rendezvous["network_id"]),
        "--state-dir",
        str(ROOT / "state" / "throughput-client"),
        "--socks-host",
        "127.0.0.1",
        "--socks-port",
        str(listen_port),
        "--overlay-port",
        str(rendezvous["overlay_port"]),
        "--overlay-udp-port",
        str(rendezvous["overlay_udp_port"]),
        "--default-exit",
        str(rendezvous["server_ip"]),
        "--status-file",
        str(status_path),
        "--startup-timeout",
        "180",
        "--connect-timeout",
        "20",
    ]
    return start_detached(command, EVIDENCE / "netloop_process.log")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", default="runtime-csharp")
    parser.add_argument("--rendezvous-artifact", required=True)
    parser.add_argument("--listen-port", type=int, default=19080)
    parser.add_argument("--file-size-mib", type=int, default=32)
    parser.add_argument("--concurrency", default="1,8,32")
    parser.add_argument("--stream-timeout", type=float, default=300)
    args = parser.parse_args()

    concurrencies = parse_concurrencies(args.concurrency)
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    result: dict[str, object] = {
        "success": False,
        "file_size_mib": args.file_size_mib,
        "concurrencies": concurrencies,
    }
    client_pid = 0

    try:
        rendezvous_dir = EVIDENCE / "rendezvous"
        wait_and_download(
            args.rendezvous_artifact,
            rendezvous_dir,
            timeout=900,
        )
        files = list(rendezvous_dir.rglob("rendezvous.json"))
        if not files:
            raise FileNotFoundError(
                "throughput rendezvous artifact did not contain rendezvous.json"
            )
        rendezvous = json.loads(files[0].read_text(encoding="utf-8"))
        result["rendezvous"] = rendezvous

        runtime = Path(args.runtime).resolve()
        executable = runtime / ("netloop.exe" if os.name == "nt" else "netloop")
        if not executable.exists():
            raise FileNotFoundError(executable)

        status_path = EVIDENCE / "client_status.json"
        client_pid = start_client(
            executable,
            rendezvous,
            args.listen_port,
            status_path,
        )
        status = wait_for_json(status_path, timeout=200)
        result["client_status"] = status

        observed_ip = retry_public_ip(args.listen_port)
        expected_ip = str(rendezvous["expected_public_ip"])
        result["observed_public_ip"] = observed_ip
        result["expected_public_ip"] = expected_ip
        if observed_ip != expected_ip:
            raise AssertionError(
                "default-exit public IP mismatch before throughput test: "
                f"observed={observed_ip}, expected={expected_ip}"
            )

        rows = run_matrix(
            "127.0.0.1",
            args.listen_port,
            concurrencies=concurrencies,
            size_mib=args.file_size_mib,
            timeout=args.stream_timeout,
        )
        result["rows"] = rows
        write_tsv(EVIDENCE / "throughput.tsv", rows)

        expected_routes = sum(concurrencies)
        observed_routes = count_route_decisions(
            EVIDENCE / "netloop_process.log",
            ingress="local",
            route="DefaultExit",
        )
        result["expected_speed_routes"] = expected_routes
        result["client_default_exit_routes"] = observed_routes
        if observed_routes < expected_routes:
            raise AssertionError(
                "client route log did not prove all throughput streams used "
                f"default_exit: expected>={expected_routes}, "
                f"observed={observed_routes}"
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
        kill_process_tree(client_pid)
        (EVIDENCE / "result.json").write_text(
            json.dumps(result, indent=2),
            encoding="utf-8",
        )


if __name__ == "__main__":
    raise SystemExit(main())
