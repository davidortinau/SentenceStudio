#!/usr/bin/env python3
"""What the runner itself checks about a fix session's pull request, after the session ends.

A session's "it is fixed" is not evidence (Captain, 2026-11-22): a check counts only when it
could have come back red. So the runner, not the session, does two things in the session's
worktree and says the result on the pull request.

Fix sessions run as a macOS user of their own (Captain, 2026-11-24), and the worktree is that
user's. With --as, every command that touches the worktree or runs the session's code runs as
that user, through sudo; what is decided and written here is this user's. The tests are code
the session wrote, so they never run as the Captain.

The proof. The session names the test that shows the fault, in a small JSON file
(docs/admin-feedback-fix.md, "The proof"). The runner runs it as the session left the worktree,
then again with every changed file outside the test projects put back to what main has, the
session's tests kept. Fails without the fix and passes with it: verified. Passes either way:
it does not show the fault. The worktree is put back as it was afterwards.

The diff. Changes a bug fix should rarely make are listed for the Captain to look at: workflows,
the check's own instructions and scripts, build and package configuration, migrations, and
existing tests that lost tests, assertions or lines. Nothing is blocked; it is said.

  admin-feedback-verify.py pr --tree DIR --declared FILE --comment FILE [--pr N] [--pushed SHA]
                              [--base REF] [--restore-config FILE] [--limit SECONDS]
                              [--result FILE] [--as USER]
      Prints a "PROOF: ..." and a "DIFF: ..." line for the run's summary, writes what to say on
      the pull request to --comment, and with --pr keeps the two verdicts for the status issue
      (state/verdicts.json in the check's folder). --pushed is the pull request's head on
      GitHub; --base what the branch was made from (origin/main); --limit how long one test run
      may take (900). --result gets the verdicts as JSON, for the usage record. --as names the
      macOS user the worktree and --declared belong to; both paths must then be absolute.

Exit 0 whatever the verdicts are; 2 on bad arguments. Nothing here talks to GitHub.
"""
import json
import os
import pwd
import re
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

OUT = Path(os.environ.get("FEEDBACK_CHECK_DIR") or os.path.expanduser("~/sstudio-feedback-check"))
# Lilliput's unattended PATH has no dotnet.
DOTNET_DIR = os.environ.get("FEEDBACK_DOTNET_DIR") or "/usr/local/share/dotnet"
LIMIT = 900
PYTHON = "/usr/bin/python3"
# What a command run as the session's user finds on its PATH (nothing of the Captain's). The
# runner hands over the one its sessions get, so a session and its proof run with the same.
SESSION_PATH = os.environ.get("FEEDBACK_SESSION_PATH") or \
    "/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
# The start of every command that touches the worktree. Empty: it runs as this user. With --as
# it is sudo to the session's user with an environment made from nothing (become).
AS = []
VIEW_TESTS = "tests/SentenceStudio.Native.SwiftUIViews.Tests/SentenceStudio.Native.SwiftUIViews.Tests.csproj"
# Hidden from git in a fix worktree (the package mirror's copy), so it is never moved.
LOCAL_ONLY = ("src/NuGet.config",)

PROJECT = re.compile(r"^[A-Za-z0-9_./-]+\.csproj$")
FILTER = re.compile(r"^[A-Za-z0-9_.~=!&|() ,-]{1,400}$")
BUILD_ERROR = re.compile(r"(?:^|\s)(error [A-Z]+[0-9]+: .*?)(?: \[[^\]]*\])?$", re.M)

# What the status issue and the pull request call each proof verdict.
PROOF_WORDS = {
    "verified": "verified",
    "same": "NOT shown",
    "failing": "FAILING",
    "unbuilt": "partly",
    "tests-only": "tests only",
    "none": "none",
    "not-run": "not run",
}


def plain(text, most=300):
    """Text a session wrote, made safe to post under the Captain's login: one line, no markup
    that could start a request or a marker, cut to length."""
    text = re.sub(r"\s+", " ", str(text or "")).strip()
    text = re.sub(r"[<>`\[\]|\\]", "", text).replace("/revise", "revise")
    return text[:most]


