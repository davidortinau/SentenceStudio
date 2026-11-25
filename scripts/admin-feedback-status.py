#!/usr/bin/env python3
"""The feedback check's status issue on GitHub, and how many fix sessions ran today.

scripts/admin-feedback-check.sh keeps one pinned issue (labelled STATUS_LABEL) whose body says
what the check is doing: the session running now, the queue, the pull requests (with their
checks and what the runner itself found of the proof and the diff) and issues waiting for the
Captain, and the last passes. It is rewritten only when what it says changes.
The repository is public, so the body holds numbers, results and times, never what a learner
wrote or a run's summary.

  admin-feedback-status.py update [--cap N] [--windows FILE] [--handled FILE] [--note TEXT] [--print]
      Rewrites the status issue from what is on GitHub and in the check's folder, creating and
      pinning it first if there is none. --note is one line said at the top (a cap). With
      --print, prints the body and changes nothing.
  admin-feedback-status.py today
      The number of fix sessions (first passes and follow-ups) that started today, UTC.

The check's folder is FEEDBACK_CHECK_DIR, ~/sstudio-feedback-check by default. Exit 0 on
success, 3 when GitHub could not be read or written.
"""
import contextlib
import fcntl
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent


def sibling(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), HERE / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


pr = sibling("admin-feedback-pr")
verify = sibling("admin-feedback-verify")

OUT = Path(os.environ.get("FEEDBACK_CHECK_DIR") or os.path.expanduser("~/sstudio-feedback-check"))
STATUS_LABEL = "feedback-status"
TITLE = "Feedback check: status"
FIX_STAGES = ("fix", "fix follow-up")
PASSES_SHOWN = 10


def now():
    return time.gmtime()


def utc(at=None):
    return time.strftime("%Y-%m-%d %H:%M UTC", at or now())


def stamp_time(stamp):
    """A run's stamp (20261119T220707Z) as a struct_time, or None."""
    try:
        return time.strptime(stamp, "%Y%m%dT%H%M%SZ")
    except ValueError:
        return None


def usage(path=None):
    path = path or OUT / "usage.jsonl"
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    found = []
    for line in lines:
        try:
            record = json.loads(line)
        except ValueError:
            continue
        if isinstance(record, dict):
            found.append(record)
    return found


def sessions_today(records, today=None):
    today = today or time.strftime("%Y-%m-%d", now())
    return sum(1 for r in records if r.get("stage") in FIX_STAGES and str(r.get("at", "")).startswith(today))


def passes(history, records, shown=PASSES_SHOWN):
    """The last passes from history.log, newest first, each with how long its sessions took."""
    try:
        lines = history.read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    seconds = {}
    for r in records:
        seconds[r.get("at")] = seconds.get(r.get("at"), 0) + int(r.get("wall_seconds") or 0)
    rows = []
    for line in reversed(lines[-shown:]):
        stamp, _, said = line.partition("  ")
        at = stamp_time(stamp.strip())
        if at is None:
            continue
        key = time.strftime("%Y-%m-%dT%H:%M:%SZ", at)
        took = seconds.get(key)
        took = '' if took is None else f', {round(took / 60)} min' if took >= 60 else ', under a minute'
        rows.append(f"- {utc(at)}{took}: {said.strip() or 'no result line'}")
    return rows


def in_flight_number(path=None):
    """The issue or pull request the running session is on, or None."""
    path = path or OUT / "state" / "session-in-flight"
    try:
        parts = path.read_text(encoding="utf-8").split()
        return int(parts[2])
    except (OSError, ValueError, IndexError):
        return None


def in_flight(path=None):
    """The session running now, said in a line, or None. It says when the session started, not
    how long so far: the status is rewritten only when what it says changes."""
    path = path or OUT / "state" / "session-in-flight"
    try:
        parts = path.read_text(encoding="utf-8").split()
        started = path.stat().st_mtime
    except OSError:
        return None
    since = time.gmtime(started)
    if len(parts) >= 3 and parts[0] == "issue":
        what = f"issue #{parts[2]}"
    elif len(parts) >= 3 and parts[0] == "follow-up":
        what = f"a follow-up pass on pull request #{parts[2]}"
    else:
        return None
    return f"Working on {what} since {utc(since)}. It aims to finish within 15 minutes and is stopped at 30."


