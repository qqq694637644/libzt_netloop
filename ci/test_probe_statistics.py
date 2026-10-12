"""Offline unit checks for the probe analyzer; no socket/network activity."""
import unittest
from datetime import datetime, timedelta, timezone

from tools.analyze_connection_probes import generate_report, percentile, summarize


class TestConnectionProbeStatistics(unittest.TestCase):
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
