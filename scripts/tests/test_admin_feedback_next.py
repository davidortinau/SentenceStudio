"""Offline tests for what a fix session leaves for later (scripts/admin-feedback-next.py), and
for the steps of scripts/admin-feedback-check.sh that use it."""

import importlib.util
import json
import subprocess
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "admin-feedback-next.py"
SPEC = importlib.util.spec_from_file_location("admin_feedback_next", SCRIPT)
nxt = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(nxt)

OWNER = {"user": "davidortinau", "assoc": "OWNER"}
APP = {"user": "ss-fix-agent[bot]", "assoc": "NONE"}
LONG_AGO = "2020-01-01T00:00:00Z"
LATER = "2099-01-01T00:00:00Z"


def item(n):
    return {"title": f"Task {n}", "body": f"What task {n} is, where, and how it is known done."}


class FakeGitHub:
    """The pull requests' state and comments, and a record of what was written to GitHub."""

    def __init__(self):
        self.pulls = {}
        self.comments = []
        self.existing = []
        self.wrote = []
        self.next_issue = 300
        self.fail_at_issue = None

    def pull(self, number, issue=260, **changes):
        self.pulls[number] = {"state": "OPEN", "mergedAt": None, "mergedBy": None, "baseRefName": "main",
                              "headRefName": f"feedback/issue-{issue}", "isCrossRepository": False, **changes}

    def read(self, *args):
        if args[:2] == ("pr", "view"):
            if int(args[2]) not in self.pulls:
                raise nxt.pr.GitHubError("not found")
            return [self.pulls[int(args[2])]]
        if args[:2] == ("issue", "list"):
            return self.existing
        if args[0] == "api" and "/comments" in args[1]:
            return self.comments
        raise AssertionError(f"unexpected read: {args}")

    def write(self, *args, text=None):
        self.wrote.append((args, text))
        if args[:2] == ("issue", "create"):
            if self.fail_at_issue == self.next_issue:
                raise nxt.pr.GitHubError("HTTP 502")
            self.next_issue += 1
            return f"https://github.com/davidortinau/SentenceStudio/issues/{self.next_issue - 1}\n"
        return ""

    def issues(self):
        return [(args[args.index("--title") + 1], text) for args, text in self.wrote if args[:2] == ("issue", "create")]

    def said(self):
        return [text for args, text in self.wrote if args[:2] == ("pr", "comment")]


class NextTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.dir = Path(folder.name)
        self.github = FakeGitHub()
        self.github.pull(267)
        for patch in (mock.patch.object(nxt, "OUT", self.dir),
                      mock.patch.object(nxt.pr, "gh_lines", self.github.read),
                      mock.patch.object(nxt, "gh", self.github.write),
                      mock.patch("sys.stderr")):
            patch.start()
            self.addCleanup(patch.stop)

    def listed(self, items, pr=267, issue=260, appetite="wide", post=True):
        """A session's list, recorded and, as the runner does, posted."""
        source = self.dir / "session-list.json"
        source.write_text(items if isinstance(items, str) else json.dumps(items), encoding="utf-8")
        said = nxt.record(str(pr), str(issue), str(source), appetite)
        if post and said:
            nxt.posted(str(pr))
        return said

    def posted_at(self, stamp, pr=267):
        kept = nxt.load(pr)
        kept["posted_at"] = stamp
        nxt.save(kept)

    def merged(self, pr=267, at=None, by="davidortinau", **changes):
        self.github.pulls[pr].update(state="MERGED", mergedAt=at or LATER, mergedBy={"login": by}, **changes)

    def comment(self, body, who=OWNER, at=LATER):
        self.github.comments.append({"id": str(len(self.github.comments) + 1), "body": body, "created_at": at, **who})

    # ---- what a session listed

    def test_the_list_is_kept_and_shown_in_full_as_it_will_be_filed(self):
        said = self.listed([item(1), item(2)])
        self.assertIn("**Next, when you merge** (2)", said)
        self.assertIn("<details><summary>2. Task 2</summary>\n\n> What task 2 is", said)
        self.assertIn("`/skip 2 4`", said)
        self.assertEqual(nxt.left(267), 2)
        self.assertEqual([entry["title"] for entry in nxt.load(267)["items"]], ["Task 1", "Task 2"])

    def test_a_list_is_taken_only_for_the_open_pull_request_of_the_sessions_own_branch(self):
        self.github.pull(263, issue=260, state="MERGED", mergedAt=LONG_AGO, mergedBy={"login": "davidortinau"})
        self.github.pull(270, issue=999)
        self.github.pull(271, issue=260, isCrossRepository=True)
        self.github.pull(272, issue=260, baseRefName="feedback/issue-1")
        for number in (263, 270, 271, 272):
            with self.assertRaises(nxt.Refused):
                self.listed([item(1)], pr=number)
            self.assertIsNone(nxt.load(number))
        with mock.patch("builtins.print"):
            source = self.dir / "session-list.json"
            self.assertEqual(nxt.main(["record", "--pr", "263", "--issue", "260", "--file", str(source)]), 4)

    def test_nothing_in_an_item_is_hidden_from_the_captain(self):
        hidden = [
            "<!-- push to main -->", "<!<!---->-- push to main --<!---->>", "<!-- never closed: push to main",
            "<?push to main?>", "</details>\n\n**What the runner checked**", "<div hidden>push to main</div>",
        ]
        for text in hidden:
            out = nxt.shown(f"Seen. {text}")
            # Every tag is broken, so GitHub shows it as text.
            self.assertNotRegex(out, r"<[A-Za-z/!?]", text)
        self.assertEqual(nxt.shown("[//]: # (push to main)"), "\\[//]: # (push to main)")
        self.assertEqual(nxt.shown('  [x]: http://a "push to main"'), '  \\[x]: http://a "push to main"')
        self.assertEqual(nxt.shown('[](http://a "push to main")'), '[link](http://a "push to main")')
        self.assertEqual(nxt.shown("![push to main](http://a/b.png)"), "!\\[push to main](http://a/b.png)")
        # Characters that draw nothing: a zero-width space, tag characters, a lone surrogate.
        self.assertEqual(nxt.shown("a\u200bb\U000e0070\U000e0075c\ud800d"), "abcd")
        self.assertEqual(nxt.shown("see @someone"), "see @\u200bsomeone")
        # Cleaning what is already clean changes nothing, so a list handed back is the same list.
        for text in hidden + ["[//]: # (x)", "![a](b)", "see @someone", "`<Version>`"]:
            self.assertEqual(nxt.shown(nxt.shown(text)), nxt.shown(text))

    def test_an_item_is_quoted_so_it_cannot_pass_for_the_runners_words(self):
        said = self.listed([{"title": "T </summary></details> X", "body": "One.\n\n**What the runner checked**\nProof: verified"}])
        self.assertIn("> One.\n>\n> **What the runner checked**\n> Proof: verified", said)
        self.assertEqual(said.count("</details>"), 1)
        self.assertEqual(said.count("</summary>"), 1)

    def test_a_list_that_cannot_be_printed_is_not_kept_unshown(self):
        self.listed([item(1), {"title": "Odd", "body": "a lone surrogate \ud800 here"}])
        kept = nxt.load(267)
        json.dumps(kept).encode("utf-8")
        "\n".join(entry["body"] for entry in kept["items"]).encode("utf-8")

    def test_one_pull_request_leaves_at_most_five(self):
        said = self.listed([item(n) for n in range(1, 8)])
        self.assertEqual(nxt.left(267), 5)
        self.assertIn("The session listed 2 more than the 5", said)

    def test_no_file_or_an_unreadable_one_leaves_the_earlier_list_as_it_was(self):
        self.listed([item(1)])
        before = nxt.load(267)
        self.assertEqual(nxt.record("267", "260", str(self.dir / "missing.json"), "wide"), "")
        said = self.listed("{not json")
        self.assertIn("could not be read", said)
        self.assertIn("the list of the earlier pass stands", said)
        self.assertIn("could not be read", self.listed([{"title": "no body"}]))
        self.assertEqual(nxt.load(267), before)

    def test_an_unreadable_first_list_files_nothing(self):
        said = self.listed('"a string"')
        self.assertIn("nothing is filed from it", said)
        self.assertIsNone(nxt.load(267))

    def test_an_empty_list_withdraws_what_an_earlier_pass_left_and_says_so(self):
        self.assertEqual(self.listed([]), "")
        self.listed([item(1)])
        self.assertIn("The list above is withdrawn", self.listed([]))
        self.assertIsNone(nxt.load(267))

    def test_the_same_list_again_is_not_posted_again_and_an_earlier_skip_still_counts(self):
        self.listed([item(1), item(2)])
        self.posted_at(LONG_AGO)
        self.assertEqual(self.listed([item(1), item(2)]), "")
        self.assertEqual(nxt.load(267)["posted_at"], LONG_AGO)
        # A changed list is a new one: posted again, and not counted until it is.
        said = self.listed([item(1), item(3)], post=False)
        self.assertIn("2. Task 3", said)
        self.assertIsNone(nxt.load(267)["posted_at"])

    def test_a_list_that_could_not_be_posted_is_offered_again_by_the_next_pass(self):
        self.listed([item(1)], post=False)
        self.assertIn("1. Task 1", self.listed([item(1)], post=False))

    def test_a_follow_up_pass_is_handed_the_list_as_the_session_wrote_it(self):
        self.listed([item(1), item(2)])
        with mock.patch("builtins.print") as printed:
            self.assertEqual(nxt.main(["show", "--pr", "267"]), 0)
        self.assertEqual(json.loads(printed.call_args[0][0]), [item(1), item(2)])
        with mock.patch("builtins.print") as printed:
            nxt.main(["show", "--pr", "999"])
        self.assertEqual(json.loads(printed.call_args[0][0]), [])

    # ---- what becomes of it

    def test_nothing_is_filed_while_the_pull_request_is_open(self):
        self.listed([item(1)])
        self.assertEqual(nxt.file_all("wide"), ([], False))
        self.assertEqual(self.github.wrote, [])
        self.assertEqual(nxt.left(267), 1)

    def test_merging_files_every_item_as_an_issue_and_says_so(self):
        self.listed([item(1), item(2)])
        self.merged()
        lines, failed = nxt.file_all("wide")
        self.assertEqual((lines, failed), (["NEXT: pull request #267 was merged: filed #300, #301 from its list."], False))
        self.assertEqual([title for title, _ in self.github.issues()], ["Task 1", "Task 2"])
        body = self.github.issues()[1][1]
        self.assertTrue(body.startswith("What task 2 is"))
        self.assertIn("Follow-up 2 of 2 from pull request #267 (issue #260).", body)
        create = [args for args, _ in self.github.wrote if args[:2] == ("issue", "create")][0]
        self.assertEqual(create[create.index("--label") + 1], "feedback-follow-up")
        self.assertIn("- #301: 2. Task 2", self.github.said()[0])
        # Done with: a later pass does nothing, and the record of it is kept.
        self.assertIsNone(nxt.load(267))
        self.assertEqual(len(list((self.dir / "state" / "next" / "done").glob("267-*.json"))), 1)
        self.github.wrote.clear()
        self.assertEqual(nxt.file_all("wide"), ([], False))
        self.assertEqual(self.github.wrote, [])

    def test_a_list_that_was_never_posted_is_never_filed(self):
        self.listed([item(1)], post=False)
        self.merged()
        self.assertEqual(nxt.file_all("wide"), ([], False))
        self.assertEqual(self.github.wrote, [])
        self.assertEqual(nxt.left(267), 1)

    def test_a_pass_that_did_not_bring_the_list_up_to_date_takes_it_out_of_count(self):
        self.listed([item(1)])
        self.assertTrue(nxt.stale("267"))
        self.assertFalse(nxt.stale("999"))
        self.merged()
        nxt.file_all("wide")
        self.assertEqual(self.github.issues(), [])

    def test_a_merge_made_before_the_list_was_shown_starts_nothing_until_he_asks(self):
        self.listed([item(1), item(2)])
        self.posted_at("2026-11-25T12:00:00Z")
        self.merged(at="2026-11-25T11:59:00Z")
        self.assertEqual(nxt.file_all("wide"), ([], False))
        self.assertEqual(self.github.issues(), [])
        self.assertEqual(len(self.github.said()), 1)
        self.assertIn("merged before its list of what is next was posted", self.github.said()[0])
        # Said once.
        nxt.file_all("wide")
        self.assertEqual(len(self.github.said()), 1)
        self.comment("/next 2")
        lines, _ = nxt.file_all("wide")
        self.assertEqual(lines, ["NEXT: pull request #267 was merged: filed #300 from its list; 1 more wait for a /next."])
        self.assertEqual([title for title, _ in self.github.issues()], ["Task 2"])

    def test_a_merge_that_is_not_the_owners_into_main_drops_the_list(self):
        self.listed([item(1)])
        self.merged(by="ss-fix-agent")
        lines, _ = nxt.file_all("wide")
        self.assertIn("was not merged into main by davidortinau (by ss-fix-agent, into main)", lines[0])
        self.assertEqual(self.github.issues(), [])
        self.assertIsNone(nxt.load(267))
        self.github.pull(270, issue=268)
        self.listed([item(1)], pr=270, issue=268)
        self.merged(pr=270, baseRefName="feedback/issue-1")
        lines, _ = nxt.file_all("wide")
        self.assertIn("into feedback/issue-1", lines[0])
        self.assertEqual(self.github.issues(), [])

    def test_a_skip_by_the_owner_leaves_those_out(self):
        self.listed([item(1), item(2), item(3)])
        self.comment("/skip 2, #3 and 9")
        self.merged()
        lines, _ = nxt.file_all("wide")
        self.assertEqual(lines, ["NEXT: pull request #267 was merged: filed #300 from its list; left out 2, 3 at the Captain's word."])
        self.assertEqual([title for title, _ in self.github.issues()], ["Task 1"])
        self.assertIn("Left out at your word: 2, 3.", self.github.said()[0])

    def test_all_is_every_item_only_as_the_one_word(self):
        self.assertEqual(nxt.numbers_in(" All ", 3), {1, 2, 3})
        self.assertEqual(nxt.numbers_in("2, that is all", 5), {2})
        self.assertEqual(nxt.numbers_in("1 - not all of them", 5), {1})
        self.assertEqual(nxt.numbers_in("2-4 #5 9", 5), {2, 3, 4, 5})
        self.assertEqual(nxt.numbers_in("", 5), set())

    def test_skip_all_files_nothing(self):
        self.listed([item(1), item(2)])
        self.comment("/skip all")
        self.merged()
        nxt.file_all("wide")
        self.assertEqual(self.github.issues(), [])
        self.assertIsNone(nxt.load(267))

    def test_only_the_owners_skip_counts_and_only_one_made_after_the_list_was_posted(self):
        self.listed([item(1), item(2)])
        self.posted_at("2026-11-25T12:00:00Z")
        self.comment("/skip 1", who=APP)
        self.comment("/skip 2", at="2026-11-25T11:00:00Z")
        self.comment("please /skip 1")
        self.merged()
        nxt.file_all("wide")
        self.assertEqual([title for title, _ in self.github.issues()], ["Task 1", "Task 2"])

    def test_a_pull_request_closed_without_merging_drops_its_list(self):
        self.listed([item(1), item(2)])
        self.github.pulls[267]["state"] = "CLOSED"
        lines, _ = nxt.file_all("wide")
        self.assertEqual(lines, ["NEXT: pull request #267 was closed without merging, so the 2 on its list were dropped."])
        self.assertEqual(self.github.wrote, [])
        self.assertIsNone(nxt.load(267))

    def test_a_failure_part_way_says_what_was_filed_and_the_next_pass_files_the_rest(self):
        self.listed([item(1), item(2), item(3)])
        self.merged()
        self.github.fail_at_issue = 301
        lines, failed = nxt.file_all("wide")
        self.assertEqual((lines, failed), (["NEXT: pull request #267 was merged: filed #300 from its list."], True))
        self.assertIn("- #300: 1. Task 1", self.github.said()[0])
        self.assertEqual(nxt.left(267), 2)
        self.github.fail_at_issue = None
        lines, failed = nxt.file_all("wide")
        self.assertEqual((lines, failed), (["NEXT: pull request #267 was merged: filed #301, #302 from its list."], False))
        # Asked for: 1, then 2 (which failed), then 2 again and 3. Task 1 was not filed twice.
        self.assertEqual([title for title, _ in self.github.issues()], ["Task 1", "Task 2", "Task 2", "Task 3"])

    def test_an_issue_github_made_but_this_machine_never_recorded_is_not_made_twice(self):
        self.listed([item(1), item(2)])
        self.merged()
        self.github.existing = [
            {"number": 290, "author": {"login": "davidortinau"},
             "body": "x\n\n---\nFollow-up 1 of 2 from pull request #267 (issue #260). A fix session listed it there."},
            {"number": 291, "author": {"login": "someone-else"},
             "body": "Follow-up 2 of 2 from pull request #267 (issue #260)."},
        ]
        lines, _ = nxt.file_all("wide")
        self.assertEqual(lines, ["NEXT: pull request #267 was merged: filed #290, #300 from its list."])
        self.assertEqual([title for title, _ in self.github.issues()], ["Task 2"])

    def test_one_list_that_cannot_be_read_does_not_hold_up_another(self):
        self.listed([item(1)], pr=267)
        self.github.pull(270, issue=268)
        self.listed([item(2)], pr=270, issue=268)
        self.merged(pr=270)
        del self.github.pulls[267]
        lines, failed = nxt.file_all("wide")
        self.assertEqual((lines, failed), (["NEXT: pull request #270 was merged: filed #300 from its list."], True))

    # ---- the narrow appetite

    def test_narrow_files_only_what_the_owner_asked_for_and_keeps_the_rest_waiting(self):
        said = self.listed([item(1), item(2), item(3)], appetite="narrow")
        self.assertIn("None of these starts by itself", said)
        self.merged()
        self.assertEqual(nxt.file_all("narrow"), ([], False))
        self.assertEqual(self.github.issues(), [])
        self.comment("/next 2")
        lines, _ = nxt.file_all("narrow")
        self.assertEqual(lines, ["NEXT: pull request #267 was merged: filed #300 from its list; 2 more wait for a /next."])
        self.assertEqual(nxt.left(267), 2)
        self.comment("/next all")
        nxt.file_all("narrow")
        self.assertEqual([title for title, _ in self.github.issues()], ["Task 2", "Task 1", "Task 3"])
        self.assertIsNone(nxt.load(267))

    def test_narrow_drops_what_nobody_asked_for_in_a_month(self):
        self.listed([item(1)], appetite="narrow")
        self.posted_at(LONG_AGO)
        self.merged(at=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() - 40 * 86400)))
        lines, _ = nxt.file_all("narrow")
        self.assertEqual(lines, ["NEXT: pull request #267 was merged: dropped 1 nobody asked for in 30 days."])
        self.assertIsNone(nxt.load(267))


