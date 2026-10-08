from __future__ import annotations

import io
import json
import os
from pathlib import Path
import time
import urllib.parse
import urllib.request
import zipfile


def _env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"missing required environment variable {name}")
    return value


def _request(url: str) -> urllib.request.Request:
    return urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {_env('GITHUB_TOKEN')}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "libzt-netloop-ci/1",
        },
    )


class _SafeRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        redirected = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirected is None:
            return None

        # GitHub's artifact API returns a signed object-storage URL.  The
        # GITHUB_TOKEN is valid only for api.github.com and must not be
        # forwarded to the storage host, otherwise Azure returns HTTP 401.
        old_host = urllib.parse.urlparse(req.full_url).hostname
        new_host = urllib.parse.urlparse(newurl).hostname
        if old_host != new_host:
            redirected.remove_header("Authorization")
            redirected.remove_header("X-GitHub-Api-Version")
            redirected.remove_header("Accept")
        return redirected


def list_current_run_artifacts() -> list[dict]:
    repository = _env("GITHUB_REPOSITORY")
    run_id = _env("GITHUB_RUN_ID")
    url = (
        f"https://api.github.com/repos/{repository}/actions/runs/{run_id}"
        "/artifacts?per_page=100"
    )
    with urllib.request.urlopen(_request(url), timeout=30) as response:
        return json.load(response).get("artifacts", [])


def wait_for_artifact(name: str, timeout: float = 600.0) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for artifact in list_current_run_artifacts():
            if artifact.get("name") == name and not artifact.get("expired", False):
                return artifact
        time.sleep(5)
    raise TimeoutError(f"timed out waiting for Actions artifact {name!r}")


def download_artifact(artifact: dict, destination: Path) -> Path:
    destination.mkdir(parents=True, exist_ok=True)
    url = artifact["archive_download_url"]
    opener = urllib.request.build_opener(_SafeRedirectHandler())
    with opener.open(_request(url), timeout=60) as response:
        content = response.read()
    destination_resolved = destination.resolve()
    with zipfile.ZipFile(io.BytesIO(content)) as archive:
        for member in archive.infolist():
            target = (destination / member.filename).resolve()
            if (
                destination_resolved not in target.parents
                and target != destination_resolved
            ):
                raise RuntimeError(f"unsafe artifact member path: {member.filename}")
        archive.extractall(destination)
    return destination


def wait_and_download(
    name: str, destination: Path, timeout: float = 600.0
) -> Path:
    artifact = wait_for_artifact(name, timeout=timeout)
    return download_artifact(artifact, destination)
