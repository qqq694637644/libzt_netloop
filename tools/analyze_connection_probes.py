#!/usr/bin/env python3
"""Offline JSONL connectivity-probe statistics. Never starts a probe or NetLoop.

All metrics describe sampled NEW connections, not uptime, packet loss, old
TCP session survival or exact network recovery duration.
"""
from __future__ import annotations

import argparse
import csv
from datetime import datetime, timedelta, timezone
import glob
import json
import math
from pathlib import Path
import sys


def parse_timestamp(value: object) -> datetime:
    if not isinstance(value, str) or not value.strip():
        raise ValueError("missing UTC timestamp")
    parsed = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("naive timestamp (UTC offset required)")
    return parsed.astimezone(timezone.utc)


def percentile(values: list[float], percentage: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * percentage / 100
    low = math.floor(position)
    high = math.ceil(position)
    return round(
        ordered[low] + (ordered[high] - ordered[low]) * (position - low), 3
    )


def paths_from_args(inputs: list[str]) -> list[Path]:
    resolved: set[Path] = set()
    for spec in inputs:
        candidate = Path(spec)
        if candidate.is_file():
            resolved.add(candidate.resolve())
        elif candidate.is_dir():
            resolved.update(path.resolve() for path in candidate.glob("*.jsonl"))
        else:
            resolved.update(
                Path(path).resolve() for path in glob.glob(spec) if Path(path).is_file()
            )
    return sorted(resolved)


def load_records(paths: list[Path]) -> tuple[list[dict], list[str], int]:
    result: list[dict] = []
    warnings: list[str] = []
    duplicate_count = 0
    seen: set[tuple] = set()

    for path in paths:
        with path.open(encoding="utf-8-sig") as handle:
            for number, line in enumerate(handle, 1):
                if not line.strip():
                    continue
                try:
                    row = json.loads(line)
                    if not isinstance(row, dict):
                        raise ValueError("JSON object expected")
                    start = parse_timestamp(row.get("ts_utc", row.get("ts")))
                    finish = parse_timestamp(row["end_utc"]) if row.get("end_utc") else start
                    if finish < start:
                        raise ValueError("end_utc precedes ts_utc")
                    status = row.get("status")
                    if status not in ("success", "failure"):
                        raise ValueError("status must be success or failure")
                    elapsed = float(row.get("elapsed_ms", (finish - start).total_seconds() * 1000))
                    if not math.isfinite(elapsed) or elapsed < 0:
                        raise ValueError("invalid elapsed_ms")
                    node = str(row.get("node") or "unknown")
                    target = str(row.get("url") or row.get("target_url") or "unspecified")
                    key = (node, target, start, finish, status, row.get("sequence"),
                           row.get("curl_exit_code"), row.get("http_code"))
                    if key in seen:
                        duplicate_count += 1
                        continue
                    seen.add(key)
                    result.append({
                        "node": node, "target": target, "start": start,
                        "finish": finish, "status": status, "elapsed_ms": elapsed,
                    })
                except (ValueError, TypeError, KeyError, json.JSONDecodeError) as error:
                    warnings.append(f"{path.name}:{number}: {error}")

    result.sort(key=lambda row: (row["start"], row["node"], row["target"]))
    return result, warnings, duplicate_count


def summarize(records: list[dict]) -> dict:
    """Records must belong to a single node+target; order is chronological."""
    ordered = sorted(records, key=lambda item: item["start"])
    success = [row for row in ordered if row["status"] == "success"]
    failures = len(ordered) - len(success)
    durations = [row["elapsed_ms"] for row in success]
    max_streak = 0
    consecutive = 0
    pending_failure = None
    observed_transitions: list[float] = []

    for record in ordered:
        if record["status"] == "failure":
            consecutive += 1
            max_streak = max(max_streak, consecutive)
            if pending_failure is None:
                pending_failure = record
        else:
            consecutive = 0
            if pending_failure is not None:
                # This is strictly an observation window between samples,
                # NOT the duration of a ZeroTier outage or TCP recovery.
                observed_transitions.append(
                    (record["finish"] - pending_failure["start"]).total_seconds()
                )
                pending_failure = None

    attempts = len(ordered)
    return {
        "attempts": attempts,
        "success": len(success),
        "failure": failures,
        "sample_success_percent": round(100 * len(success) / attempts, 3)
        if attempts else None,
        "successful_elapsed_p50_ms": percentile(durations, 50),
        "successful_elapsed_p95_ms": percentile(durations, 95),
        "successful_elapsed_max_ms": round(max(durations), 3) if durations else None,
        "max_consecutive_failure_samples": max_streak,
        "failure_to_success_sample_windows": len(observed_transitions),
        "failure_to_success_sample_window_p95_s": percentile(
            observed_transitions, 95
        ),
        "failure_to_success_sample_window_max_s": round(
            max(observed_transitions), 3
        ) if observed_transitions else None,
        "unresolved_failure_at_end": bool(pending_failure),
    }


def generate_report(records: list[dict], bad_lines: list[str],
                    duplicate_count: int, files: int) -> dict:
    grouped: dict[tuple[str, str], list[dict]] = {}
    for row in records:
        grouped.setdefault((row["node"], row["target"]), []).append(row)

    groups = []
    for (node, target), group in sorted(grouped.items()):
        hourly: dict[str, list[dict]] = {}
        for row in group:
            label = row["start"].strftime("%Y-%m-%dT%H:00:00Z")
            hourly.setdefault(label, []).append(row)

        newest = max(row["start"] for row in group)
        latest_window = {}
        for hours in (72, 168):
            cutoff = newest - timedelta(hours=hours)
            latest_window[f"last_{hours}h_relative_to_last_sample"] = summarize(
                [row for row in group if row["start"] >= cutoff]
            )
        groups.append({
            "node": node,
            "target": target,
            "first_utc": group[0]["start"].isoformat(),
            "last_utc": newest.isoformat(),
            "all_samples": summarize(group),
            "windows": latest_window,
            "hourly": [
                {"hour_utc": hour, **summarize(items)}
                for hour, items in sorted(hourly.items())
            ],
        })

    return {
        "schema": 1,
        "analysis": "Offline sampled new-connection results; not true uptime/MTTR",
        "file_count": files,
        "bad_line_count": len(bad_lines),
        "duplicate_sample_count": duplicate_count,
        "groups": groups,
    }


def write_csv(path: Path, report: dict) -> None:
    fieldnames = [
        "node", "target", "hour_utc", "attempts", "success", "failure",
        "sample_success_percent", "successful_elapsed_p50_ms",
        "successful_elapsed_p95_ms", "max_consecutive_failure_samples",
    ]
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for group in report["groups"]:
            for hour in group["hourly"]:
                writer.writerow({key: (group.get(key) if key in ("node", "target")
                                       else hour.get(key)) for key in fieldnames})


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", nargs="+", required=True,
                        help="JSONL files, directories, and quoted glob patterns")
    parser.add_argument("--json-out", type=Path, help="Optional JSON report path")
    parser.add_argument("--csv-out", type=Path, help="Optional hourly CSV path")
    args = parser.parse_args(argv)

    paths = paths_from_args(args.input)
    if not paths:
        parser.error("no JSONL files matched --input")

    records, warnings, duplicate_count = load_records(paths)
    if not records:
        print("No valid probe rows found.", file=sys.stderr)
        return 2
    report = generate_report(records, warnings, duplicate_count, len(paths))

    for warning in warnings[:25]:
        print("WARN " + warning, file=sys.stderr)
    if len(warnings) > 25:
        print(f"WARN {len(warnings) - 25} additional malformed lines", file=sys.stderr)

    if args.json_out:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(
            json.dumps(report, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    if args.csv_out:
        write_csv(args.csv_out, report)

    print(f"files={len(paths)} samples={len(records)} "
          f"duplicates={duplicate_count} invalid_lines={len(warnings)}")
    for group in report["groups"]:
        all_samples = group["all_samples"]
        print(
            f"node={group['node']} target={group['target']} "
            f"success={all_samples['success']}/{all_samples['attempts']} "
            f"({all_samples['sample_success_percent']}%) "
            f"p95={all_samples['successful_elapsed_p95_ms']}ms "
            f"max_failure_streak={all_samples['max_consecutive_failure_samples']} "
            f"sample_transition_max={all_samples['failure_to_success_sample_window_max_s']}s"
        )
    print("NOTE: Sample transitions are NOT exact network recovery times.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
