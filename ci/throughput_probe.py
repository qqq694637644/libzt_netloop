from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import http.client
import json
from pathlib import Path
import ssl
import statistics
import threading
import time

import csharp_e2e_client as desktop_e2e


TARGET_HOST = "speed.cloudflare.com"
TARGET_PORT = 443


def count_route_decisions(
    path: Path,
    *,
    ingress: str,
    route: str,
    target_host: str = TARGET_HOST,
) -> int:
    if not path.exists():
        return 0

    count = 0
    for line in path.read_text(
        encoding="utf-8",
        errors="replace",
    ).splitlines():
        try:
            payload = json.loads(line)
        except json.JSONDecodeError:
            continue
        if payload.get("event") != "route_decision":
            continue
        data = payload.get("data")
        if not isinstance(data, dict):
            continue
        if (
            data.get("ingress") == ingress
            and data.get("route") == route
            and str(data.get("target", "")).startswith(f"{target_host}:")
        ):
            count += 1
    return count


def _prepare_tls(
    proxy_host: str,
    proxy_port: int,
    *,
    timeout: float,
) -> ssl.SSLSocket:
    raw = desktop_e2e.socks_connect(
        proxy_host,
        proxy_port,
        TARGET_HOST,
        TARGET_PORT,
        timeout=timeout,
    )
    context = ssl.create_default_context()
    try:
        tls = context.wrap_socket(raw, server_hostname=TARGET_HOST)
        tls.settimeout(timeout)
        return tls
    except Exception:
        raw.close()
        raise


def _download_prepared(
    tls: ssl.SSLSocket,
    size_bytes: int,
    start: threading.Event,
    *,
    timeout: float,
) -> dict[str, float | int | None]:
    try:
        if not start.wait(timeout=30):
            raise TimeoutError("timed out waiting for synchronized throughput start")

        started = time.monotonic()
        request = (
            f"GET /__down?bytes={size_bytes} HTTP/1.1\r\n"
            f"Host: {TARGET_HOST}\r\n"
            "User-Agent: libzt-netloop-throughput-ci/1\r\n"
            "Accept-Encoding: identity\r\n"
            "Cache-Control: no-cache\r\n"
            "Connection: close\r\n\r\n"
        ).encode("ascii")
        tls.sendall(request)

        response = http.client.HTTPResponse(tls)
        response.begin()
        if response.status != 200:
            raise RuntimeError(
                f"throughput endpoint returned HTTP {response.status} {response.reason}"
            )

        first_byte_at: float | None = None
        received = 0
        while True:
            chunk = response.read(256 * 1024)
            if not chunk:
                break
            if first_byte_at is None:
                first_byte_at = time.monotonic()
            received += len(chunk)

        finished = time.monotonic()
        if received != size_bytes:
            raise RuntimeError(
                f"throughput payload size mismatch: expected={size_bytes}, received={received}"
            )

        elapsed = finished - started
        return {
            "bytes": received,
            "started": started,
            "finished": finished,
            "elapsed_seconds": elapsed,
            "ttfb_ms": (
                None
                if first_byte_at is None
                else (first_byte_at - started) * 1000.0
            ),
            "mbps": received * 8.0 / elapsed / 1_000_000.0,
        }
    finally:
        try:
            tls.close()
        except Exception:
            pass


