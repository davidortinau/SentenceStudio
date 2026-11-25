#!/usr/bin/env python3
"""What a fix session leaves for later, and what becomes of it (docs/admin-feedback-check.md,
"What a pull request leaves for later"; Captain, 2026-11-25).

A session used to end its pull request with a list, "Left undone", of things the Captain could
ask for one at a time by copying a /revise line, waiting a pass for each and holding the merge
meanwhile. Now the session writes what is left as a list of tasks ("Next"), and merging the pull
request is what starts them: the runner files each as an issue under the Captain's login, and
the check works them in turn, one pull request each.

  admin-feedback-next.py record --pr N --issue M --file FILE [--appetite wide|narrow]
      After a session: takes the session's list (FILE, a JSON list of {"title", "body"}), keeps
      it in state/next/<pr>.json, and prints what the runner's comment on the pull request says
      of it. A missing or unreadable FILE leaves the list an earlier pass recorded as it was.
  admin-feedback-next.py show --pr N
      The list as the session's file, for a follow-up pass to bring up to date. "[]" when
      there is none.
  admin-feedback-next.py file [--appetite wide|narrow]
      At the start of every pass: for each recorded list whose pull request has been merged,
      files its items as issues and says so on the pull request; drops the list of one that
      was closed without merging. Prints one line for the run's summary, beginning "NEXT:",
      for each pull request it acted on.
  admin-feedback-next.py left --pr N
      How many items of the list are still to be filed.

Whose words these are. The check's rule was that nothing a session wrote becomes a later run's
task, because a session reads learners' reports and could be misled by one. A follow-up is
exactly that, so it runs only through the Captain: the runner's comment on the pull request
shows every item in full, as it will be filed (hidden text taken out), and nothing is filed
until he merges that pull request. Only the runner files, as his login; a session cannot open an
issue. At most MAX_ITEMS for one pull request.

How much starts by itself is the appetite (FEEDBACK_APPETITE in admin-feedback-check.sh):
  wide    every item is filed when the pull request is merged, except those he left out with a
          comment `/skip 2 4` or `/skip all` on the pull request.
  narrow  nothing is filed unless he asks with `/next 1 3` or `/next all` on the pull request,
          before or after merging it; what he has not asked for within KEEP_DAYS is dropped.
A `/skip` or `/next` counts when the owner wrote it after the list was last changed: the numbers
are those of the runner's newest comment.
"""

import calendar
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

OUT = Path(os.environ.get("FEEDBACK_CHECK_DIR") or os.path.expanduser("~/sstudio-feedback-check"))
MAX_ITEMS = 5
TITLE_LENGTH = 120
BODY_LENGTH = 6000
KEEP_DAYS = 30
ORDER = re.compile(r"^\s*/(skip|next)\b([^\n]*)")
HIDDEN = re.compile(r"<!--.*?(?:-->|\Z)", re.S)


def folder():
    return OUT / "state" / "next"


def path_of(number):
    return folder() / f"{int(number)}.json"


