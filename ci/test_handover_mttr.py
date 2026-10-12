"""Offline handover MTTR analyzer tests; synthetic probe timings only."""
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from tools.analyze_handover_mttr import analyze_scenario, compare, load_markers

TARGET = "http://172.26.0.254:8080/"
START = datetime(2026, 10, 12, 11, 28, tzinfo=timezone.utc)


def timestamp(seconds: float) -> str:
    return (START + timedelta(seconds=seconds)).isoformat()


def write_jsonl(path: Path, rows: list[dict]) -> Path:
    path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    return path


def marker(seconds: int, reason: str = "network_available") -> dict:
    return {"ts": timestamp(seconds), "event": "runtime_reset_requested",
            "data": {"reason": reason}}


def sample(seconds: float, status: str, url: str = TARGET) -> dict:
    return {"ts_utc": timestamp(seconds), "end_utc": timestamp(seconds + 0.2),
            "event": "connection_probe", "node": "A", "target": url,
            "status": status, "elapsed_ms": 200}


class TestHandoverComparison(unittest.TestCase):
    def setUp(self):
        self.directory = TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def scenario(self, prefix: str, recovered_at: int):
        runtime = write_jsonl(self.root / f"{prefix}-runtime.jsonl", [
            marker(10, "command:manual"), marker(10), marker(11),
            {"ts": timestamp(12), "event": "libzt_physical_network_refresh_requested", "data": {}},
        ])
        probes = write_jsonl(self.root / f"{prefix}-probes.jsonl", [
            sample(0, "success"), sample(10.5, "failure"),
            sample(16, "failure"), sample(recovered_at, "success"),
            sample(recovered_at + 2, "success"),
        ])
        return analyze_scenario(runtime, probes, TARGET)

    def test_comparison_and_same_marker(self):
        baseline = self.scenario("baseline", 211)  # roughly 201 s after marker
        candidate = self.scenario("candidate", 25)  # about 15 s after marker
        self.assertEqual(baseline["marker_source"], "runtime_reset_requested")
        self.assertEqual(candidate["handover_markers_in_probe_window"], 1)
        self.assertEqual(baseline["qualified_recovery_episodes"], 1)
        self.assertEqual(candidate["qualified_recovery_episodes"], 1)
        result = compare(baseline, candidate)
        self.assertTrue(result["comparable"])
        self.assertTrue(result["all_candidate_episodes_faster"])
        self.assertGreater(result["paired_sampled_recovery_s"][0]["baseline"],
                           result["paired_sampled_recovery_s"][0]["candidate"])

    def test_single_success_does_not_confirm_recovery(self):
        runtime = write_jsonl(self.root / "runtime.jsonl", [marker(10)])
        probes = write_jsonl(self.root / "probes.jsonl", [
            sample(0, "success"), sample(11, "failure"), sample(20, "success"),
        ])
        result = analyze_scenario(runtime, probes, TARGET)
        self.assertEqual(result["qualified_recovery_episodes"], 0)
        self.assertFalse(result["episodes"][0]["two_consecutive_successes"])

    def test_transient_success_must_not_hide_later_stable_recovery(self):
        runtime = write_jsonl(self.root / "runtime.jsonl", [marker(10)])
        probes = write_jsonl(self.root / "probes.jsonl", [
            sample(0, "success"), sample(11, "failure"),
            sample(15, "success"), sample(17, "failure"),
            sample(21, "success"), sample(23, "success"),
        ])
        result = analyze_scenario(runtime, probes, TARGET)
        self.assertEqual(result["qualified_recovery_episodes"], 1)
        self.assertGreater(result["episodes"][0]["marker_to_first_success_s"], 11)

    def test_no_post_marker_outage_is_not_recovery(self):
        runtime = write_jsonl(self.root / "runtime.jsonl", [marker(10)])
        probes = write_jsonl(self.root / "probes.jsonl", [
            sample(0, "success"), sample(11, "success"), sample(13, "success"),
        ])
        result = analyze_scenario(runtime, probes, TARGET)
        self.assertEqual(result["qualified_recovery_episodes"], 0)
        self.assertFalse(result["episodes"][0]["first_post_marker_probe_failed"])
        self.assertIsNone(result["episodes"][0]["marker_to_first_success_s"])

    def test_fallback_native_marker_and_exact_target(self):
        runtime = write_jsonl(self.root / "runtime.jsonl", [
            {"ts": timestamp(10), "event": "libzt_physical_network_refresh_requested"},
        ])
        probes = write_jsonl(self.root / "probes.jsonl", [
            sample(0, "success"), sample(11, "failure", "https://example.com/"),
            sample(12, "failure"), sample(19, "success"), sample(21, "success"),
        ])
        result = analyze_scenario(runtime, probes, TARGET)
        self.assertEqual(result["marker_source"], "libzt_physical_network_refresh_requested")
        self.assertEqual(result["matched_probe_samples"], 4)
        self.assertEqual(result["qualified_recovery_episodes"], 1)

    def test_mismatched_markers_are_not_comparable(self):
        baseline = self.scenario("baseline", 211)
        candidate = self.scenario("candidate", 25)
        candidate["marker_source"] = "libzt_physical_network_refresh_requested"
        self.assertFalse(compare(baseline, candidate)["comparable"])


if __name__ == "__main__":
    unittest.main()
