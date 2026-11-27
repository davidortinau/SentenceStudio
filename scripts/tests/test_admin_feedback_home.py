#!/usr/bin/env python3
"""A clean start for every fix session: scripts/session-clean.sh run on a stand-in for the
session user's home, and the steps of scripts/admin-feedback-check.sh that use it, that keep
the repository's mirror, and that give each session a clone of its own.

Nothing here runs as another user or touches a real home, and the temporary folder the script
is told to empty is always a stand-in made by the test.

Run: python3 -I scripts/tests/test_admin_feedback_home.py
"""

import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

# The script is for the Mac the check runs on, and its tests run what that Mac has (its tar, its
# rsync, its bash): elsewhere they are skipped, and the steps that only need git still run.
ON_A_MAC = unittest.skipUnless(sys.platform == "darwin", "scripts/session-clean.sh runs on macOS only")
SCRIPTS = Path(__file__).resolve().parents[1]
CLEAN = SCRIPTS / "session-clean.sh"
CHECK = SCRIPTS / "admin-feedback-check.sh"
STAMP = "20261127T000000Z"


def write(path, text="x\n", mode=None):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    if mode is not None:
        path.chmod(mode)
    return path


def tree(folder):
    """Every file and link under a folder, as paths from it."""
    return sorted(str(path.relative_to(folder)) for path in folder.rglob("*") if path.is_file() or path.is_symlink())


class HomeTestCase(unittest.TestCase):
    def setUp(self):
        holder = tempfile.TemporaryDirectory()
        self.addCleanup(self.unlock_and_remove, holder)
        self.root = Path(holder.name).resolve()
        self.home = self.root / "home"
        self.image = self.root / "image"
        # Never the real one: the script empties the folder it is given.
        self.tmp = self.root / "session-tmp"
        self.tmp.mkdir()
        write(self.tmp / ".sstudio-session-tmp", "a stand-in for a session user's temporary folder\n")
        self.a_home()

    def unlock_and_remove(self, holder):
        subprocess.run(["chmod", "-R", "u+rwX", str(self.root)], capture_output=True)
        holder.cleanup()

    def a_home(self):
        """A home as a session should find it: tools, caches, what macOS keeps, the token."""
        home = self.home
        write(home / ".claude-token", "token\n", 0o600)
        write(home / ".dotnet/tools/aspire", "the aspire tool\n", 0o755)
        (home / ".dotnet/tools/link").symlink_to(".store/aspire")
        write(home / ".nuget/NuGet/NuGet.Config", "<configuration />\n", 0o600)
        write(home / ".nuget/packages/some.package/1.0.0/lib.dll", "a package\n")
        write(home / ".local/share/NuGet/http-cache/entry", "cached\n")
        write(home / ".local/state/gh/state.yml", "gh\n")
        write(home / ".colima/default/colima.yaml", "cpu: 2\n")
        write(home / ".colima/_lima/colima/disk", "the machine's disk\n")
        write(home / ".config/NuGet/NuGet.Config", "<configuration />\n")
        write(home / ".npmrc", "registry=https://example.invalid/\n")
        write(home / "Library/Keychains/login.keychain-db", "keychain\n")
        write(home / "Library/Developer/CoreSimulator/Devices/device", "simulator\n")
        write(home / "sstudio/fix/issue-12-activity.md", "what the session did\n")
        # What the runner writes before it has the home cleaned: it is a session user's.
        write(home / "sstudio/.session-home", "")
        (home / "Documents").mkdir()

    def run_clean(self, *args, home=None):
        return subprocess.run(["/bin/bash", "-s", *args], input=CLEAN.read_text(encoding="utf-8"), capture_output=True,
                              text=True, timeout=120, env={"HOME": str(home or self.home), "PATH": "/usr/bin:/bin"})

    def take_image(self):
        self.image.mkdir()
        taken = subprocess.run(["/bin/bash", "-s", "image"], input=CLEAN.read_bytes(), capture_output=True, timeout=120,
                               env={"HOME": str(self.home), "PATH": "/usr/bin:/bin"})
        self.assertEqual(taken.returncode, 0, taken.stderr)
        unpacked = subprocess.run(["/usr/bin/tar", "-xf", "-", "-C", str(self.image)], input=taken.stdout, capture_output=True)
        self.assertEqual(unpacked.returncode, 0, unpacked.stderr)
        # What the runner writes when the image is complete.
        names = sorted(path.name for path in self.image.iterdir())
        write(self.image / ".sstudio-home-image", "".join(f"{name}\n" for name in names))
        return names

    def clean(self, *more, stamp=STAMP, tmp=None):
        return self.run_clean("clean", stamp, str(self.image), str(tmp or self.tmp), *more)

    def cleaned(self, *more, **how):
        done = self.clean(*more, **how)
        self.assertEqual(done.returncode, 0, done.stderr)
        return done.stdout.splitlines()

    def aside(self, stamp=STAMP):
        return self.home / "sstudio/set-aside" / stamp


