#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", default="dist-csharp")
    parser.add_argument("--expected-version")
    args = parser.parse_args()

    runtime = Path(args.runtime).resolve()
    executable_name = "netloop.exe" if os.name == "nt" else "netloop"
    native_name = "libzt.dll" if os.name == "nt" else "libzt.so"
    executable = runtime / executable_name
    native = runtime / native_name
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
        print(f"$ {executable_name} {flag}\n{result.stdout}{result.stderr}")
        if result.returncode != 0:
            return result.returncode
        if (
            flag == "--version"
            and args.expected_version
            and result.stdout.strip() != f"netloop {args.expected_version}"
        ):
            raise AssertionError(
                "unexpected NetLoop version: "
                f"expected={args.expected_version!r}, "
                f"observed={result.stdout.strip()!r}"
            )

    invalid = subprocess.run(
        [str(executable), "--definitely-unknown", "1"],
        cwd=runtime,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=20,
    )
    print(
        f"$ {executable_name} --definitely-unknown 1\n"
        f"{invalid.stdout}{invalid.stderr}"
    )
    if invalid.returncode == 0 or "Unknown option" not in (
        invalid.stdout + invalid.stderr
    ):
        raise AssertionError("unknown CLI option was not rejected")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
