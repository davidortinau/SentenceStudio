#!/usr/bin/env python3
"""The fix sessions' own GitHub identity: the GitHub App ss-fix-agent (ss-fix-agent[bot]).

scripts/admin-feedback-check.sh runs every fix session as this app instead of the Captain's
login (docs/admin-feedback-check.md, "Whose login a session uses"). The session's gh and git
read a token the runner gets from this script and writes again every 20 minutes. A token lasts
an hour; the one kept in CACHE (readable only by this user) is used again while it has more
than MIN_LEFT left, so a token handed out always outlives the next renewal.

  admin-feedback-agent-token.py token
      Prints a token for the app's installation on the repository, and nothing else.
  admin-feedback-agent-token.py check
      Prints the app, its installation, the token's permissions and repositories, and the bot's
      commit identity, as JSON. Never a token or a key.
  admin-feedback-agent-token.py identity
      Prints two lines: the bot's name and its commit email. They never change, so they are
      looked up once and kept in IDENTITY; check looks them up again.

The app's private key is kept in the login keychain (service KEY_SERVICE, account KEY_ACCOUNT,
base64 of the .pem), where the Captain put it himself. It is read only to sign the request for a
token; it is never written anywhere but a file that is deleted at once, readable only by this
user. A fix session runs as a macOS user of its own (docs/admin-feedback-check.md, "The
sessions' macOS user") and cannot read this user's keychain or the cache.

Exit 0 on success, 3 when the keychain, openssl or GitHub did not answer as expected.
"""
import base64
import calendar
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

APP_ID = "5826863"
REPO = "davidortinau/SentenceStudio"
KEY_SERVICE = "sstudio-fix-agent-key"
KEY_ACCOUNT = "sstudio-fix-agent"
API = "https://api.github.com"
STATE = os.path.join(os.environ.get("FEEDBACK_CHECK_DIR") or os.path.expanduser("~/sstudio-feedback-check"),
                     "state")
CACHE = os.path.join(STATE, "agent-token.json")
IDENTITY = os.path.join(STATE, "agent-identity.json")
# More than the runner's 20-minute renewal, with ten minutes to spare.
MIN_LEFT = 1800
SECONDS = 30


class TokenError(Exception):
    pass


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def private_key():
    done = subprocess.run(["security", "find-generic-password", "-a", KEY_ACCOUNT, "-s", KEY_SERVICE, "-w"],
                          capture_output=True, text=True, timeout=SECONDS)
    if done.returncode != 0 or not done.stdout.strip():
        raise TokenError(f"the keychain has no item {KEY_SERVICE} for {KEY_ACCOUNT}")
    try:
        pem = base64.b64decode(done.stdout.strip(), validate=True)
    except ValueError:
        raise TokenError("the keychain item is not base64")
    if b"PRIVATE KEY" not in pem:
        raise TokenError("the keychain item is not a private key")
    return pem


def app_jwt(now=None):
    now = int(now or time.time())
    header = b64url(json.dumps({"alg": "RS256", "typ": "JWT"}).encode())
    claims = b64url(json.dumps({"iat": now - 60, "exp": now + 540, "iss": APP_ID}).encode())
    signing = f"{header}.{claims}".encode()
    with tempfile.TemporaryDirectory() as folder:
        os.chmod(folder, 0o700)
        path = os.path.join(folder, "key.pem")
        with open(os.open(path, os.O_WRONLY | os.O_CREAT, 0o600), "wb") as key:
            key.write(private_key())
        done = subprocess.run(["openssl", "dgst", "-sha256", "-sign", path], input=signing,
                              capture_output=True, timeout=SECONDS)
    if done.returncode != 0 or not done.stdout:
        raise TokenError("openssl could not sign with the key")
    return f"{header}.{claims}.{b64url(done.stdout)}"


