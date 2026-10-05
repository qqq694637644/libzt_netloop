#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import traceback

import csharp_e2e_client as desktop_e2e
from github_artifacts import wait_and_download


ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "evidence" / "android-client"
PACKAGE = "com.libzt.netloop"
ACTIVITY = f"{PACKAGE}/com.libzt.netloop.MainActivity"
CI_RECEIVER = f"{PACKAGE}/com.libzt.netloop.CiAutomationReceiver"
ACTION_RESET = "com.libzt.netloop.ci.RESET"
STATUS_PATH = (
    f"/sdcard/Android/data/{PACKAGE}/files/netloop-ci-status.json"
)
RESET_BUDGET_SECONDS = 3.0
RESET_RETRY_INTERVAL_SECONDS = 0.05


def adb(
    *args: str,
    timeout: float = 60.0,
    check: bool = True,
) -> subprocess.CompletedProcess[str]:
    command = ["adb", *args]
    result = subprocess.run(
        command,
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
    )
    if check and result.returncode != 0:
        raise RuntimeError(
            f"{' '.join(command)} failed with {result.returncode}: "
            f"{result.stderr.strip()}"
        )
    return result


def write_adb_diagnostics() -> None:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    commands = {
        "logcat.txt": ("logcat", "-d"),
        "properties.txt": ("shell", "getprop"),
        "packages.txt": ("shell", "dumpsys", "package", PACKAGE),
        "services.txt": ("shell", "dumpsys", "activity", "services", PACKAGE),
    }
    for filename, command in commands.items():
        try:
            result = adb(*command, timeout=45, check=False)
            (EVIDENCE / filename).write_text(
                f"$ adb {' '.join(command)}\n"
                f"exit={result.returncode}\n\n"
                f"STDOUT\n{result.stdout}\n\n"
                f"STDERR\n{result.stderr}\n",
                encoding="utf-8",
            )
        except Exception as exc:
            (EVIDENCE / filename).write_text(
                repr(exc),
                encoding="utf-8",
            )


def read_status() -> dict | None:
    result = adb(
        "exec-out",
        "cat",
        STATUS_PATH,
        timeout=10,
        check=False,
    )
    if result.returncode != 0:
        result = adb(
            "exec-out",
            "run-as",
            PACKAGE,
            "cat",
            STATUS_PATH,
            timeout=10,
            check=False,
        )
    if result.returncode != 0:
        return None
    text = result.stdout.strip()
    if not text:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return None


def wait_for_status(
    *,
    timeout: float,
    expected_reset_count: int | None = None,
    expected_node_id: str | None = None,
) -> dict:
    deadline = time.monotonic() + timeout
    last: dict | None = None
    while time.monotonic() < deadline:
        status = read_status()
        if status is not None:
            last = status
            if status.get("phase") == "error":
                raise RuntimeError(
                    "Android NetLoop service failed: "
                    f"{status.get('error_type')}: {status.get('error')}"
                )
            if status.get("phase") == "ready":
                reset_ok = (
                    expected_reset_count is None
                    or int(status.get("reset_count", -1))
                    == expected_reset_count
                )
                node_ok = (
                    expected_node_id is None
                    or status.get("node_id") == expected_node_id
                )
                if reset_ok and node_ok:
                    return status
        time.sleep(0.2)
    raise TimeoutError(
        "timed out waiting for Android NetLoop status; "
        f"expected_reset_count={expected_reset_count}, last={last}"
    )


def configure_and_start(rendezvous: dict) -> dict:
    network_id = str(rendezvous["network_id"])
    server_ip = str(rendezvous["server_ip"])

    adb(
        "shell",
        "pm",
        "grant",
        PACKAGE,
        "android.permission.POST_NOTIFICATIONS",
        check=False,
    )

    adb(
        "shell",
        "am",
        "start",
        "-W",
        "-n",
        ACTIVITY,
        "--ez",
        "netloop_ci_start",
        "true",
        "--es",
        "network_id",
        network_id,
        "--es",
        "peers",
        server_ip,
        "--es",
        "default_exit",
        server_ip,
        timeout=30,
    )
    return wait_for_status(timeout=200)


def trigger_reset() -> None:
    adb(
        "shell",
        "am",
        "broadcast",
        "-a",
        ACTION_RESET,
        "-n",
        CI_RECEIVER,
        timeout=15,
    )


def wait_stale_close(
    sock: socket.socket,
    *,
    started: float,
    deadline: float,
    closed: threading.Event,
    state: dict[str, object],
) -> None:
    sock.settimeout(0.05)
    while time.monotonic() < deadline:
        try:
            payload = sock.recv(1)
            if payload:
                state["error"] = (
                    "stale Android SOCKS connection produced data during reset"
                )
                return
            closed_at = time.monotonic()
            state["closed_at"] = closed_at
            state["ms"] = (closed_at - started) * 1000.0
            closed.set()
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
            state["closed_at"] = closed_at
            state["ms"] = (closed_at - started) * 1000.0
            closed.set()
            return

    state["error"] = "stale Android SOCKS connection did not close in time"


