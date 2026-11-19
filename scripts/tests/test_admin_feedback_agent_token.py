"""Offline tests for the fix sessions' GitHub App token (scripts/admin-feedback-agent-token.py)."""

import base64
import importlib.util
import io
import json
import os
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "admin-feedback-agent-token.py"
SPEC = importlib.util.spec_from_file_location("admin_feedback_agent_token", SCRIPT)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

PEM = b"-----BEGIN RSA PRIVATE KEY-----\nnot-a-real-key\n-----END RSA PRIVATE KEY-----\n"


def done(code=0, out=""):
    return mock.Mock(returncode=code, stdout=out)


def in_minutes(minutes):
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + minutes * 60))


class TokenTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.cache = os.path.join(folder.name, "state", "agent-token.json")
        self.identity = os.path.join(folder.name, "state", "agent-identity.json")
        for name, value in (("CACHE", self.cache), ("IDENTITY", self.identity)):
            patch = mock.patch.object(agent, name, value)
            patch.start()
            self.addCleanup(patch.stop)

    def keep(self, token, expires_at):
        os.makedirs(os.path.dirname(self.cache), exist_ok=True)
        with open(self.cache, "w", encoding="utf-8") as out:
            json.dump({"token": token, "expires_at": expires_at}, out)

    def test_a_token_with_time_left_is_used_again(self):
        self.keep("ghs_kept", in_minutes(40))
        with mock.patch.object(agent, "new_token") as fresh:
            self.assertEqual(agent.token(), "ghs_kept")
        fresh.assert_not_called()

    def test_a_token_that_would_not_outlive_the_next_renewal_is_replaced_and_kept_private(self):
        self.keep("ghs_old", in_minutes(25))
        with mock.patch.object(agent, "new_token",
                               return_value=({"id": 1}, {"token": "ghs_new", "expires_at": in_minutes(60)})):
            self.assertEqual(agent.token(), "ghs_new")
        self.assertEqual(os.stat(self.cache).st_mode & 0o777, 0o600)
        with open(self.cache, encoding="utf-8") as source:
            self.assertEqual(json.load(source)["token"], "ghs_new")

    def test_a_damaged_or_strange_cache_asks_for_a_new_token(self):
        os.makedirs(os.path.dirname(self.cache), exist_ok=True)
        for content in ("{}", "[]", '{"token": "ghs_x", "expires_at": "2026-11-19T04:32:41.5Z"}', "not json"):
            with open(self.cache, "w", encoding="utf-8") as out:
                out.write(content)
            self.assertIsNone(agent.cached(), content)

    def test_the_time_left_is_read_in_utc_whatever_the_local_zone(self):
        self.keep("ghs_kept", in_minutes(40))
        old = os.environ.get("TZ")
        try:
            for zone in ("America/Los_Angeles", "Europe/London", "UTC"):
                os.environ["TZ"] = zone
                time.tzset()
                self.assertEqual(agent.cached(), "ghs_kept", zone)
        finally:
            if old is None:
                os.environ.pop("TZ", None)
            else:
                os.environ["TZ"] = old
            time.tzset()

    def test_the_token_is_asked_for_this_repository_only(self):
        calls = []

        def call(method, path, bearer, body=None):
            calls.append((method, path, body))
            if path.endswith("/installation"):
                return {"id": 42, "account": {"login": "davidortinau"}}
            return {"token": "ghs_new", "expires_at": in_minutes(60)}

        with mock.patch.object(agent, "app_jwt", return_value="jwt"), mock.patch.object(agent, "call", call):
            agent.new_token()
        self.assertEqual(calls[-1], ("POST", "/app/installations/42/access_tokens",
                                     {"repositories": ["SentenceStudio"]}))

    def test_check_never_prints_the_token(self):
        app = {"slug": "ss-fix-agent", "id": 5826863, "owner": {"login": "davidortinau"}}
        with mock.patch.object(agent, "app_jwt", return_value="jwt"), \
                mock.patch.object(agent, "identity", return_value=(app, "ss-fix-agent[bot]", "1+ss-fix-agent[bot]@users.noreply.github.com")), \
                mock.patch.object(agent, "new_token", return_value=(
                    {"id": 42, "account": {"login": "davidortinau"}},
                    {"token": "ghs_secret", "expires_at": in_minutes(60), "permissions": {"contents": "write"}})), \
                mock.patch("sys.stdout", new_callable=io.StringIO) as out:
            self.assertEqual(agent.main(["check"]), 0)
        self.assertNotIn("ghs_secret", out.getvalue())
        self.assertIn('"commit_name": "ss-fix-agent[bot]"', out.getvalue())


    def test_the_bots_identity_is_looked_up_once_and_kept_private(self):
        who = ({"slug": "ss-fix-agent"}, "ss-fix-agent[bot]", "1+ss-fix-agent[bot]@users.noreply.github.com")
        with mock.patch.object(agent, "identity", return_value=who) as asked:
            self.assertEqual(agent.kept_identity(), who[1:])
            self.assertEqual(agent.kept_identity(), who[1:])
        asked.assert_called_once()
        self.assertEqual(os.stat(self.identity).st_mode & 0o777, 0o600)
        self.assertEqual(os.stat(os.path.dirname(self.identity)).st_mode & 0o777, 0o700)

    def test_a_damaged_identity_file_is_looked_up_again(self):
        os.makedirs(os.path.dirname(self.identity), exist_ok=True)
        who = ({"slug": "ss-fix-agent"}, "ss-fix-agent[bot]", "1+ss-fix-agent[bot]@users.noreply.github.com")
        for content in ("not json", "[]", '{"login": "davidortinau", "email": "dave@example.com"}'):
            with open(self.identity, "w", encoding="utf-8") as out:
                out.write(content)
            with mock.patch.object(agent, "identity", return_value=who):
                self.assertEqual(agent.kept_identity(), who[1:], content)

    def test_a_failed_write_leaves_no_temporary_file(self):
        with mock.patch.object(agent.json, "dump", side_effect=ValueError("boom")):
            with self.assertRaises(ValueError):
                agent.keep({"token": "ghs_x", "expires_at": in_minutes(60)})
        self.assertEqual(os.listdir(os.path.dirname(self.cache)), [])