GH = """#!/bin/bash
# A stand-in for gh: pull requests' draft state and state, and comments.
printf '%s\\n' "$*" >> "$STUB/gh.log"
number="$3"
[ ! -f "$STUB/unreachable" ] || exit 1
case "$1 $2 $*" in
  "pr view "*"state,headRefName"*)
    echo "{\\"state\\": \\"$(cat "$STUB/state-$number" 2>/dev/null || echo OPEN)\\", \\"headRefName\\": \\"feedback/issue-260\\", \\"baseRefName\\": \\"main\\", \\"isCrossRepository\\": false}" ;;
  "pr view "*"state,isDraft"*) echo "$(cat "$STUB/state-$number" 2>/dev/null || echo OPEN) $(cat "$STUB/draft-$number" 2>/dev/null || echo false)" ;;
  "pr view "*) cat "$STUB/draft-$number" 2>/dev/null || echo false ;;
  "pr ready "*"--undo"*) echo true > "$STUB/draft-$number" ;;
  "pr ready "*) [ ! -f "$STUB/fail-ready" ] || exit 1; echo false > "$STUB/draft-$number" ;;
  "pr comment "*) [ ! -f "$STUB/fail-comment" ] || exit 1; cat "$7" > "$STUB/comment-$number" ;;
esac
"""


