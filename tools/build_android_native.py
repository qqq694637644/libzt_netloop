#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import time


ROOT = Path(__file__).resolve().parents[1]
PATCHES = [
    ROOT / "patches" / "libzt" / "0001-host-network-change-refresh.patch",
    ROOT / "patches" / "libzt" / "0002-android-pinvoke.patch",
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


def apply_patches() -> None:
    libzt = ROOT / "external" / "libzt"
    for patch in PATCHES:
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


def resolve_ndk(explicit: str | None) -> Path:
    candidates = [
        explicit,
        os.environ.get("ANDROID_NDK_HOME"),
        os.environ.get("ANDROID_NDK_ROOT"),
        os.environ.get("ANDROID_NDK"),
    ]
    for candidate in candidates:
        if not candidate:
            continue
        path = Path(candidate).expanduser().resolve()
        toolchain = path / "build" / "cmake" / "android.toolchain.cmake"
        if toolchain.exists():
            return path
    raise FileNotFoundError(
        "Android NDK not found. Pass --ndk or set ANDROID_NDK_HOME."
    )


def read_ndk_revision(ndk: Path) -> str:
    source_properties = ndk / "source.properties"
    if not source_properties.exists():
        return "unknown"
    for line in source_properties.read_text(encoding="utf-8").splitlines():
        if line.startswith("Pkg.Revision"):
            return line.split("=", 1)[1].strip()
    return "unknown"


def find_libzt(build_dir: Path) -> Path:
    candidates = sorted(
        build_dir.rglob("libzt.so"),
        key=lambda path: len(path.parts),
    )
    if not candidates:
        raise FileNotFoundError(
            f"unable to locate Android libzt.so under {build_dir}"
        )
    return candidates[0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ndk")
    parser.add_argument("--api", type=int, default=24)
    parser.add_argument("--abi", default="arm64-v8a")
    parser.add_argument(
        "--build-dir",
        default=str(ROOT / "build-android-native"),
    )
    parser.add_argument(
        "--output",
        default=str(ROOT / "native-cache" / "android-arm64" / "libzt.so"),
    )
    parser.add_argument("--parallel", type=int)
    parser.add_argument("--use-sccache", action="store_true")
    parser.add_argument("--clean", action="store_true")
    args = parser.parse_args()

    if args.abi not in {"arm64-v8a", "x86_64"}:
        raise ValueError(
            "NetLoop Android supports arm64-v8a release builds and "
            "x86_64 CI emulator builds."
        )
    if args.api < 24:
        raise ValueError("Android API level must be >= 24.")

    started = time.monotonic()
    ndk = resolve_ndk(args.ndk)
    toolchain = ndk / "build" / "cmake" / "android.toolchain.cmake"

    ensure_submodules()
    apply_patches()

    build_dir = Path(args.build_dir).resolve()
    if args.clean and build_dir.exists():
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
        f"-DCMAKE_TOOLCHAIN_FILE={toolchain}",
        f"-DANDROID_ABI={args.abi}",
        f"-DANDROID_PLATFORM=android-{args.api}",
        "-DZTS_NDK_ONLY=ON",
    ]
    if args.use_sccache:
        configure.extend(
            [
                "-DCMAKE_C_COMPILER_LAUNCHER=sccache",
                "-DCMAKE_CXX_COMPILER_LAUNCHER=sccache",
            ]
        )
    run(configure)

    parallel = str(args.parallel or max(1, min(4, os.cpu_count() or 1)))
    run(
        [
            "cmake",
            "--build",
            str(build_dir),
            "--target",
            "zt-shared",
            "--parallel",
            parallel,
        ]
    )

    built = find_libzt(build_dir)
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(built, output)

    libzt_tree = git_output("ls-tree", "HEAD", "external/libzt").split()
    if len(libzt_tree) < 3:
        raise RuntimeError("unable to resolve external/libzt gitlink")

    manifest = {
        "libzt_commit": libzt_tree[2],
        "cmake_sha": git_output("hash-object", "CMakeLists.txt"),
        "build_script_sha": git_output(
            "hash-object", "tools/build_android_native.py"
        ),
        "patches": {
            patch.name: git_output(
                "hash-object",
                str(patch.relative_to(ROOT)),
            )
            for patch in PATCHES
        },
        "ndk_revision": read_ndk_revision(ndk),
        "android_abi": args.abi,
        "android_api": args.api,
        "source": str(
            built.relative_to(ROOT) if built.is_relative_to(ROOT) else built
        ),
        "output": str(output),
        "duration_seconds": round(time.monotonic() - started, 3),
        "sccache": bool(args.use_sccache),
    }
    manifest_path = output.with_suffix(output.suffix + ".manifest.json")
    manifest_path.write_text(
        json.dumps(manifest, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(manifest, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
