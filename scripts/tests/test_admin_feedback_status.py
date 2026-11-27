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
        # Never this machine's own launchd file.
        agent = mock.patch.object(status, "AGENT", self.dir / "agent.plist")
        agent.start()
        self.addCleanup(agent.stop)

    def test_the_bridge_section_is_what_the_release_last_decided_and_absent_without_it(self):
        self.assertEqual(status.bridge_note(), ("", ""))
        self.assertNotIn("## SwiftUI bridge", status.render("", None, [], [], [], [], 0, 12, bridge=""))
        (self.dir / "state" / "bridge-release-note").write_text("released\nVersion 0.1.0-preview.68 was packed.\n", encoding="utf-8")
        self.assertEqual(status.bridge_note(), ("released", "Version 0.1.0-preview.68 was packed."))
        body = status.render("", None, [], [], [], [], 0, 12, bridge=status.bridge_note()[1])
        self.assertIn("## SwiftUI bridge\n\nVersion 0.1.0-preview.68 was packed.\n\n## Pull requests waiting for you", body)

    def test_while_the_bridge_is_packed_the_check_does_not_say_it_is_idle(self):
        said = {}
        with mock.patch.object(status, "gather", return_value=([], [], [])), \
             mock.patch.object(status, "print", create=True), \
             mock.patch.object(status, "render", side_effect=lambda *a: said.update(running=a[1], bridge=a[9]) or "body\n"):
            (self.dir / "state" / "bridge-release-note").write_text("packing\nPacking version 0.1.0-preview.68.\n", encoding="utf-8")
            status.write(12, [], set(), "", show=True)
            self.assertIn("Packing a new version of the SwiftUI bridge", said["running"])
            self.assertEqual(said["bridge"], "Packing version 0.1.0-preview.68.")
            (self.dir / "state" / "bridge-release-note").write_text("released\nVersion 0.1.0-preview.68 was packed.\n", encoding="utf-8")
            status.write(12, [], set(), "", show=True)
            self.assertIsNone(said["running"])

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

    def write_calls(self, spare, quota=None, held=False):
        """How many times the issue is written by one update."""
        def gather(windows, handled):
            return ["- #239 (app)"], [], []
        import fcntl
        other = open(self.dir / "state" / "status.lock", "a", encoding="utf-8")
        self.addCleanup(other.close)
        if held:
            fcntl.flock(other, fcntl.LOCK_EX)
        with mock.patch.object(status, "gather", gather), \
                mock.patch.object(status, "status_issue", return_value=7), \
                mock.patch.object(status, "LOCK_SECONDS", 0), \
                mock.patch.object(status, "calls_to_spare", return_value=quota) as asked, \
                mock.patch.object(status.subprocess, "run", return_value=mock.Mock(returncode=0)) as ran:
            status.update(12, [], set(), "", spare=spare)
        return ran.call_count, asked.call_count

    def test_one_rewrite_holds_the_lock_at_a_time(self):
        import fcntl
        with status.one_at_a_time(0) as first:
            self.assertTrue(first)
            with status.one_at_a_time(0) as second:
                self.assertFalse(second)
            with open(self.dir / "state" / "status.lock", "a", encoding="utf-8") as other:
                with self.assertRaises(OSError):
                    fcntl.flock(other, fcntl.LOCK_EX | fcntl.LOCK_NB)
        with status.one_at_a_time(0) as after:
            self.assertTrue(after)

    def test_a_spare_rewrite_is_skipped_while_another_runs(self):
        self.assertEqual(self.write_calls(spare=True, quota=True, held=True), (0, 0))

    def test_a_spare_rewrite_is_skipped_when_few_calls_are_left(self):
        self.assertEqual(self.write_calls(spare=True, quota=False), (0, 1))

    def test_a_spare_rewrite_writes_otherwise(self):
        self.assertEqual(self.write_calls(spare=True, quota=True), (1, 1))

    def test_the_runs_own_rewrite_goes_ahead_after_its_wait_and_asks_nothing(self):
        self.assertEqual(self.write_calls(spare=False, held=True), (1, 0))

    def test_calls_to_spare_reads_both_limits(self):
        def left(core, graphql):
            return json.dumps({"core": {"remaining": core, "limit": 5000},
                               "graphql": {"remaining": graphql, "limit": 5000}})
        with mock.patch.object(status, "gh", return_value=left(4000, 4000)):
            self.assertTrue(status.calls_to_spare())
        with mock.patch.object(status, "gh", return_value=left(4000, 900)):
            self.assertFalse(status.calls_to_spare())
        with mock.patch.object(status, "gh", side_effect=status.pr.GitHubError("down")):
            self.assertFalse(status.calls_to_spare())

    def test_the_idle_line_says_how_often_launchd_starts_it(self):
        import plistlib
        said = {}
        for seconds in (60, 300, 90, 0):
            with open(self.dir / "agent.plist", "wb") as agent:
                plistlib.dump({"StartInterval": seconds}, agent)
            said[seconds] = status.looks_every()
        self.assertEqual(said, {60: "every minute", 300: "every 5 minutes", 90: "every 90 seconds", 0: "regularly"})
        (self.dir / "agent.plist").unlink()
        self.assertEqual(status.looks_every(), "regularly")
        self.assertIn("looks for work every 5 minutes",
                      status.render("", None, [], [], [], [], 0, 12, "every 5 minutes"))

    def test_the_cap_is_said_from_the_sessions_counted_as_it_writes(self):
        today = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        (self.dir / "usage.jsonl").write_text(
            "".join(json.dumps({"stage": "fix", "at": today}) + "\n" for _ in range(2)), encoding="utf-8")
        printed = []
        with mock.patch.object(status, "gather", lambda windows, handled: ([], [], [])), \
                mock.patch("builtins.print", lambda *a, **k: printed.append(a[0])):
            status.update(2, [], set(), "", show=True)
            status.update(3, [], set(), "", show=True)
        self.assertIn("The daily cap of 2 fix sessions is reached", printed[0])
        self.assertNotIn("daily cap", printed[1])

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


