---
name: "agent-progress-diagnostic"
description: "How to tell whether a long-running background agent is hung vs. making progress, before reaching for stop_bash."
domain: "squad-orchestration"
confidence: "low"
source: "observed (Wash publish #9, 2026-05-07 — one occurrence)"
---

## Context

Squad agents like Wash (publish), Kaylee (feature work), Jayne (E2E) routinely make 50–150 tool calls per task. From the Coordinator's seat, a long-quiet agent at ~80+ tool calls **looks** identical to a hung agent. The reflex is to assume it's stuck and either kill it (`stop_bash`) or escalate to Captain. Both are usually wrong.

**Apply this skill when:** an `async` background agent has been running long enough that you're tempted to ask "is it hung?" Before any kill action, run the rubric.

## Patterns

### Tool-call envelope per role (rough budgets)

| Agent role | Typical envelope | Concern threshold |
|------------|------------------|-------------------|
| Wash (publish) | 60–120 tool calls | >150 with no inbox-file write |
| Kaylee (feature) | 80–200 tool calls | >250 with no commits |
| Jayne (E2E) | 50–150 tool calls | >200 with no screenshot output |
| Scribe (merge) | 20–50 tool calls | >80 with no decisions.md write |

These are observational — refine as more data lands.

### Diagnostic rubric (run in order)

1. **Check elapsed seconds and tool-call count.** Within envelope? → almost certainly fine, just wait. Past envelope? → continue.

2. **Send a status `write_agent` ping.** One line, low-cost:
   ```
   "status check — what step are you on?"
   ```
   - If reply arrives within 30 seconds → agent is alive and processing. Wait.
   - If reply is queued behind a long turn → still alive. Wait for the in-flight turn to finish.
   - If no reply after 2+ minutes → continue to step 3.

3. **Check filesystem for files the agent should be writing.** Most Squad agents land an inbox decision file (`/.squad/decisions/inbox/<agent>-<task>.md`) or a screenshot near completion. Use `ls -la` on the expected output dir:
   ```bash
   ls -la /Users/davidortinau/work/SentenceStudio/.squad/decisions/inbox/
   ```
   A file with a recent timestamp (mtime within last 60s) means the agent IS writing — give it more time.

4. **Check the agent's history/log file.** Most Squad agents append to `.squad/agents/<name>/history.md` or write orchestration log entries. Recent timestamp → alive.

5. **Only consider stop_bash if all four signals say stuck.** Specifically: tool-call count past envelope AND ping unanswered AND no recent file writes AND no log activity. Even then, prefer `write_agent` with a "wrap up and report what you have" message over a hard kill — graceful shutdown preserves work in progress.

### Heuristics

- **A long-running agent at 80+ tool calls is making progress 95% of the time.** The only reason it looks hung is that you can't see the in-flight turn.
- **The "is it hung?" question is usually a Coordinator anxiety, not an agent problem.** When in doubt, send a status ping — it's cheap and definitive.
- **Never kill an agent that's writing a decision file.** Inbox file writes can be the last 5–10 tool calls of an otherwise complete task. Killing during that window destroys the work product.
- **If Captain asks "is it hung?", run the rubric out loud** — narrate each check. This documents the diagnosis and trains the rubric.

## Examples

### 2026-05-07 — Wash Publish #9 (the founding case)

- **Symptom:** Wash at ~83 tool calls, ~12 minutes elapsed, no recent stdout. Captain: "is it hung?"
- **Rubric applied:**
  1. Tool-call count: 83. Wash envelope is 60–120. Within range. → Likely fine.
  2. Status `write_agent`: "status check?" → Wash replied within seconds with "writing decision file."
  3. Filesystem check: `.squad/decisions/inbox/` had a fresh `wash-publish-9-*.md` mtime within 30s. → Confirmed alive.
  4. No kill needed. Wash completed successfully ~2 minutes later.
- **Outcome:** Coordinator avoided a false-positive kill. This case is why the skill exists.

## Anti-Patterns

- **❌ Reaching for `stop_bash` because "it's been a while."** Set a stopwatch, run the rubric, only kill on hard evidence.
- **❌ Escalating to Captain with "I think Wash is hung"** before pinging the agent yourself. Captain will rightly ask "did you check?"
- **❌ Comparing tool-call count against the WRONG envelope.** Wash's normal range is not Scribe's normal range. Use the table above.
- **❌ Killing during inbox-file write window.** If `.squad/decisions/inbox/` has a fresh mtime, the agent is in its closing moves. Wait.

## References

- **Founding case:** Wash Publish #9, 2026-05-07. See `.squad/agents/wash/history.md` (most recent entry) and `.squad/decisions.md` (Publish #9 section).
- **Tools:** `write_agent`, `read_agent`, `list_agents`, `stop_bash`. See Copilot CLI tool reference.
- **Companion:** `.squad/orchestration.md` — broader Coordinator runbook.
- **Confidence note:** Currently low (n=1). Upgrade to medium after 3+ observations validate the rubric across different agent roles.
