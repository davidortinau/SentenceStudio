---
name: "squad-help"
description: "How to actually use Squad — Squad is a custom Copilot agent (invoked via the task tool with agent_type='Squad'), not a skill. This file explains the right invocation paths for setting up a team, listing squad commands, and initializing Squad in a new project."
allowedTools: []
confidence: high
domain: squad-onboarding
---

# Skill: squad-help

> **Quick reference.** If you're reading this because a user said "use squad" or "squad" or "set up a squad", you're in the right place — read on for the correct invocation paths.

---

## Squad is a custom agent, not a skill

The Squad framework registers a **custom Copilot CLI agent** at `.github/agents/squad.agent.md`. The agent is named **`Squad`** and its description is *"Your AI team. Describe what you're building, get a team of specialists that live in your repo."*

Copilot CLI agents and skills are different things:

| Thing | How to invoke | Example |
|---|---|---|
| **Skill** | `skill(name)` tool call or natural-language match | `skill("squad")` |
| **Agent** | `task` tool with `agent_type=<name>` | `task(agent_type="Squad", model="gpt-6-sol", reasoning_effort="max", context_tier="long_context", prompt="...")` |
| **Slash command** | Installed command catalog or CLI keyword | `/squad`, `/agent`, `/skills` |

The installed `/squad` command opens the catalog from `.github/skills/squad/SKILL.md`; `skill("squad")` can load that same catalog. To run a Squad-coordinated task, launch the `Squad` agent with an explicit GPT model, maximum supported reasoning effort, and long context. Do not confuse the catalog with the coordinator agent.

---

## How to actually use Squad

Pick the path that matches the user's intent:

### A) Invoke the Squad coordinator agent (most common)

The Squad coordinator orchestrates a team of specialists. It routes work to the right agent, scaffolds a team if none exists, and enforces handoffs.

```text
task(
  name="<short-task-name>",
  agent_type="Squad",
  model="gpt-6-sol",
  reasoning_effort="max",
  context_tier="long_context",
  prompt="<what you want the team to do>"
)
```

If that GPT model or either requested capability is unavailable, stop before spawning rather than using an implicit provider/model or effort/context default.

Use this when the user says things like:
- *"Use Squad to build X"*
- *"Set up an AI team for this project"*
- *"Have the Squad coordinator design Y"*
- *"Spawn Squad"* / *"Squad, help me with ..."*

### B) See what Squad commands exist

The installed `/squad` command opens `.github/skills/squad/SKILL.md`, the categorized catalog of Squad operations. The coordinator can present the same catalog as an interactive menu.

Trigger by natural-language match: `"squad commands"`, `"what can squad do"`, `"show me squad options"`, `"slash commands"`, `"what commands are available"`.

Use this when the user says things like:
- *"What can Squad do?"*
- *"Show me the squad commands"*
- *"squad help"*

### C) Initialize Squad in a fresh project

`squad init` is a **shell command**, not a tool call. The user runs it in their terminal in a project that has no `.squad/` directory yet.

```bash
squad init
```

Do **not** try to invoke this from inside an existing Copilot session — `.squad/` is already initialized if you're reading this file.

---

## What NOT to do

- Do not call `skill(Squad)` expecting to launch the coordinator; `skill("squad")` loads the command catalog, not the agent.
- Do not start a Squad session without explicit GPT model, maximum supported reasoning effort, and long context.
- ❌ Do not call `task(agent_type="Squad", …)` for tiny tasks the current agent can handle directly. Squad is for work that needs orchestration; trivial edits do not.

---

## How this skill was discovered

This skill ships from the Squad SDK templates at `.github/skills/squad-help/SKILL.md`, beside the installed command catalog.

If you removed this skill on purpose, the model will fall back to its own reasoning and may make the lookup mistakes described above.

---

## See also

- `.github/agents/squad.agent.md` — the actual Squad coordinator agent
- `.github/skills/squad/SKILL.md` — the `/squad` command catalog
- `.github/skills/squad-conventions/SKILL.md` — conventions for working on the Squad codebase itself
- `.github/skills/squad-version-check/SKILL.md` — version-stamping mechanics
