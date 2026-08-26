# Model Selection

> Determines which LLM model to use for each agent spawn.

## SCOPE

✅ THIS SKILL PRODUCES:
- A resolved `model` parameter for every `task` tool call
- Persistent model preferences in `.squad/config.json`
- Spawn acknowledgments that include the resolved model

❌ THIS SKILL DOES NOT PRODUCE:
- Code, tests, or documentation
- Model performance benchmarks
- Cost reports or billing artifacts

## Context

Squad uses OpenAI GPT models exclusively across premium, standard, code-specialist, and fast tiers. The coordinator must pass an explicit allowed model for every agent spawn. Users can set persistent GPT preferences that survive across sessions.

## 5-Layer Model Resolution Hierarchy

Resolution is **first-match-wins** — the highest layer with a value wins.

| Layer | Name | Source | Persistence |
|-------|------|--------|-------------|
| **0a** | Per-Agent Config | `.squad/config.json` → `agentModelOverrides.{name}` | Persistent (survives sessions) |
| **1** | Session Directive | User said "use X" in current session | Session-only |
| **2** | Charter Preference | Agent's `charter.md` → `## Model` section | Persistent (in charter) |
| **3** | Task-Aware Auto | Code → Terra, docs → GPT mini, architecture/vision → Sol | Computed per-spawn |
| **4** | Global Default | `.squad/config.json` → `defaultModel` | Persistent fail-closed fallback |

**Provider policy:** Every layer must resolve to an allowed OpenAI GPT model. Reject invalid or non-OpenAI values. `defaultModel` is required for the final fail-closed fallback, not an all-agent override: never select it before task-aware selection. Never omit the model parameter because the platform default may use another provider.

## AGENT WORKFLOW

### On Session Start

1. READ `.squad/config.json`
2. CHECK that `defaultModel` is in the allowed OpenAI catalog; if it is missing or invalid, restore it to `gpt-5.6-sol` before any spawn
3. CHECK that any `agentModelOverrides` values are in the allowed OpenAI catalog; discard invalid overrides rather than selecting them
4. STORE the validated values in session context for the duration

### On Every Agent Spawn

1. CHECK Layer 0a: Is there an allowed `agentModelOverrides.{agentName}` in config.json? → Use it.
2. CHECK Layer 1: Did the user give an allowed GPT session directive? → Use it.
3. CHECK Layer 2: Does the agent's charter specify an allowed GPT model? → Use it.
4. CHECK Layer 3: Determine task type:
   - Code (implementation, tests, refactoring, bug fixes) → `gpt-5.6-terra`
   - Large, specialized code generation → `gpt-5.3-codex`
   - Prompts and agent designs → `gpt-5.6-terra`
   - Architecture, reviewer gates, security, or vision → `gpt-5.6-sol`
   - Non-code (docs, planning, triage, changelogs) → `gpt-5.4-mini`
5. FALLBACK Layer 4: Use the validated explicit `defaultModel` from config.json.
6. INCLUDE the explicit model in the spawn acknowledgment: `{Name} ({resolved_model}) — {task}`

### When User Sets a Preference

**Trigger phrases:** "always use X", "use X for everything", "switch to X", "default to X"

1. VALIDATE the model ID against the OpenAI-only catalog
2. WRITE `defaultModel` to `.squad/config.json` (merge, don't overwrite)
3. ACKNOWLEDGE that the GPT preference was saved

**Per-agent trigger:** "use X for {agent}"

1. VALIDATE the model ID against the OpenAI-only catalog
2. WRITE to `agentModelOverrides.{agent}` in `.squad/config.json`
3. ACKNOWLEDGE that the agent's GPT preference was saved

### When User Clears a Preference

**Trigger phrases:** "switch back to automatic", "clear model preference", "use default models"

1. SET `defaultModel` to `gpt-5.6-sol` in `.squad/config.json`
2. REMOVE optional per-agent overrides only when requested
3. ACKNOWLEDGE that the OpenAI default was restored

### STOP

After resolving the model and including it in the spawn template, this skill is done. Do NOT:
- Generate model comparison reports
- Run benchmarks or speed tests
- Create new config files (only modify existing `.squad/config.json`)
- Change the model after spawn (fallback chains handle runtime failures)

## Config Schema

`.squad/config.json` model-related fields:

```json
{
  "defaultModel": "gpt-5.6-sol",
  "agentModelOverrides": {
    "fenster": "gpt-5.6-terra",
    "mcmanus": "gpt-5.4-mini"
  }
}
```

- `defaultModel` — required final GPT fallback after per-agent, session, charter, and task-aware resolution
- `agentModelOverrides` — per-agent overrides that take priority over every lower layer
- `defaultModel` must remain present so every spawn has an explicit GPT default.
- Every configured value must be in the allowed catalog.

## Fallback Chains

If a model is unavailable, retry only with explicit OpenAI GPT candidates:

```
Premium:        gpt-5.6-sol → gpt-5.6-terra → gpt-5.6-luna → gpt-5.5
Standard:       gpt-5.6-terra → gpt-5.6-luna → gpt-5.5 → gpt-5.4
Code specialist: gpt-5.3-codex → gpt-5.6-terra → gpt-5.6-luna → gpt-5.4
Fast:           gpt-5.4-mini → gpt-5-mini
```

**Never fall UP in tier.** A fast task won't land on a premium model via fallback.
Always pass the model parameter. If all GPT candidates fail, stop and surface the failure; never cross providers or use a platform-default fallback.

## Allowed Model Catalog

- Premium: `gpt-5.6-sol`
- Standard: `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.4`
- Code specialist: `gpt-5.3-codex`
- Fast: `gpt-5.4-mini`, `gpt-5-mini`
