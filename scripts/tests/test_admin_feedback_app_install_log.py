"""Offline tests for the app install log (scripts/ios-phone.sh, docs/app-install-log.md).

A fix session dates a report of a fault in a native screen against that log to find which build
the phone had, so each install on a phone has to leave its line there. The script runs here in
a git repository made for the test, with stand-ins for xcrun, plutil, sleep and the build:
nothing is built, and nothing reaches a phone."""

import os
import re
import shutil
import stat
import subprocess
import tempfile
import time
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
LOG = "docs/app-install-log.md"
SUBJECT = "fix(quiz): the footer keeps its rows"
APP = "src/SentenceStudio.Native.iOS/bin/Release/net11.0-ios/ios-arm64/SentenceStudio.Native.iOS.app"
DEVICES = {"Duotopia": "00008160-001A2D2A0C60000A", "DX24": "CF4F94E3-A1C9-5617-A089-9ABB0110A09F"}

# Answers each devicectl call the script makes from files under $STUB, so a test changes what
# the phone "does" by writing a file. Every call is logged: a test reads the log to see whether
# an install was asked for. "installs" holds one word per attempt, used up from the top.
XCRUN = """#!/bin/bash
echo "$*" >> "$STUB/xcrun.log"
case "$*" in
  *"device info details"*) [ ! -e "$STUB/unreachable" ] ;;
  *"device info processes"*)
    echo "1 /usr/libexec/backboardd"
    if [ -e "$STUB/running" ] || [ -e "$STUB/launched" ]; then echo "2 /private/var/App/SentenceStudio.Native.iOS"; fi ;;
  *"device install app"*)
    answer=$(head -n 1 "$STUB/installs" 2>/dev/null)
    sed -i.bak 1d "$STUB/installs" 2>/dev/null
    if [ "$answer" = fails ]; then echo "ERROR: The operation couldn't be completed. (NWError 57)"; exit 1; fi
    echo "App installed:" ;;
  *"device process launch"*)
    [ ! -e "$STUB/locked" ] || exit 1
    [ -e "$STUB/dies" ] || touch "$STUB/launched" ;;
esac
"""

# Stands in for scripts/ios-build.sh device: leaves an .app where the real one does and prints
# its path last, as the real one does.
BUILD = """#!/bin/bash
echo "device" >> "$STUB/build.log"
[ ! -e "$STUB/build-fails" ] || { echo "FAIL: the build failed"; exit 1; }
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$ROOT/%s"
echo built >> "$ROOT/%s/SentenceStudio.Native.iOS.dll"
echo "APP=$ROOT/%s"
""" % (APP, APP, APP)


def run(args, cwd, env=None):
    return subprocess.run(args, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          universal_newlines=True)


class AppInstallLogTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="app-install-log-test-"))
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.tree = self.tmp / "tree"
        self.stub = self.tmp / "stub"
        (self.tree / "scripts").mkdir(parents=True)
        (self.tree / "docs").mkdir()
        (self.tree / "src").mkdir()
        (self.stub / "bin").mkdir(parents=True)
        shutil.copy(REPO / "scripts" / "ios-phone.sh", self.tree / "scripts" / "ios-phone.sh")
        if (REPO / LOG).exists():
            shutil.copy(REPO / LOG, self.tree / LOG)
        (self.tree / "src" / "app.txt").write_text("the app\n")
        (self.tree / ".gitignore").write_text("bin/\n")
        self.executable(self.tree / "scripts" / "ios-build.sh", BUILD)
        self.executable(self.stub / "bin" / "xcrun", XCRUN)
        self.executable(self.stub / "bin" / "plutil",
                        "#!/bin/bash\necho com.simplyprofound.sentencestudio.native.preview\n")
        self.executable(self.stub / "bin" / "sleep", "#!/bin/bash\n")
        self.env = dict(os.environ, STUB=str(self.stub),
                        PATH=str(self.stub / "bin") + os.pathsep + os.environ["PATH"])
        self.git("init", "-q")
        self.git("add", "-A")
        self.commit(SUBJECT)
        self.before = (self.tree / LOG).read_text() if (self.tree / LOG).exists() else ""

    def executable(self, path, text):
        path.write_text(text)
        path.chmod(path.stat().st_mode | stat.S_IXUSR)

    def git(self, *args):
        done = run(["git", "-c", "user.name=test", "-c", "user.email=test@localhost", *args], self.tree)
        self.assertEqual(done.returncode, 0, done.stdout)
        return done.stdout.strip()

    def commit(self, subject):
        self.git("commit", "-q", "--allow-empty", "-m", subject)
        return self.git("rev-parse", "HEAD")

    def phone(self, *args):
        return run(["bash", "scripts/ios-phone.sh", *args], self.tree, self.env)

    def set(self, name, text=""):
        (self.stub / name).write_text(text)

    def calls(self, name="xcrun.log"):
        log = self.stub / name
        return log.read_text().splitlines() if log.exists() else []

    def installs_asked(self):
        return [call for call in self.calls() if "device install app" in call]

    def added(self):
        """The lines the script added to the log, and nothing of what was there is changed."""
        self.assertTrue((self.tree / LOG).exists(), LOG + " does not exist")
        now = (self.tree / LOG).read_text()
        self.assertTrue(now.startswith(self.before), "the log's earlier text was changed")
        return now[len(self.before):].splitlines()

    def test_an_install_that_succeeds_leaves_one_line(self):
        head = self.git("rev-parse", "HEAD")
        started = datetime.now(timezone.utc)
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertEqual(len(self.installs_asked()), 1)
        self.assertIn(DEVICES["Duotopia"], self.installs_asked()[0])
        self.assertEqual(len(self.added()), 1, done.stdout)
        found = re.fullmatch(r"- (\d{4}-\d\d-\d\d \d\d:\d\d) UTC `([0-9a-f]{40})` (.*) \(phone `Duotopia`\)",
                             self.added()[0])
        self.assertTrue(found, self.added())
        self.assertEqual(found.group(2), head)
        self.assertEqual(found.group(3), SUBJECT)
        when = datetime.strptime(found.group(1), "%Y-%m-%d %H:%M").replace(tzinfo=timezone.utc)
        self.assertLessEqual(started - timedelta(minutes=1), when)
        self.assertLessEqual(when, datetime.now(timezone.utc))

    def test_the_line_is_left_for_the_captain_to_commit(self):
        head = self.git("rev-parse", "HEAD")
        done = self.phone("DX24", "--yes")
        self.assertEqual(self.git("rev-parse", "HEAD"), head)
        self.assertEqual(self.git("status", "--porcelain"), "M " + LOG)
        self.assertIn("recorded in %s (not committed; commit and push it)" % LOG, done.stdout)
        self.assertIn("(phone `DX24`)", self.added()[0])
        self.assertIn(DEVICES["DX24"], self.installs_asked()[0])

    def test_a_run_that_stops_before_installing_leaves_no_line(self):
        done = self.phone("Duotopia")
        self.assertEqual(done.returncode, 2, done.stdout)
        self.assertIn("READY:", done.stdout)
        self.assertEqual(self.installs_asked(), [])
        self.assertEqual(self.added(), [])

    def test_an_install_that_fails_twice_leaves_no_line(self):
        self.set("installs", "fails\nfails\n")
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertIn("FAIL: install failed twice", done.stdout)
        self.assertEqual(len(self.installs_asked()), 2)
        self.assertEqual(self.added(), [])
        self.assertEqual(self.git("status", "--porcelain"), "")

    def test_an_install_that_succeeds_on_its_second_attempt_leaves_one_line(self):
        self.set("installs", "fails\n")
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertEqual(len(self.installs_asked()), 2)
        self.assertEqual(len(self.added()), 1)

    def test_a_phone_that_cannot_be_reached_leaves_no_line(self):
        self.set("unreachable")
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertIn("STOP: Duotopia is not reachable", done.stdout)
        self.assertEqual(self.installs_asked(), [])
        self.assertEqual(self.added(), [])

    def test_an_app_that_is_running_on_the_phone_leaves_no_line(self):
        self.set("running")
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertIn("STOP: SentenceStudio is running on Duotopia", done.stdout)
        self.assertEqual(self.installs_asked(), [])
        self.assertEqual(self.added(), [])

    def test_a_build_that_fails_leaves_no_line(self):
        self.set("build-fails")
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertEqual(self.calls(), [])
        self.assertEqual(self.added(), [])

    def test_an_install_whose_launch_fails_is_still_recorded(self):
        # The build is on the phone whether or not it could be opened (a locked phone).
        self.set("locked")
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertIn("WARN: installed, but launch failed", done.stdout)
        self.assertEqual(len(self.added()), 1)

    def test_an_install_whose_app_does_not_stay_running_is_still_recorded(self):
        self.set("dies")
        done = self.phone("Duotopia", "--yes")
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertIn("FAIL: launched but not running", done.stdout)
        self.assertEqual(len(self.added()), 1)

    def test_the_approved_rerun_names_the_commit_that_was_built_after_the_checkout_moved(self):
        # The usual two steps: build and stop, then --yes --no-build once the Captain approves.
        built = self.git("rev-parse", "HEAD")
        ready = self.phone("Duotopia")
        self.assertEqual(ready.returncode, 2, ready.stdout)
        self.commit("feat: pulled after the build")
        done = self.phone("Duotopia", "--yes", "--no-build")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertEqual(self.calls("build.log"), ["device"])
        self.assertEqual(len(self.added()), 1)
        self.assertIn("`%s` %s (phone `Duotopia`)" % (built, SUBJECT), self.added()[0])
        self.assertNotIn("note: this script did not build", done.stdout)

    def test_an_app_built_another_way_is_recorded_as_head_and_said_to_be_unchecked(self):
        run(["bash", "scripts/ios-build.sh", "device"], self.tree, self.env)
        head = self.git("rev-parse", "HEAD")
        done = self.phone("Duotopia", "--yes", "--no-build")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertIn("`%s` %s (phone `Duotopia`)" % (head, SUBJECT), self.added()[0])
        self.assertIn("note: this script did not build the app it installed", done.stdout)
        self.assertIn("correct the line before committing it", done.stdout)

    def test_an_app_built_again_another_way_is_not_given_the_earlier_build_s_commit(self):
        self.phone("Duotopia")
        head = self.commit("feat: built by hand after the script's build")
        later = time.time() + 120
        os.utime(self.tree / APP / "SentenceStudio.Native.iOS.dll", (later, later))
        done = self.phone("Duotopia", "--yes", "--no-build")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertIn("`%s` feat: built by hand after the script's build (phone `Duotopia`)" % head, self.added()[0])
        self.assertIn("note: this script did not build the app it installed", done.stdout)

    def test_a_build_that_failed_does_not_give_its_commit_to_the_app_left_from_before(self):
        self.phone("Duotopia")
        head = self.commit("feat: does not build")
        self.set("build-fails")
        self.assertEqual(self.phone("Duotopia").returncode, 1)
        done = self.phone("Duotopia", "--yes", "--no-build")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertIn("`%s` feat: does not build (phone `Duotopia`)" % head, self.added()[0])
        self.assertIn("note: this script did not build the app it installed", done.stdout)

    def test_a_build_with_uncommitted_changes_says_so_on_its_line(self):
        head = self.git("rev-parse", "HEAD")
        (self.tree / "src" / "app.txt").write_text("changed, not committed\n")
        self.phone("Duotopia")
        self.git("checkout", "-q", "--", "src/app.txt")
        done = self.phone("Duotopia", "--yes", "--no-build")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertTrue(self.added()[0].endswith(
            "`%s` %s (phone `Duotopia`; built with uncommitted changes)" % (head, SUBJECT)), self.added())

    def test_an_uncommitted_line_from_the_last_install_is_not_an_uncommitted_change(self):
        self.phone("Duotopia", "--yes")
        (self.stub / "launched").unlink()
        done = self.phone("DX24", "--yes")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertEqual(len(self.added()), 2)
        self.assertTrue(self.added()[0].endswith("(phone `Duotopia`)"), self.added())
        self.assertTrue(self.added()[1].endswith("(phone `DX24`)"), self.added())

    def test_the_same_build_installed_again_gets_a_line_of_its_own(self):
        self.phone("Duotopia", "--yes")
        (self.stub / "launched").unlink()
        done = self.phone("Duotopia", "--yes", "--no-build")
        self.assertEqual(done.returncode, 0, done.stdout)
        self.assertEqual(len(self.added()), 2)

    def test_a_log_saved_without_its_last_newline_still_gets_a_line_of_its_own(self):
        log = self.tree / LOG
        log.write_text(self.before + "- an earlier line")
        self.before = log.read_text() + "\n"
        self.phone("Duotopia", "--yes")
        self.assertEqual(len(self.added()), 1)
        self.assertTrue(self.added()[0].endswith("(phone `Duotopia`)"), self.added())

    @unittest.skipIf(os.geteuid() == 0, "root writes to a read-only file")
    def test_a_log_that_cannot_be_written_is_said_and_not_called_recorded(self):
        log = self.tree / LOG
        self.assertTrue(log.exists(), LOG + " does not exist")
        log.chmod(0o444)
        self.addCleanup(log.chmod, 0o644)
        done = self.phone("Duotopia", "--yes")
        self.assertNotEqual(done.returncode, 0, done.stdout)
        self.assertEqual(log.read_text(), self.before)
        self.assertNotIn("ok:   recorded in " + LOG, done.stdout)
        self.assertIn("FAIL: could not write to %s; this install is not recorded. The log is as it was." % LOG,
                      done.stdout)
        self.assertIn("(phone `Duotopia`)", done.stdout)
        # The install itself stands, and the script goes on to open the app.
        self.assertIn("ok:   installed on Duotopia", done.stdout)
        self.assertIn("ok:   launched and running on Duotopia", done.stdout)
        self.assertIn("FAIL: the app is installed on Duotopia, but the install is not in " + LOG, done.stdout)

    def test_a_write_that_fails_part_way_leaves_the_log_as_it_was(self):
        # Stands in for a full disk: the new log cannot be moved into the old one's place.
        self.executable(self.stub / "bin" / "mv", "#!/bin/bash\necho 'mv: No space left on device' >&2\nexit 1\n")
        done = self.phone("Duotopia", "--yes")
        self.assertNotEqual(done.returncode, 0, done.stdout)
        self.assertEqual(self.added(), [])
        self.assertEqual(self.git("status", "--porcelain"), "")
        self.assertNotIn("ok:   recorded in " + LOG, done.stdout)

    def test_a_missing_log_is_said_and_the_install_stands(self):
        (self.tree / LOG).unlink(missing_ok=True)
        done = self.phone("Duotopia", "--yes")
        self.assertNotEqual(done.returncode, 0, done.stdout)
        self.assertIn("FAIL: %s is missing; this install is not recorded" % LOG, done.stdout)
        self.assertIn("ok:   installed on Duotopia", done.stdout)

    def test_the_log_says_what_a_line_means_and_ends_where_the_script_adds(self):
        text = (REPO / LOG).read_text()
        self.assertIn("scripts/ios-phone.sh", text)
        self.assertIn("## Installs", text)
        for line in text.split("## Installs", 1)[1].splitlines():
            self.assertTrue(not line or line.startswith("- "), "not an install's line: " + line)

    def test_the_fix_session_s_instructions_and_the_runbook_name_the_log(self):
        for document in ("admin-feedback-fix.md", "deploy-runbook.md"):
            self.assertTrue(LOG in (REPO / "docs" / document).read_text(), document + " does not name " + LOG)


if __name__ == "__main__":
    unittest.main()
