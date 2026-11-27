#!/usr/bin/env python3
"""The feedback check's status issue on GitHub, and how many fix sessions ran today.

scripts/admin-feedback-check.sh keeps one pinned issue (labelled STATUS_LABEL) whose body says
what the check is doing: the session running now, the queue, the pull requests (with their
checks and what the runner itself found of the proof and the diff) and issues waiting for the
Captain, and the last passes. It is rewritten only when what it says changes.
The repository is public, so the body holds numbers, results and times, never what a learner
wrote or a run's summary.

  admin-feedback-status.py update [--cap N] [--windows FILE] [--handled FILE] [--note TEXT] [--spare] [--print]
      Rewrites the status issue from what is on GitHub and in the check's folder, creating and
      pinning it first if there is none. It says at the top when the day's cap is reached;
      --note adds one more line there. With --print, prints the body and changes nothing.
      --spare is the rewrite a run makes every minute while it has work: it does nothing when
      another rewrite is running or when little is left of the hour's GitHub calls.
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
import plistlib
import re
import signal
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
later = sibling("admin-feedback-next")

OUT = Path(os.environ.get("FEEDBACK_CHECK_DIR") or os.path.expanduser("~/sstudio-feedback-check"))
STATUS_LABEL = "feedback-status"
# What starts the check on Lilliput, and so how often an idle check looks for work.
AGENT = Path(os.path.expanduser("~/Library/LaunchAgents/com.sentencestudio.feedback-check.plist"))
# How long a rewrite waits for another one before it goes ahead anyway.
LOCK_SECONDS = 20
# A spare rewrite leaves the last of the hour's GitHub calls to the run and to the Captain.
SPARE_FRACTION = 0.2
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


def looks_every(path=None):
    """How often launchd starts the check, in words, read from what starts it: said in the
    status, it must be what happens and not what a document says."""
    try:
        with open(path or AGENT, "rb") as agent:
            seconds = int(plistlib.load(agent)["StartInterval"])
    except (OSError, ValueError, KeyError, TypeError, plistlib.InvalidFileException):
        return "regularly"
    if seconds <= 0:
        return "regularly"
    if seconds < 120:
        return "every minute" if 45 <= seconds <= 75 else f"every {seconds} seconds"
    return f"every {round(seconds / 60)} minutes"


def bridge_note(path=None):
    """What the runner last decided about the SwiftUI bridge's main
    (scripts/admin-feedback-bridge-release.sh), as (state, text). The file's first line is the
    state (packing, released, nothing or failed) and the rest is what to say."""
    try:
        state, _, text = (path or OUT / "state" / "bridge-release-note").read_text(encoding="utf-8").partition("\n")
    except OSError:
        return "", ""
    return state.strip(), " ".join(text.split())


def render(note, running, queue, prs, waiting_on_you, recent, today, cap, every="regularly", bridge=""):
    lines = [f"_Kept by the feedback check on Lilliput (docs/admin-feedback-check.md). Last changed {utc()}._", ""]
    if note:
        lines += [f"**{note}**", ""]
    lines += ["## Now", "", running or f"Idle: it looks for work {every} and runs passes back to back while there is some.",
              "", f"Fix sessions today: {today} of {cap}.", "", "## Queue", ""]
    lines += queue or ["Nothing waiting."]
    if bridge:
        lines += ["", "## SwiftUI bridge", "", bridge]
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


def review_note(head, number):
    """What the reviewer on GitHub recommends for the pull request's head commit
    (docs/pr-review-agent.md), put first on its line because it is what he acts on."""
    if not head:
        return ""
    try:
        words = pr.review_of(head, number, OUT / "state" / "reviews-borne-out.json")
    except pr.GitHubError:
        return ""
    return f"**Review: {words}.** " if words else ""


def fingerprint(body):
    """The body without its timestamp line, so an unchanged status is not rewritten."""
    return hashlib.sha256("\n".join(body.splitlines()[1:]).encode()).hexdigest()


def next_note(number):
    """What the pull request leaves for later (scripts/admin-feedback-next.py): how many tasks
    its merge starts, or offers under the narrow appetite."""
    later.OUT = OUT  # one folder for both, also when a test points this script at its own
    count = later.left(number)
    return f". Next list: {count}" if count else ""


def gather(windows, handled):
    own = pr.own_pull_requests()
    queue = []
    running = in_flight_number()
    for found in pr.pending(handled, every=True):
        if found["pr"] != running:
            what = "the reviewer's findings" if found["ids"][0].startswith("v") else "`/revise`"
            queue.append(f"- {what} on #{found['pr']} (issue #{found['issue']})")
    for number, source in pr.waiting_issues(windows):
        if number != running:
            queue.append(f"- #{number} ({source})")
    prs = []
    for item in own:
        lines, _ = pr.checks(item["pr"], 0)
        prs.append(f"- #{item['pr']} for #{item['issue']}: {review_note(head_in(lines[0]), item['pr'])}"
                   f"{lines[0].removeprefix('CHECKS: ')}"
                   f"{checked(item['pr'], head_in(lines[0]))}{next_note(item['pr'])}")
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
def one_at_a_time(wait=LOCK_SECONDS, path=None):
    """Takes the status lock and yields whether it got it. While a run has work two things
    rewrite the status: the run, when a session starts or ends, and its keeper, every minute.
    Without the lock a rewrite that read GitHub earlier could land after one that read it later,
    and the issue would say the older thing until the next change. It waits at most `wait`
    seconds, so a run is never held up by a slow rewrite. The lock goes with the process, so a
    killed rewrite leaves nothing to clear."""
    path = path or OUT / "state" / "status.lock"
    path.parent.mkdir(parents=True, exist_ok=True)
    until = time.monotonic() + wait
    with open(path, "a", encoding="utf-8") as held:
        got = False
        while True:
            try:
                fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
                got = True
                break
            except OSError:
                if time.monotonic() >= until:
                    break
                time.sleep(0.5)
        yield got


def calls_to_spare():
    """Whether enough of the hour's GitHub calls are left for a rewrite nobody is waiting for.
    Asking costs none of them. When it cannot be read, the rewrite is skipped."""
    try:
        left = json.loads(gh("api", "rate_limit", "--jq", ".resources"))
        return all(left[kind]["remaining"] >= SPARE_FRACTION * left[kind]["limit"] for kind in ("core", "graphql"))
    except (pr.GitHubError, subprocess.TimeoutExpired, ValueError, KeyError, TypeError):
        return False


def update(cap, windows, handled, note, show=False, spare=False):
    if show:
        return write(cap, windows, handled, note, show=True)
    with one_at_a_time(0 if spare else LOCK_SECONDS) as alone:
        if spare and not (alone and calls_to_spare()):
            return None
        return write(cap, windows, handled, note)


def write(cap, windows, handled, note, show=False):
    records = usage()
    today = sessions_today(records)
    capped = (f"The daily cap of {cap} fix sessions is reached; work waiting starts after 00:00 UTC."
              if today >= cap else "")
    note = " ".join(part for part in (paused_note(), capped, note) if part)
    queue, prs, waiting_on_you = gather(windows, handled)
    packing, bridge = bridge_note()
    # No session runs while a version of the bridge is packed, and the check is not idle.
    running = in_flight() or ("Packing a new version of the SwiftUI bridge (below); sessions start when it is done."
                              if packing == "packing" and bridge else None)
    body = render(note, running, queue, prs, waiting_on_you,
                  passes(OUT / "history.log", records), today, cap, looks_every(), bridge)
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
    # A rewrite that is stopped (the run ending its keeper) ends its gh call with it:
    # subprocess.run kills its child when an exception passes through it.
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    try:
        if mode == "today":
            print(sessions_today(usage()))
            return 0
        if mode == "update":
            cap = pr.option(args, "--cap") or "36"
            update(int(cap), pr.session_windows(pr.option(args, "--windows")),
                   pr.ids_in(pr.option(args, "--handled")), pr.option(args, "--note") or "", "--print" in args,
                   "--spare" in args)
            return 0
    except (pr.GitHubError, subprocess.TimeoutExpired, KeyError, IndexError, TypeError, ValueError, OSError) as error:
        print(f"STOP: the status issue could not be updated: {error}", file=sys.stderr)
        return 3
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