def become(user):
    """The command that runs something as the session's macOS user, with nothing of this user's
    environment: the same one scripts/admin-feedback-check.sh starts a session with
    (as_session; change both together). Raises KeyError when there is no such user."""
    found = pwd.getpwnam(user)
    if found.pw_uid in (0, os.getuid()):
        raise KeyError(user)
    return ["sudo", "-n", "-H", "-u", user, "/usr/bin/env", "-i", f"HOME={found.pw_dir}", f"USER={user}",
            f"LOGNAME={user}", "LANG=en_US.UTF-8", f"PATH={SESSION_PATH}"]


def tool(command, limit=120):
    """A short command, as whoever owns the worktree."""
    return subprocess.run([*AS, *command], capture_output=True, text=True, errors="replace", timeout=limit,
                          cwd="/" if AS else None)


def git(tree, *args, check=True):
    done = tool(["git", "-C", str(tree), *args])
    if check and done.returncode != 0:
        raise RuntimeError(f"git {' '.join(args[:2])} failed: {(done.stderr or done.stdout).strip()[:200]}")
    return done.stdout


def read_file(path, most=1_000_000):
    """The start of a file in the session's space. Raises OSError when it cannot be read."""
    done = tool(["/usr/bin/head", "-c", str(most), "--", str(path)])
    if done.returncode != 0:
        raise OSError(f"{path} could not be read")
    return done.stdout


def is_file(path):
    return tool(["/bin/test", "-f", str(path)]).returncode == 0


def remove(*paths, folders=False):
    if paths:
        tool(["/bin/rm", "-rf" if folders else "-f", "--", *[str(path) for path in paths]])


def find_files(folder, *tests):
    done = tool(["/usr/bin/find", str(folder), "-type", "f", *tests])
    return sorted(line for line in done.stdout.splitlines() if line)


# ---------------------------------------------------------------- which files are tests

def is_test_path(path):
    """A file inside a test project or a tests folder."""
    for part in Path(path).parts[:-1]:
        # SentenceStudio.UnitTests, App.Tests, tests; not Contests.
        if part.lower() in ("tests", "test") or part.lower().endswith(".tests") or part.endswith("Tests"):
            return True
    return False


def changes(tree, base):
    """(status, path) for every file the branch changed since it left base. No renames: a
    rename reads as a delete and an add."""
    found = []
    # With -z a status and its path are two NUL-ended fields.
    fields = git(tree, "diff", "--no-renames", "--name-status", "-z", base, "HEAD").split("\0")
    for status, path in zip(fields[0::2], fields[1::2]):
        if status and path:
            found.append((status[0], path))
    return found


# ---------------------------------------------------------------- the declaration

def read_declared(path):
    """The session's proof declaration: ({"proof": ...}, None) or (None, why it cannot be used)."""
    try:
        text = read_file(path, 100_000)
    except (OSError, subprocess.TimeoutExpired):
        return None, "the session wrote no proof file"
    try:
        found = json.loads(text)
    except ValueError:
        return None, "the session's proof file is not JSON"
    if not isinstance(found, dict):
        return None, "the session's proof file is not a JSON object"
    kind = found.get("proof")
    if kind == "none":
        return {"proof": "none", "reason": plain(found.get("reason")) or "no reason given"}, None
    if kind == "test":
        project, chosen = found.get("project"), found.get("filter")
        if not isinstance(project, str) or not PROJECT.match(project) or ".." in Path(project).parts \
                or project.startswith("/") or not is_test_path(project):
            return None, "the proof's project is not a .csproj inside a test project of this repository"
        if not isinstance(chosen, str) or not FILTER.match(chosen) or chosen.startswith("-"):
            return None, "the proof's filter is missing or has characters a test filter does not need"
        return {"proof": "test", "project": project, "filter": chosen}, None
    if kind == "view-test":
        cases = found.get("cases")
        if not isinstance(cases, list) or not cases or len(cases) > 20 \
                or not all(isinstance(case, str) and case.strip() for case in cases):
            return None, "the proof's cases must be a list of one to twenty view test names"
        return {"proof": "view-test", "cases": [case.strip() for case in cases]}, None
    return None, 'the proof file names no known kind of proof ("test", "view-test" or "none")'