class KeyTests(unittest.TestCase):
    def test_a_missing_keychain_item_stops_and_says_so(self):
        with mock.patch.object(agent.subprocess, "run", return_value=done(44)), \
                mock.patch("sys.stderr", new_callable=io.StringIO) as err, \
                mock.patch.object(agent, "CACHE", "/nonexistent/agent-token.json"):
            self.assertEqual(agent.main(["token"]), 3)
        self.assertIn("the keychain has no item", err.getvalue())

    def test_something_that_is_not_a_key_is_refused(self):
        with mock.patch.object(agent.subprocess, "run", return_value=done(0, base64.b64encode(b"hello").decode())):
            with self.assertRaises(agent.TokenError):
                agent.private_key()

    def test_the_key_file_for_openssl_is_private_and_gone_afterwards(self):
        seen = {}

        def run(command, **kwargs):
            if command[0] == "security":
                return done(0, base64.b64encode(PEM).decode())
            path = command[command.index("-sign") + 1]
            seen["mode"] = os.stat(path).st_mode & 0o777
            seen["path"] = path
            return mock.Mock(returncode=0, stdout=b"signature")

        with mock.patch.object(agent.subprocess, "run", run):
            jwt = agent.app_jwt(now=1_000_000)
        self.assertEqual(seen["mode"], 0o600)
        self.assertFalse(os.path.exists(seen["path"]))
        header, claims, _ = jwt.split(".")
        payload = json.loads(base64.urlsafe_b64decode(claims + "=="))
        self.assertEqual(payload, {"iat": 999_940, "exp": 1_000_540, "iss": "5826863"})


if __name__ == "__main__":
    unittest.main()
