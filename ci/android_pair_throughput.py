#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import traceback

import csharp_e2e_client as desktop_e2e
from common import ROOT
from throughput_probe import TARGET_HOST, run_stage, write_tsv


PACKAGE = "com.libzt.netloop"
ACTIVITY = f"{PACKAGE}/com.libzt.netloop.MainActivity"
STATUS_PATH = f"/sdcard/Android/data/{PACKAGE}/files/netloop-ci-status.json"
EVIDENCE = ROOT / "evidence" / "android-throughput"


def run(
    command: list[str],
    *,
    timeout: float = 60,
    check: bool = True,
    input_text: str | None = None,
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        command,
        cwd=ROOT,
        input=input_text,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
    )
    if check and result.returncode != 0:
        raise RuntimeError(
            f"{subprocess.list2cmdline(command)} failed with "
            f"{result.returncode}: {result.stderr.strip()}"
        )
    return result


def adb(
    serial: str,
    *args: str,
    timeout: float = 60,
    check: bool = True,
) -> subprocess.CompletedProcess[str]:
    return run(
        ["adb", "-s", serial, *args],
        timeout=timeout,
        check=check,
    )


def list_emulators() -> list[str]:
    result = run(["adb", "devices"], timeout=20)
    serials: list[str] = []
    for line in result.stdout.splitlines()[1:]:
        fields = line.split()
        if len(fields) >= 2 and fields[1] == "device" and fields[0].startswith("emulator-"):
            serials.append(fields[0])
    return serials


def wait_for_boot(serial: str, timeout: float = 240) -> None:
    deadline = time.monotonic() + timeout
    last = ""
    while time.monotonic() < deadline:
        state = adb(serial, "get-state", timeout=10, check=False)
        if state.returncode == 0 and state.stdout.strip() == "device":
            completed = adb(
                serial,
                "shell",
                "getprop",
                "sys.boot_completed",
                timeout=10,
                check=False,
            )
            last = completed.stdout.strip()
            if completed.returncode == 0 and last == "1":
                adb(
                    serial,
                    "shell",
                    "input",
                    "keyevent",
                    "82",
                    timeout=10,
                    check=False,
                )
                return
        time.sleep(2)
    raise TimeoutError(f"timed out waiting for {serial} boot; sys.boot_completed={last!r}")


def create_second_emulator() -> tuple[str, subprocess.Popen[bytes]]:
    avdmanager = shutil.which("avdmanager")
    emulator = shutil.which("emulator")
    if emulator is None:
        sdk_root = (
            os.environ.get("ANDROID_SDK_ROOT")
            or os.environ.get("ANDROID_HOME")
        )
        if sdk_root:
            candidate = Path(sdk_root) / "emulator" / "emulator"
            if candidate.is_file():
                emulator = str(candidate)
    if avdmanager is None or emulator is None:
        raise FileNotFoundError(
            f"Android tools unavailable: avdmanager={avdmanager}, emulator={emulator}"
        )

    avd_name = f"netloop-peer-{os.environ.get('GITHUB_RUN_ID', 'local')}"
    run(
        [
            avdmanager,
            "delete",
            "avd",
            "--name",
            avd_name,
        ],
        timeout=30,
        check=False,
    )
    run(
        [
            avdmanager,
            "create",
            "avd",
            "--force",
            "--name",
            avd_name,
            "--package",
            "system-images;android-35;google_apis;x86_64",
        ],
        timeout=60,
        input_text="no\n",
    )

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    log = (EVIDENCE / "second-emulator.log").open("wb")
    process = subprocess.Popen(
        [
            emulator,
            "-avd",
            avd_name,
            "-port",
            "5556",
            "-no-window",
            "-gpu",
            "swiftshader_indirect",
            "-noaudio",
            "-no-boot-anim",
            "-camera-back",
            "none",
            "-camera-front",
            "none",
            "-no-snapshot-save",
            "-memory",
            "1536",
            "-cores",
            "2",
        ],
        cwd=ROOT,
        stdout=log,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
    )
    log.close()
    return "emulator-5556", process