# ---------------------------------------------------------------- running a test

class Ran:
    """One run of the named tests. built False means there was nothing to count."""

    def __init__(self, built, passed=0, failed=0, skipped=0, error="", timed_out=False, seconds=0):
        self.built, self.passed, self.failed, self.skipped = built, passed, failed, skipped
        self.error, self.timed_out, self.seconds = error, timed_out, seconds

    def said(self):
        if self.timed_out:
            return "did not finish in time"
        if not self.built:
            return f"did not build ({self.error})" if self.error else "did not build"
        parts = [f"{self.passed} passed", f"{self.failed} failed"]
        if self.skipped:
            parts.append(f"{self.skipped} skipped or not run")
        return ", ".join(parts)


# Runs a command in a folder, in a process group of its own, and ends the whole group when it
# runs over its limit. It runs as whoever owns the worktree: this user cannot end another
# user's processes. Arguments: seconds, folder, the command. Exit 124 means it ran out of time.
SUPERVISOR = """
import os, signal, subprocess, sys
limit, folder, command = float(sys.argv[1]), sys.argv[2], sys.argv[3:]
try:
    os.chdir(folder)
    child = subprocess.Popen(command, start_new_session=True, stdin=subprocess.DEVNULL)
except OSError as error:
    print(f"error RUN0001: {command[0]} could not be started: {error}")
    sys.exit(127)
try:
    code = child.wait(timeout=limit)
except subprocess.TimeoutExpired:
    try:
        os.killpg(child.pid, signal.SIGKILL)
    except OSError:
        pass
    child.wait()
    sys.exit(124)
sys.exit(code if code >= 0 else 128 - code)
"""
OUT_OF_TIME = 124


def run(command, tree, limit):
    """Runs a command in the worktree, as whoever owns it, in a process group of its own, so a
    run over its limit is ended with everything it started. Returns (exit code or None when it
    ran out of time, output)."""
    path = DOTNET_DIR + os.pathsep + (SESSION_PATH if AS else os.environ.get("PATH", ""))
    # No build servers left holding the worktree's files after the run.
    settings = ["/usr/bin/env", f"PATH={path}", "MSBUILDDISABLENODEREUSE=1", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
                "DOTNET_NOLOGO=1", "DOTNET_CLI_USE_MSBUILD_SERVER=0"]
    try:
        started = subprocess.Popen(
            [*AS, PYTHON, "-I", "-c", SUPERVISOR, str(limit), str(tree), *settings, *command],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace",
            cwd="/" if AS else None)
    except OSError as error:
        return 127, f"error RUN0001: {command[0]} could not be started: {error}"
    try:
        output, _ = started.communicate(timeout=limit + 60)
    except subprocess.TimeoutExpired:
        # The supervisor itself hung. sudo passes the signal on to it.
        try:
            started.terminate()
        except OSError:
            pass
        try:
            started.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            pass
        return None, ""
    if started.returncode == OUT_OF_TIME:
        return None, ""
    return started.returncode, output


def first_error(output, tree=None):
    """The first build error, without where the worktree is on this machine: it is posted."""
    output = output or ""
    if tree:
        output = output.replace(os.path.realpath(str(tree)) + os.sep, "").replace(str(tree) + os.sep, "")
    # Nor whose home anything else is in.
    output = re.sub(r"/Users/[^/\s]+/", "~/", output)
    found = BUILD_ERROR.search(output)
    return plain(found.group(1), 200) if found else ""


def counter(element, name):
    value = element.get(name) or ""
    return int(value) if value.isdigit() else 0


def trx_counts(folder):
    """(passed, failed, skipped) summed over the result files a dotnet test run left, or None
    when it left none."""
    files = find_files(folder, "-name", "*.trx")
    if not files:
        return None
    passed = failed = skipped = 0
    for file in files:
        try:
            root = ET.fromstring(read_file(file, 20_000_000))
        except (ET.ParseError, OSError):
            continue
        for element in root.iter():
            if element.tag.rsplit("}", 1)[-1] != "Counters":
                continue
            ok = counter(element, "passed")
            bad = sum(counter(element, name) for name in ("failed", "error", "timeout", "aborted"))
            passed += ok
            failed += bad
            # A skipped test is counted in total and nowhere else (notExecuted stays 0).
            skipped += max(counter(element, "total") - ok - bad, 0)
    return passed, failed, skipped


