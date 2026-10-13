#!/usr/bin/env python3
"""Stable cache fingerprint shared by all Native build workflows.

The patch manifests in the build scripts are the only source of patch order.
Do not separately duplicate patch lists in GitHub Actions YAML.
"""
from __future__ import annotations

import argparse
from hashlib import sha256
from pathlib import Path
import subprocess

from build_csharp import ROOT, LIBZT_PATCHES, ZERO_TIER_ONE_PATCHES


def fingerprint_files(paths: list[Path], *, identity: str) -> str:
    digest = sha256()
    digest.update(b"netloop-native-cache-v1\0")
    digest.update(identity.encode("utf-8") + b"\0")
    for path in paths:
        if not path.is_file():
            raise FileNotFoundError(path)
        digest.update(path.relative_to(ROOT).as_posix().encode("utf-8") + b"\0")
        digest.update(sha256(path.read_bytes()).digest())
    return digest.hexdigest()[:40]


def native_cache_key(platform: str, arch: str, ndk_revision: str = "") -> str:
    if platform not in {"windows", "linux", "android"}:
        raise ValueError("unsupported native platform")
    gitlink = subprocess.check_output(
        ["git", "ls-tree", "HEAD", "external/libzt"],
        cwd=ROOT, text=True, encoding="utf-8"
    ).split()
    if len(gitlink) < 3:
        raise RuntimeError("external/libzt pinned gitlink is missing")

    build_script = ROOT / "tools" / (
        "build_android_native.py" if platform == "android" else "build_csharp.py"
    )
    if platform == "android":
        from build_android_native import PATCHES
        libzt_patches = PATCHES
    else:
        libzt_patches = LIBZT_PATCHES

    paths = [
        ROOT / "CMakeLists.txt",
        build_script,
        ROOT / "tools" / "native_cache_key.py",
        *libzt_patches,
        *ZERO_TIER_ONE_PATCHES,
    ]
    return fingerprint_files(
        paths, identity=f"{platform}|{arch}|{ndk_revision}|{gitlink[2]}"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["windows", "linux", "android"])
    parser.add_argument("--arch", required=True)
    parser.add_argument("--ndk-revision", default="")
    args = parser.parse_args()
    print(native_cache_key(args.platform, args.arch, args.ndk_revision))


if __name__ == "__main__":
    main()
