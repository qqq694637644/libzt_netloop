#!/usr/bin/env python3
from __future__ import annotations

import argparse
from pathlib import Path
import subprocess


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", default="dist-csharp")
    args = parser.parse_args()

    runtime = Path(args.runtime).resolve()
    executable = runtime / "netloop.exe"
    native = runtime / "libzt.dll"
    if not executable.exists():
        raise FileNotFoundError(executable)
    if not native.exists():
        raise FileNotFoundError(native)

    for flag in ("--version", "--help"):
        result = subprocess.run(
            [str(executable), flag],
            cwd=runtime,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=20,
        )
        print(f"$ netloop.exe {flag}\n{result.stdout}{result.stderr}")
        if result.returncode != 0:
            return result.returncode
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