@ON_A_MAC
class ImageTests(HomeTestCase):
    def test_the_image_holds_the_tool_folders_and_nothing_kept_in_place(self):
        names = self.take_image()

        self.assertEqual(names, [".colima", ".config", ".dotnet", ".local", ".npmrc", ".nuget"])
        self.assertEqual(tree(self.image), [
            ".colima/default/colima.yaml", ".config/NuGet/NuGet.Config", ".dotnet/tools/aspire", ".dotnet/tools/link",
            ".local/state/gh/state.yml", ".npmrc", ".nuget/NuGet/NuGet.Config", ".sstudio-home-image"])
        self.assertTrue((self.image / ".dotnet/tools/link").is_symlink())
        # The folders that hold a large cache are there, so the cache has somewhere to stay.
        self.assertTrue((self.image / ".local/share").is_dir())

    def test_what_a_session_or_a_shell_would_obey_is_never_taken_into_an_image(self):
        home = self.home
        for name in (".claude/projects/x/memory/note.md", ".claude.json", ".claude.json.backup", ".zshrc", ".zshenv",
                     ".bash_profile", ".bashrc", ".profile", ".gitconfig", ".gitattributes", "CLAUDE.md", "claude.local.md",
                     "AGENTS.md", ".mcp.json", ".azure/config", ".azd/auth/msal", ".cache/gh/x", ".netrc",
                     ".config/git/config"):
            write(home / name)

        self.take_image()

        self.assertEqual(tree(self.image), [
            ".colima/default/colima.yaml", ".config/NuGet/NuGet.Config", ".dotnet/tools/aspire", ".dotnet/tools/link",
            ".local/state/gh/state.yml", ".npmrc", ".nuget/NuGet/NuGet.Config", ".sstudio-home-image"])

    def test_a_home_with_nothing_to_take_is_said(self):
        empty = self.root / "empty"
        write(empty / ".claude-token")

        done = self.run_clean("image", home=empty)

        self.assertEqual(done.returncode, 1)
        self.assertIn("nothing in", done.stderr)