def run_stage(
    proxy_host: str,
    proxy_port: int,
    *,
    concurrency: int,
    size_bytes: int,
    timeout: float = 300.0,
) -> dict[str, object]:
    if concurrency < 1:
        raise ValueError("concurrency must be >= 1")
    if size_bytes < 1:
        raise ValueError("size_bytes must be >= 1")

    stage_started = time.monotonic()
    with ThreadPoolExecutor(max_workers=concurrency) as executor:
        prepared = list(
            executor.map(
                lambda _: _prepare_tls(
                    proxy_host,
                    proxy_port,
                    timeout=timeout,
                ),
                range(concurrency),
            )
        )
        setup_finished = time.monotonic()
        start = threading.Event()
        futures = [
            executor.submit(
                _download_prepared,
                tls,
                size_bytes,
                start,
                timeout=timeout,
            )
            for tls in prepared
        ]
        start.set()
        flows = [future.result(timeout=timeout + 30) for future in futures]

    stage_finished = time.monotonic()
    transfer_started = min(float(flow["started"]) for flow in flows)
    transfer_finished = max(float(flow["finished"]) for flow in flows)
    transfer_seconds = transfer_finished - transfer_started
    end_to_end_seconds = stage_finished - stage_started
    total_bytes = sum(int(flow["bytes"]) for flow in flows)
    flow_mbps = [float(flow["mbps"]) for flow in flows]
    ttfb_ms = [
        float(flow["ttfb_ms"])
        for flow in flows
        if flow["ttfb_ms"] is not None
    ]

    return {
        "concurrency": concurrency,
        "bytes_per_flow": size_bytes,
        "total_bytes": total_bytes,
        "setup_seconds": setup_finished - stage_started,
        "transfer_seconds": transfer_seconds,
        "end_to_end_seconds": end_to_end_seconds,
        "aggregate_mbps": total_bytes * 8.0 / transfer_seconds / 1_000_000.0,
        "end_to_end_mbps": total_bytes * 8.0 / end_to_end_seconds / 1_000_000.0,
        "flow_mbps_min": min(flow_mbps),
        "flow_mbps_mean": statistics.fmean(flow_mbps),
        "flow_mbps_max": max(flow_mbps),
        "ttfb_ms_mean": statistics.fmean(ttfb_ms) if ttfb_ms else None,
        "flows": flows,
    }


def run_matrix(
    proxy_host: str,
    proxy_port: int,
    *,
    concurrencies: list[int],
    size_mib: int,
    timeout: float = 300.0,
) -> list[dict[str, object]]:
    size_bytes = size_mib * 1024 * 1024
    rows: list[dict[str, object]] = []
    for concurrency in concurrencies:
        row = run_stage(
            proxy_host,
            proxy_port,
            concurrency=concurrency,
            size_bytes=size_bytes,
            timeout=timeout,
        )
        rows.append(row)
        print(
            "throughput "
            f"concurrency={concurrency} "
            f"size_mib={size_mib} "
            f"aggregate_mbps={row['aggregate_mbps']:.2f} "
            f"end_to_end_mbps={row['end_to_end_mbps']:.2f} "
            f"transfer_seconds={row['transfer_seconds']:.3f}",
            flush=True,
        )
    return rows


def write_tsv(path, rows: list[dict[str, object]]) -> None:
    lines = [
        "concurrency\tfile_mib\ttotal_mib\tsetup_s\ttransfer_s\t"
        "aggregate_mbps\tend_to_end_mbps\tflow_min_mbps\t"
        "flow_mean_mbps\tflow_max_mbps\tttfb_mean_ms"
    ]
    for row in rows:
        bytes_per_flow = int(row["bytes_per_flow"])
        total_bytes = int(row["total_bytes"])
        lines.append(
            "\t".join(
                [
                    str(row["concurrency"]),
                    f"{bytes_per_flow / 1024 / 1024:.0f}",
                    f"{total_bytes / 1024 / 1024:.0f}",
                    f"{float(row['setup_seconds']):.3f}",
                    f"{float(row['transfer_seconds']):.3f}",
                    f"{float(row['aggregate_mbps']):.2f}",
                    f"{float(row['end_to_end_mbps']):.2f}",
                    f"{float(row['flow_mbps_min']):.2f}",
                    f"{float(row['flow_mbps_mean']):.2f}",
                    f"{float(row['flow_mbps_max']):.2f}",
                    (
                        ""
                        if row["ttfb_ms_mean"] is None
                        else f"{float(row['ttfb_ms_mean']):.1f}"
                    ),
                ]
            )
        )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
