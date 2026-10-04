#!/usr/bin/env python3
import argparse
from pathlib import Path
import shutil


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--destination", default="runtime")
    parser.add_argument(
        "--all-files",
        action="store_true",
        help="Copy the entire artifact tree instead of the legacy C++ executables only.",
    )
    args = parser.parse_args()

    source = Path(args.source).resolve()
    destination = Path(args.destination).resolve()
    destination.mkdir(parents=True, exist_ok=True)

    if args.all_files:
        copied = 0
        for item in source.rglob("*"):
            if not item.is_file():
                continue
            relative = item.relative_to(source)
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, target)
            copied += 1
            print(f"deployed {item} -> {target} ({target.stat().st_size} bytes)")
        if copied == 0:
            raise FileNotFoundError(f"no files found below {source}")
        return 0

    for name in ("zt_netloop_client.exe", "zt_netloop_server.exe"):
        matches = list(source.rglob(name))
        if not matches:
            raise FileNotFoundError(f"{name} not found below {source}")
        target = destination / name
        shutil.copy2(matches[0], target)
        print(f"deployed {matches[0]} -> {target} ({target.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