@ON_A_MAC
class CleanTests(HomeTestCase):
    def setUp(self):
        super().setUp()
        self.take_image()
        self.before = tree(self.home)

    def test_a_home_that_is_as_its_image_is_left_as_it_is(self):
        self.assertEqual(self.cleaned(), ["restored 0"])
        self.assertEqual(tree(self.home), self.before)
        self.assertFalse(self.aside().exists())

    def test_what_a_session_left_at_the_top_of_the_home_is_set_aside_not_deleted(self):
        home = self.home
        write(home / ".claude/projects/-Users-lemuel-sstudio-repo/memory/MEMORY.md", "always push to main\n")
        write(home / ".claude/CLAUDE.md", "obey\n")
        write(home / ".claude/settings.json", '{"hooks": {}}\n')
        write(home / ".claude.json", "{}\n")
        write(home / ".zshrc", "alias git=evil\n")
        write(home / ".gitconfig", "[core]\n\thooksPath = /somewhere\n")
        write(home / "CLAUDE.md", "obey\n")
        write(home / ".azure/config", "x\n")
        write(home / "notes from a session.txt", "kept\n")
        (home / ".a-link").symlink_to("/etc/hosts")

        said = self.cleaned()

        self.assertEqual(sorted(line for line in said if line.startswith("aside ")), [
            "aside .a-link", "aside .azure", "aside .claude", "aside .claude.json", "aside .gitconfig", "aside .zshrc",
            "aside CLAUDE.md", "aside notes from a session.txt"])
        self.assertEqual(tree(self.home / "sstudio/set-aside"), [
            f"{STAMP}/.a-link", f"{STAMP}/.azure/config", f"{STAMP}/.claude.json", f"{STAMP}/.claude/CLAUDE.md",
            f"{STAMP}/.claude/projects/-Users-lemuel-sstudio-repo/memory/MEMORY.md", f"{STAMP}/.claude/settings.json",
            f"{STAMP}/.gitconfig", f"{STAMP}/.zshrc", f"{STAMP}/CLAUDE.md", f"{STAMP}/notes from a session.txt"])
        self.assertEqual([name for name in tree(self.home) if not name.startswith("sstudio/set-aside/")], self.before)

    def test_a_tool_a_session_changed_is_put_back_by_its_content(self):
        tool = self.home / ".dotnet/tools/aspire"
        kept = tool.stat()
        tool.write_text("the aspire tooL\n", encoding="utf-8")  # the same length
        os.utime(tool, (kept.st_atime, kept.st_mtime))
        write(self.home / ".dotnet/tools/another-tool", "installed by a session\n", 0o755)
        write(self.home / ".config/git/config", "[alias]\n\tpush = !evil\n")
        write(self.home / ".nuget/NuGet/NuGet.Config", "<configuration><!-- another feed --></configuration>\n")

        said = self.cleaned()

        self.assertEqual(tool.read_text(encoding="utf-8"), "the aspire tool\n")
        self.assertTrue(os.access(tool, os.X_OK))
        self.assertEqual((self.home / ".nuget/NuGet/NuGet.Config").read_text(encoding="utf-8"), "<configuration />\n")
        self.assertEqual(oct((self.home / ".nuget/NuGet/NuGet.Config").stat().st_mode & 0o777), "0o600")
        self.assertEqual(tree(self.home), self.before)
        changed = sorted(line for line in said if line.startswith("changed ") and not line.endswith("/"))
        self.assertEqual(changed, ["changed .config/git/config", "changed .dotnet/tools/another-tool",
                                   "changed .dotnet/tools/aspire", "changed .nuget/NuGet/NuGet.Config"])
        self.assertEqual(said[-1][:9], "restored ")
        self.assertGreaterEqual(int(said[-1].split()[1]), 4)

    def test_a_file_whose_permissions_changed_is_put_back_too(self):
        tool, config = self.home / ".dotnet/tools/aspire", self.home / ".nuget/NuGet/NuGet.Config"
        tool.chmod(0o644)
        config.chmod(0o666)

        said = self.cleaned()

        self.assertEqual((oct(tool.stat().st_mode & 0o777), oct(config.stat().st_mode & 0o777)), ("0o755", "0o600"))
        self.assertEqual(said[-1], "restored 2")

    def test_a_file_a_tool_rewrote_as_it_was_is_not_counted_as_a_change(self):
        (self.home / ".npmrc").write_text((self.home / ".npmrc").read_text(encoding="utf-8"), encoding="utf-8")
        os.utime(self.home / ".npmrc", (time.time() - 3600,) * 2)

        self.assertEqual(self.cleaned(), ["restored 0"])

    def test_the_caches_and_what_macos_keeps_are_not_looked_into(self):
        for name in (".nuget/packages/some.package/1.0.0/lib.dll", ".local/share/NuGet/http-cache/entry",
                     ".colima/_lima/colima/disk", "Library/Keychains/login.keychain-db", ".claude-token"):
            (self.home / name).write_text("changed since the image\n", encoding="utf-8")
        write(self.home / ".nuget/packages/new.package/2.0.0/lib.dll")
        write(self.home / "Library/Preferences/com.example.plist")
        write(self.home / "Documents/a file")

        self.assertEqual(self.cleaned(), ["restored 0"])

        self.assertEqual((self.home / ".nuget/packages/some.package/1.0.0/lib.dll").read_text(encoding="utf-8"),
                         "changed since the image\n")
        self.assertTrue((self.home / ".nuget/packages/new.package/2.0.0/lib.dll").exists())
        self.assertTrue((self.home / "Library/Preferences/com.example.plist").exists())
        self.assertTrue((self.home / "Documents/a file").exists())

    def test_something_its_owner_locked_is_still_set_aside(self):
        locked = self.home / ".locked"
        write(locked / "inside/file")
        (locked / "inside").chmod(0o000)
        locked.chmod(0o000)

        said = self.cleaned()

        self.assertIn("aside .locked", said)
        self.assertFalse(locked.exists())
        self.assertTrue((self.aside() / ".locked").is_dir())

    def test_nothing_claude_code_would_read_as_instructions_stays_where_a_session_loads_from(self):
        sstudio = self.home / "sstudio"
        write(sstudio / "CLAUDE.md")
        write(sstudio / ".claude/skills/x/SKILL.md")
        write(sstudio / "trees/agents.md")
        write(sstudio / "trees/feedback-issue-12/AGENTS.md", "the repository's own, in a worktree\n")
        write(sstudio / "clones/.mcp.json")
        write(sstudio / "fix/CLAUDE.local.md")
        write(sstudio / "fix/.claude/settings.json")
        write(sstudio / "fix/notes.md", "not one of the check's files\n")
        write(sstudio / "fix/issue-12-proof.json", "{}\n")
        write(sstudio / "docker.log")

        said = self.cleaned()

        self.assertEqual(sorted(line for line in said if line.startswith("aside ")), [
            "aside sstudio-clones-.mcp.json", "aside sstudio-fix-.claude", "aside sstudio-fix-CLAUDE.local.md",
            "aside sstudio-fix-notes.md", "aside sstudio-sstudio-.claude", "aside sstudio-sstudio-CLAUDE.md",
            "aside sstudio-trees-agents.md"])
        self.assertEqual([name for name in tree(sstudio) if not name.startswith("set-aside/")], [
            ".session-home", "docker.log", "fix/issue-12-activity.md", "fix/issue-12-proof.json",
            "trees/feedback-issue-12/AGENTS.md"])

    def test_the_temporary_folder_it_is_given_is_emptied(self):
        write(self.tmp / "left/by/a/session")
        write(self.tmp / ".hidden")

        self.cleaned()

        # All but the mark that makes it a stand-in.
        self.assertEqual([path.name for path in self.tmp.iterdir()], [".sstudio-session-tmp"])

    def test_no_temporary_folder_is_ever_worked_out_or_taken_on_trust(self):
        # On 2026-11-27 this script asked the system for the temporary folder and, run by a
        # test as the Captain, emptied his.
        text = CLEAN.read_text(encoding="utf-8")
        code = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("#"))
        for word in ("getconf", "DARWIN_USER_TEMP_DIR", "TMPDIR", "mktemp"):
            self.assertNotIn(word, code)
        write(self.tmp / "must-stay")
        inside = self.home / "sstudio/tmp"
        write(inside / ".sstudio-session-tmp")
        unmarked = self.root / "unmarked"
        write(unmarked / "must-stay")
        for folder, why in (("", "was not named"), (str(self.root), "holds the home"), (str(inside), "is inside the home"),
                            (str(self.home), "holds the home"), ("relative/folder", "is not a full path"),
                            (str(self.root / "missing"), "is not a folder of this user's own"),
                            (str(unmarked), "neither a per-user temporary folder nor marked as a stand-in"),
                            ("/tmp", "is not"), ("/", "is not"), ("/var/folders", "is not")):
            done = self.run_clean("clean", STAMP, str(self.image), folder)
            self.assertEqual(done.returncode, 1, folder)
            self.assertIn(why, done.stderr, folder)
            self.assertEqual(done.stdout, "", folder)
        self.assertTrue((self.tmp / "must-stay").exists())
        self.assertTrue((unmarked / "must-stay").exists())
        self.assertEqual(tree(self.home), self.before + ["sstudio/tmp/.sstudio-session-tmp"])

    def test_a_real_temporary_folder_is_only_emptied_for_the_real_home_of_whoever_runs_it(self):
        # Read, not run: no test hands this script a real temporary folder, whatever it is
        # expected to do with it. A folder that is not a marked stand-in has to be a per-user
        # temporary folder, and HOME has to be the home the system has on record for the user.
        text = CLEAN.read_text(encoding="utf-8")
        rule = text[text.index('if [ -f "$tmp/.sstudio-session-tmp" ]'):text.index("# 1. The top level of the home")]
        self.assertIn("/var/folders/*/*/T|/private/var/folders/*/*/T) ;;", rule)
        self.assertIn('*) fail "the temporary folder $tmp is neither a per-user temporary folder nor marked as a stand-in', rule)
        self.assertIn('[ "$home" = "$own_home" ] || fail "the temporary folder $tmp is a real one and $HOME is not this user\'s real home', rule)
        self.assertNotIn("DARWIN_USER_TEMP_DIR", Path(__file__).read_text(encoding="utf-8").replace('"DARWIN_USER_TEMP_DIR"', ""))

    def test_a_home_that_is_not_marked_as_a_session_users_is_refused_before_anything_moves(self):
        (self.home / "sstudio/.session-home").unlink()
        write(self.home / ".zshrc")
        write(self.tmp / "must-stay")

        done = self.clean()

        self.assertEqual(done.returncode, 1)
        self.assertIn("is not marked as a session user's home", done.stderr)
        self.assertEqual(done.stdout, "")
        self.assertTrue((self.home / ".zshrc").exists())
        self.assertTrue((self.tmp / "must-stay").exists())

    def test_the_real_home_of_whoever_runs_it_is_refused_when_that_is_an_administrator(self):
        # Read, not run: running it would mean pointing the script at a real home.
        text = CLEAN.read_text(encoding="utf-8")
        guard = text.index('if [ "$home" = "$own_home" ]; then')
        self.assertIn("getpwuid($<)", text[:guard])
        self.assertIn('*" admin "*', text[guard:guard + 400])
        self.assertIn("nothing was moved", text[guard:guard + 400])
        # Before the first thing that moves or deletes.
        for later in ("set_aside() {", "rsync $options", 'find "$tmp" -mindepth 1'):
            self.assertLess(guard, text.index(later), later)

    def test_a_dash_leaves_the_temporary_folder_out(self):
        write(self.tmp / "must-stay")

        done = self.run_clean("clean", STAMP, str(self.image), "-")

        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertTrue((self.tmp / "must-stay").exists())

    def test_a_dry_run_says_the_same_and_changes_nothing(self):
        write(self.home / ".zshrc")
        write(self.home / ".claude/settings.json")
        (self.home / ".dotnet/tools/aspire").write_text("changed\n", encoding="utf-8")
        write(self.home / "sstudio/fix/notes.md")
        write(self.tmp / "must-stay")
        before = tree(self.home)

        dry = self.cleaned("--dry-run")

        self.assertEqual(tree(self.home), before)
        self.assertTrue((self.tmp / "must-stay").exists())
        self.assertEqual((self.home / ".dotnet/tools/aspire").read_text(encoding="utf-8"), "changed\n")
        self.assertEqual(dry, self.cleaned())
        self.assertEqual(dry, ["aside .claude", "aside .zshrc", "changed .dotnet/tools/aspire", "restored 1",
                               "aside sstudio-fix-notes.md"])

    def test_an_image_that_is_not_complete_moves_nothing(self):
        write(self.home / ".zshrc")
        for spoil, why in ((lambda: (self.image / ".sstudio-home-image").unlink(), "is not complete"),
                           (lambda: write(self.image / ".sstudio-home-image", ".dotnet\n.nuget\n.missing\n"), "lacks .missing"),
                           (lambda: write(self.image / ".sstudio-home-image", ".dotnet\nLibrary\n") and
                            (self.image / "Library").mkdir(), "which is kept in place")):
            spoil()
            done = self.clean()
            self.assertEqual(done.returncode, 1)
            self.assertIn(why, done.stderr)
            self.assertEqual(done.stdout, "")
            self.assertTrue((self.home / ".zshrc").exists())
            self.assertTrue((self.home / ".dotnet/tools/aspire").exists())

    def test_a_token_that_is_not_a_plain_file_stops_it(self):
        token = self.home / ".claude-token"
        token.unlink()
        token.symlink_to(self.root / "elsewhere")
        write(self.root / "elsewhere", "another token\n")

        done = self.clean()

        self.assertEqual(done.returncode, 1)
        self.assertIn(".claude-token is not a plain file", done.stderr)

    def test_set_aside_folders_are_kept_two_weeks(self):
        old, recent = self.aside("20261101T000000Z"), self.aside("20261120T000000Z")
        write(old / ".zshrc")
        write(recent / ".zshrc")
        day = 24 * 3600
        os.utime(old, (time.time() - 15 * day,) * 2)
        os.utime(recent, (time.time() - 13 * day,) * 2)

        self.cleaned()

        self.assertFalse(old.exists())
        self.assertTrue((recent / ".zshrc").exists())

    def test_a_second_thing_of_the_same_name_does_not_replace_the_first(self):
        write(self.home / ".zshrc", "first\n")
        self.cleaned()
        write(self.home / ".zshrc", "second\n")
        self.cleaned()

        self.assertEqual((self.aside() / ".zshrc").read_text(encoding="utf-8"), "first\n")
        self.assertEqual((self.aside() / ".zshrc.2").read_text(encoding="utf-8"), "second\n")

    def test_a_stamp_that_is_not_plain_is_refused(self):
        for stamp in ("", "../../elsewhere", "a b"):
            done = self.clean(stamp=stamp)
            self.assertEqual(done.returncode, 1, stamp)


