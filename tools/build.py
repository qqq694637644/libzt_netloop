#!/usr/bin/env python3
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import re


ROOT = Path(__file__).resolve().parents[1]


def normalize_version(value: str) -> str:
    version = value.strip()
    if version.startswith("v"):
        version = version[1:]
    if not version or not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z._+-]*", version):
        raise ValueError(f"invalid version: {value!r}")
    return version


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


def find_sccache() -> str | None:
    configured = os.environ.get("SCCACHE_PATH", "").strip()
    if configured and Path(configured).is_file():
        return configured
    return shutil.which("sccache")


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Windows libzt_netloop binaries.")
    parser.add_argument("--config", default="Release", choices=["Release", "Debug"])
    parser.add_argument("--build-dir", default=str(ROOT / "build"))
    parser.add_argument("--dist-dir", default=str(ROOT / "dist"))
    parser.add_argument("--version", default="0.1.0")
    parser.add_argument("--generator", default="auto", choices=["auto", "ninja"])
    parser.add_argument("--use-sccache", action="store_true")
    parser.add_argument("--skip-submodules", action="store_true")
    args = parser.parse_args()
    version = normalize_version(args.version)

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

    if args.use_sccache and args.generator != "ninja":
        print("--use-sccache requires --generator ninja because CMake compiler launchers are not supported by Visual Studio generators", file=sys.stderr)
        return 2
    sccache = find_sccache() if args.use_sccache else None
    if args.use_sccache and not sccache:
        print("--use-sccache was requested but sccache was not found", file=sys.stderr)
        return 2
    if args.generator == "ninja" and not shutil.which("ninja"):
        print("Ninja generator was requested but ninja was not found", file=sys.stderr)
        return 2

    try:
        with log_path.open("w", encoding="utf-8") as log:
            if not args.skip_submodules:
                run_logged(["git", "submodule", "update", "--init", "--recursive"], log)
            configure_command = [
                "cmake",
                "-S",
                str(ROOT),
                "-B",
                str(build_dir),
                "-DCMAKE_POLICY_VERSION_MINIMUM=3.5",
                f"-DNETLOOP_VERSION={version}",
            ]
            if args.generator == "ninja":
                configure_command.extend(
                    [
                        "-G",
                        "Ninja",
                        f"-DCMAKE_BUILD_TYPE={args.config}",
                    ]
                )
            else:
                configure_command.extend(["-A", "x64"])

            if sccache:
                configure_command.extend(
                    [
                        "-DCMAKE_C_COMPILER_LAUNCHER=sccache",
                        "-DCMAKE_CXX_COMPILER_LAUNCHER=sccache",
                    ]
                )

            run_logged(configure_command, log)

            build_command = [
                "cmake",
                "--build",
                str(build_dir),
            ]
            if args.generator != "ninja":
                build_command.extend(["--config", args.config])
            build_command.extend(
                [
                    "--target",
                    "zt_netloop_client",
                    "zt_netloop_server",
                    "--parallel",
                    "4",
                ]
            )
            run_logged(build_command, log)

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
            "version": version,
            "generator": args.generator,
            "sccache": bool(sccache),
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
