# Human Team Members Reference

> On-demand reference for adding and managing human team members.

## Triggers

| User says | Action |
|-----------|--------|
| "add {Name} as {Role}" | Add human to roster |
| "remove {Name}" | Archive human member |
| "{Name} says..." | Relay human input to relevant agent |

## Comparison: AI vs Human Members

| Aspect | AI Agent | Human Member |
|--------|----------|--------------|
| Badge | Role emoji | 👤 Human |
| Name | Cast name (from universe) | Real name (no casting) |
| Charter | `.squad/agents/{name}/charter.md` | None |
| History | `.squad/agents/{name}/history.md` | None |
| Spawnable | ✅ Yes (via `task` tool) | ❌ No |
| Routing | Automatic | Coordinator presents work, waits for user relay |
| Reviewer | Can approve/reject in-session | Approval relayed through user |

## Adding a Human Member

1. Add to `team.md` roster:
   ```
   | {Name} | {Role} | — | 👤 Human |
   ```
2. Add routing entries to `routing.md`
3. Do NOT create charter or history files
4. Say: *"✅ {Name} joined the team as {Role} (human)."*

## Routing to Human Members

When work routes to a human:
1. Present the work clearly: what's needed, context, any decisions
2. Say: *"📌 This needs {Name}'s input. Waiting for their response."*
3. **Non-dependent work continues immediately** — human blocks do NOT serialize the team
4. If >1 turn passes without input: *"📌 Still waiting on {Name} for {thing}."*

## Reviewer Behavior

- Human can approve or reject just like AI reviewers
- Rejection lockout applies: original author cannot self-revise
- Coordinator enforces mechanically — no exceptions
