---
name: "coordinator-source-of-truth"
description: "The complete file-by-file source-of-truth hierarchy for Squad: which files are authoritative, which are derived/append-only, who may write each one, who may read each one, and the precedence rules when they conflict. Squad coordinator loads this on demand when it needs to resolve a write conflict, decide where a piece of state belongs, or answer a 'who owns this file' question."
allowedTools: []
confidence: high
domain: squad-internals
source: "Extracted from squad.agent.md as part of the slimming effort (bradygaster/squad#1308). Coordinator loads this satellite skill when a routing decision needs the full hierarchy."
---

> **Load this skill when:** the coordinator (or any agent) needs to resolve a "where does this state belong?" or "who is allowed to write this file?" question — e.g., when about to write `.squad/decisions.md`, when reviewing whether an agent broke the append-only rule, when answering a user question about Squad's file layout, or when adjudicating a conflict between two files. The short summary in `squad.agent.md` is for routing; this skill is the full reference.

## State backend note

Mutable state includes the **authoritative** decision ledger as well as derived histories and logs. Access mutable state with runtime tools (`squad_state_read`, `squad_state_write`, `squad_state_append`, `squad_state_delete`, `squad_state_list`); the runtime selects the local, git-native, or external backend. Other authoritative files are static config on disk. On non-local backends, never fall back to direct file or git writes for mutable state; if the state bridge is unavailable, stop before mutating it.

## File hierarchy

| File | Status | Who May Write | Who May Read |
|------|--------|---------------|--------------|
| `.github/agents/squad.agent.md` | **Authoritative governance.** All roles, handoffs, gates, and enforcement rules. | Repo maintainer (human) | Squad (Coordinator) |
| `.squad/decisions.md` | **Authoritative, mutable decision ledger.** Single canonical location for accepted scope, architecture, and process decisions; append-only outside separately authorized archival. | Scribe — merge-only append of Coordinator-accepted decisions; separately authorized archival follows the preservation rules | All agents |
| `.squad/decisions/inbox/{agent}-{slug}.md` | **Mutable decision proposals.** Submission does not confer acceptance. | Originating agent — own entry only; Scribe — delete only after an accepted entry is verified in the ledger | Originating agent, Squad (Coordinator), Scribe |
| `.squad/team.md` | **Authoritative roster.** Current team composition. | Squad (Coordinator) | All agents |
| `.squad/routing.md` | **Authoritative routing.** Work assignment rules. | Squad (Coordinator) | Squad (Coordinator) |
| `.squad/ceremonies.md` | **Authoritative ceremony config.** Definitions, triggers, and participants for team ceremonies. | Squad (Coordinator) | Squad (Coordinator), Facilitator agent (read-only at ceremony time) |
| `.squad/casting/policy.json` | **Authoritative casting config.** Universe allowlist and capacity. | Squad (Coordinator) | Squad (Coordinator) |
| `.squad/casting/registry.json` | **Authoritative name registry.** Persistent agent-to-name mappings. | Squad (Coordinator) | Squad (Coordinator) |
| `.squad/casting/history.json` | **Derived / append-only.** Universe usage history and assignment snapshots. | Squad (Coordinator) — append only | Squad (Coordinator) |
| `.squad/agents/{name}/charter.md` | **Authoritative agent identity.** Per-agent role and boundaries. | Squad (Coordinator) at creation; agent may not self-modify | Squad (Coordinator) reads to inline at spawn; owning agent receives via prompt |
| `.squad/agents/{name}/history.md` | **Derived / append-only.** Personal learnings. Never authoritative for enforcement. | Owning agent (append only), Scribe (cross-agent updates, summarization) | Owning agent only |
| `.squad/agents/{name}/history-archive.md` | **Derived / append-only.** Archived history entries. Preserved for reference. | Scribe | Owning agent (read-only) |
| `.squad/orchestration-log/` | **Derived / append-only.** Agent routing evidence. Never edited after write. | Scribe | All agents (read-only) |
| `.squad/log/` | **Derived / append-only.** Session logs. Diagnostic archive. Never edited after write. | Scribe | All agents (read-only) |
| `.squad/templates/` | **Reference.** Format guides for runtime files. Not authoritative for enforcement. | Squad (Coordinator) at init | Squad (Coordinator) |
| `.squad/rai/policy.md` | **Authoritative RAI policy.** Check categories, terminology standards, and opt-out rules. | Squad (Coordinator) at init; Rai may propose updates via decisions inbox | Rai, All agents (read-only) |
| `.squad/rai/audit-trail.md` | **Derived / append-only.** RAI review evidence log. Redacted — never contains raw secrets or harmful content. | Rai (append only) | Rai, Squad (Coordinator) |
| `.squad/fact-checker/policy.md` | **Authoritative verification + Devil's Advocate policy.** Confidence rating taxonomy, hard anti-fabrication rules, mode triggers, opt-out model. | Squad (Coordinator) at init; Fact Checker may propose updates via decisions inbox | Fact Checker, All agents (read-only) |
| `.squad/fact-checker/audit-trail.md` | **Derived / append-only.** Verification verdicts + DA brief evidence log. Succinct — verdict + citation, never raw source material. | Fact Checker (append only) | Fact Checker, Squad (Coordinator) |
| `.squad/plugins/marketplaces.json` | **Authoritative plugin config.** Registered marketplace sources. | Squad CLI (`squad plugin marketplace`) | Squad (Coordinator) |

## Rules

1. **If `squad.agent.md` and any other file conflict, `squad.agent.md` wins.** It is the only file with hard governance authority.
2. **Append-only files must never be retroactively edited** to change meaning. They are diagnostic and audit-trail material. Corrections go in a new entry that references the prior one.
3. **Agents may only write to files listed in their "Who May Write" column above.** Violations are a contract bug; non-local backends forbid direct file writes to mutable state.
4. **Any agent may submit its own decision proposal** through `squad_decide` or `squad_state_write` to its own inbox entry. Only Squad (Coordinator) accepts or rejects a proposal; an explicit user directive may supply that acceptance. Unaccepted entries remain in the inbox.
5. **Scribe records, but never accepts, decisions.** For accepted proposals only, Scribe deduplicates, normalizes headings, appends to `decisions.md` with `squad_state_append`, rereads the ledger to verify the content, and then deletes the processed inbox entry with `squad_state_delete`. Scribe cannot rewrite existing decisions as part of a merge; corrections are new entries.