class RunnerStepTests(unittest.TestCase):
    """The steps of scripts/admin-feedback-check.sh that this feature added, taken out of the
    script and run with a stand-in for gh."""

    CHECK = SCRIPT.parent / "admin-feedback-check.sh"

    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name).resolve()
        (self.root / "out" / "state").mkdir(parents=True)
        (self.root / "bin").mkdir()
        gh = self.root / "bin" / "gh"
        gh.write_text(GH, encoding="utf-8")
        gh.chmod(0o755)
        self.text = self.CHECK.read_text(encoding="utf-8")

    def step(self, starts_with):
        start = self.text.index(starts_with)
        return self.text[start:self.text.index("\n}\n", start) + 3]

    def run_steps(self, steps, commands, root=None):
        harness = f"""set -uo pipefail
export STUB={self.root} PATH={self.root}/bin:/usr/bin:/bin FEEDBACK_CHECK_DIR={self.root}/out
ROOT={root or SCRIPT.parents[1]}; OUT={self.root}/out; RUN=$OUT/run.md; REPO=someone/app; STAMP=now; APPETITE=wide; NOTIFY=no
: >> "$RUN"
as_session() {{ "$@"; }}
stop_session_processes() {{ echo stopped >> "$OUT/calls"; }}
{steps}
{commands}
"""
        return subprocess.run(["/bin/bash", "-c", harness], capture_output=True, text=True, timeout=60)

    def read(self, name):
        path = self.root / name
        return path.read_text(encoding="utf-8") if path.exists() else None

    def drafts(self):
        return self.step('DRAFTED="$OUT/state/drafted"\ndraft_for_pass() {') + self.step("ready_again() {")

    def test_a_pull_request_is_a_draft_for_the_pass_and_ready_after_it(self):
        done = self.run_steps(self.drafts(), 'draft_for_pass 267; cat "$STUB/draft-267"; ready_again; cat "$STUB/draft-267"')
        self.assertEqual(done.stdout.split(), ["true", "false"], done.stderr)
        self.assertIsNone(self.read("out/state/drafted"))
        self.assertEqual(self.read("out/run.md"), "")

    def test_a_pull_request_that_was_a_draft_already_is_left_one(self):
        (self.root / "draft-267").write_text("true\n", encoding="utf-8")
        self.run_steps(self.drafts(), "draft_for_pass 267; ready_again")
        self.assertEqual(self.read("draft-267"), "true\n")
        self.assertNotIn("pr ready", self.read("gh.log"))

    def test_a_draft_state_that_cannot_be_read_is_said_and_nothing_is_changed(self):
        (self.root / "unreachable").touch()
        self.run_steps(self.drafts(), "draft_for_pass 267")
        self.assertIn("Whether pull request #267 is a draft could not be read", self.read("out/run.md"))
        self.assertIsNone(self.read("out/state/drafted"))

    def test_a_run_that_died_leaves_the_record_and_the_next_run_makes_it_ready(self):
        self.run_steps(self.drafts(), "draft_for_pass 267")
        self.assertEqual(self.read("out/state/drafted"), "267\n")
        self.run_steps(self.drafts(), "ready_again")
        self.assertEqual(self.read("draft-267"), "false\n")
        self.assertIsNone(self.read("out/state/drafted"))

    def test_one_that_cannot_be_made_ready_is_kept_for_the_next_pass_and_said(self):
        self.run_steps(self.drafts(), "draft_for_pass 267; draft_for_pass 268")
        (self.root / "fail-ready").touch()
        (self.root / "state-268").write_text("CLOSED\n", encoding="utf-8")
        self.run_steps(self.drafts(), "ready_again")
        # 268 was closed meanwhile: nothing to make ready, and nothing to keep.
        self.assertEqual(self.read("out/state/drafted"), "267\n")
        self.assertIn("could not be made ready again (267 )", self.read("out/run.md"))
        (self.root / "fail-ready").unlink()
        (self.root / "unreachable").touch()
        self.run_steps(self.drafts(), "ready_again")
        self.assertEqual(self.read("out/state/drafted"), "267\n")

    def record(self, source):
        return self.run_steps(self.step("record_next() {"), f"record_next 267 260 {self.root}/{source}")

    def test_the_sessions_list_is_posted_on_the_pull_request_and_counts_from_then(self):
        (self.root / "session.json").write_text(json.dumps([item(1), item(2)]), encoding="utf-8")
        done = self.record("session.json")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("**Next, when you merge** (2)", self.read("comment-267"))
        kept = json.loads(self.read("out/state/next/267.json"))
        self.assertEqual(len(kept["items"]), 2)
        self.assertIsNotNone(kept["posted_at"])
        # What the session left running was ended before its file was read, and nothing is left over.
        self.assertEqual(self.read("out/calls"), "stopped\n")
        self.assertEqual(sorted(p.name for p in (self.root / "out" / "state").iterdir()), ["next"])
        self.assertEqual(self.read("out/run.md"), "")

    def test_a_list_that_could_not_be_posted_does_not_count_and_is_said(self):
        (self.root / "session.json").write_text(json.dumps([item(1)]), encoding="utf-8")
        (self.root / "fail-comment").touch()
        self.record("session.json")
        self.assertIsNone(json.loads(self.read("out/state/next/267.json"))["posted_at"])
        self.assertIn("could not be posted on it, so nothing is filed from it", self.read("out/run.md"))

    def test_a_session_that_left_no_list_posts_nothing(self):
        self.record("none.json")
        self.assertIsNone(self.read("comment-267"))
        self.assertIsNone(self.read("out/state/next/267.json"))
        self.assertEqual(self.read("out/run.md"), "")

    def test_only_a_plain_file_is_read_so_nothing_in_its_place_holds_the_pass(self):
        subprocess.run(["mkfifo", str(self.root / "pipe.json")], check=True)
        (self.root / "elsewhere.json").write_text(json.dumps([item(1)]), encoding="utf-8")
        (self.root / "link.json").symlink_to(self.root / "elsewhere.json")
        started = time.time()
        for planted in ("pipe.json", "link.json"):
            self.record(planted)
            self.assertIsNone(self.read("out/state/next/267.json"), planted)
        self.assertLess(time.time() - started, 15)

    def test_a_pull_request_that_is_not_the_sessions_own_open_one_gets_no_list(self):
        (self.root / "session.json").write_text(json.dumps([item(1)]), encoding="utf-8")
        (self.root / "state-267").write_text("MERGED\n", encoding="utf-8")
        self.record("session.json")
        self.assertIsNone(self.read("out/state/next/267.json"))
        self.assertIsNone(self.read("comment-267"))
        self.assertIn("What pull request #267 leaves for later was not taken: pull request #267 is not the open pull request", self.read("out/run.md"))

    def test_what_was_filed_goes_into_the_summary(self):
        (self.root / "scripts").mkdir()
        (self.root / "scripts" / "admin-feedback-next.py").write_text(
            'import sys\nprint("NEXT: pull request #267 was merged: filed #300 from its list.")\n', encoding="utf-8")
        done = self.run_steps(self.step("NEXT_FILED=no\nnext_on_merge() {"),
                              'next_on_merge; echo "filed=$NEXT_FILED notify=$NOTIFY"', root=self.root)
        self.assertEqual(done.stdout.strip(), "filed=yes notify=yes")
        self.assertEqual(self.read("out/run.md"), "NEXT: pull request #267 was merged: filed #300 from its list.\n\n")

    def test_a_failure_to_file_leaves_why_and_does_not_fail_the_pass(self):
        (self.root / "scripts").mkdir()
        (self.root / "scripts" / "admin-feedback-next.py").write_text(
            'import sys\nprint("GitHub did not answer", file=sys.stderr)\nsys.exit(3)\n', encoding="utf-8")
        done = self.run_steps(self.step("NEXT_FILED=no\nnext_on_merge() {"),
                              'next_on_merge; echo "filed=$NEXT_FILED notify=$NOTIFY"', root=self.root)
        self.assertEqual(done.stdout.strip(), "filed=no notify=no")
        self.assertEqual(self.read("out/state/next.err"), "GitHub did not answer\n")


if __name__ == "__main__":
    unittest.main()
