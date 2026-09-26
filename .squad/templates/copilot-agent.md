# Copilot Coding Agent Member

On-demand reference for adding the GitHub Copilot coding agent (@copilot) to the Squad roster.

## Adding @copilot

When the user says "add copilot", "add the coding agent", or "use @copilot for issues":

1. **Add to team.md roster:**
   ```markdown
   | @copilot | Coding Agent | — | 🤖 Coding Agent |
   ```
2. **Add capability profile** (below the roster table):
   ```markdown
   <!-- copilot-auto-assign: false -->
   ### @copilot — Capability Profile

   | Capability | Level | Notes |
   |-----------|-------|-------|
   | Bug fixes (well-scoped) | 🟢 | Best for isolated, test-covered fixes |
   | Feature implementation | 🟡 | Works well with clear specs; may need review |
   | Refactoring | 🟡 | Handles mechanical refactors; verify scope |
   | Architecture decisions | 🔴 | Cannot make cross-cutting design choices |
   | Multi-repo coordination | 🔴 | Limited to single-repo context |
   | Test writing | 🟢 | Strong at adding tests for existing code |
   | Documentation | 🟢 | Generates docs from code effectively |
   ```
3. **Add a blocking rule** to routing.md; do not route issues to @copilot.
4. **Do not create** `charter.md` — @copilot uses `copilot-instructions.md` instead.

## Comparison: Spawned Agent vs. @copilot

| | Spawned Agent | @copilot |
|---|--------------|----------|
| Execution model | Sync sub-task within session | Async — picks up assigned issues |
| Branch convention | `squad/{issue}-{slug}` | `copilot/{slug}` |
| Trigger | Coordinator spawns directly | Issue assignment (blocked for Squad) |
| Charter source | `.squad/agents/{name}/charter.md` | `.github/copilot-instructions.md` |
| Context window | Inherits full session context | Fresh context per issue |
| Reviewer gating | ✅ Enforced by coordinator | ✅ Via PR review process |
| Speed | Immediate (in-session) | Minutes (async queue) |

## Roster Format

In `team.md`, @copilot always appears as:

```markdown
| @copilot | Coding Agent | — | 🤖 Coding Agent |
```

- **No casting** — always "@copilot" (literal handle).
- **No charter file** — configuration lives in `.github/copilot-instructions.md`.
- **No history file** — work is tracked via PRs and issue comments.

## Issue Assignment Gate

Keep the HTML comment in team.md disabled:

```markdown
<!-- copilot-auto-assign: false -->
```

| Setting | Behavior |
|---------|----------|
| `true` | Forbidden for Squad; triage fails closed. |
| `false` | Required. Lead routes issues to a Squad member; labels and manual issue assignments to @copilot remain blocked. |

GitHub issue assignment can specify `agent_assignment.model: gpt-6-sol`, but cannot enforce `max` reasoning effort and `long_context`. The model-only request is retained in a permanently disabled workflow step for structural policy regression testing, not as an available dispatch path. Do not use the GitHub UI, `gh issue edit`, the issues API, or a token to assign @copilot. Lift the gate only when a supported interface can set and verify **all three** settings together.

## Lead Triage Integration

During triage, Lead may read @copilot's capability profile, but must route to a Squad member:

1. **🟢 Match** — Route to a Squad member; @copilot issue assignment is still blocked.
2. **🟡 Match** — Route to a Squad member with appropriate review.
3. **🔴 Match** — Route to the appropriate specialist.

## Routing Details

Add to `routing.md`:

```markdown
| `squad:copilot` | Blocked: issue assignment cannot enforce GPT model, max effort, and long_context | No assignment |
```

The coordinator must not claim that a label, dispatch, or issue assignment succeeded. Route this work to a member instead. Do not start a cloud agent session from an issue unless its interface enforces and verifies all three settings.

## Monitoring @copilot Work

On each watch cycle (or when user asks "status"):
- Check for open PRs from `copilot/*` branches.
- Report: "🤖 @copilot: {N} PRs open ({list}). {M} issues assigned, pending."
