# After Agent Reference

### After Agent Work

<!-- KNOWN PLATFORM BUGS: (1) "Silent Success" — ~7-10% of background spawns complete
     file writes but return no text. Mitigated by RESPONSE ORDER + filesystem checks.
     (2) "Server Error Retry Loop" — context overflow after fan-out. Mitigated by lean
     post-work turn + Scribe delegation + compact result presentation. -->

**⚡ Keep the post-work turn LEAN.** Coordinator's job: collect results, run any applicable Pre-Ship gate, present compact results, then spawn Scribe. No orchestration logs, no decision consolidation, no heavy file I/O.

**⚡ Context budget rule:** After collecting results from 3+ agents, use compact format (agent + 1-line outcome). Full details go in orchestration log via Scribe.

After each batch of agent work:

1. **Collect results** via `read_agent` (wait: true, timeout: 300).

2. **Silent success detection** — when `read_agent` returns empty/no response:
   - Check filesystem: history.md modified? New decision inbox files? Output files created?
   - Files found → `"⚠️ {Name} completed (files verified) but response lost."` Treat as DONE.
   - No files → `"❌ {Name} failed — no work product."` Consider re-spawn.

3. **Gate finalization:** For a new or independently revised user-facing artifact due for delivery, merge, or publication, run the automatic Pre-Ship ceremony in `.squad/ceremonies.md` exactly once for this revision. Fact Checker facilitates and gathers Rai's independent verdict. Wait for both. Contradicted claims, a Red verdict, or a missing verdict block finalization; do not convert a failure to an advisory warning. Ceremony output and Scribe bookkeeping are never inputs that retrigger Pre-Ship.

4. **Show compact results:** `{emoji} {Name} — {1-line summary of what they did}`. If the gate blocked, report the blocker rather than claiming completion.

5. **Accept or reject decision proposals:** Read pending inbox entries with state tools.
   The Coordinator decides which entries are accepted (or relays the user's explicit
   acceptance). Pass those exact keys to Scribe; do not delegate acceptance to Scribe.

6. **Spawn Scribe** (background, never wait). Only if agents ran or inbox has files:

Resolve this Scribe spawn through the GPT-only model-selection reference. Require `max` reasoning effort and `long_context`; stop if the spawn interface cannot enforce both:

```
agent_type: "general-purpose"
model: "{resolved_model}"
reasoning_effort: "max"
context_tier: "long_context"
mode: "background"
name: "scribe"
description: "📋 Scribe: Log session & merge decisions"
prompt: |
  You are the Scribe. Read .squad/agents/scribe/charter.md.
  TEAM ROOT: {team_root}
  CURRENT_DATETIME: <resolved CURRENT_DATETIME literal>
  STATE_BACKEND: {state_backend}

  SPAWN MANIFEST: {spawn_manifest}
  ACCEPTED DECISION KEYS: {inbox keys explicitly accepted by Coordinator, or none}
  ARCHIVAL AUTHORIZATION: {separate Coordinator approval for retention, or none}

  Tasks (in order):
  0. PRE-CHECK: Run `squad_state_health` when available. If state tools are unavailable,
     stop without mutating files or git state.
  0b. PRE-CHECK: Read `decisions.md` and list `decisions/inbox` with state tools.
     Record measurements.
  1. DECISIONS RETENTION CHECK [HARD GATE]: Measure decisions.md. If >= 20480 bytes, report entries older than 30 days; if >= 51200 bytes, report entries older than 7 days. Archive only with separate Coordinator authorization and the ARCHIVAL SAFETY RULES below; otherwise leave accepted history intact and report the pending retention work.
  2. DECISION INBOX: Use `squad_state_list` and `squad_state_read` on `decisions/inbox`,
     merge ONLY accepted keys into `decisions.md` with `squad_state_append` after
     heading normalization and deduplication. DEMOTE body headings so the shallowest
     lands at `####` beneath an `###` entry; preserve fenced code and relative structure.
     Re-read `decisions.md` to verify content, then delete only processed entries with
     `squad_state_delete`. Leave unaccepted entries pending; never decide acceptance
     or rewrite existing blocks during a merge.
  3. ORCHESTRATION LOG: Write `orchestration-log/{timestamp}-{agent}.md` with `squad_state_write` per agent. Use ISO 8601 UTC timestamp. Replace `:` with `-` in `{timestamp}` so filenames are valid on all platforms (e.g. `2026-06-02T21-15-30Z`).
  4. SESSION LOG: Write `log/{timestamp}-{topic}.md` with `squad_state_write`. Brief. Use ISO 8601 UTC timestamp. Replace `:` with `-` in `{timestamp}` so filenames are valid on all platforms.
  5. CROSS-AGENT: Append team updates to affected agents' `agents/{agent}/history.md` with `squad_state_append`.
  6. HISTORY SUMMARIZATION [HARD GATE]: If any history.md >= 15360 bytes (15KB), summarize now. The ARCHIVAL SAFETY RULES apply here too — summarization moves content out of a file exactly like decision archival does.
  7. HEALTH REPORT: Report ENTRY COUNTS, never file sizes: `N removed from source / N added to destination` for every archival, plus inbox count processed and history files summarized. Write with `squad_state_write` or `squad_state_append`.

  ARCHIVAL SAFETY RULES (apply to every operation that moves content out of a file):
  A. DESTINATION MUST BE DURABLE. On the local backend, run `git ls-files --error-unmatch <destination>`
     before archival; if untracked, use an existing tracked archive or ABORT. On non-local
     backends, verify the destination through state tools instead of git tracking. Never
     remove source content if destination persistence cannot be verified.
  B. APPEND FIRST, VERIFY, THEN DELETE. Append to the destination. Re-read the destination and
     confirm every moved heading is literally present AND the entry count grew by exactly the
     number moved. Only then remove from the source. If the append cannot be verified, DO NOT
     trim — leave the source intact and report the failure. Losing history is far worse than
     leaving a file over its size gate.
  C. COUNT ENTRIES, NOT BYTES. File size is not a valid integrity signal: a merge and an archive
     in the same pass move size in opposite directions, so a size delta proves nothing.
  D. NEVER REPORT A GATE OUTCOME YOU DID NOT MEASURE. "No archival required" must come from an
     actual measurement. A gate that reports without measuring is worse than no gate.
  E. If a state tool cannot perform these checks, STOP and report rather than proceeding with an
     unverified move.

  Runtime state tools own persistence. Never switch branches, push note refs, reset
  `.squad/`, or commit mutable squad state from this prompt.

  Never speak to user. ⚠️ End with plain text summary after all tool calls.
```

7. **Immediately assess:** Does anything trigger follow-up work? Launch it NOW.

8. **Ralph check:** If Ralph is active (see Ralph — Work Monitor), after chaining any follow-up work, IMMEDIATELY run Ralph's work-check cycle (Step 1). Do NOT stop. Do NOT wait for user input. Ralph keeps the pipeline moving until the board is clear.
