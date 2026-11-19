#!/usr/bin/env python3
"""The fix sessions' own GitHub identity: the GitHub App sentence-studio-fix-agent.

scripts/admin-feedback-check.sh runs every fix session as this app instead of the Captain's
login (docs/admin-feedback-check.md, "Whose login a session uses"). The session's gh and git
ask this script for a token each time they need one; a token lasts an hour and is kept until it
has less than ten minutes left.

  admin-feedback-agent-token.py token
      Prints a token for the app's installation on the repository, and nothing else.
  admin-feedback-agent-token.py check
      Prints the app, its installation, the token's permissions and repositories, and the bot's
      commit identity, as JSON. Never a token or a key.
  admin-feedback-agent-token.py identity
      Prints two lines: the bot's name and its commit email.

The app's private key is kept in the login keychain (service KEY_SERVICE, account KEY_ACCOUNT,
base64 of the .pem), where the Captain put it himself. It is read only to sign the request for a
token; it is never written anywhere but a file that is deleted at once, readable only by this
user. A session runs as this macOS user, so it could read the key itself: this narrows what a
session's GitHub actions look like and can do, it is not a boundary.

Exit 0 on success, 3 when the keychain, openssl or GitHub did not answer as expected.
"""
import base64
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
CACHE = os.path.join(os.environ.get("FEEDBACK_CHECK_DIR") or os.path.expanduser("~/sstudio-feedback-check"),
                     "state", "agent-token.json")
MIN_LEFT = 600
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


def new_token():
    jwt = app_jwt()
    found = installation(jwt)
    # Only this repository, whatever else the app may one day be installed on.
    token = call("POST", f"/app/installations/{found['id']}/access_tokens", jwt,
                 {"repositories": [REPO.split("/")[1]]})
    return found, token


def cached():
    try:
        with open(CACHE, encoding="utf-8") as source:
            saved = json.load(source)
    except (OSError, ValueError):
        return None
    left = time.mktime(time.strptime(saved["expires_at"], "%Y-%m-%dT%H:%M:%SZ")) - time.mktime(time.gmtime())
    return saved["token"] if left > MIN_LEFT else None


def token():
    found = cached()
    if found:
        return found
    _, fresh = new_token()
    os.makedirs(os.path.dirname(CACHE), mode=0o700, exist_ok=True)
    temporary = CACHE + ".tmp"
    with open(os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as out:
        json.dump({"token": fresh["token"], "expires_at": fresh["expires_at"]}, out)
    os.replace(temporary, CACHE)
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


def main(args):
    mode = args[0] if args else ""
    try:
        if mode == "token":
            print(token())
            return 0
        if mode == "identity":
            _, login, email = identity()
            print(login)
            print(email)
            return 0
        if mode == "check":
            jwt = app_jwt()
            app, login, email = identity(jwt)
            found, fresh = new_token()
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
