#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess


def assert_rejected(
    executable: Path,
    runtime: Path,
    name: str,
    arguments: list[str],
    expected_text: str,
) -> None:
    result = subprocess.run(
        [str(executable), *arguments],
        cwd=runtime,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=20,
    )
    output = result.stdout + result.stderr
    print(f"$ {executable.name} {' '.join(arguments)}\n{output}")
    if result.returncode == 0 or expected_text not in output:
        raise AssertionError(
            f"{name} was not rejected as expected: "
            f"returncode={result.returncode}, expected={expected_text!r}"
        )


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

    assert_rejected(
        executable,
        runtime,
        "unknown CLI option",
        ["--definitely-unknown", "1"],
        "Unknown option",
    )

    base = [
        "--network",
        "8056c2e21c000001",
        "--state-dir",
        "smoke-state",
    ]
    assert_rejected(
        executable,
        runtime,
        "missing default exit",
        base,
        "Missing required argument --default-exit",
    )
    assert_rejected(
        executable,
        runtime,
        "duplicate network",
        [
            *base,
            "--network",
            "8056c2e21c000002",
            "--default-exit",
            "172.26.0.254",
        ],
        "Duplicate option: --network",
    )
    assert_rejected(
        executable,
        runtime,
        "username without password",
        [
            *base,
            "--default-exit",
            "172.26.0.254",
            "--egress",
            "upstream-socks5",
            "--upstream-host",
            "127.0.0.1",
            "--upstream-user",
            "netloop",
        ],
        "must either both be set or both be absent",
    )
    assert_rejected(
        executable,
        runtime,
        "empty RFC1929 credentials",
        [
            *base,
            "--default-exit",
            "172.26.0.254",
            "--egress",
            "upstream-socks5",
            "--upstream-host",
            "127.0.0.1",
            "--upstream-user",
            "",
            "--upstream-password",
            "",
        ],
        "must each be 1..255 UTF-8 bytes",
    )
    assert_rejected(
        executable,
        runtime,
        "oversized RFC1929 username",
        [
            *base,
            "--default-exit",
            "172.26.0.254",
            "--egress",
            "upstream-socks5",
            "--upstream-host",
            "127.0.0.1",
            "--upstream-user",
            "u" * 256,
            "--upstream-password",
            "p",
        ],
        "must each be 1..255 UTF-8 bytes",
    )
    assert_rejected(
        executable,
        runtime,
        "direct egress with upstream option",
        [
            *base,
            "--default-exit",
            "172.26.0.254",
            "--egress",
            "direct",
            "--upstream-host",
            "127.0.0.1",
        ],
        "--upstream-* options are only valid with --egress upstream-socks5",
    )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
