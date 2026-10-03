#!/usr/bin/env python3
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

from build import normalize_version


ROOT = Path(__file__).resolve().parents[1]


def run(command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
    print("+", subprocess.list2cmdline(command), flush=True)
    return subprocess.run(
        command,
        cwd=ROOT,
        check=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=capture,
    )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Create a GitHub Release with the two Windows EXE assets."
    )
    parser.add_argument("--version", required=True)
    parser.add_argument("--dist-dir", default=str(ROOT / "dist"))
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    args = parser.parse_args()

    if not args.repo:
        raise RuntimeError("--repo or GITHUB_REPOSITORY is required")

    version = normalize_version(args.version)
    tag = f"v{version}"
    dist_dir = Path(args.dist_dir).resolve()
    client = dist_dir / "zt_netloop_client.exe"
    server = dist_dir / "zt_netloop_server.exe"

    missing = [str(path) for path in (client, server) if not path.is_file()]
    if missing:
        raise FileNotFoundError("missing release binaries: " + ", ".join(missing))

    head = run(["git", "rev-parse", "HEAD"], capture=True).stdout.strip()
    branch = os.environ.get("RELEASE_BRANCH", "").strip() or run(
        ["git", "branch", "--show-current"], capture=True
    ).stdout.strip()

    existing = subprocess.run(
        ["gh", "release", "view", tag, "--repo", args.repo],
        cwd=ROOT,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    if existing.returncode == 0:
        raise RuntimeError(f"release {tag} already exists; refusing to overwrite it")

    notes = (
        f"Windows x64 build from branch {branch} at commit {head}.\n\n"
        "Assets are published separately; no ZIP package is created."
    )
    command = [
        "gh",
        "release",
        "create",
        tag,
        str(client),
        str(server),
        "--repo",
        args.repo,
        "--target",
        head,
        "--title",
        f"libzt_netloop {tag}",
        "--notes",
        notes,
    ]
    if "-" in version:
        command.append("--prerelease")
    run(command)

    result = run(
        [
            "gh",
            "release",
            "view",
            tag,
            "--repo",
            args.repo,
            "--json",
            "tagName,url,assets,isDraft,isPrerelease",
        ],
        capture=True,
    )
    release = json.loads(result.stdout)
    asset_names = sorted(asset["name"] for asset in release.get("assets", []))
    expected = sorted([client.name, server.name])
    if asset_names != expected:
        raise RuntimeError(
            f"unexpected release assets: got {asset_names}, expected {expected}"
        )

    print(json.dumps(release, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as exc:
        print(f"command failed with exit code {exc.returncode}", file=sys.stderr)
        raise
