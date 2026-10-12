"""Offline unit checks for the probe analyzer; no socket/network activity."""
import unittest
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
from tempfile import TemporaryDirectory

from tools.analyze_connection_probes import generate_report, load_records, percentile, summarize


class TestConnectionProbeStatistics(unittest.TestCase):
    def test_powershell_probe_target_is_not_discarded(self):
        with TemporaryDirectory() as directory:
            path = Path(directory) / "probe.jsonl"
            path.write_text(json.dumps({
                "ts_utc": "2026-10-12T11:28:58Z",
                "end_utc": "2026-10-12T11:29:00Z",
                "event": "connection_probe",
                "node": "A",
                "target": "http://172.26.0.254:8080/",
                "status": "failure",
                "elapsed_ms": 2000,
            }) + "\n", encoding="utf-8")
            records, warnings, duplicates = load_records([path])
            self.assertFalse(warnings)
            self.assertEqual(duplicates, 0)
            self.assertEqual(records[0]["target"], "http://172.26.0.254:8080/")

    def test_success_percentiles(self):
        self.assertIsNone(percentile([], 95))
        self.assertEqual(percentile([100, 200, 300], 50), 200)
        self.assertEqual(percentile([100, 200, 300], 95), 290)

    def test_failure_then_success_is_observed_window_not_downtime(self):
        now = datetime(2026, 10, 11, tzinfo=timezone.utc)
        def entry(seconds, success, duration):
            start = now + timedelta(seconds=seconds)
            return {
                "node": "A", "target": "test", "start": start,
                "finish": start + timedelta(milliseconds=duration),
                "elapsed_ms": duration,
                "status": "success" if success else "failure"
            }
        records = [
            entry(0, True, 100),
            entry(20, False, 3000),
            entry(40, False, 3000),
            entry(60, True, 150),
            entry(80, True, 200),
        ]
        metrics = summarize(records)
        self.assertEqual(metrics["attempts"], 5)
        self.assertEqual(metrics["failure"], 2)
        self.assertEqual(metrics["max_consecutive_failure_samples"], 2)
        self.assertEqual(metrics["failure_to_success_sample_windows"], 1)
        self.assertEqual(metrics["failure_to_success_sample_window_max_s"], 40.15)
        report = generate_report(records, [], 0, 1)
        self.assertEqual(report["groups"][0]["all_samples"]["success"], 3)


if __name__ == "__main__":
    unittest.main()
