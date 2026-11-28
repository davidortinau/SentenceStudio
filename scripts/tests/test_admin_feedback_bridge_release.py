"""Offline tests for the SwiftUI bridge's release (scripts/admin-feedback-bridge-release.sh).

The bridge is a git repository made here, the builder and gh are stand-ins, and the feed and
the check's folder are temporary: nothing is built and nothing leaves this machine."""

import json
import os
import re
import shutil
import stat
import subprocess
import tempfile
import time
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "admin-feedback-bridge-release.sh"
PROJECT = "src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj"

# Two packs of one commit are never the same bytes; neither are this stand-in's.
BUILDER = """#!/bin/bash
echo "$1 $(git -C "$SESSION_BRIDGE" rev-parse HEAD) $(git -C "$SESSION_BRIDGE" status --porcelain | wc -l | tr -d ' ')" >> "$STUB/builder.log"
version="$(sed -n 's#.*<Version>\\(.*\\)</Version>.*#\\1#p' "$SESSION_BRIDGE/src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj")"
case "$1" in
  test)
    cp "$FEEDBACK_CHECK_DIR/state/bridge-release-note" "$STUB/note-during-the-build"
    [ ! -f "$STUB/fail-test" ] || { echo "STOP: the bridge's tests did not all pass. The whole output is nowhere." >&2; exit 1; }
    echo "Managed tests: 81/81 passed"
    echo "Swift tests: 91/91 passed" ;;
  package)
    [ ! -f "$STUB/fail-package" ] || { echo "STOP: the bridge's pack and verify failed." >&2; exit 1; }
    # An annotated tag, as a person's `git tag -a` makes; the release's own is a plain one.
    [ ! -f "$STUB/tag-during-the-build" ] || git -C "$FEEDBACK_BRIDGE_URL" -c user.name=other -c user.email=other@localhost \\
      tag -a -m "not made by the release" "v$version" "$(cat "$STUB/tag-during-the-build")"
    mkdir -p "$SESSION_BRIDGE/artifacts"
    printf 'package %s %s %s' "$version" "$RANDOM$RANDOM" "$(date +%s)" > "$SESSION_BRIDGE/artifacts/Microsoft.SwiftUI.$version.nupkg"
    echo "VERSION=$version"
    echo "PACKAGE=$SESSION_BRIDGE/artifacts/Microsoft.SwiftUI.$version.nupkg" ;;
esac
"""

# gh: the rules of main, a ruleset's bypass list (as the script's --jq would give them), and a
# release, which makes the tag as GitHub does: at --target, unless the tag is there already.
GH = """#!/bin/bash
printf '%s\\n' "$*" >> "$STUB/gh.log"
case "$1 $2 $3" in
  api\\ --paginate\\ repos/someone/bridge/rules/branches/main)
    [ ! -f "$STUB/unreadable-rules" ] || exit 1
    [ -f "$STUB/open-main" ] || echo 7 ;;
  api\\ repos/someone/bridge/rulesets/7\\ *)
    if [ -f "$STUB/app-may-bypass" ]; then echo 1; else echo 0; fi ;;
  release\\ create\\ *)
    [ ! -f "$STUB/fail-release" ] || { echo "HTTP 502" >&2; exit 1; }
    while [ $# -gt 0 ]; do [ "$1" != --target ] || target="$2"; shift; done
    tag="$(sed -n 's/^release create \\(v[^ ]*\\) .*/\\1/p' "$STUB/gh.log" | tail -1)"
    [ ! -f "$STUB/tag-made-first" ] || target="$(cat "$STUB/tag-made-first")"
    git -C "$FEEDBACK_BRIDGE_URL" rev-parse -q --verify "refs/tags/$tag" >/dev/null || git -C "$FEEDBACK_BRIDGE_URL" tag "$tag" "$target"
    echo "https://github.com/someone/bridge/releases/tag/$tag" ;;
  *) echo "the stand-in for gh does not know: $*" >&2; exit 1 ;;
esac
"""


class BridgeReleaseTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.dir = Path(folder.name).resolve()
        self.bridge = self.dir / "bridge"
        self.out = self.dir / "check"
        self.feed = self.dir / "feed"
        self.packages = self.dir / "packages"
        self.stub = self.dir / "stub"
        for made in (self.bridge, self.out / "state", self.feed, self.stub / "bin"):
            made.mkdir(parents=True)
        for name, text in (("builder.sh", BUILDER), ("bin/gh", GH)):
            path = self.stub / name
            path.write_text(text, encoding="utf-8")
            path.chmod(path.stat().st_mode | stat.S_IXUSR)
        self.git("init", "-q", "-b", "main")
        self.first = self.commit("0.1.0-preview.7", "feat: the first (0.1.0-preview.7)")
        self.git("tag", "v0.1.0-preview.7")
        self.package("0.1.0-preview.7").write_text("given out before", encoding="utf-8")

    def git(self, *args):
        # Not this machine's git configuration: no hooks, templates or signing of the developer's.
        done = subprocess.run(
            ["git", "-C", str(self.bridge), "-c", "user.name=test", "-c", "user.email=test@localhost", *args],
            capture_output=True, text=True, check=True,
            env={"PATH": "/usr/bin:/bin", "HOME": str(self.dir), "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"})
        return done.stdout.strip()

    def commit(self, version, subject, code=None):
        project = self.bridge / PROJECT
        project.parent.mkdir(parents=True, exist_ok=True)
        project.write_text(f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>\n",
                           encoding="utf-8")
        if code is not None:
            (self.bridge / "src" / "Microsoft.SwiftUI" / "View.cs").write_text(code, encoding="utf-8")
        (self.bridge / "README.md").write_text(f"{subject}\n", encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", subject)
        return self.git("rev-parse", "HEAD")

    def run_script(self, mode, **extra):
        env = {
            "HOME": str(self.dir), "PATH": f"{self.stub / 'bin'}:/usr/bin:/bin:/usr/sbin:/sbin",
            "FEEDBACK_CHECK_DIR": str(self.out), "FEEDBACK_BRIDGE_REPO": "someone/bridge",
            "FEEDBACK_BRIDGE_URL": str(self.bridge), "FEEDBACK_BRIDGE_FEED": str(self.feed),
            "FEEDBACK_BRIDGE_BUILDER": str(self.stub / "builder.sh"), "NUGET_PACKAGES": str(self.packages),
            "STUB": str(self.stub), "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
        }
        env.update(extra)
        return subprocess.run(["/bin/bash", str(SCRIPT), mode], capture_output=True, text=True, env=env, timeout=120)

    def read(self, name):
        try:
            return (self.out / "state" / name).read_text(encoding="utf-8").strip()
        except OSError:
            return ""

    def log(self, name):
        try:
            return (self.stub / name).read_text(encoding="utf-8").splitlines()
        except OSError:
            return []

    def package(self, version):
        return self.feed / f"Microsoft.SwiftUI.{version}.nupkg"

    def built(self):
        return [line.split()[0] for line in self.log("builder.log")]

    def releases(self):
        return [line for line in self.log("gh.log") if line.startswith("release create")]

    def test_a_version_that_has_its_tag_on_main_is_not_packed(self):
        head = self.git("rev-parse", "HEAD")
        due = self.run_script("due")
        self.assertEqual((due.returncode, due.stdout), (0, ""))
        self.assertEqual(self.read("bridge-release-seen").split()[::3], [head, "nothing"])
        self.assertTrue(self.read("bridge-release-note").startswith("nothing\nNothing to pack"))
        released = self.run_script("release")
        self.assertEqual((released.returncode, released.stdout), (0, ""))
        self.assertEqual(self.log("builder.log"), [])
        self.assertEqual(self.log("gh.log"), [])

    def test_main_where_it_was_is_not_looked_into_again(self):
        self.run_script("due")
        # Were it looked into, the missing clone would be made again.
        subprocess.run(["rm", "-rf", str(self.out / "bridge-release" / "source")], check=True)
        again = self.run_script("due")
        self.assertEqual((again.returncode, again.stdout), (0, ""))
        self.assertFalse((self.out / "bridge-release" / "source").exists())

    def test_a_merged_version_without_a_tag_is_packed_put_in_the_feed_and_released(self):
        head = self.commit("0.1.0-preview.8", "fix: a row keeps its widths (0.1.0-preview.8)", code="class View {}\n")
        due = self.run_script("due")
        self.assertEqual(due.stdout.strip(), f"0.1.0-preview.8 {head[:7]}")
        self.assertFalse(self.package("0.1.0-preview.8").exists())

        released = self.run_script("release")
        self.assertEqual(released.returncode, 0, released.stderr)
        self.assertTrue(released.stdout.startswith(f"BRIDGE: packed 0.1.0-preview.8 from the bridge's main ({head[:7]})"))
        self.assertTrue(self.package("0.1.0-preview.8").read_text(encoding="utf-8").startswith("package 0.1.0-preview.8 "))
        # While it built, the status had it as packing.
        self.assertTrue((self.stub / "note-during-the-build").read_text(encoding="utf-8").startswith("packing\nPacking version 0.1.0-preview.8"))
        self.assertEqual(self.git("rev-parse", "v0.1.0-preview.8"), head)
        # Tested and packed from main's commit, in a copy with nothing uncommitted.
        self.assertEqual(self.log("builder.log"), [f"test {head} 0", f"package {head} 0"])
        create = self.releases()
        self.assertEqual(len(create), 1)
        self.assertIn(f"release create v0.1.0-preview.8 {self.package('0.1.0-preview.8')} --repo someone/bridge --target {head}", create[0])
        # The notes are several lines of that call; the commits since the last tag are in them.
        whole = "\n".join(self.log("gh.log"))
        self.assertIn("- fix: a row keeps its widths (0.1.0-preview.8)", whole)
        self.assertNotIn("the first (0.1.0-preview.7)", whole)
        self.assertTrue(whole.rstrip().endswith("--prerelease"), whole[-200:])
        self.assertEqual(self.read("bridge-release-seen").split()[::3], [head, "released"])
        note = self.read("bridge-release-note")
        self.assertTrue(note.startswith("released\n"), note)
        self.assertIn("put in your feed and tagged v0.1.0-preview.8", note)
        # Both counts the test step said are in the status, the release's notes and the record.
        counts = "Managed tests: 81/81 passed, Swift tests: 91/91 passed"
        self.assertIn(f"after the bridge's own tests passed ({counts}): https://", note)
        self.assertIn(f"after the bridge's own tests passed ({counts}). Download into", whole)
        self.assertEqual(self.read("bridge-release-fed").splitlines()[1], counts)
        # The copy it was built in is gone, and a second look does nothing.
        self.assertFalse((self.out / "bridge-release" / "build").exists())
        self.assertEqual(self.run_script("due").stdout, "")
        self.assertEqual(self.run_script("release").stdout, "")
        self.assertEqual(len(self.releases()), 1)

    def test_main_that_anyone_can_update_is_not_packed(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "open-main").touch()
        released = self.run_script("release")
        self.assertIn("was not packed: no ruleset restricts updates of the bridge's main", released.stdout)
        self.assertEqual(self.log("builder.log"), [])
        self.assertFalse(self.package("0.1.0-preview.8").exists())
        self.assertEqual(self.read("bridge-release-seen").split()[3], "failed")

    def test_main_that_someone_besides_the_admins_may_bypass_is_not_packed(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "app-may-bypass").touch()
        released = self.run_script("release")
        self.assertIn("no ruleset restricts updates of the bridge's main to the repository's admins", released.stdout)
        self.assertEqual(self.log("builder.log"), [])
        self.assertEqual(self.releases(), [])

    def test_a_branch_whose_name_ends_like_main_is_not_taken_for_main(self):
        head = self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        self.git("switch", "-q", "-c", "decoy/refs/heads/main")
        self.commit("0.1.0-preview.9", "feat: not merged (0.1.0-preview.9)")
        self.git("switch", "-q", "main")
        self.assertEqual(self.run_script("due").stdout.strip(), f"0.1.0-preview.8 {head[:7]}")

    def test_a_tag_on_main_without_a_package_in_the_feed_is_said(self):
        self.package("0.1.0-preview.7").unlink()
        due = self.run_script("due")
        self.assertEqual(due.stdout, "")
        self.assertIn("has its tag, but your feed", self.read("bridge-release-note"))
        self.assertEqual(self.log("builder.log"), [])

    def test_a_tag_of_the_version_made_elsewhere_during_the_build_gets_no_release(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "tag-during-the-build").write_text(self.first, encoding="utf-8")
        released = self.run_script("release")
        self.assertIn(f"a tag v0.1.0-preview.8 has appeared on someone/bridge at {self.first[:7]}", released.stdout)
        self.assertEqual(self.releases(), [])
        self.assertEqual(self.read("bridge-release-seen").split()[3], "failed")

    def test_a_release_whose_tag_is_not_at_mains_commit_is_not_called_released(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "tag-made-first").write_text(self.first, encoding="utf-8")
        released = self.run_script("release")
        self.assertIn(f"but its tag is at {self.first}, not at main's", released.stdout)
        self.assertEqual(self.read("bridge-release-seen").split()[3], "failed")

    def test_rules_that_cannot_be_read_are_not_taken_for_closed(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "unreadable-rules").touch()
        released = self.run_script("release")
        self.assertIn("the rules of the bridge's main could not be read", released.stdout)
        self.assertEqual(self.log("builder.log"), [])

    def test_failing_tests_stop_the_release_before_the_feed(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "fail-test").touch()
        released = self.run_script("release")
        self.assertIn("the bridge's tests did not pass (the bridge's tests did not all pass). It is tried again", released.stdout)
        self.assertIn("try 1 of 3", released.stdout)
        self.assertFalse(self.package("0.1.0-preview.8").exists())
        self.assertEqual(self.releases(), [])
        self.assertTrue(self.read("bridge-release-note").startswith("failed\nVersion 0.1.0-preview.8 on the bridge's main"))
        # The copy it failed in is kept to look at.
        self.assertTrue((self.out / "bridge-release" / "build").exists())

    def test_a_failure_is_tried_again_after_the_wait_and_three_times_in_all(self):
        head = self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "fail-package").touch()
        self.assertIn("try 1 of 3", self.run_script("release").stdout)
        # Within the hour nothing is due.
        self.assertEqual(self.run_script("due").stdout, "")
        self.assertEqual(self.run_script("release").stdout, "")
        self.assertIn("try 2 of 3", self.run_script("release", FEEDBACK_BRIDGE_RETRY_AFTER="0").stdout)
        third = self.run_script("release", FEEDBACK_BRIDGE_RETRY_AFTER="0").stdout
        self.assertIn("try 3 of 3: it is not tried again until main changes", third)
        self.assertEqual(self.run_script("due", FEEDBACK_BRIDGE_RETRY_AFTER="0").stdout, "")
        self.assertEqual(self.read("bridge-release-seen").split()[::3], [head, "failed"])
        # Main changing starts it over, and so does removing the record.
        (self.out / "state" / "bridge-release-seen").unlink()
        self.assertEqual(self.run_script("due").stdout.strip(), f"0.1.0-preview.8 {head[:7]}")

    def test_a_failure_that_mended_is_released_on_the_next_try(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "fail-release").touch()
        first = self.run_script("release").stdout
        self.assertIn("the package is in your feed, but the release v0.1.0-preview.8 could not be made", first)
        self.assertTrue(self.package("0.1.0-preview.8").exists())
        kept = self.package("0.1.0-preview.8").read_text(encoding="utf-8")
        (self.stub / "fail-release").unlink()
        second = self.run_script("release", FEEDBACK_BRIDGE_RETRY_AFTER="0").stdout
        self.assertTrue(second.startswith("BRIDGE: packed 0.1.0-preview.8"), second)
        # The package the first try put in the feed is the one released: built once, never replaced.
        self.assertEqual(self.built(), ["test", "package"])
        self.assertEqual(self.package("0.1.0-preview.8").read_text(encoding="utf-8"), kept)
        self.assertIn("(Managed tests: 81/81 passed, Swift tests: 91/91 passed)", self.read("bridge-release-note"))

    def test_another_build_of_the_version_in_the_feed_is_never_replaced(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        self.package("0.1.0-preview.8").write_text("the Captain's own build", encoding="utf-8")
        released = self.run_script("release")
        self.assertIn("your feed already holds a build of 0.1.0-preview.8 that this check did not put there", released.stdout)
        self.assertEqual(self.package("0.1.0-preview.8").read_text(encoding="utf-8"), "the Captain's own build")
        self.assertEqual(self.releases(), [])
        # Known before anything is built.
        self.assertEqual(self.built(), [])

    def test_another_build_in_the_package_folder_is_named(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        kept = self.packages / "microsoft.swiftui" / "0.1.0-preview.8"
        kept.mkdir(parents=True)
        (kept / "microsoft.swiftui.0.1.0-preview.8.nupkg").write_text("an older build", encoding="utf-8")
        released = self.run_script("release")
        self.assertIn(f"Your package folder holds another build of 0.1.0-preview.8 ({kept})", released.stdout)
        self.assertEqual((kept / "microsoft.swiftui.0.1.0-preview.8.nupkg").read_text(encoding="utf-8"), "an older build")
        self.assertEqual(self.read("bridge-release-seen").split()[3], "released")

    def test_code_changed_under_a_version_that_has_its_tag_is_said_and_not_packed(self):
        self.commit("0.1.0-preview.7", "fix: forgot the version", code="class View { int changed; }\n")
        due = self.run_script("due")
        self.assertEqual(due.stdout, "")
        self.assertIn("has changed the bridge's code since v0.1.0-preview.7 without a new version", self.read("bridge-release-note"))

    def test_a_tag_that_is_not_on_main_is_said_and_not_packed(self):
        self.git("switch", "-q", "-c", "side")
        self.commit("0.1.0-preview.8", "feat: on a branch (0.1.0-preview.8)")
        self.git("tag", "v0.1.0-preview.8")
        self.git("switch", "-q", "main")
        self.commit("0.1.0-preview.8", "feat: the same number on main (0.1.0-preview.8)")
        due = self.run_script("due")
        self.assertEqual(due.stdout, "")
        self.assertIn("the tag v0.1.0-preview.8 is at a commit that is not on main", self.read("bridge-release-note"))

    def test_a_bridge_that_cannot_be_read_is_an_error_and_decides_nothing(self):
        due = self.run_script("due", FEEDBACK_BRIDGE_URL=str(self.dir / "nowhere"))
        self.assertEqual((due.returncode, due.stdout), (2, ""))
        self.assertIn("could not be read", due.stderr)
        self.assertEqual(self.read("bridge-release-seen"), "")

    def test_a_second_release_does_not_run_beside_the_first(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.out / "bridge-release" / "lock").mkdir(parents=True)
        held = self.run_script("release")
        self.assertEqual((held.returncode, held.stdout), (2, ""))
        self.assertTrue((self.out / "bridge-release" / "lock").exists())
        # A lock older than any release is a release that died.
        old = time.time() - 2 * 3600
        os.utime(self.out / "bridge-release" / "lock", (old, old))
        self.assertTrue(self.run_script("release").stdout.startswith("BRIDGE: packed"))
        self.assertFalse((self.out / "bridge-release" / "lock").exists())

    def test_the_lock_of_a_release_that_is_no_longer_running_is_taken_over(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        gone = subprocess.Popen(["/usr/bin/true"])
        gone.wait()
        (self.out / "bridge-release" / "lock").mkdir(parents=True)
        (self.out / "bridge-release" / "lock" / "pid").write_text(f"{gone.pid}\n", encoding="utf-8")
        self.assertTrue(self.run_script("release").stdout.startswith("BRIDGE: packed"))
        self.assertFalse((self.out / "bridge-release" / "lock").exists())

    def test_its_own_unreleased_package_gives_way_when_main_moves_under_the_same_version(self):
        self.commit("0.1.0-preview.8", "fix: something (0.1.0-preview.8)")
        (self.stub / "fail-release").touch()
        self.run_script("release")
        first = self.package("0.1.0-preview.8").read_text(encoding="utf-8")
        (self.stub / "fail-release").unlink()
        head = self.commit("0.1.0-preview.8", "docs: a word more, same version")
        released = self.run_script("release")
        self.assertTrue(released.stdout.startswith(f"BRIDGE: packed 0.1.0-preview.8 from the bridge's main ({head[:7]})"), released.stdout)
        self.assertNotEqual(self.package("0.1.0-preview.8").read_text(encoding="utf-8"), first)
        self.assertEqual(self.built(), ["test", "package", "test", "package"])
        self.assertEqual(self.git("rev-parse", "v0.1.0-preview.8"), head)

    @unittest.skipUnless(shutil.which("jq"), "needs jq to run the script's own filter")
    def test_only_a_ruleset_that_names_admins_alone_counts_as_closed(self):
        found = re.search(r"repos/\$BRIDGE_REPO/rulesets/\$id\" --jq \\\n\s+'([^']+)'", SCRIPT.read_text(encoding="utf-8"))
        self.assertIsNotNone(found)

        def others(ruleset):
            done = subprocess.run(["jq", "-r", found.group(1)], input=json.dumps(ruleset), capture_output=True, text=True, check=True)
            return done.stdout.strip()

        admin = {"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always"}
        self.assertEqual(others({"bypass_actors": [admin]}), "0")
        self.assertEqual(others({"bypass_actors": []}), "0")
        self.assertEqual(others({"bypass_actors": [admin, {"actor_id": 5826863, "actor_type": "Integration"}]}), "1")
        self.assertEqual(others({"bypass_actors": [{"actor_id": 2, "actor_type": "RepositoryRole"}]}), "1")
        # A list GitHub left out is not an empty one.
        self.assertEqual(others({"id": 7}), "unknown")
        self.assertEqual(others({"bypass_actors": None}), "unknown")


class RunnerStepTests(unittest.TestCase):
    """bridge_release in scripts/admin-feedback-check.sh, taken out of the script and run with a
    stand-in for the release script: what a pass does with what that script says."""

    CHECK = SCRIPT.parent / "admin-feedback-check.sh"

    def run_step(self, release_script):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        root = Path(folder.name).resolve()
        (root / "scripts").mkdir()
        (root / "out" / "state").mkdir(parents=True)
        (root / "scripts" / "admin-feedback-bridge-release.sh").write_text(release_script, encoding="utf-8")
        text = self.CHECK.read_text(encoding="utf-8")
        start = text.index("BRIDGE_RELEASED=no\nbridge_release() {")
        step = text[start:text.index("\n}\n", start) + 3]
        harness = f"""set -uo pipefail
ROOT={root}; OUT={root}/out; RUN={root}/out/run.md; NOTIFY=no; : > "$RUN"
status() {{ echo status >> "$OUT/calls"; }}
keep_status() {{ echo keep_status >> "$OUT/calls"; }}
{step}
bridge_release
echo "released=$BRIDGE_RELEASED notify=$NOTIFY"
"""
        done = subprocess.run(["/bin/bash", "-c", harness], capture_output=True, text=True, timeout=30)
        out = root / "out"
        calls = (out / "calls").read_text(encoding="utf-8").split() if (out / "calls").exists() else []
        err = (out / "state" / "bridge-release.err")
        return done.stdout.strip(), (out / "run.md").read_text(encoding="utf-8"), calls, err.read_text(encoding="utf-8") if err.exists() else None

    def test_nothing_due_costs_the_pass_nothing(self):
        said, summary, calls, err = self.run_step('[ "$1" = due ] || echo "BRIDGE: must not run"\n')
        self.assertEqual((said, summary, calls, err), ("released=no notify=no", "", [], None))

    def test_a_release_goes_into_the_summary_and_the_status_is_kept_while_it_runs(self):
        said, summary, calls, err = self.run_step(
            'if [ "$1" = due ]; then echo "0.1.0-preview.8 abc1234"; else echo "BRIDGE: packed 0.1.0-preview.8"; fi\n')
        self.assertEqual(said, "released=yes notify=yes")
        self.assertEqual(summary, "BRIDGE: packed 0.1.0-preview.8\n\n")
        self.assertEqual(calls, ["keep_status"])
        self.assertIsNone(err)

    def test_a_bridge_that_could_not_be_looked_at_leaves_why_and_does_not_fail_the_pass(self):
        said, summary, calls, err = self.run_step('echo "the bridge could not be read." >&2; exit 2\n')
        self.assertEqual((said, summary, calls), ("released=no notify=no", "", []))
        self.assertEqual(err, "the bridge could not be read.\n")


if __name__ == "__main__":
    unittest.main()
