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
      it in state/next/<pr>.json, and prints what the runner is to post on the pull request:
      the list in full when it is new or changed (or was never posted), a line when it is now
      empty, nothing when it stands as posted. A missing or unreadable FILE leaves the list an
      earlier pass recorded as it was. Exit 4, with why on stderr, when N is not the open pull
      request of issue M's own branch.
  admin-feedback-next.py posted --pr N --url ADDRESS
      The runner posted what record printed, as the comment at ADDRESS: the list counts from
      the time GitHub gives that comment, and the comment is remembered as it then was.
  admin-feedback-next.py stale --pr N
      A pass ended without bringing the list up to date: it no longer counts, until a pass
      records and posts it again. Prints "yes" when there was a list.
  admin-feedback-next.py show --pr N
      The list as the session's file, for a follow-up pass to bring up to date. "[]" when
      there is none.
  admin-feedback-next.py file [--appetite wide|narrow]
      At the start of every pass: for each list that counts and whose pull request has been
      merged, files its items as issues and says so on the pull request; drops the list of one
      that was closed without merging. Prints one line for the run's summary, beginning
      "NEXT:", for each pull request it acted on. Exit 3 when one could not be acted on.
  admin-feedback-next.py left --pr N
      How many items of the list are still to be filed.

Whose words these are. The check's rule was that nothing a session wrote becomes a later run's
task, because a session reads learners' reports and could be misled by one. A follow-up is
exactly that, so it runs only through the Captain:
- A list is taken only for the open pull request of the session's own branch
  (feedback/issue-<n>, into main, not from a fork): a session cannot name another.
- The runner posts every item in full on the pull request, as it will be filed. Each body is
  shown as literal text, in a code block, so nothing in it can render unseen or pass for the
  runner's own words; characters that draw nothing are taken out (see visible).
- The sessions' login may be able to edit a comment on the pull request, the owner's
  included. So the list's comment is remembered as posted, and nothing is filed when it has
  been changed since; and a `/skip` or `/next` counts only while it is as its author wrote it.
