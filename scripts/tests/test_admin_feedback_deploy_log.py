"""Offline tests for the deploy log (scripts/prod-deploy.sh after, docs/deploy-log.md).

A fix session dates a learner's flag against that log to find which build answered, so each
production deploy has to leave its line there. The script runs here in a git repository made
for the test, with stand-ins for az and for the post-deploy validator: nothing reaches Azure
and nothing is deployed."""

import os
import re
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
LOG = "docs/deploy-log.md"
SUBJECT = "fix(sam): a reply holds what it announces"

# Answers each az call the script makes from files under $STUB, so a test changes what Azure
# "says" by writing a file. "revision list" is matched before createdTime: its query sorts by it.
AZ = """#!/bin/bash
echo "$*" >> "$STUB/az.log"
case "$*" in
  *"account show"*) echo 66f9fa8f-604f-4688-bec1-16ff9a86a8e5 ;;
  *"flexible-server show"*) echo Ready ;;
  *"lock list"*) echo CanNotDelete ;;
  *"keyvault secret show"*) echo stand-in ;;
  *"revision list"*) printf 'api--before\\n%s\\n' "$(cat "$STUB/revision")" ;;
  *"sticky-sessions set"*) ;;
  *stickySessions.affinity*) echo sticky ;;
  *latestRevisionName*) cat "$STUB/revision" ;;
  *healthState*) echo Healthy ;;
  *runningState*) echo Running ;;
  *createdTime*) cat "$STUB/created" ;;
  *"length("*) echo 0 ;;
  *"].name"*) echo Coach__Enabled ;;
  *Coach__*|*Admin__*) printf 'Name\\tvalue\\n' ;;
esac
"""


def run(args, cwd, env=None):
    return subprocess.run(args, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          universal_newlines=True)


class DeployLogTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="deploy-log-test-"))
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.tree = self.tmp / "tree"
        self.stub = self.tmp / "stub"
        (self.tree / "scripts").mkdir(parents=True)
        (self.tree / "docs").mkdir()
        (self.tree / "src").mkdir()
        (self.tree / ".azure" / "sstudio-prod-biz").mkdir(parents=True)
        (self.stub / "bin").mkdir(parents=True)
        shutil.copy(REPO / "scripts" / "prod-deploy.sh", self.tree / "scripts" / "prod-deploy.sh")
        shutil.copy(REPO / LOG, self.tree / LOG)
        (self.tree / "src" / "app.txt").write_text("the app\n")
        (self.tree / ".gitignore").write_text(".azure\n")
        self.executable(self.stub / "bin" / "az", AZ)
        self.validator(passes=True)
        self.azure(revision="api--0000041", created="2026-11-26T01:40:12.1234567+00:00")
        self.env = dict(os.environ, STUB=str(self.stub),
                        PATH=str(self.stub / "bin") + os.pathsep + os.environ["PATH"])
        self.git("init", "-q")
        self.git("add", "-A")
        self.commit(SUBJECT, "2026-11-26T01:00:00+00:00")
        self.before = (self.tree / LOG).read_text()

    def executable(self, path, text):
        path.write_text(text)
        path.chmod(path.stat().st_mode | stat.S_IXUSR)

    def validator(self, passes):
        self.executable(self.tree / "scripts" / "post-deploy-validate.sh",
                        "#!/bin/bash\nexit %d\n" % (0 if passes else 1))

    def azure(self, revision=None, created=None):
        if revision is not None:
            (self.stub / "revision").write_text(revision + "\n")
        if created is not None:
            (self.stub / "created").write_text(created + "\n")

    def git(self, *args, when=None):
        env = dict(os.environ)
        if when:
            env.update(GIT_AUTHOR_DATE=when, GIT_COMMITTER_DATE=when)
        done = run(["git", "-c", "user.name=test", "-c", "user.email=test@localhost", *args], self.tree, env)
        self.assertEqual(done.returncode, 0, done.stdout)
        return done.stdout.strip()

    def commit(self, subject, when):
        self.git("commit", "-q", "--allow-empty", "-m", subject, when=when)
        return self.git("rev-parse", "HEAD")

    def script(self, step):
        return run(["bash", "scripts/prod-deploy.sh", step], self.tree, self.env)

    def added(self):
        """The lines the script added to the log, and nothing of what was there is changed."""
        now = (self.tree / LOG).read_text()
        self.assertTrue(now.startswith(self.before), "the log's earlier text was changed")
        return now[len(self.before):].splitlines()

    def test_a_deploy_that_passes_its_checks_leaves_one_line(self):
        head = self.git("rev-parse", "HEAD")
        done = self.script("after")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertEqual(self.added(), [
            "- 2026-11-26 01:40 UTC `%s` %s (API revision `api--0000041`)" % (head, SUBJECT)])
        self.assertTrue(re.fullmatch(r"[0-9a-f]{40}", head))

    def test_the_line_is_left_for_the_captain_to_commit(self):
        head = self.git("rev-parse", "HEAD")
        done = self.script("after")
        self.assertEqual(self.git("rev-parse", "HEAD"), head)
        self.assertEqual(self.git("status", "--porcelain"), "M " + LOG)
        self.assertIn("recorded in %s (not committed; commit and push it)" % LOG, done.stdout)

    def test_the_time_is_the_revision_s_in_utc_whatever_offset_azure_gives(self):
        self.azure(created="2026-11-25T19:40:12-06:00")
        self.script("after")
        self.assertTrue(self.added()[0].startswith("- 2026-11-26 01:40 UTC "), self.added())
        self.azure(revision="api--0000042", created="2026-11-26T03:05:59Z")
        self.script("after")
        self.assertTrue(self.added()[1].startswith("- 2026-11-26 03:05 UTC "), self.added())

    def test_running_after_again_does_not_write_the_deploy_twice(self):
        self.script("after")
        again = self.script("after")
        self.assertEqual(again.returncode, 0, again.stdout)
        self.assertEqual(len(self.added()), 1)
        self.assertIn("api--0000041 is already in " + LOG, again.stdout)

    def test_the_next_deploy_is_added_below_the_last(self):
        first = self.git("rev-parse", "HEAD")
        self.script("after")
        self.git("add", "-A")
        second = self.commit("docs: the deploy log's line", "2026-11-26T02:00:00+00:00")
        self.azure(revision="api--0000042", created="2026-11-26T02:30:00+00:00")
        done = self.script("after")
        self.assertEqual(done.returncode, 0, done.stdout)
        now = (self.tree / LOG).read_text().splitlines()
        self.assertIn("`%s`" % first, now[-2])
        self.assertEqual(now[-1], "- 2026-11-26 02:30 UTC `%s` docs: the deploy log's line "
                                  "(API revision `api--0000042`)" % second)

    def test_a_deploy_whose_checks_fail_is_not_recorded(self):
        self.validator(passes=False)
        done = self.script("after")
        self.assertNotEqual(done.returncode, 0)
        self.assertEqual(self.added(), [])
        self.assertIn("this deploy is not recorded in " + LOG, done.stdout)

    def test_a_checkout_that_moved_since_the_deploy_is_not_recorded_as_the_deploy(self):
        self.commit("feat: made after the deploy", "2026-11-26T05:00:00+00:00")
        done = self.script("after")
        self.assertNotEqual(done.returncode, 0)
        self.assertEqual(self.added(), [])
        self.assertIn("is not what was deployed", done.stdout)

    def test_no_time_from_azure_is_said_and_nothing_is_written(self):
        self.azure(created="")
        done = self.script("after")
        self.assertNotEqual(done.returncode, 0)
        self.assertEqual(self.added(), [])
        self.assertIn("Azure gave no creation time for api--0000041", done.stdout)

    def test_a_log_saved_without_its_last_newline_still_gets_a_line_of_its_own(self):
        log = self.tree / LOG
        log.write_text(self.before + "- an earlier line")
        self.before = log.read_text() + "\n"
        self.script("after")
        self.assertEqual(len(self.added()), 1)
        self.assertTrue(self.added()[0].startswith("- 2026-11-26 01:40 UTC "))

    def test_an_uncommitted_log_line_does_not_stop_the_next_deploy_s_check(self):
        self.script("after")
        done = self.script("check")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertIn("ok:   clean checkout", done.stdout)
        self.assertIn("note: %s holds a deploy line that is not committed" % LOG, done.stdout)

    def test_any_other_uncommitted_change_still_stops_the_check(self):
        (self.tree / "src" / "app.txt").write_text("changed\n")
        done = self.script("check")
        self.assertNotEqual(done.returncode, 0)
        self.assertIn("FAIL: uncommitted changes would ship", done.stdout)

    def test_the_fix_session_s_instructions_and_the_runbook_name_the_log(self):
        for document in ("admin-feedback-fix.md", "deploy-runbook.md"):
            self.assertTrue(LOG in (REPO / "docs" / document).read_text(), document + " does not name " + LOG)


if __name__ == "__main__":
    unittest.main()
