"""Offline tests for the feedback check's status issue (scripts/admin-feedback-status.py)."""

import importlib.util
import json
import os
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "admin-feedback-status.py"
SPEC = importlib.util.spec_from_file_location("admin_feedback_status", SCRIPT)
status = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(status)


class StatusTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.dir = Path(folder.name)
        (self.dir / "state").mkdir()
        patch = mock.patch.object(status, "OUT", self.dir)
        patch.start()
        self.addCleanup(patch.stop)

    def test_only_fix_sessions_started_today_count_toward_the_cap(self):
        today = time.strftime("%Y-%m-%d", time.gmtime())
        records = [
            {"stage": "fix", "at": f"{today}T01:00:00Z"},
            {"stage": "fix follow-up", "at": f"{today}T02:00:00Z"},
            {"stage": "triage", "at": f"{today}T03:00:00Z"},
            {"stage": "fix", "at": "2020-01-01T01:00:00Z"},
        ]
        self.assertEqual(status.sessions_today(records), 2)

    def test_a_damaged_usage_line_is_skipped(self):
        (self.dir / "usage.jsonl").write_text('{"stage": "fix", "at": "x"}\nnot json\n[]\n', encoding="utf-8")
        self.assertEqual(len(status.usage()), 1)

    def test_passes_are_newest_first_with_the_time_their_sessions_took(self):
        history = self.dir / "history.log"
        history.write_text("20261119T100705Z  FIX RESULT: diagnosed (#223) \n"
                           "20261119T220707Z  FIX RESULT: pr x (#235) \n"
                           "garbage\n", encoding="utf-8")
        records = [{"at": "2026-11-19T22:07:07Z", "wall_seconds": 24},
                   {"at": "2026-11-19T22:07:07Z", "wall_seconds": 708}]
        rows = status.passes(history, records)
        self.assertEqual(rows, ["- 2026-11-19 22:07 UTC, 12 min: FIX RESULT: pr x (#235)",
                                "- 2026-11-19 10:07 UTC: FIX RESULT: diagnosed (#223)"])

    def test_the_session_in_flight_is_named(self):
        (self.dir / "state" / "session-in-flight").write_text("issue 123 239 /tmp/tree\n", encoding="utf-8")
        self.assertIn("issue #239", status.in_flight())
        (self.dir / "state" / "session-in-flight").write_text("follow-up 123 238 1,2 /tmp/tree\n", encoding="utf-8")
        self.assertIn("pull request #238", status.in_flight())
        (self.dir / "state" / "session-in-flight").write_text("nonsense\n", encoding="utf-8")
        self.assertIsNone(status.in_flight())

    def test_the_timestamp_alone_does_not_count_as_a_change(self):
        first = status.render("", None, [], [], [], [], 0, 12)
        with mock.patch.object(status, "now", return_value=time.gmtime(time.time() + 3600)):
            later = status.render("", None, [], [], [], [], 0, 12)
        self.assertNotEqual(first, later)
        self.assertEqual(status.fingerprint(first), status.fingerprint(later))
        self.assertNotEqual(status.fingerprint(first), status.fingerprint(status.render("", None, ["- #1 (app)"], [], [], [], 0, 12)))

    def test_an_unchanged_status_is_not_written_again(self):
        def gather(windows, handled):
            return ["- #239 (app)"], [], []
        with mock.patch.object(status, "gather", gather), \
                mock.patch.object(status, "status_issue", return_value=7) as found, \
                mock.patch.object(status.subprocess, "run", return_value=mock.Mock(returncode=0)) as ran:
            status.update(12, [], set(), "")
            status.update(12, [], set(), "")
        self.assertEqual(ran.call_count, 1)
        self.assertEqual(found.call_count, 1)

    def test_a_pause_after_failures_is_said_until_it_ends(self):
        paused = self.dir / "state" / "loop-paused-until"
        paused.write_text(f"{int(time.time()) + 600}\n", encoding="utf-8")
        self.assertIn("Paused until", status.paused_note())
        paused.write_text(f"{int(time.time()) - 1}\n", encoding="utf-8")
        self.assertEqual(status.paused_note(), "")

    def test_a_pull_request_carries_what_the_runner_found(self):
        self.assertEqual(status.checked(257, "965e0043"), "")
        (self.dir / "state" / "verdicts.json").write_text(json.dumps({
            "257": {"proof": "verified", "diff": "clean", "head": "965e004353007c3a"},
            "255": {"proof": "same", "diff": "2 to look at", "head": "56f2a4a6aaaa"},
            "250": {"proof": "a verdict this version does not know", "diff": "clean", "head": "c1101f5b"},
        }), encoding="utf-8")
        self.assertEqual(status.head_in("CHECKS: pull request #257 at 965e0043: 21 passed, 0 failed, 1 skipped"), "965e0043")
        self.assertEqual(status.checked(257, "965e0043"), ". Proof verified; diff: nothing to look at")
        self.assertEqual(status.checked(255, "56f2a4a6"), ". Proof NOT shown; diff: 2 to look at")
        self.assertEqual(status.checked(250, "c1101f5b"), "")
        self.assertEqual(status.checked(1, "abcdef12"), "")
        # A push after the runner looked: its verdict was about another commit.
        self.assertIn("has not checked", status.checked(257, "0a0a0a0a"))
        self.assertIn("has not checked", status.checked(257, ""))
        (self.dir / "state" / "verdicts.json").write_text("[1, 2]", encoding="utf-8")
        self.assertEqual(status.checked(257, "965e0043"), "")


if __name__ == "__main__":
    unittest.main()