def build_properties(restore_config, mobile_targets_off=True):
    properties = ["-p:IncludeMobileTargets=false"] if mobile_targets_off else []
    properties.append("-p:ValidateXcodeVersion=false")
    if restore_config:
        properties.append(f"-p:RestoreConfigFile={restore_config}")
    return properties


def run_dotnet_test(tree, declared, restore_config, limit):
    began = time.time()
    # Where the test run can write: a folder of the worktree's owner.
    made = tool(["/bin/sh", "-c", 'mktemp -d "${TMPDIR:-/tmp}/feedback-proof.XXXXXXXX"'])
    results = made.stdout.strip()
    if made.returncode != 0 or not results.startswith("/") or "feedback-proof" not in results:
        raise OSError("no folder for the test results could be made")
    try:
        code, output = run(["dotnet", "test", declared["project"], "--filter", declared["filter"],
                            *build_properties(restore_config),
                            "--logger", "trx;LogFileName=proof.trx", "--results-directory", results],
                           tree, limit)
        counts = trx_counts(results)
    finally:
        remove(results, folders=True)
    seconds = int(time.time() - began)
    if code is None:
        return Ran(False, timed_out=True, seconds=seconds)
    if counts is None:
        # No result file: the build or the restore failed, or the test host died. Only a compiler
        # error (see does_not_compile) is read as "the tests do not build".
        if code != 0:
            return Ran(False, error=first_error(output, tree) or f"dotnet test ended with code {code} and left no result",
                       seconds=seconds)
        return Ran(True, seconds=seconds)
    return Ran(True, *counts, seconds=seconds)


def view_cases(output, cases):
    """(passed, failed, not seen) among the named cases in the view test program's output."""
    passed, failed = set(), set()
    for line in (output or "").splitlines():
        if line.startswith("PASS "):
            passed.add(line[5:].strip())
        elif line.startswith("FAIL "):
            # "FAIL <name>: <exception>: <message>"; a name can hold a colon itself.
            rest = line[5:]
            failed.update(case for case in cases if rest == case or rest.startswith(case + ": "))
    wanted = set(cases)
    return len(wanted & passed), len(wanted & failed), len(wanted - passed - failed)


def run_view_test(tree, declared, restore_config, limit):
    began = time.time()
    # A macOS program, built as its project file says: without the server builds' property.
    code, output = run(["dotnet", "build", VIEW_TESTS, *build_properties(restore_config, mobile_targets_off=False)],
                       tree, limit)
    if code is None:
        return Ran(False, timed_out=True, seconds=int(time.time() - began))
    if code != 0:
        return Ran(False, error=first_error(output, tree) or f"the build ended with code {code}", seconds=int(time.time() - began))
    programs = find_files(Path(tree) / Path(VIEW_TESTS).parent / "bin" / "Debug",
                          "-path", "*.app/Contents/MacOS/SentenceStudio.Native.SwiftUIViews.Tests")
    if not programs:
        return Ran(False, error="the view test program was not where the build puts it", seconds=int(time.time() - began))
    # One limit for the build and the program together.
    code, output = run([str(programs[-1])], tree, max(limit - (time.time() - began), 1))
    seconds = int(time.time() - began)
    if code is None:
        return Ran(False, timed_out=True, seconds=seconds)
    passed, failed, unseen = view_cases(output, declared["cases"])
    return Ran(True, passed, failed, unseen, seconds=seconds)


def run_declared(tree, declared, restore_config, limit):
    if declared["proof"] == "view-test":
        return run_view_test(tree, declared, restore_config, limit)
    return run_dotnet_test(tree, declared, restore_config, limit)


# ---------------------------------------------------------------- with and without the fix

def clean(tree):
    """Whether the worktree holds exactly its commit. A file git does not track counts as a
    change: the build would take it in, and the pull request does not have it."""
    return not git(tree, "status", "--porcelain").strip()


def does_not_compile(ran):
    return not ran.built and ran.error.startswith("error CS")


