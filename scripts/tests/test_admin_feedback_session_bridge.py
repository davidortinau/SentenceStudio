"""Offline tests for the bridge's test step (scripts/session-bridge.sh test).

The bridge is a folder made here; its native build, dotnet, the managed test program and
swift are stand-ins that say what the real ones say. Nothing is built."""

import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "session-bridge.sh"

# The managed test program, as `dotnet build` leaves it: it says its count on one line.
MANAGED = """#!/bin/bash
echo "managed" >> "$STUB/ran.log"
if [ -f "$STUB/managed-fails" ]; then echo "FAIL: a view"; echo "Managed tests: 2/3 passed"; exit 1; fi
echo "Managed tests: 3/3 passed"
"""

# swift test, in XCTest's words: a line for each test, the count under "All tests", and after
# it the footer of Swift Testing, which counts tests of its own kind and so says 0.
SWIFT = """#!/bin/bash
echo "swift $* in $PWD" >> "$STUB/ran.log"
[ ! -f "$STUB/swift-does-not-build" ] || {
  printf '%s/native/MicrosoftSwiftUIShim/Tests/MicrosoftSwiftUIShimTests/FixedWidthTests.swift:12:9: \033[1;31merror: \033[1;39mcannot find %s in scope\033[0;0m\n' "$PWD" "'widthOf'"
  echo "error: fatalError"; exit 1; }
# A test that crashes: the suites before it said their counts, and "All tests" never does.
[ ! -f "$STUB/swift-crashes" ] || {
  echo "Test Suite 'ActivityBridgeTests' passed at 2026-11-25 19:03:31.100."
  echo "	 Executed 2 tests, with 0 failures (0 unexpected) in 0.004 (0.004) seconds"
  echo "Test Case '-[MicrosoftSwiftUIShimTests.FixedWidthTests testARowKeepsItsWidth]' started."
  echo "error: Exited with unexpected signal code 11"; exit 1; }
[ ! -f "$STUB/swift-runs-nothing" ] || {
  echo "Test Suite 'All tests' passed at 2026-11-25 19:03:31.896."
  echo "	 Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.000) seconds"; exit 0; }
echo "Test Suite 'FixedWidthTests' started at 2026-11-25 19:03:31.890."
echo "Test Case '-[MicrosoftSwiftUIShimTests.FixedWidthTests testARowKeepsItsWidth]' started."
if [ -f "$STUB/swift-fails" ]; then
  echo "$PWD/native/MicrosoftSwiftUIShim/Tests/MicrosoftSwiftUIShimTests/FixedWidthTests.swift:20: error: -[MicrosoftSwiftUIShimTests.FixedWidthTests testARowKeepsItsWidth] : XCTAssertEqual failed: (\\"200.0\\") is not equal to (\\"100.0\\")"
  echo "$PWD/native/MicrosoftSwiftUIShim/Tests/MicrosoftSwiftUIShimTests/FixedWidthTests.swift:21: error: -[MicrosoftSwiftUIShimTests.FixedWidthTests testARowKeepsItsWidth] : XCTAssertEqual failed: (\\"44.0\\") is not equal to (\\"40.0\\")"
  echo "Test Case '-[MicrosoftSwiftUIShimTests.FixedWidthTests testARowKeepsItsWidth]' failed (0.004 seconds)."
  echo "Test Suite 'FixedWidthTests' failed at 2026-11-25 19:03:31.893."
  echo "	 Executed 1 test, with 2 failures (0 unexpected) in 0.004 (0.004) seconds"
  echo "Test Suite 'All tests' failed at 2026-11-25 19:03:31.896."
  echo "	 Executed 4 tests, with 2 failures (0 unexpected) in 2.638 (2.660) seconds"
  status=1
else
  echo "Test Case '-[MicrosoftSwiftUIShimTests.FixedWidthTests testARowKeepsItsWidth]' passed (0.004 seconds)."
  echo "Test Suite 'FixedWidthTests' passed at 2026-11-25 19:03:31.893."
  echo "	 Executed 1 test, with 0 failures (0 unexpected) in 0.004 (0.004) seconds"
  echo "Test Suite 'All tests' passed at 2026-11-25 19:03:31.896."
  echo "	 Executed 4 tests, with 0 failures (0 unexpected) in 2.638 (2.660) seconds"
  status=0
fi
echo "Test run with 0 tests in 0 suites passed after 0.001 seconds."
exit "$status"
"""

QUIET = "#!/bin/bash\nexit 0\n"


class SessionBridgeTestStepTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.dir = Path(folder.name).resolve()
        self.bridge = self.dir / "bridge"
        self.stub = self.dir / "stub"
        app = self.bridge / "tests/Microsoft.SwiftUI.Tests/bin/Debug/Microsoft.SwiftUI.Tests.app/Contents/MacOS"
        for made in (self.bridge / "src/Microsoft.SwiftUI", self.bridge / "scripts", app,
                     self.stub / "bin", self.dir / "logs"):
            made.mkdir(parents=True)
        (self.bridge / "src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj").write_text(
            "<Project><PropertyGroup><Version>0.1.0-preview.7</Version></PropertyGroup></Project>\n", encoding="utf-8")
        for path, text in ((self.bridge / "scripts/build-xcframework.sh", QUIET),
                           (app / "Microsoft.SwiftUI.Tests", MANAGED),
                           (self.stub / "bin/dotnet", QUIET), (self.stub / "bin/swift", SWIFT)):
            path.write_text(text, encoding="utf-8")
            path.chmod(path.stat().st_mode | stat.S_IXUSR)
        subprocess.run(["git", "-C", str(self.bridge), "init", "-q"], check=True, capture_output=True,
                       env={"PATH": "/usr/bin:/bin", "HOME": str(self.dir), "GIT_CONFIG_GLOBAL": os.devnull,
                            "GIT_CONFIG_NOSYSTEM": "1"})

    def mark(self, name):
        (self.stub / name).write_text("", encoding="utf-8")

    def run_step(self):
        env = {
            "HOME": str(self.dir), "PATH": f"{self.stub / 'bin'}:/usr/bin:/bin:/usr/sbin:/sbin",
            "SESSION_BRIDGE": str(self.bridge), "SESSION_BRIDGE_LOGS": str(self.dir / "logs"),
            "STUB": str(self.stub), "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
        }
        return subprocess.run(["/bin/bash", str(SCRIPT), "test"], capture_output=True, text=True, env=env, timeout=120)

    def ran(self):
        try:
            return (self.stub / "ran.log").read_text(encoding="utf-8").splitlines()
        except OSError:
            return []

    def test_the_swift_tests_run_after_the_managed_ones_and_their_count_is_said(self):
        done = self.run_step()
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(), ["Managed tests: 3/3 passed", "Swift tests: 4/4 passed"])
        self.assertEqual(self.ran(), ["managed", f"swift test --package-path native/MicrosoftSwiftUIShim in {self.bridge}"])

    def test_a_swift_test_that_fails_stops_the_step_and_is_named(self):
        self.mark("swift-fails")
        done = self.run_step()
        self.assertEqual(done.returncode, 1, done.stdout)
        # One test failed, twice: the count is of tests.
        self.assertIn("Swift tests: 3/4 passed", done.stdout.splitlines())
        self.assertIn("testARowKeepsItsWidth] : XCTAssertEqual failed", done.stdout)
        # The path is said from the bridge's folder, not from this machine's root.
        self.assertIn("\nnative/MicrosoftSwiftUIShim/Tests/MicrosoftSwiftUIShimTests/FixedWidthTests.swift:20: error:", done.stdout)
        self.assertIn("STOP: the bridge's Swift tests did not all pass.", done.stderr)
        self.assertIn("XCTAssertEqual failed", (self.dir / "logs/session-bridge-test.log").read_text(encoding="utf-8"))

    def test_swift_tests_that_do_not_build_stop_the_step_with_the_error(self):
        self.mark("swift-does-not-build")
        done = self.run_step()
        self.assertEqual(done.returncode, 1, done.stdout)
        # As the compiler says it, less its colours.
        self.assertIn("FixedWidthTests.swift:12:9: error: cannot find 'widthOf' in scope\n", done.stdout)
        self.assertNotIn("Swift tests:", done.stdout)
        self.assertIn("STOP: the bridge's Swift tests did not run to their end", done.stderr)

    def test_a_swift_test_that_crashes_stops_the_step_and_no_count_is_said(self):
        # The last count before the crash is one suite's: it is not the run's.
        self.mark("swift-crashes")
        done = self.run_step()
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertIn("error: Exited with unexpected signal code 11", done.stdout)
        self.assertNotIn("Swift tests:", done.stdout)
        self.assertIn("STOP: the bridge's Swift tests did not run to their end", done.stderr)

    def test_a_swift_run_of_no_test_is_not_a_pass(self):
        self.mark("swift-runs-nothing")
        done = self.run_step()
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertIn("STOP: the bridge's Swift tests ran no test", done.stderr)

    def test_a_managed_test_that_fails_still_stops_the_step_first(self):
        self.mark("managed-fails")
        done = self.run_step()
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertIn("Managed tests: 2/3 passed", done.stdout)
        self.assertIn("STOP: the bridge's tests did not all pass.", done.stderr)
        self.assertEqual(self.ran(), ["managed"])


if __name__ == "__main__":
    unittest.main()