class RunnerTestCase(HomeTestCase):
    """Steps of the check's own script, with the session's user played by this one."""

    def setUp(self):
        super().setUp()
        self.text = CHECK.read_text(encoding="utf-8")
        self.out = self.root / "out"
        (self.out / "state").mkdir(parents=True)

    def step(self, starts_with):
        start = self.text.index(starts_with)
        return self.text[start:self.text.index("\n}\n", start) + 3]

    def stage(self, name):
        start = self.text.index(f"\n{name}() {{\n")
        return self.text[start:self.text.index("\n}\n", start)]

    def bash(self, script):
        harness = f"""set -uo pipefail
export PATH=/usr/bin:/bin
ROOT={SCRIPTS.parent}; OUT={self.out}; STAMP={STAMP}; SESSION_USER=lemuel; SESSION_HOME={self.home}
SESSION_TMP={self.tmp}; SESSION_ERROR=; RUN={self.out}/run.md; INFLIGHT={self.out}/state/session-in-flight
REPO=someone/app; SHARED={self.root}/shared; CLONES={self.home}/sstudio/clones; CLONE=$CLONES/none
WORKTREES={self.home}/sstudio/trees
as_session() {{ HOME="$SESSION_HOME" "$@"; }}
there() {{ test -e "$1"; }}
{self.step("run_here() {")}
{self.step("sgit() {")}
{script}
"""
        return subprocess.run(["/bin/bash", "-c", harness], capture_output=True, text=True, timeout=120)