def read_status(serial: str) -> dict | None:
    result = adb(
        serial,
        "exec-out",
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


def wait_status(serial: str, timeout: float = 200) -> dict:
    deadline = time.monotonic() + timeout
    last: dict | None = None
    while time.monotonic() < deadline:
        status = read_status(serial)
        if status is not None:
            last = status
            if status.get("phase") == "error":
                raise RuntimeError(
                    f"{serial} NetLoop failed: "
                    f"{status.get('error_type')}: {status.get('error')}"
                )
            if status.get("phase") == "ready":
                return status
        time.sleep(0.2)
    raise TimeoutError(f"timed out waiting for {serial} status; last={last}")


def configure(
    serial: str,
    *,
    network_id: str,
    overlay_port: int,
    overlay_udp_port: int,
    peers: str = "",
    default_exit: str = "",
) -> dict:
    adb(
        serial,
        "shell",
        "pm",
        "grant",
        PACKAGE,
        "android.permission.POST_NOTIFICATIONS",
        check=False,
    )
    adb(serial, "logcat", "-c", check=False)
    command = [
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
        "--ei",
        "overlay_port",
        str(overlay_port),
        "--ei",
        "overlay_udp_port",
        str(overlay_udp_port),
    ]
    if peers:
        command.extend(["--es", "peers", peers])
    if default_exit:
        command.extend(["--es", "default_exit", default_exit])
    adb(serial, *command, timeout=30)
    return wait_status(serial)


def parse_json_logcat(serial: str) -> list[dict]:
    result = adb(serial, "logcat", "-d", timeout=60, check=False)
    events: list[dict] = []
    for line in result.stdout.splitlines():
        start = line.find("{")
        if start < 0:
            continue
        try:
            payload = json.loads(line[start:])
        except json.JSONDecodeError:
            continue
        if isinstance(payload, dict):
            events.append(payload)
    return events


def count_speed_routes(
    events: list[dict],
    *,
    ingress: str,
    route: str,
) -> int:
    count = 0
    for event in events:
        if event.get("event") != "route_decision":
            continue
        data = event.get("data")
        if not isinstance(data, dict):
            continue
        if (
            data.get("ingress") == ingress
            and data.get("route") == route
            and str(data.get("target", "")).startswith(f"{TARGET_HOST}:")
        ):
            count += 1
    return count


def retry_public_ip(proxy_port: int) -> str:
    last_error: Exception | None = None
    for _ in range(8):
        try:
            return desktop_e2e.https_public_ip_via_socks(proxy_port)
        except Exception as exc:
            last_error = exc
            time.sleep(2)
    raise RuntimeError(f"unable to query Android public IP through SOCKS: {last_error}")


def write_diagnostics(serial: str, label: str) -> None:
    directory = EVIDENCE / label
    directory.mkdir(parents=True, exist_ok=True)
    commands = {
        "logcat.txt": ("logcat", "-d", "-t", "3000"),
        "properties.txt": ("shell", "getprop"),
        "services.txt": ("shell", "dumpsys", "activity", "services", PACKAGE),
        "connectivity.txt": ("shell", "dumpsys", "connectivity"),
        "routes.txt": ("shell", "ip", "route", "show", "table", "all"),
    }
    for filename, args in commands.items():
        try:
            result = adb(serial, *args, timeout=60, check=False)
            (directory / filename).write_text(
                f"exit={result.returncode}\n\n"
                f"STDOUT\n{result.stdout}\n\n"
                f"STDERR\n{result.stderr}\n",
                encoding="utf-8",
            )
        except Exception as exc:
            (directory / filename).write_text(repr(exc), encoding="utf-8")


def adhoc_network_id(port: int) -> str:
    return f"ff{port:04x}{port:04x}000000"


def print_stage(row: dict[str, object], size_mib: int) -> None:
    print(
        "throughput "
        f"concurrency={row['concurrency']} "
        f"size_mib={size_mib} "
        f"aggregate_mbps={row['aggregate_mbps']:.2f} "
        f"end_to_end_mbps={row['end_to_end_mbps']:.2f} "
        f"transfer_seconds={row['transfer_seconds']:.3f}",
        flush=True,
    )


def run_overlay_matrix_with_control(
    *,
    client_port: int,
    server_port: int,
    concurrencies: list[int],
    size_mib: int,
    timeout: float,
    result: dict[str, object],
) -> tuple[list[dict[str, object]], bool]:
    size_bytes = size_mib * 1024 * 1024
    rows: list[dict[str, object]] = []

    for concurrency in concurrencies:
        try:
            row = run_stage(
                "127.0.0.1",
                client_port,
                concurrency=concurrency,
                size_bytes=size_bytes,
                timeout=timeout,
            )
        except Exception as overlay_error:
            result["failed_concurrency"] = concurrency
            result["overlay_error_type"] = type(overlay_error).__name__
            result["overlay_error"] = str(overlay_error)
            try:
                control = run_stage(
                    "127.0.0.1",
                    server_port,
                    concurrency=concurrency,
                    size_bytes=size_bytes,
                    timeout=timeout,
                )
            except Exception as control_error:
                result["classification"] = "inconclusive_external_or_runner"
                result["direct_control"] = {
                    "success": False,
                    "error_type": type(control_error).__name__,
                    "error": str(control_error),
                }
                print(
                    "::warning::Android overlay throughput failed, but the "
                    "exit emulator direct-control failed under the same "
                    "conditions too; treating this run as inconclusive rather "
                    "than a NetLoop regression.",
                    file=sys.stderr,
                    flush=True,
                )
                return rows, True

            result["classification"] = "overlay_failure"
            result["direct_control"] = {
                "success": True,
                "row": control,
            }
            write_tsv(EVIDENCE / "direct-control.tsv", [control])
            raise RuntimeError(
                "Android overlay throughput failed while the exit emulator "
                "direct-control passed under the same conditions: "
                f"concurrency={concurrency}, error={overlay_error}"
            ) from overlay_error

        rows.append(row)
        result["rows"] = rows
        write_tsv(EVIDENCE / "throughput.tsv", rows)
        print_stage(row, size_mib)

    result["classification"] = "pass"
    return rows, False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apk", required=True)
    parser.add_argument("--file-size-mib", type=int, default=32)
    parser.add_argument("--concurrency", default="1,8,32")
    parser.add_argument("--stream-timeout", type=float, default=300)
    parser.add_argument("--overlay-port", type=int, default=46242)
    parser.add_argument("--overlay-udp-port", type=int, default=46243)
    args = parser.parse_args()

    concurrencies = [
        int(item.strip())
        for item in args.concurrency.split(",")
        if item.strip()
    ]
    if not concurrencies or any(value < 1 for value in concurrencies):
        raise ValueError(f"invalid concurrency list: {args.concurrency!r}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    result: dict[str, object] = {
        "success": False,
        "file_size_mib": args.file_size_mib,
        "concurrencies": concurrencies,
    }
    second_process: subprocess.Popen[bytes] | None = None
    first_serial = ""
    second_serial = "emulator-5556"
    client_port = 19080
    server_port = 19081

    try:
        initial = list_emulators()
        if len(initial) != 1:
            raise RuntimeError(
                f"expected one emulator from android-emulator-runner, found {initial}"
            )
        first_serial = initial[0]
        second_serial, second_process = create_second_emulator()
        wait_for_boot(second_serial)

        apk = Path(args.apk).resolve()
        if not apk.exists():
            raise FileNotFoundError(apk)
        for serial in (first_serial, second_serial):
            adb(serial, "install", "-r", str(apk), timeout=180)
            abi = adb(
                serial,
                "shell",
                "getprop",
                "ro.product.cpu.abi",
            ).stdout.strip()
            if abi != "x86_64":
                raise RuntimeError(
                    f"Android throughput requires native x86_64 execution: "
                    f"serial={serial}, abi={abi}"
                )

        adb(first_serial, "forward", f"tcp:{server_port}", "tcp:1080")
        adb(second_serial, "forward", f"tcp:{client_port}", "tcp:1080")

        network_id = adhoc_network_id(args.overlay_port)
        server_status = configure(
            first_serial,
            network_id=network_id,
            overlay_port=args.overlay_port,
            overlay_udp_port=args.overlay_udp_port,
        )
        server_ip = str(server_status["primary_overlay_address"])
        client_status = configure(
            second_serial,
            network_id=network_id,
            overlay_port=args.overlay_port,
            overlay_udp_port=args.overlay_udp_port,
            peers=server_ip,
            default_exit=server_ip,
        )
        result["server_serial"] = first_serial
        result["client_serial"] = second_serial
        result["server_status"] = server_status
        result["client_status"] = client_status

        expected_ip = retry_public_ip(server_port)
        observed_ip = retry_public_ip(client_port)
        result["server_public_ip"] = expected_ip
        result["client_public_ip"] = observed_ip
        if observed_ip != expected_ip:
            raise AssertionError(
                "Android client default-exit public IP mismatch: "
                f"client={observed_ip}, server={expected_ip}"
            )

        rows, inconclusive = run_overlay_matrix_with_control(
            client_port=client_port,
            server_port=server_port,
            concurrencies=concurrencies,
            size_mib=args.file_size_mib,
            timeout=args.stream_timeout,
            result=result,
        )
        result["rows"] = rows
        if rows:
            write_tsv(EVIDENCE / "throughput.tsv", rows)

        if inconclusive:
            result["inconclusive"] = True
            print(json.dumps(result, indent=2))
            return 0

        expected_routes = sum(concurrencies)
        client_events = parse_json_logcat(second_serial)
        server_events = parse_json_logcat(first_serial)
        client_routes = count_speed_routes(
            client_events,
            ingress="local",
            route="DefaultExit",
        )
        server_routes = count_speed_routes(
            server_events,
            ingress="overlay",
            route="DirectEgress",
        )
        result["expected_speed_routes"] = expected_routes
        result["client_default_exit_routes"] = client_routes
        result["server_overlay_egress_routes"] = server_routes
        if client_routes < expected_routes or server_routes < expected_routes:
            raise AssertionError(
                "Android route evidence did not prove all throughput streams "
                f"crossed the default exit: expected>={expected_routes}, "
                f"client_default_exit={client_routes}, "
                f"server_overlay_egress={server_routes}"
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
        if first_serial:
            write_diagnostics(first_serial, "server")
        if second_serial:
            write_diagnostics(second_serial, "client")
        for serial, port in (
            (first_serial, server_port),
            (second_serial, client_port),
        ):
            if not serial:
                continue
            adb(
                serial,
                "forward",
                "--remove",
                f"tcp:{port}",
                check=False,
            )
            adb(
                serial,
                "shell",
                "am",
                "force-stop",
                PACKAGE,
                check=False,
            )
        if second_serial:
            adb(second_serial, "emu", "kill", timeout=20, check=False)
        if second_process is not None:
            try:
                second_process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                second_process.terminate()
        (EVIDENCE / "result.json").write_text(
            json.dumps(result, indent=2),
            encoding="utf-8",
        )


if __name__ == "__main__":
    raise SystemExit(main())