def chunks(paths, size=100):
    for start in range(0, len(paths), size):
        yield paths[start:start + size]


def take_out(tree, base, fix):
    """Puts every changed file outside the tests back to what base has."""
    for group in chunks([path for status, path in fix if status != "A"]):
        git(tree, "checkout", base, "--", *group)
    for group in chunks([str(Path(tree) / path) for status, path in fix if status == "A"]):
        remove(*group)


def put_back(tree, fix):
    """Undoes take_out. Returns whether the worktree is as the session left it."""
    try:
        for group in chunks([path for status, path in fix if status != "D"]):
            git(tree, "checkout", "HEAD", "--", *group)
        for group in chunks([path for status, path in fix if status == "D"]):
            git(tree, "rm", "-q", "-f", "--", *group)
        return clean(tree)
    except (RuntimeError, subprocess.TimeoutExpired, OSError):
        return False


def named(declared):
    if declared["proof"] == "view-test":
        cases = declared["cases"]
        return f"the view test{'s' if len(cases) > 1 else ''} " + ", ".join(f'"{plain(case, 120)}"' for case in cases)
    return f"`{declared['filter']}` in `{Path(declared['project']).parent}`"


def prove(tree, declared, why, changed, base, restore_config=None, limit=LIMIT, runner=run_declared):
    """The proof verdict: (key, one line, sentences for the pull request, seconds spent).
    runner is replaced in tests."""
    if declared is None:
        return "none", f"none ({why})", [
            f"The runner could not use a proof from the session: {why}.",
            "Nothing but the session's own word says this change works."], 0
    if declared["proof"] == "none":
        return "none", "none (the session named no test)", [
            f"The session named no test. Its reason: {declared['reason']}",
            "Nothing but the session's own word says this change works."], 0
    what = named(declared)
    if declared["proof"] == "test" and not is_file(Path(tree) / declared["project"]):
        return "not-run", "not run (the named project is not in the worktree)", [
            f"The session named {what}, and that project is not in its worktree."], 0
    if not clean(tree):
        return "not-run", "not run (the worktree has uncommitted or untracked files)", [
            f"The session named {what}. Its worktree has uncommitted or untracked files, so the runner did not "
            "run it: what is on the pull request is not what is in the worktree."], 0

    with_fix = runner(tree, declared, restore_config, limit)
    spent = with_fix.seconds
    if with_fix.timed_out:
        return "not-run", f"not run (the test did not finish in {limit} s)", [
            f"The session named {what}. With the change it did not finish in {limit} s."], spent
    if not with_fix.built:
        return "not-run", "not run (with the change the tests did not build)", [
            f"The session named {what}. With the change the runner could not build it ({with_fix.error or 'no error line found'})."], spent
    if with_fix.failed:
        return "failing", f"FAILING (with the change: {with_fix.said()})", [
            f"The session named {what}. **With the change it does not pass: {with_fix.said()}.**"], spent
    if not with_fix.passed or (declared["proof"] == "view-test" and with_fix.skipped):
        said = with_fix.said() if with_fix.skipped else "no test matched the filter"
        return "not-run", f"not run (with the change: {said})", [
            f"The session named {what}. With the change: {said}. A test that needs a database is skipped here: "
            "the runner has none."], spent

    fix = [(status, path) for status, path in changed if not is_test_path(path) and path not in LOCAL_ONLY]
    if not fix:
        return "tests-only", f"tests only ({with_fix.said()}; nothing outside tests changed)", [
            f"The session named {what}: {with_fix.said()}.",
            "The change has nothing outside its tests, so there was no fix to take out and see them fail."], spent

    restored = True
    try:
        take_out(tree, base, fix)
        without = runner(tree, declared, restore_config, limit)
    finally:
        restored = put_back(tree, fix)
    spent += without.seconds
    left = [] if restored else [
        "**The runner could not put the worktree back as the session left it; it is kept on Lilliput.**"]
    if without.timed_out:
        return "not-run", f"not run (without the fix the test did not finish in {limit} s)", [
            f"The session named {what}. With the change: {with_fix.said()}. Without the fix it did not finish in {limit} s."] + left, spent
    if does_not_compile(without):
        return "unbuilt", "partly (passes with the fix; without it the tests do not build)", [
            f"The session named {what}. With the change: {with_fix.said()}.",
            f"Without the fix the tests do not build ({without.error}), so no run came back red: "
            "the tests need code the fix adds. They show that code exists and behaves, not that the fault was there."] + left, spent
    if not without.built:
        # A failed restore or a test host that died: not a red run, and not the session's doing.
        return "not-run", "not run (without the fix the run gave no result)", [
            f"The session named {what}. With the change: {with_fix.said()}.",
            f"Without the fix the run gave no result ({without.error or 'no error line found'}): the build did not get "
            "as far as the tests, or the test program died. That is not a run that came back red."] + left, spent
    if without.failed:
        return "verified", f"verified (without the fix: {without.said()}; with it: {with_fix.said()})", [
            f"The session named {what}.",
            f"Without the fix (its tests kept, everything else as on main): {without.said()}. With it: {with_fix.said()}."] + left, spent
    if not without.passed or (declared["proof"] == "view-test" and without.skipped):
        # Nothing failed because nothing, or not everything, ran: that is not a pass.
        said = without.said() if without.passed or without.skipped else "no test ran"
        return "not-run", f"not run (without the fix: {said})", [
            f"The session named {what}. With the change: {with_fix.said()}.",
            f"Without the fix the named tests did not all run ({said}), so whether they fail there is not known."] + left, spent
    return "same", f"NOT shown (the test passes without the fix too: {without.said()})", [
        f"The session named {what}. With the change: {with_fix.said()}.",
        f"**Without the fix it passes too ({without.said()}), so it does not show the fault.**"] + left, spent


