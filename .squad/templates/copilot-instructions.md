# Copilot Coding Agent — Squad Instructions

You are working on a project that uses **Squad**, an AI team framework. When picking up issues autonomously, follow these guidelines.

## Coordinator Canary Check

**Scope: this check applies ONLY to the top-level Squad Coordinator.** Spawned sub-agents skip it. Establish coordinator identity from the selected agent/session, **not** from the presence of a canary. A known Squad coordinator checks `agent_instructions` on EVERY session start, before doing any work.

Require the HEAD marker near the start and the distinct EOF marker as the final line of the coordinator payload. If either is missing or misplaced, the known coordinator MUST fail closed. A missing HEAD is not evidence that the agent is non-Squad; a wholly absent payload cannot be classified by canary text alone and requires a host-level identity check. Do not treat an earlier mention of the EOF marker as proof that the payload is complete.

**When either canary is missing for a known Squad coordinator, you MUST:**
1. **STOP** — do not proceed with standard Squad behavior.
2. **WARN the user** with this exact message:
   ```
   ⚠️ Squad coordinator (squad.agent.md) appears to be missing or truncated. A required canary token was not found. Do NOT proceed with standard Squad behavior — Squad's safety rails are incomplete. Please restart your session.
   ```
3. Do not continue with normal Squad routing, spawning, PR, or branch-protection behavior after emitting the warning.

GitHub issue assignment to @copilot is blocked for Squad: the issue interface does not enforce `max` reasoning effort and `long_context` alongside the GPT model. Do not assign manually, via a label, or via an automation token until an interface can set and verify all three settings.

## Team Context

Before starting work on any issue:

1. Read `.squad/team.md` for the team roster, member roles, and your capability profile.
2. Read `.squad/routing.md` for work routing rules.
3. If the issue has a `squad:{member}` label, read that member's charter at `.squad/agents/{member}/charter.md` to understand their domain expertise and coding style — work in their voice.

## Capability Self-Check

Before starting work, check your capability profile in `.squad/team.md` under the **Coding Agent → Capabilities** section.

- **🟢 Good fit** — proceed autonomously.
- **🟡 Needs review** — proceed, but note in the PR description that a squad member should review.
- **🔴 Not suitable** — do NOT start work. Instead, comment on the issue:
  ```
  🤖 This issue doesn't match my capability profile (reason: {why}). Suggesting reassignment to a squad member.
  ```

## Branch Naming

Use the squad branch convention:
```
squad/{issue-number}-{kebab-case-slug}
```
Example: `squad/42-fix-login-validation`

## PR Guidelines

When opening a PR:
- Reference the issue: `Closes #{issue-number}`
- If the issue had a `squad:{member}` label, mention the member: `Working as {member} ({role})`
- If this is a 🟡 needs-review task, add to the PR description: `⚠️ This task was flagged as "needs review" — please have a squad member review before merging.`
- Follow any project conventions in `.squad/decisions.md`

## Decisions

If you make a decision that affects other team members, submit your own proposal
with `squad_decide` or `squad_state_write` to:
```
.squad/decisions/inbox/copilot-{brief-slug}.md
```
The Coordinator decides whether to accept it; Scribe merges accepted entries
into the shared decisions file and deletes each inbox entry after verifying
the merge. On non-local state backends, never write mutable state directly.