def render(note, running, queue, prs, waiting_on_you, recent, today, cap):
    lines = [f"_Kept by the feedback check on Lilliput (docs/admin-feedback-check.md). Last changed {utc()}._", ""]
    if note:
        lines += [f"**{note}**", ""]
    lines += ["## Now", "", running or "Idle: it looks for work every minute and runs passes back to back while there is some.",
              "", f"Fix sessions today: {today} of {cap}.", "", "## Queue", ""]
    lines += queue or ["Nothing waiting."]
    lines += ["", "## Pull requests waiting for you", ""]
    lines += prs or ["None."]
    lines += ["", "## Issues waiting on you", "",
              "Worked once and left open without a pull request: diagnosed, stuck or failed. "
              "Remove the label `feedback-attempted` to have one worked again.", ""]
    lines += waiting_on_you or ["None."]
    lines += ["", f"## Last {PASSES_SHOWN} passes", ""]
    lines += recent or ["None yet."]
    return "\n".join(lines) + "\n"


def checked(number, head, path=None):
    """What the runner's own checks said about a pull request (scripts/admin-feedback-verify.py),
    or nothing for one it never checked. head is the start of the pull request's head now: a
    verdict on an earlier commit is not shown as this one's."""
    path = path or OUT / "state" / "verdicts.json"
    try:
        found = json.loads(path.read_text(encoding="utf-8")).get(str(number))
        proof = verify.PROOF_WORDS[found["proof"]]
        diff = "nothing to look at" if found["diff"] == "clean" else str(found["diff"])[:40]
        on = str(found.get("head") or "")
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return ""
    if not head or not on.startswith(head):
        return ". The runner has not checked the proof or the diff of this head"
    return f". Proof {proof}; diff: {diff}"


def head_in(line):
    """The head a CHECKS line is about ("... at 965e0043: ..."), or nothing."""
    found = re.search(r" at ([0-9a-f]{8})\b", line)
    return found.group(1) if found else ""


def fingerprint(body):
    """The body without its timestamp line, so an unchanged status is not rewritten."""
    return hashlib.sha256("\n".join(body.splitlines()[1:]).encode()).hexdigest()


def gather(windows, handled):
    own = pr.own_pull_requests()
    queue = []
    running = in_flight_number()
    for found in pr.pending(handled, every=True):
        if found["pr"] != running:
            queue.append(f"- `/revise` on #{found['pr']} (issue #{found['issue']})")
    for number, source in pr.waiting_issues(windows):
        if number != running:
            queue.append(f"- #{number} ({source})")
    prs = []
    for item in own:
        lines, _ = pr.checks(item["pr"], 0)
        prs.append(f"- #{item['pr']} for #{item['issue']}: {lines[0].removeprefix('CHECKS: ')}"
                   f"{checked(item['pr'], head_in(lines[0]))}")
    with_pr = {item["issue"] for item in own}
    attempted = pr.gh_lines(
        "issue", "list", "--repo", pr.REPO, "--state", "open", "--limit", "200",
        "--label", pr.ATTEMPTED, "--json", "number,title,labels", "--jq", ".[]")
    waiting_on_you = [f"- #{issue['number']} {issue['title']}" for issue in attempted
                      if issue["number"] not in with_pr
                      and STATUS_LABEL not in {label.get("name") for label in issue.get("labels") or []}]
    return queue, prs, waiting_on_you


def gh(*args):
    done = subprocess.run(["gh", *args], capture_output=True, text=True, timeout=pr.GH_SECONDS)
    if done.returncode != 0:
        raise pr.GitHubError((done.stderr or done.stdout).strip()[:300] or f"gh ended with code {done.returncode}")
    return done.stdout.strip()


