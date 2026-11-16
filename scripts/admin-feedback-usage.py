#!/usr/bin/env python3
"""What each feedback-check session used: tokens, time, models and tools.

The Captain asked (2026-11-16) for every effort of the feedback check to be tracked and reported,
so the check can be tuned over time. scripts/admin-feedback-check.sh pipes each headless session's
event stream through `filter`, which keeps the session's reply and a small record of what it used,
and nothing else: the events themselves are never written to disk, because the triage session's
events hold what a learner wrote.

  admin-feedback-usage.py filter --reply FILE --record FILE
      Reads a `claude -p --output-format stream-json --verbose` stream on stdin. Writes the
      session's final reply to --reply and its usage record (JSON) to --record. Exit 0 when the
      session ended in success, 1 otherwise.
  admin-feedback-usage.py transcript SESSION.jsonl --record FILE
      The same record from a saved session transcript, for a session that was not run through
      `filter`. A transcript carries no cost, so the cost is left out.
  admin-feedback-usage.py markdown RECORD.json
      One record as a few lines for a summary or a pull request comment.
  admin-feedback-usage.py report [USAGE.jsonl] [--last N]
      A table of the recorded sessions and totals by stage. Default file:
      ~/sstudio-feedback-check/usage.jsonl.

A record never holds a command's arguments, a file path, or any text a session read or wrote:
only names of tools, of the two admin scripts' subcommands, and of the first word of other shell
commands, with how many times each was used.
"""
import json
import os
import re
import sys
from collections import Counter
from datetime import datetime

ADMIN_SCRIPTS = ("admin-api.sh", "admin-feedback-issue.sh")


def shell_label(command):
    """A Bash command reduced to a name: the program, and the subcommand for the admin scripts."""
    if not isinstance(command, str):
        return "Bash"
    words = command.strip().split()
    # Skip leading environment assignments (NAME=value).
    while words and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", words[0]):
        words = words[1:]
    if not words:
        return "Bash"
    program = os.path.basename(words[0])
    if program in ADMIN_SCRIPTS and len(words) > 1 and re.match(r"^[a-z-]+$", words[1]):
        return f"Bash: {program} {words[1]}"
    if program in ("git", "gh", "dotnet", "docker", "aspire") and len(words) > 1 and re.match(r"^[a-z-]+$", words[1]):
        return f"Bash: {program} {words[1]}"
    return f"Bash: {program}" if re.match(r"^[A-Za-z0-9._-]+$", program) else "Bash"