# ---------------------------------------------------------------- the diff

CONFIG_NAMES = ("global.json", "nuget.config", "directory.build.props", "directory.build.targets",
                "directory.packages.props", ".editorconfig", "azure.yaml", "aspire.config.json",
                "launchsettings.json")
OWN_INSTRUCTIONS = re.compile(
    r"^(AGENTS\.md|CLAUDE\.md|\.claude/|\.squad/|docs/admin-feedback-[^/]*\.md$|docs/admin-api\.md$"
    r"|scripts/admin-[^/]*$|scripts/prod-[^/]*$|scripts/tests/test_admin_feedback_[^/]*$)")
TEST_MARK = re.compile(r"\[\s*(Fact|Theory|Test|TestMethod|TestCase)\b|^\s*def test_")
ASSERTION = re.compile(r"\bAssert\.|\.Should\b|\bself\.assert|\bexpect\(")
SKIP = re.compile(r"\bSkip\s*=|\[\s*Ignore\b|\bAssert\.Skip|\bAssert\.Inconclusive|@unittest\.skip|\.skip\(")
TAKEN_OUT = re.compile(r"<\s*(Compile|None|Content|EmbeddedResource)\s+Remove\s*=")
REFERENCE = re.compile(r"<\s*(PackageReference|PackageVersion|ProjectReference)\b")


def config_kind(path):
    """Which kind of build or deploy configuration a path is, or None."""
    name = Path(path).name.lower()
    if name in CONFIG_NAMES or name.endswith((".sln", ".slnx")):
        return "build"
    if name.startswith("appsettings") and name.endswith(".json"):
        return "build"
    if path.startswith("infra/"):
        return "build"
    return None


def changed_lines(tree, base, path):
    """(removed, added) lines of one file's change, without the diff's own markers."""
    removed, added = [], []
    for line in git(tree, "diff", "--no-renames", "-U0", base, "HEAD", "--", path).splitlines():
        if line.startswith("---") or line.startswith("+++"):
            continue
        if line.startswith("-") and line[1:].strip():
            removed.append(line[1:])
        elif line.startswith("+") and line[1:].strip():
            added.append(line[1:])
    return removed, added


