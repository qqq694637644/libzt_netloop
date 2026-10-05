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


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_BUILD = ROOT / "build-csharp-native"
DEFAULT_DIST = ROOT / "dist-csharp"
LIBZT_PATCHES = [
    ROOT / "patches" / "libzt" / "0001-host-network-change-refresh.patch",
]


def run(command: list[str], *, cwd: Path = ROOT, env: dict[str, str] | None = None) -> None:
    print("+", subprocess.list2cmdline(command), flush=True)
    subprocess.run(command, cwd=cwd, env=env, check=True)


def git_output(*args: str) -> str:
    return subprocess.check_output(
        ["git", *args],
        cwd=ROOT,
        text=True,
        encoding="utf-8",
    ).strip()


def ensure_submodules() -> None:
    run(["git", "submodule", "update", "--init", "--recursive"])


def apply_libzt_patches() -> None:
    libzt = ROOT / "external" / "libzt"
    for patch in LIBZT_PATCHES:
        if not patch.exists():
            raise FileNotFoundError(patch)

        check = subprocess.run(
            ["git", "apply", "--check", str(patch)],
            cwd=libzt,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        if check.returncode == 0:
            run(["git", "apply", str(patch)], cwd=libzt)
            continue

        reverse = subprocess.run(
            ["git", "apply", "--reverse", "--check", str(patch)],
            cwd=libzt,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        if reverse.returncode != 0:
            raise RuntimeError(
                f"libzt patch does not apply cleanly: {patch}\n"
                f"apply: {check.stderr}\nreverse: {reverse.stderr}"
            )


def native_cache_identity() -> dict[str, str]:
    libzt_tree = git_output("ls-tree", "HEAD", "external/libzt").split()
    if len(libzt_tree) < 3:
        raise RuntimeError("unable to resolve external/libzt gitlink")
    identity = {
        "libzt_commit": libzt_tree[2],
        "cmake_sha": git_output("hash-object", "CMakeLists.txt"),
        "build_script_sha": git_output("hash-object", "tools/build_csharp.py"),
    }
    for index, patch in enumerate(LIBZT_PATCHES, start=1):
        identity[f"libzt_patch_{index}_sha"] = git_output(
            "hash-object",
            str(patch.relative_to(ROOT)),
        )
    return identity


def find_native_library(build_dir: Path) -> Path:
    candidates: list[Path] = []
    for pattern in ("zt-shared.dll", "libzt.so", "libzt.dylib"):
        candidates.extend(build_dir.rglob(pattern))
    if not candidates:
        raise FileNotFoundError(f"unable to locate libzt shared library under {build_dir}")
    candidates.sort(key=lambda path: len(path.parts))
    return candidates[0]


def build_native(args: argparse.Namespace) -> Path:
    started = time.monotonic()
    ensure_submodules()
    apply_libzt_patches()

    build_dir = Path(args.native_build_dir).resolve()
    if args.clean_native and build_dir.exists():
        shutil.rmtree(build_dir)
    build_dir.mkdir(parents=True, exist_ok=True)

    configure = [
        "cmake",
        "-S",
        str(ROOT),
        "-B",
        str(build_dir),
        "-G",
        "Ninja",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DCMAKE_POLICY_VERSION_MINIMUM=3.5",
        "-DNETLOOP_CSHARP_NATIVE=ON",
    ]
    if args.use_sccache:
        configure.extend([
            "-DCMAKE_C_COMPILER_LAUNCHER=sccache",
            "-DCMAKE_CXX_COMPILER_LAUNCHER=sccache",
        ])
    run(configure)

    parallel = str(args.parallel or max(1, min(4, os.cpu_count() or 1)))
    run([
        "cmake",
        "--build",
        str(build_dir),
        "--target",
        "zt-shared",
        "--parallel",
        parallel,
    ])

    built = find_native_library(build_dir)
    output = Path(args.native_output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(built, output)

    identity = native_cache_identity()
    manifest = {
        **identity,
        "source": str(built.relative_to(ROOT) if built.is_relative_to(ROOT) else built),
        "output": str(output),
        "duration_seconds": round(time.monotonic() - started, 3),
        "sccache": bool(args.use_sccache),
    }
    manifest_path = output.with_suffix(output.suffix + ".manifest.json")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2), flush=True)
    return output


def build_managed(args: argparse.Namespace) -> None:
    started = time.monotonic()
    native_input = Path(args.native_input).resolve()
    if not native_input.exists():
        raise FileNotFoundError(native_input)

    project = ROOT / "src-csharp" / "NetLoop.Host" / "NetLoop.Host.csproj"
    dist = Path(args.output).resolve()
    if dist.exists():
        shutil.rmtree(dist)
    dist.mkdir(parents=True, exist_ok=True)

    restore = [
        "dotnet",
        "restore",
        str(project),
        "--runtime",
        args.runtime,
    ]
    if args.self_contained:
        restore.append("-p:SelfContained=true")
    run(restore)
    publish = [
        "dotnet",
        "publish",
        str(project),
        "--configuration",
        "Release",
        "--runtime",
        args.runtime,
        "--self-contained",
        "true" if args.self_contained else "false",
        "--no-restore",
        "--output",
        str(dist),
    ]
    if args.version:
        publish.extend([
            f"-p:Version={args.version.split('+', 1)[0]}",
            f"-p:InformationalVersion={args.version}",
            "-p:IncludeSourceRevisionInInformationalVersion=false",
        ])
    run(publish)

    native_name = "libzt.dll" if args.runtime.startswith("win-") else "libzt.so"
    shutil.copy2(native_input, dist / native_name)

    manifest = {
        **native_cache_identity(),
        "runtime": args.runtime,
        "native_file": native_name,
        "self_contained": bool(args.self_contained),
        "version": args.version,
        "duration_seconds": round(time.monotonic() - started, 3),
    }
    (dist / "build-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(manifest, indent=2), flush=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--native-only", action="store_true")
    mode.add_argument("--managed-only", action="store_true")
    parser.add_argument("--native-build-dir", default=str(DEFAULT_BUILD))
    parser.add_argument("--native-output", default=str(ROOT / "native-cache" / "libzt.dll"))
    parser.add_argument("--native-input", default=str(ROOT / "native-cache" / "libzt.dll"))
    parser.add_argument("--output", default=str(DEFAULT_DIST))
    parser.add_argument("--runtime", default="win-x64")
    parser.add_argument("--self-contained", action="store_true")
    parser.add_argument("--version")
    parser.add_argument("--parallel", type=int)
    parser.add_argument("--use-sccache", action="store_true")
    parser.add_argument("--clean-native", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    started = time.monotonic()

    if args.native_only:
        build_native(args)
    elif args.managed_only:
        build_managed(args)
    else:
        native = build_native(args)
        args.native_input = str(native)
        build_managed(args)

    print(
        json.dumps(
            {
                "phase": "build_csharp_complete",
                "duration_seconds": round(time.monotonic() - started, 3),
            },
            indent=2,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as exc:
        print(f"command failed with exit code {exc.returncode}", file=sys.stderr)
        raise