class Usage:
    def __init__(self):
        self.tools = Counter()
        self.subagent_tools = Counter()
        self.seen_blocks = set()
        self.message_usage = {}
        self.models = Counter()
        self.mcp_servers = []
        self.result = None
        self.reply = None
        self.first_time = None
        self.last_time = None

    def assistant(self, event):
        message = event.get("message") or {}
        model = message.get("model")
        message_id = message.get("id")
        in_subagent = bool(event.get("parent_tool_use_id")) or bool(event.get("isSidechain"))
        if model and message_id and message_id not in self.message_usage:
            self.models[model] += 1
        if message_id and isinstance(message.get("usage"), dict):
            # The same message can arrive in several events; the last one has its final usage.
            self.message_usage[message_id] = (model, message["usage"])
        for block in message.get("content") or []:
            if not isinstance(block, dict) or block.get("type") != "tool_use":
                continue
            block_id = block.get("id")
            if block_id in self.seen_blocks:
                continue
            self.seen_blocks.add(block_id)
            name = block.get("name") or "unknown"
            label = shell_label((block.get("input") or {}).get("command")) if name == "Bash" else name
            (self.subagent_tools if in_subagent else self.tools)[label] += 1

    def stamp(self, event):
        value = event.get("timestamp")
        if isinstance(value, str):
            try:
                at = datetime.fromisoformat(value.replace("Z", "+00:00"))
            except ValueError:
                return
            self.first_time = self.first_time or at
            self.last_time = at

    def feed(self, event):
        kind = event.get("type")
        self.stamp(event)
        if kind == "system" and event.get("subtype") == "init":
            self.mcp_servers = [s.get("name") for s in event.get("mcp_servers") or [] if isinstance(s, dict)]
        elif kind == "assistant":
            self.assistant(event)
        elif kind == "result":
            self.result = event
            if isinstance(event.get("result"), str):
                self.reply = event["result"]

    def record(self):
        tokens = {"input": 0, "output": 0, "cache_read": 0, "cache_write": 0}
        by_model = {}
        cost = None
        if self.result and isinstance(self.result.get("modelUsage"), dict):
            for model, used in self.result["modelUsage"].items():
                by_model[model] = {
                    "input": used.get("inputTokens", 0),
                    "output": used.get("outputTokens", 0),
                    "cache_read": used.get("cacheReadInputTokens", 0),
                    "cache_write": used.get("cacheCreationInputTokens", 0),
                    "cost_usd": used.get("costUSD"),
                }
            cost = self.result.get("total_cost_usd")
        else:
            # No result event (a transcript, or a session that was cut off): sum each message once.
            for model, used in self.message_usage.values():
                entry = by_model.setdefault(model or "unknown", {"input": 0, "output": 0, "cache_read": 0, "cache_write": 0, "cost_usd": None})
                entry["input"] += used.get("input_tokens", 0) or 0
                entry["output"] += used.get("output_tokens", 0) or 0
                entry["cache_read"] += used.get("cache_read_input_tokens", 0) or 0
                entry["cache_write"] += used.get("cache_creation_input_tokens", 0) or 0
        for used in by_model.values():
            for key in tokens:
                tokens[key] += used[key]

        mcp_tools = {name: count for name, count in self.tools.items() if name.startswith("mcp__")}
        seconds = None
        if self.result and self.result.get("duration_ms") is not None:
            seconds = round(self.result["duration_ms"] / 1000)
        elif self.first_time and self.last_time:
            seconds = round((self.last_time - self.first_time).total_seconds())

        return {
            "ended": (self.result or {}).get("subtype") or "no_result",
            "is_error": bool((self.result or {}).get("is_error", self.result is None)),
            "session_seconds": seconds,
            "api_seconds": round(self.result["duration_api_ms"] / 1000) if self.result and self.result.get("duration_api_ms") is not None else None,
            "turns": (self.result or {}).get("num_turns") or len(self.message_usage),
            "cost_usd": cost,
            "tokens": tokens,
            "models": by_model,
            "tool_calls": sum(self.tools.values()),
            "tools": dict(self.tools.most_common()),
            "subagent_tool_calls": sum(self.subagent_tools.values()),
            "subagent_tools": dict(self.subagent_tools.most_common()),
            "mcp_servers": self.mcp_servers,
            "mcp_tools": mcp_tools,
            "permission_denials": len((self.result or {}).get("permission_denials") or []),
        }


def read_events(stream):
    for line in stream:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if isinstance(event, dict):
            yield event


def option(args, name):
    if name in args:
        index = args.index(name)
        if index + 1 < len(args):
            return args[index + 1]
    return None


def number(value):
    return f"{value:,}" if isinstance(value, (int, float)) else "not recorded"


def duration(seconds):
    if seconds is None:
        return "not recorded"
    minutes, rest = divmod(int(seconds), 60)
    return f"{minutes} min {rest:02d} s" if minutes else f"{rest} s"


def markdown(record):
    tokens = record.get("tokens") or {}
    lines = []
    stage = record.get("stage")
    if stage:
        issue = f", issue #{record['issue']}" if record.get("issue") else ""
        lines.append(f"**Usage ({stage}{issue})**")
        lines.append("")
    models = ", ".join(record.get("models") or {}) or record.get("model_requested") or "not recorded"
    effort = f" at {record['effort']} effort" if record.get("effort") else ""
    lines.append(f"- Model: {models}{effort}")
    wall = record.get("wall_seconds", record.get("session_seconds"))
    api = record.get("api_seconds")
    lines.append(f"- Time: {duration(wall)}" + (f" ({duration(api)} waiting on the model)" if api is not None else "") + f", {number(record.get('turns'))} turns")
    lines.append(
        f"- Tokens: {number(tokens.get('input'))} input, {number(tokens.get('output'))} output, "
        f"{number(tokens.get('cache_read'))} read from cache, {number(tokens.get('cache_write'))} written to cache")
    cost = record.get("cost_usd")
    lines.append(f"- Cost at API prices: {'$%.2f' % cost if isinstance(cost, (int, float)) else 'not recorded'}")
    tools = record.get("tools") or {}
    if tools:
        listed = ", ".join(f"{name} x{count}" for name, count in list(tools.items())[:14])
        more = f", and {len(tools) - 14} more" if len(tools) > 14 else ""
        lines.append(f"- Tool calls: {number(record.get('tool_calls'))} ({listed}{more})")
    else:
        lines.append("- Tool calls: none")
    if record.get("subagent_tool_calls"):
        lines.append(f"- Subagent tool calls: {number(record['subagent_tool_calls'])}")
    servers = record.get("mcp_servers") or []
    mcp = record.get("mcp_tools") or {}
    lines.append("- MCP: " + (", ".join(f"{n} x{c}" for n, c in mcp.items()) if mcp else ("servers loaded, none used" if servers else "no servers loaded, none used")))
    if record.get("permission_denials"):
        lines.append(f"- Commands refused by the session's permissions: {record['permission_denials']}")
    return "\n".join(lines)