def probe_tcp_after_abort(
    *,
    proxy_port: int,
    rendezvous: dict,
    started: float,
    deadline: float,
    old_closed: threading.Event,
    state: dict[str, object],
) -> None:
    if not old_closed.wait(max(0.0, deadline - time.monotonic())):
        state.update(
            ok=False,
            attempts=0,
            error="old Android runtime did not abort before deadline",
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
            desktop_e2e.verify_peer_local_service(
                proxy_port,
                str(rendezvous["server_ip"]),
                int(rendezvous["local_service_port"]),
                timeout=min(1.0, remaining),
            )
            state.update(
                ok=True,
                attempts=attempts,
                ms=(time.monotonic() - started) * 1000.0,
                error=last_error,
            )
            return
        except Exception as exc:
            last_error = f"{type(exc).__name__}: {exc}"
        time.sleep(
            min(
                RESET_RETRY_INTERVAL_SECONDS,
                max(0.0, deadline - time.monotonic()),
            )
        )

    state.update(
        ok=False,
        attempts=attempts,
        ms=None,
        error=last_error or "fresh Android TCP did not recover before deadline",
    )


def probe_udp_after_abort(
    *,
    proxy_port: int,
    rendezvous: dict,
    cycle: int,
    started: float,
    deadline: float,
    old_closed: threading.Event,
    state: dict[str, object],
) -> None:
    if not old_closed.wait(max(0.0, deadline - time.monotonic())):
        state.update(
            ok=False,
            association_attempts=0,
            attempts=0,
            error="old Android runtime did not abort before deadline",
        )
        return

    payload = f"netloop-android-reset-{cycle}".encode("ascii")
    association_attempts = 0
    attempts = 0
    last_error: str | None = None

    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        association_attempts += 1
        try:
            with desktop_e2e.SocksUdpAssociation(
                "127.0.0.1",
                proxy_port,
                timeout=min(0.5, remaining),
            ) as association:
                while time.monotonic() < deadline:
                    attempts += 1
                    association.send(
                        str(rendezvous["server_ip"]),
                        int(rendezvous["local_udp_service_port"]),
                        payload,
                    )
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        break
                    try:
                        _, _, response = association.receive(
                            timeout=min(0.25, remaining)
                        )
                    except (
                        TimeoutError,
                        socket.timeout,
                        ConnectionResetError,
                        OSError,
                    ) as exc:
                        last_error = f"{type(exc).__name__}: {exc}"
                        continue

                    if response == payload:
                        state.update(
                            ok=True,
                            association_attempts=association_attempts,
                            attempts=attempts,
                            ms=(time.monotonic() - started) * 1000.0,
                            error=last_error,
                        )
                        return
                    last_error = (
                        f"UDP mismatch expected={payload!r} observed={response!r}"
                    )
        except Exception as exc:
            last_error = f"{type(exc).__name__}: {exc}"

        time.sleep(
            min(
                RESET_RETRY_INTERVAL_SECONDS,
                max(0.0, deadline - time.monotonic()),
            )
        )

    state.update(
        ok=False,
        association_attempts=association_attempts,
        attempts=attempts,
        ms=None,
        error=last_error or "fresh Android UDP did not recover before deadline",
    )


def run_reset_stress(
    proxy_port: int,
    rendezvous: dict,
    initial_status: dict,
    cycles: int,
) -> dict:
    node_id = str(initial_status["node_id"])
    initial_reset_count = int(initial_status.get("reset_count", 0))
    rows: list[dict[str, object]] = []

    for cycle in range(1, cycles + 1):
        expected_reset_count = initial_reset_count + cycle
        stale = desktop_e2e.open_negotiated_socks_sentinel(
            proxy_port,
            timeout=5.0,
        )
        started = time.monotonic()
        deadline = started + RESET_BUDGET_SECONDS
        old_closed = threading.Event()
        old_state: dict[str, object] = {}
        tcp_state: dict[str, object] = {}
        udp_state: dict[str, object] = {}

        old_thread = threading.Thread(
            target=wait_stale_close,
            kwargs={
                "sock": stale,
                "started": started,
                "deadline": deadline,
                "closed": old_closed,
                "state": old_state,
            },
            daemon=True,
        )
        tcp_thread = threading.Thread(
            target=probe_tcp_after_abort,
            kwargs={
                "proxy_port": proxy_port,
                "rendezvous": rendezvous,
                "started": started,
                "deadline": deadline,
                "old_closed": old_closed,
                "state": tcp_state,
            },
            daemon=True,
        )
        udp_thread = threading.Thread(
            target=probe_udp_after_abort,
            kwargs={
                "proxy_port": proxy_port,
                "rendezvous": rendezvous,
                "cycle": cycle,
                "started": started,
                "deadline": deadline,
                "old_closed": old_closed,
                "state": udp_state,
            },
            daemon=True,
        )

        try:
            trigger_reset()
            for thread in (old_thread, tcp_thread, udp_thread):
                thread.start()
            for thread in (old_thread, tcp_thread, udp_thread):
                thread.join(
                    max(0.0, deadline - time.monotonic()) + 0.5
                )

            status = wait_for_status(
                timeout=8.0,
                expected_reset_count=expected_reset_count,
                expected_node_id=node_id,
            )

            errors: list[str] = []
            if old_state.get("error"):
                errors.append(f"old-close: {old_state['error']}")
            if not tcp_state.get("ok"):
                errors.append(f"tcp: {tcp_state.get('error')}")
            if not udp_state.get("ok"):
                errors.append(f"udp: {udp_state.get('error')}")

            old_ms = old_state.get("ms")
            tcp_ms = tcp_state.get("ms")
            udp_ms = udp_state.get("ms")
            passed = (
                isinstance(old_ms, float)
                and old_ms <= 3000.0
                and isinstance(tcp_ms, float)
                and tcp_ms <= 3000.0
                and isinstance(udp_ms, float)
                and udp_ms <= 3000.0
                and not errors
            )
            rows.append(
                {
                    "cycle": cycle,
                    "reset_count": int(status["reset_count"]),
                    "old_close_ms": old_ms,
                    "tcp_ms": tcp_ms,
                    "udp_ms": udp_ms,
                    "tcp_attempts": tcp_state.get("attempts", 0),
                    "udp_assoc_attempts": udp_state.get(
                        "association_attempts",
                        0,
                    ),
                    "udp_attempts": udp_state.get("attempts", 0),
                    "passed": passed,
                    "errors": errors,
                }
            )
        finally:
            stale.close()

        if cycle < cycles:
            time.sleep(1.0)

    lines = [
        "cycle\told_close_ms\ttcp_ms\tudp_ms\ttcp_attempts\t"
        "udp_assoc_attempts\tudp_attempts\tresult\terrors"
    ]
    for row in rows:
        def fmt(value: object) -> str:
            return "" if not isinstance(value, float) else f"{value:.1f}"

        lines.append(
            "\t".join(
                [
                    str(row["cycle"]),
                    fmt(row.get("old_close_ms")),
                    fmt(row.get("tcp_ms")),
                    fmt(row.get("udp_ms")),
                    str(row.get("tcp_attempts", 0)),
                    str(row.get("udp_assoc_attempts", 0)),
                    str(row.get("udp_attempts", 0)),
                    "PASS" if row["passed"] else "FAIL",
                    " | ".join(row["errors"]),
                ]
            )
        )
    (EVIDENCE / "reset_timing.tsv").write_text(
        "\n".join(lines) + "\n",
        encoding="utf-8",
    )

    passed = sum(1 for row in rows if row["passed"])
    metrics = {
        "cycles": cycles,
        "passed": passed,
        "failed": cycles - passed,
        "success": passed == cycles,
        "identity_preserved": True,
        "rows": rows,
    }
    (EVIDENCE / "reset_stress.json").write_text(
        json.dumps(metrics, indent=2),
        encoding="utf-8",
    )
    print("\n".join(lines))
    return metrics


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apk", required=True)
    parser.add_argument("--rendezvous-artifact", required=True)
    parser.add_argument("--listen-port", type=int, default=19080)
    parser.add_argument("--reset-cycles", type=int, default=3)
    args = parser.parse_args()

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    desktop_e2e.EVIDENCE = EVIDENCE
    result: dict[str, object] = {
        "success": False,
        "apk_installed": False,
        "arm_translation_available": False,
        "client_joined": False,
        "peer_local_service": False,
        "peer_local_udp": False,
        "udp_frag_rejected": False,
        "udp_default_exit_matches": False,
        "udp_control_close_cleanup": False,
        "egress_ip_matches": False,
        "reset_stress": False,
        "identity_preserved": False,
    }

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
                "Android rendezvous artifact did not contain rendezvous.json"
            )
        rendezvous = json.loads(files[0].read_text(encoding="utf-8"))
        result["rendezvous"] = rendezvous

        apk = Path(args.apk).resolve()
        if not apk.exists():
            raise FileNotFoundError(apk)

        adb("install", "-r", str(apk), timeout=180)
        result["apk_installed"] = True

        abi_list = adb(
            "shell",
            "getprop",
            "ro.product.cpu.abilist",
        ).stdout.strip()
        result["emulator_abi_list"] = abi_list
        if "arm64-v8a" not in {
            value.strip() for value in abi_list.split(",")
        }:
            raise RuntimeError(
                "Android emulator does not advertise arm64-v8a translation: "
                f"{abi_list}"
            )
        result["arm_translation_available"] = True

        adb(
            "forward",
            f"tcp:{args.listen_port}",
            "tcp:1080",
        )

        status = configure_and_start(rendezvous)
        result["client_status"] = status
        result["client_joined"] = True
        node_id = str(status["node_id"])

        desktop_e2e.retry(
            "android_peer_local_service",
            lambda: desktop_e2e.verify_peer_local_service(
                args.listen_port,
                str(rendezvous["server_ip"]),
                int(rendezvous["local_service_port"]),
            ),
        )
        result["peer_local_service"] = True

        desktop_e2e.retry(
            "android_peer_local_udp",
            lambda: desktop_e2e.verify_peer_local_udp(
                args.listen_port,
                str(rendezvous["server_ip"]),
                int(rendezvous["local_udp_service_port"]),
            ),
        )
        result["peer_local_udp"] = True

        desktop_e2e.retry(
            "android_udp_frag_rejected",
            lambda: desktop_e2e.verify_udp_frag_rejected(
                args.listen_port,
                str(rendezvous["server_ip"]),
                int(rendezvous["local_udp_service_port"]),
            ),
            attempts=3,
            delay=1.0,
        )
        result["udp_frag_rejected"] = True

        observed_udp_ip = desktop_e2e.retry(
            "android_udp_default_exit",
            lambda: desktop_e2e.udp_public_ip_via_socks(
                args.listen_port,
                str(rendezvous["stun_host"]),
                int(rendezvous["stun_port"]),
            ),
            attempts=4,
            delay=2.0,
        )
        result["observed_udp_public_ip"] = observed_udp_ip
        result["expected_udp_public_ip"] = rendezvous[
            "expected_udp_public_ip"
        ]
        if observed_udp_ip != rendezvous["expected_udp_public_ip"]:
            raise AssertionError(
                "Android UDP default_exit public IP mismatch: "
                f"{observed_udp_ip} != {rendezvous['expected_udp_public_ip']}"
            )
        result["udp_default_exit_matches"] = True

        desktop_e2e.retry(
            "android_udp_control_close_cleanup",
            lambda: desktop_e2e.verify_udp_control_close_cleanup(
                args.listen_port,
                str(rendezvous["server_ip"]),
                int(rendezvous["local_udp_service_port"]),
            ),
            attempts=3,
            delay=1.0,
        )
        result["udp_control_close_cleanup"] = True

        observed_ip = desktop_e2e.retry(
            "android_egress",
            lambda: desktop_e2e.https_public_ip_via_socks(
                args.listen_port
            ),
        )
        result["observed_public_ip"] = observed_ip
        result["expected_public_ip"] = rendezvous["expected_public_ip"]
        if observed_ip != rendezvous["expected_public_ip"]:
            raise AssertionError(
                "Android default_exit public IP mismatch: "
                f"{observed_ip} != {rendezvous['expected_public_ip']}"
            )
        result["egress_ip_matches"] = True

        reset_metrics = run_reset_stress(
            args.listen_port,
            rendezvous,
            status,
            args.reset_cycles,
        )
        result["reset_metrics"] = reset_metrics
        result["reset_stress"] = bool(reset_metrics["success"])
        result["identity_preserved"] = bool(
            reset_metrics["identity_preserved"]
        )
        if not reset_metrics["success"]:
            raise AssertionError(
                "Android runtime reset stress failed: "
                f"{reset_metrics['passed']}/{reset_metrics['cycles']} "
                "cycles passed"
            )

        final_status = wait_for_status(
            timeout=5,
            expected_reset_count=int(status.get("reset_count", 0))
            + args.reset_cycles,
            expected_node_id=node_id,
        )
        result["final_status"] = final_status
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
        EVIDENCE.mkdir(parents=True, exist_ok=True)
        write_adb_diagnostics()
        try:
            adb(
                "forward",
                "--remove",
                f"tcp:{args.listen_port}",
                check=False,
            )
        except Exception:
            pass
        try:
            adb(
                "shell",
                "am",
                "force-stop",
                PACKAGE,
                check=False,
            )
        except Exception:
            pass
        (EVIDENCE / "result.json").write_text(
            json.dumps(result, indent=2),
            encoding="utf-8",
        )


if __name__ == "__main__":
    raise SystemExit(main())