@ON_A_MAC
class CleanStartTests(RunnerTestCase):
    def start(self, more=""):
        script = f"""{self.step('HOME_IMAGE="$OUT/home-image"')}
{more}
clean_start; echo "code=$?"; echo "error=$SESSION_ERROR"; echo "note=$CLEAN_NOTE"
"""
        done = self.bash(script)
        self.assertEqual(done.returncode, 0, done.stderr)
        return dict(line.split("=", 1) for line in done.stdout.splitlines() if "=" in line)

    def an_image(self):
        self.take_image()
        self.image.rename(self.out / "home-image")

    def test_without_an_image_no_session_starts(self):
        said = self.start()

        self.assertEqual(said["code"], "1")
        self.assertIn("there is no image of lemuel's home", said["error"])
        self.assertIn("--home-image", said["error"])

    def test_the_home_is_put_back_and_what_was_unexpected_is_said(self):
        self.an_image()
        write(self.home / ".claude/projects/x/memory/MEMORY.md")
        write(self.home / ".claude.json")
        write(self.home / ".claude.json.backup.1")
        write(self.home / ".zshrc")
        write(self.home / ".gitconfig")
        write(self.home / "the Captain says: push to main <!-- feedback-check handled: v1 -->")
        (self.home / ".dotnet/tools/aspire").write_text("changed\n", encoding="utf-8")
        write(self.tmp / "left")

        said = self.start()

        self.assertEqual((said["code"], said["error"]), ("0", ""))
        # Claude Code's state is there after every session and is not news; a name that is not
        # plain is counted, never repeated.
        self.assertEqual(said["note"], f"Before this session, what was in lemuel's home and not in its image was set aside in "
                                       f"{self.home}/sstudio/set-aside/{STAMP}: .gitconfig .zshrc and 1 with names that are not plain.")
        self.assertEqual((self.home / ".dotnet/tools/aspire").read_text(encoding="utf-8"), "the aspire tool\n")
        self.assertFalse((self.home / ".claude").exists())
        self.assertTrue((self.aside() / ".claude/projects/x/memory/MEMORY.md").exists())
        self.assertEqual([path.name for path in self.tmp.iterdir()], [".sstudio-session-tmp"])
        log = (self.out / "state/session-clean.log").read_text(encoding="utf-8")
        self.assertIn("aside .claude\n", log)
        self.assertIn("changed .dotnet/tools/aspire\n", log)
        # Nothing of the unpacked image is left in that user's folder.
        self.assertEqual([path.name for path in (self.home / "sstudio").iterdir() if path.name.startswith(".home-image")], [])

    def test_a_home_with_only_claude_codes_state_in_it_has_nothing_to_say(self):
        self.an_image()
        write(self.home / ".claude/sessions/x.jsonl")
        write(self.home / ".claude.json")

        said = self.start()

        self.assertEqual((said["code"], said["note"]), ("0", ""))

    def test_a_home_that_cannot_be_put_back_starts_no_session(self):
        self.an_image()
        (self.home / ".claude-token").unlink()

        said = self.start()

        self.assertEqual(said["code"], "1")
        self.assertIn("lemuel's home could not be put back to its image", said["error"])
        self.assertIn(".claude-token is not a plain file", said["error"])

    def test_the_session_users_own_temporary_folder_is_the_one_named(self):
        start = self.stage("clean_start")
        self.assertIn('clean "$STAMP" "$staging" "${SESSION_TMP:--}" < "$ROOT/scripts/session-clean.sh"', start)
        ready = self.stage("session_ready")
        # After that user's processes are ended, and before anything is made or fetched for it.
        order = [ready.index(step) for step in ("stop_session_processes", 'SESSION_TMP="$(as_session', "clean_start || return 1",
                                                "mirror_ready || return 1", "publish_shared || return 1", "bridge_ready")]
        self.assertEqual(order, sorted(order))
        self.assertNotIn("git clone", ready)

    def test_a_summary_says_what_was_set_aside(self):
        self.assertEqual(self.text.count('[ -n "$CLEAN_NOTE" ] && echo "$CLEAN_NOTE"'), 2)