def report(path, last):
    if not os.path.exists(path):
        print(f"No usage recorded yet ({path}).")
        return 0
    records = [r for r in read_events(open(path, encoding="utf-8"))]
    if last:
        records = records[-last:]
    print(f"{'when (UTC)':<17} {'stage':<7} {'issue':>5} {'model':<18} {'time':>10} {'turns':>5} {'in':>9} {'out':>8} {'cache rd':>11} {'cost':>7} {'tools':>5}  result")
    totals = {}
    for r in records:
        tokens = r.get("tokens") or {}
        model = (list((r.get("models") or {}).keys()) or [r.get("model_requested") or "?"])[0].replace("claude-", "")
        cost = r.get("cost_usd")
        wall = r.get("wall_seconds", r.get("session_seconds"))
        print(f"{(r.get('at') or '')[:16]:<17} {(r.get('stage') or ''):<7} {str(r.get('issue') or ''):>5} {model:<18} "
              f"{duration(wall):>10} {str(r.get('turns') or ''):>5} {tokens.get('input', 0):>9,} {tokens.get('output', 0):>8,} "
              f"{tokens.get('cache_read', 0):>11,} {('$%.2f' % cost) if isinstance(cost, (int, float)) else 'n/a':>7} "
              f"{r.get('tool_calls', 0):>5}  {(r.get('result_line') or '')[:60]}")
        total = totals.setdefault(r.get("stage") or "?", {"runs": 0, "seconds": 0, "input": 0, "output": 0, "cache_read": 0, "cost": 0.0, "tools": Counter()})
        total["runs"] += 1
        total["seconds"] += wall or 0
        total["input"] += tokens.get("input", 0)
        total["output"] += tokens.get("output", 0)
        total["cache_read"] += tokens.get("cache_read", 0)
        total["cost"] += cost if isinstance(cost, (int, float)) else 0
        total["tools"].update(r.get("tools") or {})
    print()
    for stage, total in totals.items():
        print(f"{stage}: {total['runs']} session(s), {duration(total['seconds'])}, {total['input']:,} input, {total['output']:,} output, "
              f"{total['cache_read']:,} read from cache, ${total['cost']:.2f} at API prices where recorded")
        print("  most used: " + ", ".join(f"{n} x{c}" for n, c in total["tools"].most_common(8)))
    return 0


def main(args):
    if not args:
        print(__doc__)
        return 2
    mode = args[0]
    if mode in ("filter", "transcript"):
        usage = Usage()
        record_path = option(args, "--record")
        if mode == "filter":
            source = sys.stdin
        else:
            source = open(args[1], encoding="utf-8")
        for event in read_events(source):
            usage.feed(event)
        record = usage.record()
        if record_path:
            with open(record_path, "w", encoding="utf-8") as out:
                json.dump(record, out)
        reply_path = option(args, "--reply")
        if reply_path and usage.reply is not None:
            with open(reply_path, "w", encoding="utf-8") as out:
                out.write(usage.reply.rstrip("\n") + "\n")
        return 0 if mode == "transcript" or (usage.result is not None and not record["is_error"]) else 1
    if mode == "markdown":
        print(markdown(json.load(open(args[1], encoding="utf-8"))))
        return 0
    if mode == "report":
        path = next((a for a in args[1:] if not a.startswith("--") and a != option(args, "--last")), None)
        last = option(args, "--last")
        return report(path or os.path.expanduser("~/sstudio-feedback-check/usage.jsonl"), int(last) if last else None)
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
