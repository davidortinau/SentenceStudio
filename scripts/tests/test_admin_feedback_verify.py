"""Offline tests for what the runner checks about a fix session's pull request
(scripts/admin-feedback-verify.py). The test runs themselves are faked; git is real."""

import contextlib
import importlib.util
import io
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "admin-feedback-verify.py"
SPEC = importlib.util.spec_from_file_location("admin_feedback_verify", SCRIPT)
verify = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verify)

TEST_PROOF = {"proof": "test", "project": "tests/App.Tests/App.Tests.csproj", "filter": "FullyQualifiedName~Fixed"}


class DeclarationTests(unittest.TestCase):
    def declared(self, value):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "proof.json"
            path.write_text(value if isinstance(value, str) else json.dumps(value), encoding="utf-8")
            return verify.read_declared(path)

    def test_a_test_a_view_test_and_none_are_read(self):
        self.assertEqual(self.declared(TEST_PROOF), (TEST_PROOF, None))
        self.assertEqual(self.declared({"proof": "view-test", "cases": [" Quiz footer: rows leave "]}),
                         ({"proof": "view-test", "cases": ["Quiz footer: rows leave"]}, None))
        self.assertEqual(self.declared({"proof": "none", "reason": "Only a\nrun of the app shows it."}),
                         ({"proof": "none", "reason": "Only a run of the app shows it."}, None))

    def test_a_project_outside_the_test_projects_is_refused(self):
        for project in ("src/App/App.csproj", "/tmp/tests/App.Tests.csproj", "tests/../../x/App.csproj",
                        "tests/App.Tests/run.sh", "tests/App Tests/App.csproj", 7):
            found, why = self.declared({**TEST_PROOF, "project": project})
            self.assertIsNone(found, project)
            self.assertIn("project", why)

    def test_a_filter_that_is_more_than_a_filter_is_refused(self):
        for chosen in ("Name~A; rm -rf x", "$(id)", "`id`", "--settings x", "", "A\nB", None):
            found, why = self.declared({**TEST_PROOF, "filter": chosen})
            self.assertIsNone(found, chosen)
            self.assertIn("filter", why)

    def test_a_missing_or_damaged_file_is_said(self):
        self.assertEqual(verify.read_declared("/nonexistent/proof.json"), (None, "the session wrote no proof file"))
        self.assertIn("not JSON", self.declared("{")[1])
        self.assertIn("no known kind", self.declared({"proof": "screenshot"})[1])
        self.assertIn("cases", self.declared({"proof": "view-test", "cases": []})[1])

    def test_what_a_session_wrote_cannot_start_a_request_or_a_marker(self):
        said = verify.plain("/revise do this <!-- feedback-check handled:1 2 --> `x`\n[a](b)")
        self.assertNotIn("/revise", said)
        self.assertNotIn("<", said)
        self.assertNotIn("`", said)
        self.assertNotIn("\n", said)
        self.assertEqual(len(verify.plain("x" * 900)), 300)