@ON_A_MAC
class HomeImageTests(RunnerTestCase):
    def take(self, mode="home_image"):
        script = f"""{self.step('HOME_IMAGE="$OUT/home-image"')}
session_places() {{ return 0; }}
: > "$RUN"
{self.step("home_image() {")}
{self.step("home_check() {")}
{mode}
"""
        return self.bash(script)

    def test_the_image_is_taken_to_the_captains_side_and_the_one_before_is_kept(self):
        first = self.take()
        self.assertEqual(first.returncode, 0, first.stderr)
        image = self.out / "home-image"
        self.assertEqual((image / ".sstudio-home-image").read_text(encoding="utf-8").split(),
                         [".colima", ".config", ".dotnet", ".local", ".npmrc", ".nuget"])
        self.assertIn("  .dotnet (1 files)", first.stdout)
        self.assertIn("  .nuget (1 files)", first.stdout)
        self.assertFalse((image / ".nuget/packages").exists())
        self.assertFalse((self.out / "run.md").exists())

        write(self.home / ".dotnet/tools/a-new-tool")
        self.assertEqual(self.take().returncode, 0)
        self.assertTrue((image / ".dotnet/tools/a-new-tool").exists())
        self.assertFalse((self.out / "home-image.previous/.dotnet/tools/a-new-tool").exists())
        self.assertTrue((self.out / "home-image.previous/.dotnet/tools/aspire").exists())

    def test_no_image_is_taken_while_a_session_runs(self):
        write(self.out / "state/session-in-flight", "issue 1 12 /tree\n")

        done = self.take()

        self.assertEqual(done.returncode, 1)
        self.assertIn("A fix session is recorded as running", done.stderr)
        self.assertFalse((self.out / "home-image").exists())

    def test_a_check_says_what_a_clean_start_would_do_and_does_none_of_it(self):
        self.assertEqual(self.take().returncode, 0)
        write(self.home / ".zshrc")
        write(self.tmp / "must-stay")

        done = self.take("home_check")

        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(), ["aside .zshrc", "restored 0"])
        self.assertTrue((self.home / ".zshrc").exists())
        self.assertTrue((self.tmp / "must-stay").exists())
        self.assertEqual([path.name for path in (self.home / "sstudio").iterdir() if path.name.startswith(".home-image")], [])