def test_change(removed, added):
    """What an existing test file lost, in words, or None when it only gained tests."""
    def count(pattern, lines):
        return sum(1 for line in lines if pattern.search(line))

    parts = []
    tests = count(TEST_MARK, removed) - count(TEST_MARK, added)
    if tests > 0:
        parts.append(f"{tests} test{'s' if tests > 1 else ''} removed")
    skipped = count(SKIP, added) - count(SKIP, removed)
    if skipped > 0:
        parts.append(f"{skipped} skipped")
    assertions = count(ASSERTION, removed)
    if assertions:
        parts.append(f"{assertions} assertion line{'s' if assertions > 1 else ''} removed or rewritten")
    if not removed and not parts:
        return None
    if not parts:
        parts.append(f"{len(removed)} line{'s' if len(removed) > 1 else ''} removed or rewritten")
    return ", ".join(parts)


def look_at(changed, lines_of):
    """What in the change the Captain should look at: [(heading, [(path, note)])].
    lines_of(path) gives (removed, added) for a file."""
    found = {}

    def add(heading, path, note=""):
        found.setdefault(heading, []).append((path, note))

    for status, path in changed:
        if path.startswith(".github/"):
            add("Workflows and CI", path)
        elif OWN_INSTRUCTIONS.match(path):
            add("The check's own instructions and scripts", path)
        elif "Migrations" in Path(path).parts:
            add("A migration or schema change", path)
        elif config_kind(path):
            add("Build, package or app configuration", path)
        elif path.endswith(".csproj"):
            removed, added = lines_of(path) if status == "M" else ([], [])
            if status == "D" and is_test_path(path):
                add("Existing tests changed", path, "test project deleted")
            elif status != "M" and not is_test_path(path):
                add("Build, package or app configuration", path, "new project" if status == "A" else "project removed")
            elif is_test_path(path) and any(TAKEN_OUT.search(line) for line in added):
                add("Existing tests changed", path, "files taken out of the test build")
            elif any(REFERENCE.search(line) for line in removed + added):
                add("Build, package or app configuration", path, "package or project references changed")
        elif is_test_path(path):
            if status == "D":
                add("Existing tests changed", path, "deleted")
            elif status == "M":
                said = test_change(*lines_of(path))
                if said:
                    add("Existing tests changed", path, said)
    order = ["Workflows and CI", "The check's own instructions and scripts", "A migration or schema change",
             "Build, package or app configuration", "Existing tests changed"]
    return [(heading, found[heading]) for heading in order if heading in found]


def size(tree, base):
    files = added = removed = 0
    for line in git(tree, "diff", "--no-renames", "--numstat", base, "HEAD").splitlines():
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        files += 1
        added += int(parts[0]) if parts[0].isdigit() else 0
        removed += int(parts[1]) if parts[1].isdigit() else 0
    return f"{files} file{'' if files == 1 else 's'}, +{added} -{removed}"


def diff_verdict(findings, how_big):
    """(key, one line, lines for the pull request)."""
    if not findings:
        return "clean", f"nothing to look at ({how_big})", [f"Nothing a fix should rarely touch was changed ({how_big})."]
    count = sum(len(paths) for _, paths in findings)
    line = "look at: " + "; ".join(f"{heading.lower()} ({len(paths)})" for heading, paths in findings) + f" ({how_big})"
    said = [f"The change touches {count} file{'' if count == 1 else 's'} a fix should rarely need ({how_big}). "
            "None of it is refused; look at each before merging."]
    for heading, paths in findings:
        said.append("")
        said.append(f"{heading}:")
        said += [f"- `{plain(path)}`" + (f" ({note})" if note else "") for path, note in paths[:20]]
        if len(paths) > 20:
            said.append(f"- and {len(paths) - 20} more")
    return f"{count} to look at", line, said


# ---------------------------------------------------------------- putting it together

def comment(proof_key, proof_said, diff_key, diff_said, notes):
    lines = ["**What the runner checked** (itself, after the session ended)", "",
             f"**Proof: {PROOF_WORDS[proof_key]}.** " + " ".join(proof_said), "",
             f"**Diff: {'nothing to look at' if diff_key == 'clean' else diff_key}.** " + diff_said[0]]
    lines += diff_said[1:]
    if notes:
        lines += ["", *notes]
    return "\n".join(lines) + "\n"


