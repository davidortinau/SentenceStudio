---
name: "client-compatibility"
description: "Platform detection and adaptive spawning for CLI vs VS Code vs other surfaces"
domain: "orchestration"
confidence: "high"
source: "extracted"
---

## Context

Squad runs on multiple Copilot surfaces (CLI, VS Code, JetBrains, GitHub.com). The coordinator must detect its platform and adapt spawning behavior accordingly. Different tools are available on different platforms, requiring conditional logic for agent spawning, SQL usage, and response timing.

## Patterns

### Platform Detection

Before spawning agents, determine the platform by checking available tools:

1. **CLI mode** — `task` tool is available → full spawning control. Use `task` with `agent_type`, `mode`, `model`, `description`, `prompt` parameters. Collect results via `read_agent`.

2. **VS Code mode** — `runSubagent` or `agent` tool is available → conditional behavior. Before every spawn, obtain the exact session model ID from trusted runtime metadata and validate it against the allowed GPT catalog. A model-picker label or assumption is not verification. If the model ID is unavailable, unknown, or non-OpenAI, STOP and do not call `runSubagent`. Only after that gate, use `runSubagent` with the task prompt. Drop `agent_type`, `mode`, and `model` parameters. Multiple subagents in one turn run concurrently (equivalent to background mode). Results return automatically — no `read_agent` needed.

3. **Fallback mode** — neither `task` nor `runSubagent`/`agent` available → work inline. Do not apologize or explain the limitation. Execute the task directly.

If both `task` and `runSubagent` are available, prefer `task` (richer parameter surface).

### VS Code Spawn Adaptations

When in VS Code mode, the coordinator changes behavior in these ways:

- **Spawning tool:** Use `runSubagent` instead of `task`. The prompt is the only required parameter — pass the full agent prompt (charter, identity, task, hygiene, response order) exactly as you would on CLI.
- **Parallelism:** Spawn ALL concurrent agents in a SINGLE turn. They run in parallel automatically. This replaces `mode: "background"` + `read_agent` polling.
- **Model selection:** VS Code cannot set a per-spawn model. Before `runSubagent`, read the exact session model ID from trusted runtime metadata and require it to match the allowed GPT catalog. The picker is not evidence. If the ID is unavailable, unknown, or non-OpenAI, refuse the spawn; do not fall back to an omitted model parameter. CLI-only fallback chains do not apply on VS Code.
- **Scribe:** Cannot fire-and-forget. Batch Scribe as the LAST subagent in any parallel group. Scribe is light work (file ops only), so the blocking is tolerable.
- **Launch table:** Skip it. Results arrive with the response, not separately. By the time the coordinator speaks, the work is already done.
- **`read_agent`:** Skip entirely. Results return automatically when subagents complete.
- **`agent_type`:** Drop it. All VS Code subagents have full tool access by default. Subagents inherit the parent's tools.
- **`description`:** Drop it. The agent name is already in the prompt.
- **Prompt content:** Keep ALL prompt structure — charter, identity, task, hygiene, response order blocks are surface-independent.

### Feature Degradation Table

| Feature | CLI | VS Code | Degradation |
|---------|-----|---------|-------------|
| Parallel fan-out | `mode: "background"` + `read_agent` | Multiple subagents in one turn | None — equivalent concurrency |
| Model selection | Per-spawn `model` param (5-layer hierarchy) | Verified GPT session model only | Stop when the exact session model is unavailable, unknown, or non-OpenAI |
| Scribe fire-and-forget | Background, never read | Sync, must wait | Batch with last parallel group |
| Launch table UX | Show table → results later | Skip table → results with response | UX only — results are correct |
| SQL tool | Available | Not available | Avoid SQL in cross-platform code paths |
| Response order bug | Critical workaround | Possibly necessary (unverified) | Keep the block — harmless if unnecessary |

### SQL Tool Caveat

The `sql` tool is **CLI-only**. It does not exist on VS Code, JetBrains, or GitHub.com. Any coordinator logic or agent workflow that depends on SQL (todo tracking, batch processing, session state) will silently fail on non-CLI surfaces. Cross-platform code paths must not depend on SQL. Use filesystem-based state (`.squad/` files) for anything that must work everywhere.

## Examples

**Example 1: CLI parallel spawn**
```typescript
// Coordinator detects task tool available → CLI mode
task({ agent_type: "general-purpose", mode: "background", model: "gpt-5.6-terra", ... })
task({ agent_type: "general-purpose", mode: "background", model: "gpt-5.4-mini", ... })
// Later: read_agent for both
```

**Example 2: VS Code parallel spawn**
```typescript
// Trusted runtime metadata already reported an exact allowed GPT session model ID.
runSubagent({ prompt: "...Fenster charter + task..." })
runSubagent({ prompt: "...Hockney charter + task..." })
runSubagent({ prompt: "...Scribe charter + task..." }) // Last in group
// Results return automatically, no read_agent
```

**Example 3: Fallback mode**
```typescript
// Neither task nor runSubagent available → work inline
// Coordinator executes the task directly without spawning
```

## Anti-Patterns

- ❌ Using SQL tool in cross-platform workflows (breaks on VS Code/JetBrains/GitHub.com)
- ❌ Spawning on VS Code without trusted verification of the exact allowed GPT session model ID
- ❌ Fire-and-forget Scribe on VS Code (must batch as last subagent)
- ❌ Showing launch table on VS Code (results already inline)
- ❌ Apologizing or explaining platform limitations to the user
- ❌ Using `task` when only `runSubagent` is available
- ❌ Dropping prompt structure (charter/identity/task) on non-CLI platforms