- Nothing is filed unless the owner himself merged the pull request into main after the list
  was posted (both times are GitHub's). One merged before its list was shown waits for his
  `/next`.
- Only the runner files, as his login; a session cannot open an issue. At most MAX_ITEMS for
  one pull request.

How much starts by itself is the appetite (FEEDBACK_APPETITE in admin-feedback-check.sh):
  wide    every item is filed when the pull request is merged, except those he left out with a
          comment `/skip 2 4` or `/skip all` on the pull request.
  narrow  nothing is filed unless he asks with `/next 1 3` or `/next all` on the pull request,
          before or after merging it; what he has not asked for within KEEP_DAYS is dropped.
A `/skip` or `/next` counts when the owner wrote it after the list was posted: the numbers are
those of the runner's newest list. It names items by number (`2`, `#2`, `2-4`); with no number
in it, `all` or `everything` means every item. One that names nothing is said on the pull
request when it is merged.
"""

import calendar
import hashlib
import html
import importlib.util
import json
import os
import re
import subprocess
import sys
import time
import unicodedata
from pathlib import Path

HERE = Path(__file__).resolve().parent


def sibling(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), HERE / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


pr = sibling("admin-feedback-pr")

OUT = Path(os.environ.get("FEEDBACK_CHECK_DIR") or os.path.expanduser("~/sstudio-feedback-check"))
OWNER = pr.REPO.split("/")[0]
MAX_ITEMS = 5
TITLE_LENGTH = 120
BODY_LENGTH = 6000
KEEP_DAYS = 30
ORDER = re.compile(r"^\s*/(skip|next)\b([^\n]*)")
# How an issue filed from a list ends, by which one that was already filed is known.
FILED_AS = ("\n\n---\nFollow-up {n} of {total} from pull request #{pr} (issue #{issue}). A fix session listed it "
            "there, and the feedback check filed it when the Captain merged that pull request.")
# Characters that draw nothing or a blank, beyond the categories visible() takes out.
BLANK = re.compile("[\u115f\u1160\u2800\u3164\uffa0\ufe00-\ufe0f\U000e0100-\U000e01ef]")


class Refused(Exception):
    """The list is not one the runner may take."""


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


def visible(text):
    """The text without characters that draw nothing (format and private-use characters,
    surrogates, unassigned ones, variation selectors, blank fillers): text can be hidden in
    them, from the Captain and not from the session that reads the issue. A name after @
    notifies nobody: the issue is filed under his login."""
    text = "".join(c for c in text if c in "\n\t" or unicodedata.category(c) not in ("Cf", "Co", "Cs", "Cn", "Cc"))
    return re.sub(r"@(?=\w)", "@\u200b", BLANK.sub("", text))


def cleaned(listed):
    """The session's list as it will be shown and filed, and how many items were left off."""
    if not isinstance(listed, list):
        raise ValueError("it is not a list")
    items = []
    for entry in listed:
        if not isinstance(entry, dict) or not isinstance(entry.get("title"), str) or not isinstance(entry.get("body"), str):
            raise ValueError('an item is not {"title": ..., "body": ...}')
        title = " ".join(visible(entry["title"]).split())[:TITLE_LENGTH].strip()
        body = visible(entry["body"]).strip()[:BODY_LENGTH].strip()
        if title and body:
            items.append({"title": title, "body": body})
    return items[:MAX_ITEMS], max(0, len(items) - MAX_ITEMS)


def waiting(kept):
    """The items still to be filed, with their numbers in the list."""
    return [(n, item) for n, item in enumerate(kept["items"], 1) if not item.get("filed") and not item.get("skipped")]


def left(number):
    """How many items a merge would start, or offer: none of a list that does not count."""
    kept = load(number)
    return len(waiting(kept)) if kept and kept.get("posted_at") else 0


def looked_at(number, fields):
    return pr.gh_lines("pr", "view", str(number), "--repo", pr.REPO, "--json", fields, "--jq", ".")[0]


def own_open(number, issue):
    """Refuses a pull request that is not the open one of this issue's own branch. The number
    comes from the session's reply, and a session must not hang a list on a pull request that
    is merged already or is someone else's."""
    found = looked_at(number, "state,headRefName,baseRefName,isCrossRepository")
    if (found.get("state") != "OPEN" or found.get("headRefName") != f"feedback/issue-{int(issue)}"
            or found.get("baseRefName") != "main" or found.get("isCrossRepository")):
        raise Refused(f"pull request #{number} is not the open pull request of feedback/issue-{int(issue)} into main "
                      f"({found.get('state')}, {found.get('headRefName')} into {found.get('baseRefName')}), "
                      "so no list was taken for it")


def record(number, issue, source, appetite):
    own_open(number, issue)
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
    earlier = [{"title": item["title"], "body": item["body"]} for item in kept["items"]] if kept else []
    if items is not None and items != earlier:
        if not items:
            path_of(number).unlink(missing_ok=True)
            return "**Next, when you merge**: nothing now. The list above is withdrawn; merging files nothing.\n"
        # Not posted yet, so it does not count yet.
        kept = {"pr": int(number), "issue": int(issue), "recorded_at": now(), "posted_at": None, "items": items}
        save(kept)
    said = markdown(kept, appetite, dropped) if kept and not kept.get("posted_at") else ""
    if problem:
        said = f"{problem}; {'the list of the earlier pass stands' if kept else 'nothing is filed from it'}.\n\n{said}"
    return said


def comment_of(comment_id):
    """A comment as GitHub has it now: when it was made, by whom, and what it says."""
    found = pr.gh_lines("api", f"repos/{pr.REPO}/issues/comments/{int(comment_id)}", "--jq",
                        "{created_at, body, user: .user.login}")[0]
    return found, hashlib.sha256((found.get("body") or "").replace("\r\n", "\n").strip().encode()).hexdigest()


def posted(number, address):
    """The list counts from the time GitHub gives the runner's comment, and that comment is
    remembered as it is now: whatever the session left running has been ended and its login is
    gone, so nobody else has touched it yet."""
    kept = load(number)
    found = re.search(r"#issuecomment-(\d+)\s*$", address or "")
    if not kept or kept.get("posted_at") or not found:
        return bool(kept and kept.get("posted_at"))
    comment, digest = comment_of(found.group(1))
    if comment.get("user") != OWNER or not comment.get("created_at"):
        return False
    kept.update(posted_at=comment["created_at"], comment_id=int(found.group(1)), comment_sha=digest)
    save(kept)
    return True


def as_posted(kept):
    """Whether the list's comment still says what the runner posted."""
    try:
        comment, digest = comment_of(kept["comment_id"])
    except (pr.GitHubError, KeyError, TypeError, ValueError, IndexError):
        return False
    return comment.get("user") == OWNER and digest == kept.get("comment_sha")


def stale(number):
    kept = load(number)
    if not kept:
        return False
    kept["posted_at"] = None
    save(kept)
    return True


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
    lines = [f"**Next, when you merge** ({len(items)})", "", how,
             "Each is the session's own text, shown as written; it is filed as it stands.", ""]
    for n, item in items:
        # As literal text, in a fence longer than any in it: what he reads is all there is.
        fence = "`" * max(3, 1 + max((len(run) for run in re.findall(r"`+", item["body"])), default=0))
        lines += [f"<details><summary>{n}. {html.escape(item['title'])}</summary>", "",
                  f"{fence}text", item["body"], fence, "", "</details>", ""]
    if dropped:
        lines += [f"The session listed {dropped} more than the {MAX_ITEMS} one pull request may leave; "
                  "they are in its description only.", ""]
    return "\n".join(lines)


def numbers_in(rest, count):
    """The items a /skip or /next names. Numbers and ranges, when it has any ("2, that is all"
    is item 2); with none, every item when it says all or everything ("all of them."); else
    nothing."""
    named = set()
    for low, high in re.findall(r"#?(\d+)(?:\s*-\s*#?(\d+))?", rest):
        named.update(range(max(1, int(low)), min(count, int(high or low)) + 1))
    if not re.search(r"\d", rest) and re.search(r"\b(all|everything)\b", rest, re.I):
        return set(range(1, count + 1))
    return named


def orders(number, kept):
    """What the owner asked on the pull request since the list was posted: the items to leave
    out, the items to start, and what he wrote that named nothing. A comment that was edited
    does not count, whoever it shows as its author: someone else may have changed it."""
    skip, start, unclear = set(), set(), []
    for comment in pr.comments_on(number):
        found = ORDER.match(comment.get("body") or "")
        if not found or not pr.owner_wrote(comment) or (comment.get("created_at") or "") < kept["posted_at"]:
            continue
        named = numbers_in(found.group(2), len(kept["items"]))
        if comment.get("updated_at") not in (None, comment.get("created_at")):
            unclear.append(f"`/{found.group(1)}{found.group(2).rstrip()[:60]}` was edited after it was written, so it was not counted")
        elif not named:
            unclear.append(f"`/{found.group(1)}{found.group(2).rstrip()[:60]}` names no item of the list (1 to {len(kept['items'])})")
        else:
            (skip if found.group(1) == "skip" else start).update(named)
    return skip, start, unclear


def gh(*args, text=None):
    try:
        done = subprocess.run(["gh", *args], input=text, capture_output=True, text=True, timeout=pr.GH_SECONDS)
    except subprocess.TimeoutExpired:
        raise pr.GitHubError(f"gh did not answer in {pr.GH_SECONDS} s")
    if done.returncode != 0:
        raise pr.GitHubError((done.stderr or done.stdout).strip()[:300] or f"gh ended with code {done.returncode}")
    return done.stdout


def ending(kept, n):
    return FILED_AS.format(n=n, total=len(kept["items"]), pr=kept["pr"], issue=kept["issue"])


def filed_before(kept):
    """The issues already filed from this list, by item: a try that was cut off after GitHub
    made the issue and before it was recorded here must not make it twice. Only an issue of the
    owner's that ends as the runner ends one: a task's own text comes before that ending, so no
    task can make itself pass for another."""
    found = pr.gh_lines("issue", "list", "--repo", pr.REPO, "--state", "all", "--limit", "200",
                        "--label", pr.FOLLOW_UP, "--search", f'"from pull request #{kept["pr"]}" in:body',
                        "--json", "number,body,author", "--jq", ".[]")
    known = {}
    for n, _ in waiting(kept):
        for issue in found:
            body = (issue.get("body") or "").replace("\r\n", "\n").rstrip()
            if (issue.get("author") or {}).get("login") == OWNER and body.endswith(ending(kept, n)):
                known[n] = issue["number"]
    return known


def open_issue(kept, n, item):
    said = gh("issue", "create", "--repo", pr.REPO, "--title", item["title"], "--label", pr.FOLLOW_UP,
              "--body-file", "-", text=f"{item['body']}{ending(kept, n)}\n")
    found = re.search(r"/issues/(\d+)\s*$", said.strip())
    if not found:
        raise pr.GitHubError(f"gh did not say which issue it opened: {said.strip()[:200]}")
    return int(found.group(1))


def days_since(stamp):
    try:
        return (time.time() - calendar.timegm(time.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ"))) / 86400
    except (TypeError, ValueError):
        return 0


def say_on(number, lines):
    try:
        gh("pr", "comment", str(number), "--repo", pr.REPO, "--body-file", "-", text="\n".join(lines) + "\n")
    except pr.GitHubError as error:
        print(f"the comment on pull request #{number} could not be posted: {error}", file=sys.stderr)


def file_one(kept, appetite):
    """Acts on one list. Returns the line for the run's summary (or None) and why it could not
    go on, when it could not."""
    number = kept["pr"]
    state = looked_at(number, "state,mergedAt,mergedBy,baseRefName")
    if state.get("state") == "OPEN":
        return None, None
    count = len(waiting(kept))
    if state.get("state") != "MERGED":
        settle(kept, "closed without merging")
        return f"NEXT: pull request #{number} was closed without merging, so the {count} on its list were dropped.", None
    merged_by = (state.get("mergedBy") or {}).get("login")
    if merged_by != OWNER or state.get("baseRefName") != "main":
        # Not his merge into main: a branch pushed into another can be marked merged too.
        settle(kept, f"merged by {merged_by} into {state.get('baseRefName')}")
        return (f"NEXT: pull request #{number} was not merged into main by {OWNER} (by {merged_by}, into "
                f"{state.get('baseRefName')}), so the {count} on its list were dropped."), None
    why = ""
    if not kept.get("posted_at"):
        why = ("its list of what is next was not posted here in a state that counts (the last pass did not "
               "finish, or the list could not be posted)")
    elif not as_posted(kept):
        why = "the comment that shows its list of what is next was changed after the feedback check posted it"
    if why:
        # He was not shown it, or cannot be known to have been: nothing of it is filed.
        say_on(number, [f"Nothing was filed from this pull request's list when it was merged: {why}. "
                        "If something on it should still be done, file it as an issue."])
        settle(kept, f"merged, nothing filed: {why}")
        return f"NEXT: pull request #{number} was merged, and nothing was filed from its list: {why}.", None
    # Merged before the list was shown to him: his merge did not accept it, so he is asked.
    late = (state.get("mergedAt") or "") < kept["posted_at"]
    asked = appetite == "narrow" or late
    skip, start, unclear = orders(number, kept)
    filed, left_out, problem = [], [], None
    try:
        known = None
        for n, item in waiting(kept):
            if n in skip and n not in start:
                item["skipped"] = True
                left_out.append(n)
                save(kept)
            elif not asked or n in start:
                if known is None:
                    # Once per batch; --force makes an existing label no error.
                    gh("label", "create", pr.FOLLOW_UP, "--repo", pr.REPO, "--force", "--color", "C5DEF5",
                       "--description", "Listed by a fix session in a pull request; filed when the Captain merged it")
                    known = filed_before(kept)
                item["filed"] = known.get(n) or open_issue(kept, n, item)
                filed.append((n, item))
                save(kept)
    except pr.GitHubError as error:
        # What was filed is recorded and said; the rest is tried by the next pass.
        problem = str(error)
    still = waiting(kept)
    lines = []
    if filed:
        lines += ["Filed from this pull request's list, now that it is merged. The check works them in turn:", ""]
        lines += [f"- #{item['filed']}: {n}. {item['title']}" for n, item in filed]
    if left_out:
        lines += ["", f"Left out at your word: {', '.join(str(n) for n in left_out)}."]
    told = kept.setdefault("told", [])
    for note in unclear:
        if note not in told:
            lines += ["", f"Your {note}."]
            told.append(note)
    if late and still and not problem and "late" not in told:
        lines += ["", "This pull request was merged before its list of what is next was posted here, so nothing "
                      "on it started by itself. To start some, comment `/next 1 3` or `/next all`."]
        told.append("late")
    if lines:
        say_on(number, lines)
    expired = bool(still) and not problem and asked and days_since(state.get("mergedAt")) > KEEP_DAYS
    if not problem and (not still or expired):
        settle(kept, "merged")
    elif path_of(number).exists():
        save(kept)
    said = []
    if filed:
        said.append("filed " + ", ".join(f"#{item['filed']}" for _, item in filed) + " from its list")
    if left_out:
        said.append(f"left out {', '.join(str(n) for n in left_out)} at the Captain's word")
    if expired:
        said.append(f"dropped {len(still)} nobody asked for in {KEEP_DAYS} days")
    elif still and asked and (filed or left_out):
        said.append(f"{len(still)} more wait for a /next")
    return (f"NEXT: pull request #{number} was merged: {'; '.join(said)}." if said else None), problem


def file_all(appetite):
    lines, failed = [], False
    for kept_path in sorted(folder().glob("*.json"), key=lambda p: int(p.stem) if p.stem.isdigit() else 0):
        kept = load(kept_path.stem) if kept_path.stem.isdigit() else None
        if not kept or "pr" not in kept or "issue" not in kept:
            continue
        try:
            line, problem = file_one(kept, appetite)
        except (pr.GitHubError, LookupError, TypeError, ValueError, OSError) as error:
            line, problem = None, str(error)
        if problem:
            print(f"the list of pull request #{kept.get('pr')} could not be acted on: {problem}", file=sys.stderr)
            failed = True
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
    if mode in ("record", "posted", "stale", "show", "left") and number and number.isdigit():
        if mode == "record":
            issue, source = pr.option(args, "--issue"), pr.option(args, "--file")
            if not (issue and issue.isdigit() and source):
                print(__doc__)
                return 2
            try:
                print(record(number, issue, source, appetite), end="")
            except (Refused, pr.GitHubError, LookupError) as error:
                print(f"{error}.", file=sys.stderr)
                return 4
            return 0
        if mode == "posted":
            try:
                return 0 if posted(number, pr.option(args, "--url")) else 5
            except (pr.GitHubError, LookupError, TypeError, ValueError) as error:
                print(f"{error}.", file=sys.stderr)
                return 5
        elif mode == "stale":
            print("yes" if stale(number) else "no")
        elif mode == "show":
            kept = load(number)
            print(json.dumps([{"title": item["title"], "body": item["body"]} for _, item in waiting(kept)] if kept else [],
                             indent=1))
        else:
            print(left(number))
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