def keep(pr, proof_key, diff_key, head):
    """The verdicts on a pull request, for the status issue."""
    path = OUT / "state" / "verdicts.json"
    try:
        kept = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(kept, dict):
            kept = {}
    except (OSError, ValueError):
        kept = {}
    kept[str(pr)] = {"proof": proof_key, "diff": diff_key, "head": head,
                     "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(kept, indent=1) + "\n", encoding="utf-8")
    temporary.replace(path)


def option(args, name):
    if name in args:
        position = args.index(name)
        if position + 1 < len(args):
            return args[position + 1]
    return None


def check_pr(args):
    tree, declared_path, comment_path = option(args, "--tree"), option(args, "--declared"), option(args, "--comment")
    if not tree or not declared_path or not comment_path:
        print(__doc__)
        return 2
    limit = int(option(args, "--limit") or LIMIT)
    user = option(args, "--as")
    if user:
        try:
            AS[:] = become(user)
        except KeyError:
            print(f"--as {user}: no such macOS user, or not one apart from this one", file=sys.stderr)
            return 2
        # That user's own temporary folder, as its sessions have: /tmp is shared with the Captain.
        try:
            own = tool(["/usr/bin/getconf", "DARWIN_USER_TEMP_DIR"]).stdout.strip()
        except (OSError, subprocess.TimeoutExpired):
            own = ""
        if re.fullmatch(r"/[A-Za-z0-9_./-]+", own):
            AS.append(f"TMPDIR={own}")
    notes = []
    try:
        base = git(tree, "merge-base", "HEAD", option(args, "--base") or "origin/main").strip()
        head = git(tree, "rev-parse", "HEAD").strip()
        changed = changes(tree, base)
    except (RuntimeError, subprocess.TimeoutExpired, OSError) as error:
        print(error, file=sys.stderr)
        print("PROOF: not run (the worktree could not be compared with main)")
        print("DIFF: not read (the worktree could not be compared with main)")
        Path(comment_path).write_text(
            "**What the runner checked**\n\nNeither the proof nor the diff could be checked: the session's "
            "worktree could not be compared with main.\n", encoding="utf-8")
        return 0
    pushed = option(args, "--pushed")
    elsewhere = bool(pushed and pushed != head)
    if elsewhere:
        notes.append(f"The worktree was at {head[:8]} and the pull request's head was {pushed[:8]}: "
                     "the diff read here is the worktree's, not the pull request's.")

    declared, why = read_declared(declared_path)
    try:
        if elsewhere and declared and declared["proof"] != "none":
            # A proof of commits that were never pushed says nothing about the pull request.
            proof_key, proof_line, seconds = "not-run", "not run (the worktree is not at the pull request's head)", 0
            proof_said = [f"The session named {named(declared)}. Its worktree is not at the pull request's head, "
                          "so the runner did not run it."]
        else:
            proof_key, proof_line, proof_said, seconds = prove(
                tree, declared, why, changed, base, option(args, "--restore-config"), limit)
    except (RuntimeError, subprocess.TimeoutExpired, OSError) as error:
        print(error, file=sys.stderr)
        proof_key, proof_line, seconds = "not-run", "not run (the runner's own run broke)", 0
        proof_said = [f"The runner could not run the session's proof: {plain(error, 200)}."]
    try:
        findings = look_at(changed, lambda path: changed_lines(tree, base, path))
        diff_key, diff_line, diff_said = diff_verdict(findings, size(tree, base))
    except (RuntimeError, subprocess.TimeoutExpired, OSError) as error:
        print(error, file=sys.stderr)
        diff_key, diff_line, diff_said = "not read", "not read (git did not answer)", ["The diff could not be read."]

    print(f"PROOF: {proof_line}")
    print(f"DIFF: {diff_line}")
    Path(comment_path).write_text(comment(proof_key, proof_said, diff_key, diff_said, notes), encoding="utf-8")
    result = option(args, "--result")
    if result:
        Path(result).write_text(json.dumps({"proof": proof_key, "diff": diff_key, "proof_seconds": seconds}), encoding="utf-8")
    pr = option(args, "--pr")
    if pr and pr.isdigit():
        try:
            keep(int(pr), proof_key, diff_key, head)
        except OSError:
            pass
    return 0


def main(args):
    if args and args[0] == "pr":
        return check_pr(args[1:])
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
