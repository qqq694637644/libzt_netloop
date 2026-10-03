#!/usr/bin/env python3
import argparse
from pathlib import Path
import subprocess


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", default="dist")
    args = parser.parse_args()
    runtime = Path(args.runtime).resolve()

    for name in ("zt_netloop_client.exe", "zt_netloop_server.exe"):
        executable = runtime / name
        if not executable.exists():
            raise FileNotFoundError(executable)
        for flag in ("--version", "--help"):
            result = subprocess.run(
                [str(executable), flag],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=20,
            )
            print(f"$ {name} {flag}\n{result.stdout}{result.stderr}")
            if result.returncode != 0:
                return result.returncode
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