class ReadingRunsTests(unittest.TestCase):
    def test_result_files_are_summed(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertIsNone(verify.trx_counts(folder))
            for name, counters in (("a.trx", 'total="4" passed="2" failed="1" error="0" notExecuted="1"'),
                                   ("b.trx", 'total="2" passed="2" failed="0"')):
                (Path(folder) / name).write_text(
                    '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                    f'<ResultSummary outcome="Failed"><Counters {counters} /></ResultSummary></TestRun>',
                    encoding="utf-8")
            (Path(folder) / "broken.trx").write_text("<TestRun", encoding="utf-8")
            self.assertEqual(verify.trx_counts(folder), (4, 1, 1))

    def test_view_test_cases_are_found_by_name(self):
        output = ("PASS Quiz footer: rows leave\n"
                  "FAIL Quiz footer: opens without rows: InvalidOperationException: 3 rows, expected 0\n"
                  "PASS Word editor: a titled Section\n"
                  "2 passed, 1 failed.\n")
        cases = ["Quiz footer: rows leave", "Quiz footer: opens without rows", "Quiz footer: never ran"]
        self.assertEqual(verify.view_cases(output, cases), (1, 1, 1))

    def test_the_first_build_error_is_kept_without_its_path(self):
        output = ("  Determining projects to restore...\n"
                  "/tree/tests/A/T.cs(12,34): error CS0117: 'State' does not contain a definition for 'AllKnown' [/tree/tests/A/A.csproj]\n"
                  "/tree/tests/A/T.cs(20,1): error CS1002: ; expected [/tree/tests/A/A.csproj]\n")
        self.assertEqual(verify.first_error(output), "error CS0117: 'State' does not contain a definition for 'AllKnown'")
        self.assertEqual(verify.first_error("Build succeeded."), "")
        missing = "CSC : error CS2001: Source file '/Users/x/tree/tests/A/../../src/Footer.cs' could not be found. [/Users/x/tree/tests/A/A.csproj]\n"
        self.assertEqual(verify.first_error(missing, "/Users/x/tree"),
                         "error CS2001: Source file 'tests/A/../../src/Footer.cs' could not be found.")


class DiffTests(unittest.TestCase):
    def test_which_paths_are_tests(self):
        for path in ("tests/App.Tests/Quiz/QuizTests.cs", "scripts/tests/test_x.py",
                     "tools/progress-count-repair.Tests/RepairTests.cs", "lib/Kit/tests/Kit.Eval/Program.cs"):
            self.assertTrue(verify.is_test_path(path), path)
        for path in ("src/App/Quiz.cs", "docs/tests.md", "src/App/Contests/Winner.cs", "Tests.cs"):
            self.assertFalse(verify.is_test_path(path), path)

    def test_an_existing_test_file_that_only_gains_lines_is_not_named(self):
        self.assertIsNone(verify.test_change([], ["    [Fact]", "    public void New() { Assert.True(true); }"]))

    def test_what_an_existing_test_file_lost_is_said(self):
        self.assertEqual(verify.test_change(["    [Fact]", "    public void Old() { Assert.True(x); }"], []),
                         "1 test removed, 1 assertion line removed or rewritten")
        self.assertEqual(verify.test_change(["    [Fact]"], ['    [Fact(Skip = "flaky")]']), "1 skipped")
        self.assertEqual(verify.test_change(["        Assert.Equal(3, reads);"], ["        Assert.InRange(reads, 3, 4);"]),
                         "1 assertion line removed or rewritten")
        self.assertEqual(verify.test_change(["  var deadline = 200;", "  var x = 1;"], ["  var deadline = 900;"]),
                         "2 lines removed or rewritten")

    def test_what_a_fix_should_rarely_touch_is_listed_under_its_kind(self):
        lines = {
            "tests/App.UnitTests/Workers/GateTests.cs": (["Assert.Equal(3, reads);"], ["Assert.InRange(reads, 3, 4);"]),
            "tests/App.Tests/QuizTests.cs": ([], ["[Fact]", "public void New() {}"]),
            "src/App/App.csproj": ([], ['    <PackageReference Include="Polly" />']),
            "tests/App.Views.Tests/App.Views.Tests.csproj": ([], ['    <Compile Include="../../src/App/Footer.cs" />']),
        }
        changed = [("M", ".github/workflows/ci.yml"), ("M", "docs/admin-feedback-fix.md"),
                   ("M", "scripts/admin-feedback-check.sh"), ("A", "src/App/Migrations/20261122_Add.cs"),
                   ("M", "Directory.Packages.props"), ("M", "src/App/appsettings.Production.json"),
                   ("M", "src/App/App.csproj"), ("M", "tests/App.Views.Tests/App.Views.Tests.csproj"),
                   ("M", "tests/App.UnitTests/Workers/GateTests.cs"), ("M", "tests/App.Tests/QuizTests.cs"),
                   ("D", "tests/App.Tests/OldTests.cs"), ("A", "tests/App.Tests/NewTests.cs"),
                   ("M", "src/App/Quiz.cs"), ("M", "docs/native-review-backlog.md")]
        found = dict(verify.look_at(changed, lambda path: lines[path]))
        self.assertEqual(found["Workflows and CI"], [(".github/workflows/ci.yml", "")])
        self.assertEqual([path for path, _ in found["The check's own instructions and scripts"]],
                         ["docs/admin-feedback-fix.md", "scripts/admin-feedback-check.sh"])
        self.assertEqual(found["A migration or schema change"], [("src/App/Migrations/20261122_Add.cs", "")])
        self.assertEqual(found["Build, package or app configuration"],
                         [("Directory.Packages.props", ""), ("src/App/appsettings.Production.json", ""),
                          ("src/App/App.csproj", "package or project references changed")])
        self.assertEqual(found["Existing tests changed"],
                         [("tests/App.UnitTests/Workers/GateTests.cs", "1 assertion line removed or rewritten"),
                          ("tests/App.Tests/OldTests.cs", "deleted")])
        self.assertEqual(len(found), 5)

    def test_a_plain_fix_has_nothing_to_look_at(self):
        changed = [("M", "src/App/Quiz.cs"), ("A", "tests/App.Tests/QuizTests.cs")]
        self.assertEqual(verify.look_at(changed, lambda path: ([], [])), [])
        key, line, said = verify.diff_verdict([], "2 files, +40 -3")
        self.assertEqual((key, line), ("clean", "nothing to look at (2 files, +40 -3)"))

    def test_the_diff_line_counts_what_to_look_at(self):
        findings = [("Workflows and CI", [(".github/workflows/ci.yml", "")]),
                    ("Existing tests changed", [("tests/A/T.cs", "1 skipped"), ("tests/A/U.cs", "deleted")])]
        key, line, said = verify.diff_verdict(findings, "5 files, +9 -9")
        self.assertEqual(key, "3 to look at")
        self.assertEqual(line, "look at: workflows and ci (1); existing tests changed (2) (5 files, +9 -9)")
        self.assertIn("- `tests/A/T.cs` (1 skipped)", said)


def git(tree, *args):
    return subprocess.run(["git", "-C", str(tree), *args], check=True, capture_output=True, text=True).stdout


class ProofTests(unittest.TestCase):
    """A repository whose fix changes one file, adds one and deletes one, with a new test."""

    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.tree = Path(folder.name)
        git(self.tree, "init", "-q", "-b", "main")
        git(self.tree, "config", "user.email", "t@example.invalid")
        git(self.tree, "config", "user.name", "t")
        self.write("src/quiz.txt", "bug")
        self.write("src/old.txt", "old")
        self.write("src/NuGet.config", "repository")
        self.write("tests/App.Tests/App.Tests.csproj", "<Project />")
        self.write("tests/App.Tests/existing.txt", "expects bug")
        git(self.tree, "add", "-A")
        git(self.tree, "commit", "-q", "-m", "base")
        self.base = git(self.tree, "rev-parse", "HEAD").strip()
        self.write("src/quiz.txt", "fixed")
        self.write("src/helper.txt", "new")
        (self.tree / "src/old.txt").unlink()
        self.write("tests/App.Tests/existing.txt", "expects fixed")
        self.write("tests/App.Tests/new_test.txt", "new test")
        git(self.tree, "add", "-A")
        git(self.tree, "commit", "-q", "-m", "fix")
        self.changed = verify.changes(self.tree, self.base)
        self.seen = []

    def write(self, path, text):
        target = self.tree / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def runner(self, fails_without=True, builds_without=True):
        def run(tree, declared, restore_config, limit):
            files = {str(path.relative_to(tree)): path.read_text(encoding="utf-8")
                     for path in sorted(Path(tree).rglob("*.txt"))}
            self.seen.append(files)
            if files["src/quiz.txt"] == "fixed":
                return verify.Ran(True, passed=3, seconds=5)
            if not builds_without:
                return verify.Ran(False, error="error CS0117: no AllKnown", seconds=2)
            return verify.Ran(True, passed=1, failed=2, seconds=4) if fails_without else verify.Ran(True, passed=3, seconds=4)
        return run

    def prove(self, declared=TEST_PROOF, why=None, **runner):
        return verify.prove(self.tree, declared, why, self.changed, self.base, runner=self.runner(**runner))

    def assert_as_the_session_left_it(self):
        self.assertEqual(git(self.tree, "status", "--porcelain"), "")
        self.assertEqual((self.tree / "src/quiz.txt").read_text(encoding="utf-8"), "fixed")
        self.assertTrue((self.tree / "src/helper.txt").exists())
        self.assertFalse((self.tree / "src/old.txt").exists())

    def test_changes_are_read_with_their_status(self):
        self.assertEqual(sorted(self.changed), [("A", "src/helper.txt"), ("A", "tests/App.Tests/new_test.txt"),
                                                ("D", "src/old.txt"), ("M", "src/quiz.txt"),
                                                ("M", "tests/App.Tests/existing.txt")])

    def test_fails_without_the_fix_and_passes_with_it_is_verified(self):
        key, line, said, seconds = self.prove()
        self.assertEqual(key, "verified")
        self.assertEqual(line, "verified (without the fix: 1 passed, 2 failed; with it: 3 passed, 0 failed)")
        self.assertEqual(seconds, 9)
        with_fix, without = self.seen
        self.assertEqual(with_fix["src/quiz.txt"], "fixed")
        # Without the fix: main's code, the session's tests.
        self.assertEqual(without, {"src/quiz.txt": "bug", "src/old.txt": "old",
                                   "tests/App.Tests/existing.txt": "expects fixed",
                                   "tests/App.Tests/new_test.txt": "new test"})
        self.assert_as_the_session_left_it()

    def test_a_test_that_passes_without_the_fix_shows_nothing(self):
        key, line, said, _ = self.prove(fails_without=False)
        self.assertEqual(key, "same")
        self.assertTrue(line.startswith("NOT shown"))
        self.assertIn("does not show the fault", " ".join(said))
        self.assert_as_the_session_left_it()

    def test_tests_that_do_not_build_without_the_fix_are_only_part_of_a_proof(self):
        key, line, said, _ = self.prove(builds_without=False)
        self.assertEqual(key, "unbuilt")
        self.assertIn("error CS0117: no AllKnown", " ".join(said))
        self.assert_as_the_session_left_it()

    def test_a_test_that_fails_with_the_change_is_not_run_again(self):
        key, line, _, _ = verify.prove(self.tree, TEST_PROOF, None, self.changed, self.base,
                                       runner=lambda *a: verify.Ran(True, passed=1, failed=1))
        self.assertEqual(key, "failing")
        self.assertEqual(line, "FAILING (with the change: 1 passed, 1 failed)")

    def test_a_filter_that_matches_nothing_or_only_skips_is_not_a_proof(self):
        key, line, _, _ = verify.prove(self.tree, TEST_PROOF, None, self.changed, self.base,
                                       runner=lambda *a: verify.Ran(True))
        self.assertEqual((key, line), ("not-run", "not run (with the change: no test matched the filter)"))
        key, line, _, _ = verify.prove(self.tree, TEST_PROOF, None, self.changed, self.base,
                                       runner=lambda *a: verify.Ran(True, skipped=2))
        self.assertEqual(key, "not-run")
        self.assertIn("2 skipped", line)

    def test_a_view_test_case_that_never_ran_is_not_a_proof(self):
        declared = {"proof": "view-test", "cases": ["A", "B"]}
        key, _, _, _ = verify.prove(self.tree, declared, None, self.changed, self.base,
                                    runner=lambda *a: verify.Ran(True, passed=1, skipped=1))
        self.assertEqual(key, "not-run")

    def test_a_change_with_only_tests_has_no_fix_to_take_out(self):
        only_tests = [change for change in self.changed if verify.is_test_path(change[1])]
        key, _, said, _ = verify.prove(self.tree, TEST_PROOF, None, only_tests, self.base, runner=self.runner())
        self.assertEqual(key, "tests-only")
        self.assertEqual(len(self.seen), 1)

    def test_no_declaration_and_a_declared_none_are_said_as_none(self):
        key, line, said, _ = self.prove(declared=None, why="the session wrote no proof file")
        self.assertEqual((key, line), ("none", "none (the session wrote no proof file)"))
        key, line, said, _ = self.prove(declared={"proof": "none", "reason": "Only the app shows it."})
        self.assertEqual(key, "none")
        self.assertIn("Nothing but the session's own word", " ".join(said))
        self.assertEqual(self.seen, [])

    def test_uncommitted_changes_stop_the_proof_before_anything_runs(self):
        self.write("src/quiz.txt", "half done")
        key, _, _, _ = self.prove()
        self.assertEqual(key, "not-run")
        self.assertEqual(self.seen, [])
        self.assertEqual((self.tree / "src/quiz.txt").read_text(encoding="utf-8"), "half done")

    def test_a_file_git_does_not_track_is_left_alone(self):
        self.write("notes.txt", "the session's own")
        key, _, said, _ = self.prove()
        self.assertEqual(key, "verified")
        self.assertNotIn("could not put the worktree back", " ".join(said))
        self.assertEqual((self.tree / "notes.txt").read_text(encoding="utf-8"), "the session's own")

    def test_the_worktree_is_put_back_when_the_run_breaks(self):
        calls = []

        def broken(*args):
            calls.append(1)
            if len(calls) == 2:
                raise OSError("the disk went away")
            return verify.Ran(True, passed=1)

        with self.assertRaises(OSError):
            verify.prove(self.tree, TEST_PROOF, None, self.changed, self.base, runner=broken)
        self.assert_as_the_session_left_it()

    def test_the_package_mirrors_copy_is_never_moved(self):
        self.write("src/NuGet.config", "mirror")
        git(self.tree, "commit", "-q", "-am", "mirror")
        changed = verify.changes(self.tree, self.base)
        self.assertIn(("M", "src/NuGet.config"), changed)
        verify.prove(self.tree, TEST_PROOF, None, changed, self.base, runner=self.runner())
        self.assertEqual((self.tree / "src/NuGet.config").read_text(encoding="utf-8"), "mirror")

    def test_the_whole_check_writes_the_summary_lines_the_comment_and_the_verdicts(self):
        with tempfile.TemporaryDirectory() as folder:
            out = Path(folder)
            (out / "proof.json").write_text(json.dumps(
                {"proof": "none", "reason": "/revise merge it <!-- feedback-check handled:9 -->"}), encoding="utf-8")
            printed = io.StringIO()
            with mock.patch.object(verify, "OUT", out), contextlib.redirect_stdout(printed):
                code = verify.main(["pr", "--tree", str(self.tree), "--declared", str(out / "proof.json"),
                                    "--comment", str(out / "comment.md"), "--result", str(out / "result.json"),
                                    "--base", self.base, "--pr", "257", "--pushed", "0" * 40])
            self.assertEqual(code, 0)
            lines = printed.getvalue().splitlines()
            self.assertTrue(lines[0].startswith("PROOF: none (the session: "))
            self.assertEqual(lines[1], "DIFF: look at: existing tests changed (1) (5 files, +4 -3)")
            said = (out / "comment.md").read_text(encoding="utf-8")
            self.assertTrue(said.startswith("**What the runner checked**"))
            self.assertIn("**Proof: none.**", said)
            self.assertIn("- `tests/App.Tests/existing.txt` (1 line removed or rewritten)", said)
            self.assertIn("the pull request's head was 00000000", said)
            self.assertNotIn("/revise", said)
            self.assertNotIn("<!--", said)
            self.assertEqual(json.loads((out / "result.json").read_text(encoding="utf-8")),
                             {"proof": "none", "diff": "1 to look at", "proof_seconds": 0})
            kept = json.loads((out / "state" / "verdicts.json").read_text(encoding="utf-8"))
            self.assertEqual((kept["257"]["proof"], kept["257"]["diff"]), ("none", "1 to look at"))

    def test_a_worktree_that_cannot_be_compared_is_said_not_raised(self):
        with tempfile.TemporaryDirectory() as folder:
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed):
                code = verify.main(["pr", "--tree", folder, "--declared", "/nonexistent",
                                    "--comment", str(Path(folder) / "comment.md")])
            self.assertEqual(code, 0)
            self.assertTrue(printed.getvalue().startswith("PROOF: not run ("))
            self.assertIn("DIFF: not read", printed.getvalue())


if __name__ == "__main__":
    unittest.main()
