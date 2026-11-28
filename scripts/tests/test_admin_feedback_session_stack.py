"""Offline tests for the stack a fix session starts to run the app (scripts/session-stack.sh).
aspire, docker, dotnet and ls are stand-ins on PATH; the script itself is the real one."""

import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "session-stack.sh"

ASPIRE = """#!/bin/bash
echo "$*" >> "$FAKES/aspire.calls"
case "$1" in
  wait)
    if [ -e "$FAKES/api-does-not-start" ]; then
      echo "Timed out waiting for resource '$2' to be healthy." >&2
      echo "See AppHost logs at $FAKES/cli_1_detach-child_1.log" >&2
      exit 17
    fi ;;
  logs) echo "$2: a line of its own output" ;;
  describe)
    echo '{"resources": [{"name": "api-abc", "urls": [{"url": "http://localhost:5101"}]},
                         {"name": "webapp-abc", "urls": [{"url": "http://localhost:5102"}]}]}' ;;
esac
exit 0
"""
QUIET = "#!/bin/bash\necho \"$(basename \"$0\") $*\" >> \"$FAKES/other.calls\"\nexit 0\n"
# /home as it is when the automounter does not answer: the open never returns.
LS = "#!/bin/bash\n[ -e \"$FAKES/home-does-not-open\" ] && exec sleep 60\nexit 0\n"


class SessionStackTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.fakes = Path(folder.name) / "fakes"
        self.tmp = Path(folder.name) / "tmp"
        self.fakes.mkdir()
        self.tmp.mkdir()
        for name, text in (("aspire", ASPIRE), ("docker", QUIET), ("dotnet", QUIET), ("ls", LS)):
            path = self.fakes / name
            path.write_text(text, encoding="utf-8")
            path.chmod(path.stat().st_mode | stat.S_IXUSR)
        (self.fakes / "cli_1_detach-child_1.log").write_text("the AppHost's log\n", encoding="utf-8")
        self.addCleanup(self.run_script, "down")

    def run_script(self, *arguments):
        environment = dict(os.environ, FAKES=str(self.fakes), TMPDIR=str(self.tmp),
                           PATH=f"{self.fakes}{os.pathsep}{os.environ['PATH']}")
        environment.pop("DOCKER_HOST", None)
        return subprocess.run(["/bin/bash", str(SCRIPT), *arguments], env=environment,
                              capture_output=True, text=True, timeout=120)

    def calls(self, name="aspire.calls"):
        path = self.fakes / name
        return path.read_text(encoding="utf-8").splitlines() if path.exists() else []

    def test_each_wait_is_given_the_limit_the_message_names(self):
        result = self.run_script("up", "--no-build")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.split(), ["API=http://localhost:5101", "WEBAPP=http://localhost:5102"])
        waits = [call for call in self.calls() if call.startswith("wait ")]
        self.assertEqual(len(waits), 2)
        for call in waits:
            # Without it aspire gives up after 120 s, and the script said "300 s".
            self.assertIn("--timeout 300", call)

    def test_an_api_that_does_not_start_leaves_its_own_output_where_down_keeps_it(self):
        (self.fakes / "api-does-not-start").touch()
        result = self.run_script("up", "--no-build")
        self.assertEqual(result.returncode, 1)
        self.assertIn("STOP: api was not ready in 300 s", result.stderr)
        named = [word for word in result.stderr.split() if word.endswith("/api.log")]
        self.assertEqual(len(named), 1, result.stderr)
        kept = Path(named[0])
        self.assertEqual(self.run_script("down").returncode, 0)
        self.assertIn("api: a line of its own output", kept.read_text(encoding="utf-8"))
        self.assertEqual((kept.parent / "apphost.log").read_text(encoding="utf-8"), "the AppHost's log\n")
        self.assertTrue((kept.parent / "db.log").exists())

    def test_a_home_folder_that_does_not_open_stops_it_before_anything_is_started(self):
        (self.fakes / "home-does-not-open").touch()
        result = self.run_script("up")
        self.assertEqual(result.returncode, 1)
        self.assertIn("STOP: /home does not open on this Mac", result.stderr)
        self.assertEqual(self.calls(), [])
        self.assertEqual(self.calls("other.calls"), [])
        # Nothing is recorded, so the next up does not ask for a down first.
        self.assertEqual(list(self.tmp.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