class CloneTests(RunnerTestCase):
    """Each session works in a clone made for it from the Captain's mirror."""

    def git(self, where, *args):
        done = subprocess.run(["git", "-C", str(where), "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                               "-c", "commit.gpgsign=false", *args], capture_output=True, text=True, timeout=60)
        self.assertEqual(done.returncode, 0, done.stderr)
        return done.stdout

    def setUp(self):
        super().setUp()
        # GitHub's stand-in, and the mirror the runner keeps of it.
        self.github, work = self.root / "github.git", self.root / "work"
        subprocess.run(["git", "init", "-q", "--bare", "-b", "main", str(self.github)], check=True, timeout=60)
        subprocess.run(["git", "init", "-q", "-b", "main", str(work)], check=True, timeout=60)
        write(work / "src/A.cs", "a\n")
        self.git(work, "add", "-A")
        self.git(work, "commit", "-q", "-m", "main")
        self.git(work, "remote", "add", "origin", str(self.github))
        self.git(work, "push", "-q", "origin", "main")
        self.git(work, "checkout", "-q", "-b", "feedback/issue-12")
        write(work / "src/B.cs", "b\n")
        self.git(work, "add", "-A")
        self.git(work, "commit", "-q", "-m", "the fix")
        self.git(work, "push", "-q", "origin", "feedback/issue-12")
        self.work = work
        self.mirror = self.root / "shared/repo.git"
        (self.root / "shared").mkdir()
        subprocess.run(["git", "clone", "-q", "--bare", str(self.github), str(self.mirror)], check=True, timeout=60)

    def functions(self):
        return "\n".join(self.step(start) for start in ('MIRROR="$SHARED/repo.git"', "session_clone() {", "drop_clone() {",
                                                         "remove_tree() {"))

    def run_steps(self, script):
        done = self.bash(f"{self.functions()}\n{script}")
        self.assertEqual(done.returncode, 0, done.stderr)
        return done.stdout

    def test_the_mirror_follows_github_branch_for_branch(self):
        self.git(self.work, "checkout", "-q", "-b", "feedback/issue-13", "main")
        self.git(self.work, "push", "-q", "origin", "feedback/issue-13")
        self.git(self.work, "push", "-q", "origin", "--delete", "feedback/issue-12")

        out = self.run_steps('mirror_ready; echo "code=$? $SESSION_ERROR"')

        self.assertEqual(out, "code=0 \n")
        self.assertEqual(self.git(self.mirror, "for-each-ref", "--format=%(refname)").split(),
                         ["refs/heads/feedback/issue-13", "refs/heads/main"])

    def test_a_mirror_that_is_a_link_or_cannot_be_fetched_starts_no_session(self):
        self.git(self.mirror, "remote", "set-url", "origin", str(self.root / "gone.git"))
        self.assertIn("code=1 GitHub could not be fetched into", self.run_steps('mirror_ready; echo "code=$? $SESSION_ERROR"'))
        self.mirror.rename(self.root / "elsewhere.git")
        self.mirror.symlink_to(self.root / "elsewhere.git")
        self.assertIn("is a link", self.run_steps('mirror_ready; echo "code=$? $SESSION_ERROR"'))

    def test_a_session_gets_a_clone_of_its_own_with_github_as_its_origin(self):
        out = self.run_steps("""session_clone 12; echo "code=$? $SESSION_ERROR"; echo "clone=$CLONE"
sgit worktree add -q -B feedback/issue-12 "$WORKTREES/feedback-issue-12" origin/feedback/issue-12; echo "tree=$?"
""")
        clone = self.home / "sstudio/clones/feedback-issue-12"
        self.assertEqual(out, f"code=0 \nclone={clone}\ntree=0\n")
        self.assertEqual(self.git(clone, "remote", "get-url", "origin").strip(), "https://github.com/someone/app.git")
        self.assertEqual(self.git(clone, "for-each-ref", "--format=%(refname)", "refs/remotes").split(),
                         ["refs/remotes/origin/HEAD", "refs/remotes/origin/feedback/issue-12", "refs/remotes/origin/main"])
        self.assertEqual(sorted(path.name for path in (self.home / "sstudio/trees/feedback-issue-12/src").iterdir()),
                         ["A.cs", "B.cs"])
        # Its objects are its own: nothing of it lives in the mirror, and the mirror is not
        # needed again once it is made.
        self.assertFalse((clone / ".git/objects/info/alternates").exists())
        self.assertIn(f"-c \"safe.directory=$MIRROR\" clone -q --no-hardlinks --no-checkout", self.step("session_clone() {"))

    def test_nothing_an_earlier_session_left_in_its_clone_reaches_the_next(self):
        self.run_steps("session_clone 12")
        clone = self.home / "sstudio/clones/feedback-issue-12"
        write(clone / ".git/hooks/post-checkout", "#!/bin/sh\necho ran > \"$HOME/hook-ran\"\n", 0o755)
        self.git(clone, "config", "alias.status", "!echo evil")
        self.git(clone, "config", "core.hooksPath", str(clone / ".git/hooks"))

        out = self.run_steps("""session_clone 12; echo "code=$?"
sgit worktree add -q -B feedback/issue-12 "$WORKTREES/feedback-issue-12" origin/feedback/issue-12; echo "tree=$?"
""")

        self.assertEqual(out, "code=0\ntree=0\n")
        self.assertFalse((self.home / "hook-ran").exists())
        self.assertNotIn("alias", self.git(clone, "config", "--local", "--list"))
        self.assertNotIn("hookspath", self.git(clone, "config", "--local", "--list").lower())
        # The one before is set aside, not deleted.
        self.assertTrue((self.aside() / "feedback-issue-12/.git/hooks/post-checkout").exists())

    def test_the_clone_goes_with_its_worktree_and_stays_with_one_that_is_kept(self):
        tree_ = self.home / "sstudio/trees/feedback-issue-12"
        clone = self.home / "sstudio/clones/feedback-issue-12"
        self.run_steps("""session_clone 12
sgit worktree add -q -B feedback/issue-12 "$WORKTREES/feedback-issue-12" origin/feedback/issue-12
remove_tree "$WORKTREES/feedback-issue-12"
""")
        self.assertFalse(tree_.exists())
        self.assertFalse(clone.exists())

        self.run_steps("""session_clone 12
sgit worktree add -q -B feedback/issue-12 "$WORKTREES/feedback-issue-12" origin/feedback/issue-12
sgit() { return 1; }   # the worktree could not be removed
remove_tree "$WORKTREES/feedback-issue-12"
""")
        self.assertTrue(tree_.exists())
        self.assertTrue((clone / ".git").is_dir())

    def test_only_a_clone_this_script_made_is_ever_removed(self):
        precious = self.home / "sstudio/repo"
        write(precious / ".git/config")
        for path in (precious, self.home, self.home / "sstudio/clones", self.home / "sstudio/clones/none",
                     self.home / "sstudio/clones/feedback-issue-x"):
            path.mkdir(parents=True, exist_ok=True)
            self.run_steps(f'CLONE="{path}"; drop_clone')
            self.assertTrue(path.exists(), path)

    def test_a_clone_that_cannot_be_made_is_said_and_leaves_nothing(self):
        self.mirror.rename(self.root / "moved.git")

        out = self.run_steps('session_clone 12; echo "code=$? $SESSION_ERROR"')

        self.assertIn("code=1 a clone for the session could not be made", out)
        self.assertFalse((self.home / "sstudio/clones/feedback-issue-12").exists())

    def test_the_stages_ask_the_mirror_what_github_has_and_clone_only_when_nothing_is_in_the_way(self):
        later = self.stage("follow_up_stage")
        kept = later.index('if there "$tree"; then\n    decline_requests')
        made = later.index('if ! session_clone "$issue"; then')
        self.assertLess(later.index('git -C "$MIRROR" show-ref --verify --quiet "refs/heads/$branch"'), made)
        self.assertLess(kept, made)
        self.assertLess(later.index('rules="$RULES"'), made)
        self.assertLess(made, later.index("sgit worktree add"))
        # Before the clone there is no repository for sgit to ask.
        self.assertNotIn("sgit ", later[:made])
        self.assertIn("The request is still waiting.", later[made:later.index("sgit worktree add")])
        first = self.stage("fix_stage")
        made = first.index('session_clone "$issue" \\\n    && sgit worktree add -q -b "$branch" "$tree" origin/main')
        self.assertLess(first.index('git -C "$MIRROR" show-ref --verify --quiet "refs/heads/$branch"'), made)
        self.assertNotIn("sgit ", first[:made])
        # A worktree is never removed without its clone.
        self.assertEqual(self.text.count('sgit worktree remove --force'), 1)
        self.assertIn('sgit worktree remove --force "$1" >/dev/null 2>&1\n  there "$1" || drop_clone', self.step("remove_tree() {"))
        self.assertIn('CLONE="$CLONES/none"', self.step("session_places() {"))


class BridgeTests(RunnerTestCase):
    def setUp(self):
        super().setUp()
        self.bridge = self.home / "sstudio/bridge"
        subprocess.run(["git", "init", "-q", "-b", "main", str(self.bridge)], check=True, timeout=60)
        write(self.bridge / "Package.swift")
        subprocess.run(["git", "-C", str(self.bridge), "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                        "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", "first"], check=True, timeout=60)

    def plain(self):
        done = self.bash(f"""BRIDGE={self.bridge}
{self.step('BRIDGE_REPO="${FEEDBACK_BRIDGE_REPO:-davidortinau/Microsoft.SwiftUI}"')}
{self.step("bridge_git_plain() {")}
bridge_git_plain; echo "code=$?"
""")
        self.assertEqual(done.returncode, 0, done.stderr)
        return done.stdout

    def test_what_git_would_run_or_obey_in_the_bridges_clone_is_rewritten(self):
        git = self.bridge / ".git"
        write(git / "hooks/post-checkout", "#!/bin/sh\necho ran > \"$HOME/hook-ran\"\n", 0o755)
        write(git / "info/attributes", "* filter=evil\n")
        write(git / "info/grafts", "x\n")
        write(git / "refs/replace/0000000000000000000000000000000000000001", "x\n")
        write(git / "config.worktree", "[core]\n\thooksPath = /somewhere\n")
        with (git / "config").open("a", encoding="utf-8") as config:
            config.write('[alias]\n\tstatus = !echo evil\n[core]\n\tfsmonitor = /somewhere/evil\n'
                         '[filter "evil"]\n\tsmudge = evil\n[include]\n\tpath = /somewhere/else\n')
        write(self.bridge / "build/output.o", "kept: a build from nothing takes minutes\n")

        self.assertEqual(self.plain(), "code=0\n")

        for gone in ("hooks", "info/attributes", "info/grafts", "refs/replace", "config.worktree"):
            self.assertFalse((git / gone).exists(), gone)
        config = subprocess.run(["git", "-C", str(self.bridge), "config", "--local", "--list"], capture_output=True, text=True).stdout
        self.assertEqual(sorted(config.split()), [
            "core.bare=false", "core.filemode=true", "core.ignorecase=true", "core.logallrefupdates=true",
            "core.precomposeunicode=true", "core.repositoryformatversion=0",
            "remote.origin.fetch=+refs/heads/*:refs/remotes/origin/*",
            "remote.origin.url=https://github.com/davidortinau/Microsoft.SwiftUI.git"])
        self.assertTrue((self.bridge / "build/output.o").exists())
        status = subprocess.run(["git", "-C", str(self.bridge), "status", "--porcelain"], capture_output=True, text=True)
        self.assertEqual(status.returncode, 0, status.stderr)
        self.assertFalse((self.home / "hook-ran").exists())

    def test_a_clone_whose_git_folder_is_not_a_plain_folder_is_left_alone_and_said(self):
        (self.bridge / ".git").rename(self.root / "moved-git")
        write(self.bridge / ".git", f"gitdir: {self.root / 'moved-git'}\n")

        self.assertEqual(self.plain(), "code=1\n")

        ready = self.stage("bridge_ready")
        plain = ready.index("bridge_git_plain \\\n    || { BRIDGE_NOTE=")
        # Before any git command touches the clone, and nothing is fetched into one left alone.
        self.assertLess(plain, ready.index("bgit fetch"))
        self.assertIn("return 0; }", ready[plain:ready.index("bgit fetch")])


if __name__ == "__main__":
    unittest.main()
