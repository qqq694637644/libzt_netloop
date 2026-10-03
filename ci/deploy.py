#!/usr/bin/env python3
import argparse
from pathlib import Path
import shutil


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--destination", default="runtime")
    args = parser.parse_args()

    source = Path(args.source).resolve()
    destination = Path(args.destination).resolve()
    destination.mkdir(parents=True, exist_ok=True)

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
