"""Offline tests: manual C# E2E readiness uses existing JSONL events only."""

import json
import tempfile
import threading
import time
import unittest
from pathlib import Path

from ci.common import wait_for_netloop_ready


def ready_event(node="0123456789", ip="10.70.0.2", reset=0, phase="ready"):
    return json.dumps({
        "ts": "2026-10-12T00:00:00Z",
        "level": "info",
        "event": "runtime_phase",
        "data": {
            "phase": phase,
            "node": node,
            "bind_address": ip,
            "reset_count": reset,
        },
    }).encode("utf-8") + b"\n"


class NetLoopReadyEventTests(unittest.TestCase):
    def test_ready_event_replaces_status_file(self):
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / "runtime.jsonl"
            log.write_bytes(ready_event(phase="starting") + ready_event())
            observed = wait_for_netloop_ready(log, 0.3, process_id=1234)
            self.assertEqual(observed["phase"], "ready")
            self.assertEqual(observed["process_id"], 1234)
            self.assertEqual(observed["node_id"], "0123456789")
            self.assertEqual(observed["primary_overlay_address"], "10.70.0.2")
            self.assertEqual(observed["reset_count"], 0)

    def test_offset_reset_generation_and_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / "runtime.jsonl"
            log.write_bytes(ready_event())
            old_length = log.stat().st_size
            with log.open("ab") as output:
                output.write(ready_event(reset=1, phase="resetting"))
                output.write(ready_event(reset=1))
            observed = wait_for_netloop_ready(
                log, 0.3, process_id=1234, reset_count=1,
                expected_node_id="0123456789", start_offset=old_length)
            self.assertEqual(observed["reset_count"], 1)
            with self.assertRaises(AssertionError):
                wait_for_netloop_ready(
                    log, 0.3, process_id=1234, reset_count=1,
                    expected_node_id="bad-node", start_offset=old_length)
            with self.assertRaises(TimeoutError):
                wait_for_netloop_ready(
                    log, 0.15, process_id=1234, reset_count=2,
                    start_offset=old_length)

    def test_partial_json_line_accepted_once_complete(self):
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / "runtime.jsonl"
            line = ready_event()
            log.write_bytes(line[:15])

            def finish():
                time.sleep(0.08)
                with log.open("ab") as output:
                    output.write(line[15:])

            thread = threading.Thread(target=finish)
            thread.start()
            try:
                observed = wait_for_netloop_ready(log, 0.5, process_id=78)
                self.assertEqual(observed["process_id"], 78)
            finally:
                thread.join(timeout=1)


if __name__ == "__main__":
    unittest.main()