def call(method, path, bearer, body=None):
    request = urllib.request.Request(
        API + path, method=method, data=json.dumps(body).encode() if body is not None else None,
        headers={"Authorization": f"Bearer {bearer}", "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "sentence-studio-feedback-check"})
    try:
        with urllib.request.urlopen(request, timeout=SECONDS) as answer:
            return json.load(answer)
    except urllib.error.HTTPError as error:
        raise TokenError(f"GitHub answered {error.code} to {method} {path.split('?')[0]}")
    except (urllib.error.URLError, TimeoutError, ValueError) as error:
        raise TokenError(f"GitHub did not answer {method} {path.split('?')[0]}: {type(error).__name__}")


def installation(jwt):
    owner, name = REPO.split("/")
    return call("GET", f"/repos/{owner}/{name}/installation", jwt)


def new_token(jwt=None):
    jwt = jwt or app_jwt()
    found = installation(jwt)
    # Only this repository, whatever else the app may one day be installed on.
    token = call("POST", f"/app/installations/{found['id']}/access_tokens", jwt,
                 {"repositories": [REPO.split("/")[1]]})
    return found, token


def cached():
    """The kept token while it has more than MIN_LEFT left; None for anything else, a file
    that is missing, damaged or of another shape included, so that a new one is asked for."""
    try:
        with open(CACHE, encoding="utf-8") as source:
            saved = json.load(source)
        left = calendar.timegm(time.strptime(saved["expires_at"], "%Y-%m-%dT%H:%M:%SZ")) - time.time()
        return saved["token"] if left > MIN_LEFT and saved["token"] else None
    except (OSError, ValueError, KeyError, TypeError):
        return None


def write_private(path, content):
    """content as JSON in path, readable only by this user, replaced whole: each writer has its
    own temporary file, so two at once cannot mix their contents."""
    folder = os.path.dirname(path)
    os.makedirs(folder, mode=0o700, exist_ok=True)
    os.chmod(folder, 0o700)
    handle, temporary = tempfile.mkstemp(dir=folder, prefix=".agent-", suffix=".tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as out:
            json.dump(content, out)
        os.replace(temporary, path)
    except BaseException:
        os.unlink(temporary)
        raise


def keep(fresh):
    write_private(CACHE, {"token": fresh["token"], "expires_at": fresh["expires_at"]})


def token():
    found = cached()
    if found:
        return found
    _, fresh = new_token()
    keep(fresh)
    return fresh["token"]


def identity(jwt=None):
    app = call("GET", "/app", jwt or app_jwt())
    login = f"{app['slug']}[bot]"
    request = urllib.request.Request(f"{API}/users/{urllib.request.quote(login)}",
                                     headers={"Accept": "application/vnd.github+json",
                                              "User-Agent": "sentence-studio-feedback-check"})
    try:
        with urllib.request.urlopen(request, timeout=SECONDS) as answer:
            user = json.load(answer)
    except (urllib.error.URLError, TimeoutError, ValueError) as error:
        raise TokenError(f"GitHub did not answer for the bot user: {type(error).__name__}")
    return app, login, f"{user['id']}+{login}@users.noreply.github.com"


def kept_identity():
    try:
        with open(IDENTITY, encoding="utf-8") as source:
            saved = json.load(source)
        if saved["login"].endswith("[bot]") and saved["email"].endswith("@users.noreply.github.com"):
            return saved["login"], saved["email"]
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        pass
    _, login, email = identity()
    write_private(IDENTITY, {"login": login, "email": email})
    return login, email


def main(args):
    mode = args[0] if args else ""
    try:
        if mode == "token":
            print(token())
            return 0
        if mode == "identity":
            login, email = kept_identity()
            print(login)
            print(email)
            return 0
        if mode == "check":
            jwt = app_jwt()
            app, login, email = identity(jwt)
            found, fresh = new_token(jwt)
            keep(fresh)
            write_private(IDENTITY, {"login": login, "email": email})
            print(json.dumps({
                "app": app["slug"], "app_id": app["id"], "owner": app["owner"]["login"],
                "installation": found["id"], "installed_on": found["account"]["login"],
                "repository_selection": fresh.get("repository_selection"),
                "repositories": [r["full_name"] for r in fresh.get("repositories", [])],
                "permissions": fresh.get("permissions"), "expires_at": fresh["expires_at"],
                "commit_name": login, "commit_email": email,
            }, indent=2, sort_keys=True))
            return 0
    except (TokenError, subprocess.TimeoutExpired, OSError, KeyError) as error:
        print(f"STOP: no token for the fix sessions: {error}", file=sys.stderr)
        return 3
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