def status_issue():
    """The status issue's number, made and pinned if there is none."""
    kept = OUT / "state" / "status-issue"
    try:
        number = int(kept.read_text(encoding="utf-8").strip())
        if gh("issue", "view", str(number), "--repo", pr.REPO, "--json", "state", "--jq", ".state") == "OPEN":
            return number
    except (OSError, ValueError, pr.GitHubError):
        pass
    found = pr.gh_lines("issue", "list", "--repo", pr.REPO, "--state", "open", "--label", STATUS_LABEL,
                        "--json", "number", "--jq", ".[]")
    if found:
        number = min(issue["number"] for issue in found)
    else:
        gh("label", "create", STATUS_LABEL, "--repo", pr.REPO, "--force", "--color", "5319e7",
           "--description", "The feedback check's status issue")
        # feedback-attempted keeps it out of the fix queue: the owner's login opens it.
        url = gh("issue", "create", "--repo", pr.REPO, "--title", TITLE,
                 "--label", STATUS_LABEL, "--label", pr.ATTEMPTED, "--body", "Starting.")
        number = int(url.rstrip("/").rsplit("/", 1)[-1])
        try:
            gh("issue", "pin", str(number), "--repo", pr.REPO)
        except pr.GitHubError:
            pass
    kept.parent.mkdir(parents=True, exist_ok=True)
    kept.write_text(f"{number}\n", encoding="utf-8")
    return number


def paused_note(path=None):
    """What to say while the loop waits after two failed passes (scripts/admin-feedback-run.sh)."""
    path = path or OUT / "state" / "loop-paused-until"
    try:
        until = int(path.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return ""
    if until <= time.time():
        return ""
    return f"Paused until {utc(time.gmtime(until))} after two failed passes in a row; the last run's summary on Lilliput says why."


@contextlib.contextmanager
def one_at_a_time(path=None):
    """Holds the status lock. While a run has work two things rewrite the status: the run, when
    a session starts or ends, and its keeper, every minute. Without the lock a rewrite that read
    GitHub earlier could land after one that read it later, and the issue would say the older
    thing until the next change. The lock goes with the process, so a killed rewrite leaves
    nothing to clear."""
    path = path or OUT / "state" / "status.lock"
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as held:
        fcntl.flock(held, fcntl.LOCK_EX)
        yield


def update(cap, windows, handled, note, show=False):
    if not show:
        with one_at_a_time():
            return write(cap, windows, handled, note)
    return write(cap, windows, handled, note, show=True)


def write(cap, windows, handled, note, show=False):
    note = " ".join(part for part in (paused_note(), note) if part)
    records = usage()
    queue, prs, waiting_on_you = gather(windows, handled)
    body = render(note, in_flight(), queue, prs, waiting_on_you,
                  passes(OUT / "history.log", records), sessions_today(records), cap)
    if show:
        print(body, end="")
        return
    kept = OUT / "state" / "status-fingerprint"
    # At least once an hour it is written anyway, which opens a new one if it was closed.
    try:
        if kept.read_text(encoding="utf-8").strip() == fingerprint(body) and time.time() - kept.stat().st_mtime < 3600:
            return
    except OSError:
        pass
    number = status_issue()
    done = subprocess.run(["gh", "issue", "edit", str(number), "--repo", pr.REPO, "--body-file", "-"],
                          input=body, capture_output=True, text=True, timeout=pr.GH_SECONDS)
    if done.returncode != 0:
        raise pr.GitHubError((done.stderr or done.stdout).strip()[:300])
    kept.write_text(fingerprint(body) + "\n", encoding="utf-8")


def main(args):
    mode = args[0] if args else ""
    try:
        if mode == "today":
            print(sessions_today(usage()))
            return 0
        if mode == "update":
            cap = pr.option(args, "--cap") or "12"
            update(int(cap), pr.session_windows(pr.option(args, "--windows")),
                   pr.ids_in(pr.option(args, "--handled")), pr.option(args, "--note") or "", "--print" in args)
            return 0
    except (pr.GitHubError, subprocess.TimeoutExpired, KeyError, IndexError, TypeError, ValueError, OSError) as error:
        print(f"STOP: the status issue could not be updated: {error}", file=sys.stderr)
        return 3
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