def now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def load(number):
    try:
        kept = json.loads(path_of(number).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return kept if isinstance(kept, dict) and isinstance(kept.get("items"), list) else None


def save(kept):
    folder().mkdir(parents=True, exist_ok=True)
    target = path_of(kept["pr"])
    fresh = target.with_suffix(".new")
    fresh.write_text(json.dumps(kept, indent=1) + "\n", encoding="utf-8")
    fresh.replace(target)


def settle(kept, outcome):
    """A list that is finished with is kept, out of the way, as a record of what became of it."""
    done = folder() / "done"
    done.mkdir(parents=True, exist_ok=True)
    kept["outcome"] = outcome
    (done / f"{int(kept['pr'])}-{time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())}.json").write_text(
        json.dumps(kept, indent=1) + "\n", encoding="utf-8")
    path_of(kept["pr"]).unlink(missing_ok=True)


def shown(text):
    """The text as the Captain sees it rendered: what a comment would hide is taken out, and a
    name after @ is kept from notifying anyone, since the issue is filed under his login."""
    return re.sub(r"@(?=\w)", "@​", HIDDEN.sub("", text))


def cleaned(listed):
    """The session's list as it will be shown and filed, and how many items were left off."""
    if not isinstance(listed, list):
        raise ValueError("it is not a list")
    items = []
    for entry in listed:
        if not isinstance(entry, dict) or not isinstance(entry.get("title"), str) or not isinstance(entry.get("body"), str):
            raise ValueError('an item is not {"title": ..., "body": ...}')
        title = " ".join(shown(entry["title"]).split())[:TITLE_LENGTH].strip()
        body = shown(entry["body"]).strip()[:BODY_LENGTH].strip()
        if title and body:
            items.append({"title": title, "body": body})
    return items[:MAX_ITEMS], max(0, len(items) - MAX_ITEMS)


def waiting(kept):
    """The items still to be filed, with their numbers in the list."""
    return [(n, item) for n, item in enumerate(kept["items"], 1) if not item.get("filed") and not item.get("skipped")]


def left(number):
    kept = load(number)
    return len(waiting(kept)) if kept else 0


def record(number, issue, source, appetite):
    kept = load(number)
    problem = ""
    try:
        with open(source, encoding="utf-8") as handle:
            text = handle.read()
        items, dropped = cleaned(json.loads(text)) if text.strip() else (None, 0)
    except OSError:
        items, dropped = None, 0
    except ValueError as error:
        items, dropped = None, 0
        problem = f"The session's list of what is next could not be read ({error})"
    if items is not None:
        earlier = [{"title": item["title"], "body": item["body"]} for item in kept["items"]] if kept else []
        if items != earlier:
            kept = {"pr": int(number), "issue": int(issue), "recorded_at": now(), "items": items}
            if items:
                save(kept)
            else:
                path_of(number).unlink(missing_ok=True)
                kept = None
    said = markdown(kept, appetite, dropped) if kept else ""
    if problem:
        said = f"{problem}; {'the list of the earlier pass stands' if kept else 'nothing is filed from it'}.\n\n{said}"
    return said


def markdown(kept, appetite, dropped=0):
    items = waiting(kept)
    if not items:
        return ""
    if appetite == "narrow":
        how = ("None of these starts by itself. To have some worked once this is merged, comment "
               "`/next 1 3` or `/next all` here, before or after you merge. To have them done in this "
               "pull request first, comment `/revise all` or `/revise 2`.")
    else:
        how = ("Merging this pull request files each of these as an issue of its own, and the check works "
               "them in turn, one pull request each. To leave some out, comment `/skip 2 4` or `/skip all` "
               "before you merge. To have them done in this pull request first, comment `/revise all` or "
               "`/revise 2`.")
    lines = [f"**Next, when you merge** ({len(items)})", "", how, ""]
    for n, item in items:
        lines += [f"<details><summary>{n}. {item['title']}</summary>", "", item["body"], "", "</details>", ""]
    if dropped:
        lines += [f"The session listed {dropped} more than the {MAX_ITEMS} one pull request may leave; "
                  "they are in its description only.", ""]
    return "\n".join(lines)


def numbers_in(rest, count):
    """The items a /skip or /next names: every one for "all", else the numbers that exist."""
    words = re.findall(r"[A-Za-z]+|\d+", rest)
    if any(word.lower() == "all" for word in words):
        return set(range(1, count + 1))
    return {int(word) for word in words if word.isdigit() and 1 <= int(word) <= count}


def orders(number, kept):
    """What the owner asked on the pull request since the list was last changed: the items to
    leave out and the items to start."""
    skip, start = set(), set()
    for comment in pr.comments_on(number):
        found = ORDER.match(comment.get("body") or "")
        if not found or not pr.owner_wrote(comment) or (comment.get("created_at") or "") < kept["recorded_at"]:
            continue
        named = numbers_in(found.group(2), len(kept["items"]))
        (skip if found.group(1) == "skip" else start).update(named)
    return skip, start


def gh(*args, text=None):
    try:
        done = subprocess.run(["gh", *args], input=text, capture_output=True, text=True, timeout=pr.GH_SECONDS)
    except subprocess.TimeoutExpired:
        raise pr.GitHubError(f"gh did not answer in {pr.GH_SECONDS} s")
    if done.returncode != 0:
        raise pr.GitHubError((done.stderr or done.stdout).strip()[:300] or f"gh ended with code {done.returncode}")
    return done.stdout


def open_issue(kept, n, item):
    total = len(kept["items"])
    body = (f"{item['body']}\n\n---\nFollow-up {n} of {total} from pull request #{kept['pr']} (issue #{kept['issue']}). "
            "A fix session listed it there, and the feedback check filed it when the Captain merged that pull request.\n")
    said = gh("issue", "create", "--repo", pr.REPO, "--title", item["title"], "--label", pr.FOLLOW_UP,
              "--body-file", "-", text=body)
    found = re.search(r"/issues/(\d+)\s*$", said.strip())
    if not found:
        raise pr.GitHubError(f"gh did not say which issue it opened: {said.strip()[:200]}")
    return int(found.group(1))


def days_since(stamp):
    try:
        return (time.time() - calendar.timegm(time.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ"))) / 86400
    except (TypeError, ValueError):
        return 0


def file_one(kept, appetite):
    """Acts on one recorded list. Returns the line for the run's summary, or None."""
    number = kept["pr"]
    state = pr.gh_lines("pr", "view", str(number), "--repo", pr.REPO, "--json", "state,mergedAt", "--jq", ".")[0]
    if state.get("state") == "OPEN":
        return None
    if state.get("state") != "MERGED":
        count = len(waiting(kept))
        settle(kept, "closed without merging")
        return f"NEXT: pull request #{number} was closed without merging, so the {count} on its list were dropped."
    skip, start = orders(number, kept)
    filed, left_out = [], []
    for n, item in waiting(kept):
        if n in skip and n not in start:
            item["skipped"] = True
            left_out.append(n)
            save(kept)
        elif appetite != "narrow" or n in start:
            if not filed:
                # Asked for once per batch; --force makes an existing label no error.
                gh("label", "create", pr.FOLLOW_UP, "--repo", pr.REPO, "--force", "--color", "C5DEF5",
                   "--description", "Listed by a fix session in a pull request; filed when the Captain merged it")
            item["filed"] = open_issue(kept, n, item)
            filed.append((n, item))
            save(kept)
    if filed or left_out:
        lines = []
        if filed:
            lines += ["Filed from this pull request's list, now that it is merged. The check works them in turn:", ""]
            lines += [f"- #{item['filed']}: {n}. {item['title']}" for n, item in filed]
        if left_out:
            lines += ["", f"Left out at your word: {', '.join(str(n) for n in left_out)}."]
        try:
            gh("pr", "comment", str(number), "--repo", pr.REPO, "--body-file", "-", text="\n".join(lines) + "\n")
        except pr.GitHubError as error:
            print(f"the comment on pull request #{number} could not be posted: {error}", file=sys.stderr)
    still = waiting(kept)
    expired = bool(still) and days_since(state.get("mergedAt")) > KEEP_DAYS
    if not still or appetite != "narrow" or expired:
        settle(kept, "merged")
    said = []
    if filed:
        said.append("filed " + ", ".join(f"#{item['filed']}" for _, item in filed) + " from its list")
    if left_out:
        said.append(f"left out {', '.join(str(n) for n in left_out)} at the Captain's word")
    if still and appetite == "narrow":
        said.append(f"dropped {len(still)} nobody asked for in {KEEP_DAYS} days" if expired
                    else f"{len(still)} more wait for a /next")
    return f"NEXT: pull request #{number} was merged: {'; '.join(said)}." if filed or left_out or expired else None


def file_all(appetite):
    lines, failed = [], False
    for kept_path in sorted(folder().glob("*.json"), key=lambda p: int(p.stem) if p.stem.isdigit() else 0):
        kept = load(kept_path.stem) if kept_path.stem.isdigit() else None
        if not kept or "recorded_at" not in kept or "pr" not in kept:
            continue
        try:
            line = file_one(kept, appetite)
        except (pr.GitHubError, IndexError, KeyError, TypeError, OSError) as error:
            # What was filed before the failure is recorded; the rest is tried by the next pass.
            print(f"the list of pull request #{kept.get('pr')} could not be acted on: {error}", file=sys.stderr)
            failed = True
            continue
        if line:
            lines.append(line)
    return lines, failed


def main(args):
    mode = args[0] if args else ""
    appetite = pr.option(args, "--appetite") or "wide"
    if mode == "file":
        lines, failed = file_all(appetite)
        print("\n".join(lines), end="\n" if lines else "")
        return 3 if failed else 0
    number = pr.option(args, "--pr")
    if mode in ("record", "show", "left") and number and number.isdigit():
        if mode == "record":
            issue, source = pr.option(args, "--issue"), pr.option(args, "--file")
            if not (issue and issue.isdigit() and source):
                print(__doc__)
                return 2
            print(record(number, issue, source, appetite), end="")
            return 0
        if mode == "show":
            kept = load(number)
            print(json.dumps([{"title": item["title"], "body": item["body"]} for _, item in waiting(kept)] if kept else [],
                             indent=1))
            return 0
        print(left(number))
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
