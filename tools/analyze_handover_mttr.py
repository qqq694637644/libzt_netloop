#!/usr/bin/env python3
"""Compare observed new-connection recovery after physical handover.

This is NOT the physical network's exact MTTR. The event marker is emitted
*after* the OS delivers its notification and the probe samples periodically.
Never infer a successful handover without a post-marker failed sample and
fresh successful connections.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timedelta
import json
from pathlib import Path
import statistics
import sys

from tools.analyze_connection_probes import load_records, parse_timestamp

HANDOVER_WINDOW = timedelta(seconds=30)
MAX_OBSERVATION = timedelta(minutes=10)


def load_markers(path: Path) -> tuple[list[datetime], str]:
    """Prefer the same host marker on both revisions; fall back to native."""
    host: list[datetime] = []
    native: list[datetime] = []
    reset: list[datetime] = []
    with path.open(encoding="utf-8-sig") as stream:
        for index, line in enumerate(stream, 1):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
                if not isinstance(row, dict):
                    continue
                event = row.get("event")
                data = row.get("data") or {}
                if not isinstance(data, dict):
                    continue
                if event == "runtime_reset_requested":
                    reason = data.get("reason") or ""
                    if isinstance(reason, str) and reason.startswith("network_"):
                        host.append(parse_timestamp(row.get("ts")))
                elif event == "libzt_physical_network_refresh_requested":
                    native.append(parse_timestamp(row.get("ts")))
                elif event == "runtime_handover_reset":
                    reset.append(parse_timestamp(row.get("ts")))
            except (ValueError, TypeError, json.JSONDecodeError) as error:
                raise ValueError(f"{path}:{index}: invalid handover log: {error}") from error

    for kind, items in (("runtime_reset_requested", host),
                        ("libzt_physical_network_refresh_requested", native),
                        ("runtime_handover_reset", reset)):
        if items:
            grouped: list[datetime] = []
            for stamp in sorted(items):
                if not grouped or stamp - grouped[-1] > HANDOVER_WINDOW:
                    grouped.append(stamp)
            return grouped, kind
    return [], "none"


def analyze_scenario(runtime_log: Path, probe_log: Path, target_url: str) -> dict:
    markers, source = load_markers(runtime_log)
    records, warnings, duplicates = load_records([probe_log])
    if warnings:
        raise ValueError(f"{probe_log}: {len(warnings)} invalid probe samples: {warnings[0]}")
    samples = sorted((x for x in records if x["target"] == target_url),
                     key=lambda x: x["start"])
    if not samples:
        raise ValueError(f"No samples for exact target {target_url!r} in {probe_log}")

    # Only compare handover markers actually covered by continuous probe data.
    covered = [m for m in markers if samples[0]["start"] <= m <= samples[-1]["finish"]]
    episodes: list[dict] = []
    for i, marker in enumerate(covered):
        deadline = min(marker + MAX_OBSERVATION,
                       covered[i + 1] if i + 1 < len(covered) else marker + MAX_OBSERVATION)
        subsequent = [x for x in samples if marker <= x["start"] < deadline]
        previous_ok = any(x["status"] == "success" and
                          marker - timedelta(seconds=90) <= x["start"] < marker
                          for x in samples)
        first_failed = next((x for x in subsequent if x["status"] == "failure"), None)
        first_is_failure = bool(subsequent and subsequent[0]["status"] == "failure")
        first_success = None
        confirmed = False
        if first_is_failure and first_failed is not None:
            for j, sample in enumerate(subsequent):
                if sample["start"] <= first_failed["start"] or sample["status"] != "success":
                    continue
                if j + 1 < len(subsequent) and subsequent[j + 1]["status"] == "success":
                    first_success = sample
                    confirmed = True
                    break

        episodes.append({
            "marker_utc": marker.isoformat(),
            "previous_success_observed": previous_ok,
            "first_post_marker_probe_failed": first_is_failure,
            "failed_samples": sum(x["status"] == "failure" for x in subsequent),
            "first_new_connection_success_utc": first_success["finish"].isoformat()
                if first_success else None,
            "two_consecutive_successes": confirmed,
            # This is measured from a *logged* network notification, not the
            # actual physical outage onset. It is a sampled elapsed time.
            "marker_to_first_success_s": round(
                (first_success["finish"] - marker).total_seconds(), 3)
                if first_success else None,
            "first_failure_to_success_s": round(
                (first_success["finish"] - first_failed["start"]).total_seconds(), 3)
                if first_success and first_failed else None,
        })

    valid = [x["marker_to_first_success_s"] for x in episodes
             if x["previous_success_observed"] and x["two_consecutive_successes"]
             and x["marker_to_first_success_s"] is not None]
    return {
        "marker_source": source,
        "target": target_url,
        "matched_probe_samples": len(samples),
        "duplicate_probe_samples": duplicates,
        "handover_markers_in_probe_window": len(covered),
        "qualified_recovery_episodes": len(valid),
        "sampled_recovery_median_s": round(statistics.median(valid), 3) if valid else None,
        "sampled_recovery_max_s": max(valid) if valid else None,
        "episodes": episodes,
    }


def compare(baseline: dict, candidate: dict) -> dict:
    n_base = baseline["qualified_recovery_episodes"]
    n_new = candidate["qualified_recovery_episodes"]
    comparable = (n_base > 0 and n_base == n_new and
                  len(baseline["episodes"]) == n_base and
                  len(candidate["episodes"]) == n_new and
                  baseline["marker_source"] == candidate["marker_source"])
    if not comparable:
        return {"comparable": False, "all_candidate_episodes_faster": None,
                "reason": "Need equal, fully qualified handover episodes and the same event marker"}
    paired = [(a["marker_to_first_success_s"], b["marker_to_first_success_s"])
              for a, b in zip(baseline["episodes"], candidate["episodes"])]
    return {
        "comparable": True,
        "paired_sampled_recovery_s": [
            {"baseline": a, "candidate": b, "delta_s": round(b - a, 3)}
            for a, b in paired
        ],
        "all_candidate_episodes_faster": all(b < a for a, b in paired),
        "note": "Observed new-connection recovery, NOT exact physical-network MTTR or causal proof",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-runtime", type=Path, required=True)
    parser.add_argument("--baseline-probe", type=Path, required=True)
    parser.add_argument("--candidate-runtime", type=Path, required=True)
    parser.add_argument("--candidate-probe", type=Path, required=True)
    parser.add_argument("--target-url", required=True)
    parser.add_argument("--json-out", type=Path)
    args = parser.parse_args(argv)
    try:
        baseline = analyze_scenario(args.baseline_runtime, args.baseline_probe,
                                    args.target_url)
        candidate = analyze_scenario(args.candidate_runtime, args.candidate_probe,
                                     args.target_url)
    except (OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2
    result = {
        "schema": 1,
        "measurement": "Sampled new-connection recovery from logged OS/native marker; not exact MTTR",
        "baseline": baseline,
        "candidate": candidate,
        "comparison": compare(baseline, candidate),
    }
    text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    print(text, end="")
    if args.json_out:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(text, encoding="utf-8")
    return 0 if result["comparison"]["comparable"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