class QueueTests(unittest.TestCase):
    """What waits for a pass, as the status issue lists it."""

    def test_a_pass_the_reviewers_findings_start_is_named_as_one(self):
        waiting = [{"pr": 297, "issue": 290, "ids": ["41", "r7"]}, {"pr": 312, "issue": 306, "ids": ["v900"]}]
        with mock.patch.object(status.pr, "own_pull_requests", return_value=[]), \
             mock.patch.object(status.pr, "pending", return_value=waiting) as asked, \
             mock.patch.object(status.pr, "waiting_issues", return_value=[(321, "app"), (320, "follow-up")]), \
             mock.patch.object(status.pr, "gh_lines", return_value=[]), \
             mock.patch.object(status, "in_flight_number", return_value=None):
            queue, prs, waiting_on_you = status.gather([], set())

        # In the order the work is taken.
        self.assertEqual(queue, ["- `/revise` on #297 (issue #290)", "- #321 (app)",
                                 "- the reviewer's findings on #312 (issue #306)", "- #320 (follow-up)"])
        self.assertEqual((prs, waiting_on_you), ([], []))
        asked.assert_called_once_with(set(), every=True)

    def test_the_passes_the_runner_recorded_reach_the_count(self):
        with tempfile.TemporaryDirectory() as folder:
            kept = Path(folder) / "handled"
            kept.write_text("v900\n312:v900\n", encoding="utf-8")
            with mock.patch.object(status, "update") as asked:
                self.assertEqual(status.main(["update", "--handled", str(kept)]), 0)
        self.assertEqual(asked.call_args[0][2].passes, {312: {"v900"}})


class ReviewNoteTests(unittest.TestCase):
    """The reviewer's recommendation leads a pull request's line on the status issue."""

    def test_the_recommendation_comes_first_and_stands_out(self):
        with mock.patch.object(status.pr, "review_of", return_value="fix first (2 to fix)") as asked:
            self.assertEqual(status.review_note("16ce7bba", 280), "**Review: fix first (2 to fix).** ")
        asked.assert_called_once_with("16ce7bba", 280, status.OUT / "state" / "reviews-borne-out.json")

    def test_no_recommendation_adds_nothing(self):
        with mock.patch.object(status.pr, "review_of", return_value=""):
            self.assertEqual(status.review_note("16ce7bba", 280), "")

    def test_a_head_that_is_not_known_is_not_asked_about(self):
        with mock.patch.object(status.pr, "review_of") as asked:
            self.assertEqual(status.review_note("", 280), "")
        asked.assert_not_called()

    def test_github_not_answering_is_said(self):
        with mock.patch.object(status.pr, "review_of", side_effect=status.pr.GitHubError("HTTP 502")):
            self.assertEqual(status.review_note("16ce7bba", 280), "**Review: could not be read.** ")


if __name__ == "__main__":
    unittest.main()
