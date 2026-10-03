#!/usr/bin/env python3
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parents[1]


def run_logged(command: list[str], log, cwd: Path = ROOT) -> None:
    print("+", subprocess.list2cmdline(command), flush=True)
    log.write("+ " + subprocess.list2cmdline(command) + "\n")
    log.flush()
    process = subprocess.Popen(
        command,
        cwd=cwd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    assert process.stdout is not None
    for line in process.stdout:
        print(line, end="")
        log.write(line)
    return_code = process.wait()
    log.flush()
    if return_code:
        raise subprocess.CalledProcessError(return_code, command)


def locate_executable(build_dir: Path, name: str) -> Path:
    matches = list(build_dir.rglob(name))
    if not matches:
        raise FileNotFoundError(f"{name} was not produced under {build_dir}")
    release_matches = [path for path in matches if "Release" in path.parts]
    return release_matches[0] if release_matches else matches[0]


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Windows libzt_netloop binaries.")
    parser.add_argument("--config", default="Release", choices=["Release", "Debug"])
    parser.add_argument("--build-dir", default=str(ROOT / "build"))
    parser.add_argument("--dist-dir", default=str(ROOT / "dist"))
    parser.add_argument("--skip-submodules", action="store_true")
    args = parser.parse_args()

    evidence = ROOT / "evidence" / "build"
    evidence.mkdir(parents=True, exist_ok=True)
    log_path = evidence / "build.log"
    started = time.time()

    if os.name != "nt":
        print("This project intentionally builds only on Windows.", file=sys.stderr)
        return 2

    build_dir = Path(args.build_dir).resolve()
    dist_dir = Path(args.dist_dir).resolve()
    build_dir.mkdir(parents=True, exist_ok=True)
    dist_dir.mkdir(parents=True, exist_ok=True)

    try:
        with log_path.open("w", encoding="utf-8") as log:
            if not args.skip_submodules:
                run_logged(["git", "submodule", "update", "--init", "--recursive"], log)
            run_logged(
                [
                    "cmake",
                    "-S",
                    str(ROOT),
                    "-B",
                    str(build_dir),
                    "-A",
                    "x64",
                    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5",
                ],
                log,
            )
            run_logged(
                [
                    "cmake",
                    "--build",
                    str(build_dir),
                    "--config",
                    args.config,
                    "--target",
                    "zt_netloop_client",
                    "zt_netloop_server",
                    "--parallel",
                    "4",
                ],
                log,
            )

        binaries = {}
        for name in ("zt_netloop_client.exe", "zt_netloop_server.exe"):
            source = locate_executable(build_dir, name)
            destination = dist_dir / name
            shutil.copy2(source, destination)
            binaries[name] = {
                "source": str(source),
                "destination": str(destination),
                "bytes": destination.stat().st_size,
            }

        manifest = {
            "config": args.config,
            "duration_seconds": round(time.time() - started, 3),
            "binaries": binaries,
        }
        (dist_dir / "manifest.json").write_text(
            json.dumps(manifest, indent=2), encoding="utf-8"
        )
        print(json.dumps(manifest, indent=2))
        return 0
    except Exception as exc:
        failure = {
            "type": type(exc).__name__,
            "message": str(exc),
            "duration_seconds": round(time.time() - started, 3),
            "log": str(log_path),
        }
        (evidence / "failure.json").write_text(
            json.dumps(failure, indent=2), encoding="utf-8"
        )
        print(json.dumps(failure, indent=2), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
