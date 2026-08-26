# Model Selection Reference

## Per-Agent Model Selection

Before spawning an agent, determine which model to use. Check these layers in order — first match wins:

**OpenAI-only policy:** Every resolved model must be one of the OpenAI GPT model IDs in the catalog below. This applies to persistent configuration, session directives, charter preferences, task-aware selection, retries, and fallbacks. Reject non-OpenAI model requests rather than crossing providers.

**Layer 0 — Per-Agent Override (`.squad/config.json`):** On session start, read `.squad/config.json`. If `agentModelOverrides.{agentName}` contains an allowed GPT model, use it for that agent. Discard invalid overrides; never select them.

- **When the user sets a global or per-agent model:** Validate it against the OpenAI catalog before writing `defaultModel` or `agentModelOverrides.{agent}`.
- **When the user clears a preference:** Restore `defaultModel` to `gpt-5.6-sol`; never remove the last explicit GPT model.

**Layer 1 — Session Directive:** Use a session model only when it is in the allowed OpenAI catalog. Otherwise explain the policy and do not spawn.

**Layer 2 — Charter Preference:** Use a specific charter preference only when it is in the allowed OpenAI catalog. `auto` continues to Layer 3.

**Layer 3 — Task-Aware Auto-Selection:** Use the governing principle: **cost first, unless code is being written.** Match the agent's task to determine output type, then select accordingly:

| Task Output | Model | Tier | Rule |
|-------------|-------|------|------|
| Writing code (implementation, refactoring, test code, bug fixes) | `gpt-5.6-terra` | Standard | Quality and accuracy matter for code. |
| Large or specialized code generation | `gpt-5.3-codex` | Code specialist | Use for complex implementation and multi-file refactoring. |
| Writing prompts or agent designs | `gpt-5.6-terra` | Standard | Prompts are executable; treat them like code. |
| Non-code work (docs, planning, triage, logs, changelogs, mechanical ops) | `gpt-5.4-mini` | Fast | Cost first for bounded work. |
| Architecture, reviewer gates, security audits, or vision-capable work | `gpt-5.6-sol` | Premium | Preserve premium reasoning and vision capability within OpenAI. |

**Role-to-model mapping** (applying cost-first principle):

| Role | Default Model | Why | Override When |
|------|--------------|-----|---------------|
| Core Dev / Backend / Frontend | `gpt-5.6-terra` | Writes code; quality first | Heavy code generation → `gpt-5.3-codex` |
| Tester / QA | `gpt-5.6-terra` | Writes and evaluates test code | Simple scaffolding → `gpt-5.4-mini` |
| Lead / Architect | `gpt-5.6-sol` | Architecture and reviewer gates require premium reasoning | Bounded triage → `gpt-5.4-mini` |
| Prompt Engineer | `gpt-5.6-terra` | Prompt design functions like code | High-impact prompt architecture → `gpt-5.6-sol` |
| Copilot SDK Expert | `gpt-5.6-terra` | Technical analysis often touches code | Pure bounded research → `gpt-5.4-mini` |
| Designer / Visual | `gpt-5.6-sol` | Premium vision-capable work | Never downgrade outside the premium GPT chain |
| DevRel / Writer | `gpt-5.4-mini` | Bounded documentation and writing | Complex strategy → `gpt-5.6-terra` |
| Scribe / Logger | `gpt-5.4-mini` | Mechanical file operations | Keep in the fast GPT tier |
| Git / Release | `gpt-5.4-mini` | Mechanical release operations | Keep in the fast GPT tier |

**Task complexity adjustments** (apply at most ONE — no cascading):
- **Bump UP to premium:** architecture proposals, reviewer gates, security audits, multi-agent coordination (output feeds 3+ agents)
- **Bump DOWN to fast/cheap:** typo fixes, renames, boilerplate, scaffolding, changelogs, version bumps
- **Switch to code specialist (`gpt-5.3-codex`):** large multi-file refactors, complex implementation from spec, heavy code generation (500+ lines)

**Layer 4 — Explicit Global Default (`.squad/config.json`):** Use `defaultModel` only after Layers 0–3 have no allowed match. It is the required fail-closed GPT fallback, not an all-agent override. On session start, restore a missing or invalid `defaultModel` to `gpt-5.6-sol` before any spawn.

**Fallback chains — when a model is unavailable:**

If a spawn fails because the selected model is unavailable, retry only with explicit OpenAI GPT model IDs:

```
Premium:        gpt-5.6-sol → gpt-5.6-terra → gpt-5.6-luna → gpt-5.5
Standard:       gpt-5.6-terra → gpt-5.6-luna → gpt-5.5 → gpt-5.4
Code specialist: gpt-5.3-codex → gpt-5.6-terra → gpt-5.6-luna → gpt-5.4
Fast:           gpt-5.4-mini → gpt-5-mini
```

**Fallback rules:**
- Always pass the selected GPT model explicitly; never omit the `model` parameter.
- Never cross providers and never use the platform default as a fallback.
- If every candidate fails, stop and surface the model availability failure.
- Do not promote fast work into a premium tier during fallback.
- Log retry attempts to the orchestration log.

**Passing the model to spawns:**

Pass the resolved model as the `model` parameter on every `task` tool call:

```
agent_type: "general-purpose"
model: "{resolved_model}"
mode: "background"
name: "{name}"
description: "{emoji} {Name}: {brief task summary}"
prompt: |
  ...
```

The `model` parameter is mandatory on every spawn, including retries.

**Spawn output format — show the model choice:**

When spawning, include the model in your acknowledgment:

```
Fenster (`gpt-5.6-terra`) — refactoring auth module
Redfoot (`gpt-5.6-sol`, vision) — designing color system
Scribe (`gpt-5.4-mini`, fast) — logging session
Keaton (`gpt-5.6-sol`, premium) — reviewing proposal
McManus (`gpt-5.4-mini`, fast) — updating docs
```

Include tier annotation only when the model was bumped or a specialist was chosen. Default-tier spawns just show the model name.

**Allowed OpenAI models (current platform catalog):**

Premium: `gpt-5.6-sol`
Standard: `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.4`
Code specialist: `gpt-5.3-codex`
Fast: `gpt-5.4-mini`, `gpt-5-mini`
