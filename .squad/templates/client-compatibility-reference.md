# Client Compatibility Reference

### Client Compatibility

Squad runs on multiple Copilot surfaces. The coordinator MUST detect its platform and adapt spawning behavior accordingly. See `docs/scenarios/client-compatibility.md` for the full compatibility matrix.

#### Platform Detection

Before spawning agents, determine the platform by checking available tools:

1. **CLI mode** — `task` tool is available → full spawning control. Use `task` with `agent_type`, `mode`, `model`, `description`, `prompt` parameters. Collect results via `read_agent`.

2. **VS Code mode** — `runSubagent` or `agent` tool is available → conditional behavior. Before every spawn, obtain the exact session model ID from trusted runtime metadata and verify it is a compatible GPT with `max` reasoning effort and `long_context`. A model-picker label or assumption is not verification. If the ID or either setting cannot be verified, STOP and do not call `runSubagent`. Only after that gate, pass the full task prompt; drop `agent_type`, `mode`, and `model` parameters. Multiple subagents in one turn run concurrently and return automatically — no `read_agent` needed.

3. **Fallback mode** — neither `task` nor `runSubagent`/`agent` available → work inline. Do not apologize or explain the limitation. Execute the task directly.

If both `task` and `runSubagent` are available, prefer `task` (richer parameter surface).

#### VS Code Spawn Adaptations

When in VS Code mode, the coordinator changes behavior in these ways:

- **Spawning tool:** Use `runSubagent` instead of `task`. The prompt is the only required parameter — pass the full agent prompt (charter, identity, task, hygiene, response order) exactly as you would on CLI.
- **Parallelism:** Spawn ALL concurrent agents in a SINGLE turn. They run in parallel automatically. This replaces `mode: "background"` + `read_agent` polling.
- **Model selection:** VS Code cannot set a per-spawn model. Verify the exact session ID from trusted runtime metadata is `gpt-6-sol` by default, or an explicitly selected compatible GPT fallback, with `max` effort and `long_context`. The picker is not evidence. If the ID or settings are unavailable or unsupported, refuse the spawn; CLI-only fallback chains do not apply on VS Code.
- **Scribe:** Cannot fire-and-forget. Batch Scribe as the LAST subagent in any parallel group. Scribe is light work (file ops only), so the blocking is tolerable.
- **Launch table:** Skip it. Results arrive with the response, not separately. By the time the coordinator speaks, the work is already done.
- **`read_agent`:** Skip entirely. Results return automatically when subagents complete.
- **`agent_type`:** Drop it. All VS Code subagents have full tool access by default. Subagents inherit the parent's tools.
- **`description`:** Drop it. The agent name is already in the prompt.
- **Prompt content:** Keep ALL prompt structure — charter, identity, task, hygiene, response order blocks are surface-independent.

#### Feature Degradation Table

| Feature | CLI | VS Code | Degradation |
|---------|-----|---------|-------------|
| Parallel fan-out | `mode: "background"` + `read_agent` | Multiple subagents in one turn | None — equivalent concurrency |
| Model selection | Explicit GPT `model`, `max` effort, `long_context` | Verified GPT session model and settings only | Stop if the exact model ID or either setting cannot be confirmed |
| Scribe fire-and-forget | Background, never read | Sync, must wait | Batch with last parallel group |
| Launch table UX | Show table → results later | Skip table → results with response | UX only — results are correct |
| SQL tool | Available | Not available | Avoid SQL in cross-platform code paths |
| Response order bug | Critical workaround | Possibly necessary (unverified) | Keep the block — harmless if unnecessary |

#### SQL Tool Caveat

The `sql` tool is **CLI-only**. It does not exist on VS Code, JetBrains, or GitHub.com. Any coordinator logic or agent workflow that depends on SQL (todo tracking, batch processing, session state) will silently fail on non-CLI surfaces. Cross-platform code paths must not depend on SQL. Use filesystem-based state (`.squad/` files) for anything that must work everywhere.
