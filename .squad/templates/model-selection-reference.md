# Model Selection Reference

## Per-Agent Model Selection

**This repository uses GPT models exclusively, at maximum available quality.** Every role and task (including Scribe, triage, release work, and reviews) requires `max` reasoning effort and `long_context`; the default model is `gpt-6-sol`. Cost and task type do not lower these settings. If the chosen GPT or interface cannot enforce either setting, stop dispatch.

### Resolve and validate

1. Read `.squad/config.json` at session start. Its `defaultModel`, `defaultReasoningEffort`, and `defaultContextTier` apply to every spawn. An explicit `agentModelOverrides.{agentName}`, session directive, or charter preference may select a *different GPT* only if it does not conflict with the current GPT-only, maximum-quality requirement.
2. Validate the resolved model against the currently available platform catalog before dispatch. Reject non-GPT IDs and any override that would choose another provider. If no model is specified or the preference is cleared, restore `gpt-6-sol`; never use automatic provider selection.
3. Require explicit `max` and `long_context` for the selected GPT. If a fallback model or interface cannot set and verify both, stop. Do not lower either tier or silently omit reasoning or context parameters.

### Explicit GPT-only fallback chain

Try the strongest available compatible GPT in this order, skipping IDs that cannot support `max` and `long_context` in this session:

```
gpt-6-sol → gpt-6-astra → gpt-6-luna → gpt-5.6-sol → gpt-5.6-terra → gpt-5.5 → gpt-5.4
```

The catalog may change. If it offers a stronger GPT than the remaining candidates, prefer it after checking its capabilities. For a task requiring vision or another special capability, skip any fallback that lacks it. **Never select Anthropic, Claude, Gemini, another non-GPT provider, or a platform-default model.** If no GPT supports both required tiers through this interface, stop dispatching and report the availability failure; do not present the task as completed.

Always pass the selected model explicitly to `task` or `create_session`, along with `max` effort and `long_context`. For a CLI task spawn:

```
agent_type: "general-purpose"
model: "{resolved_gpt_model}"
reasoning_effort: "max"
context_tier: "long_context"
name: "{name}"
description: "{Name}: {brief task summary}"
prompt: |
  ...
```

For `create_session`, set and verify `kickoff.model`, `kickoff.reasoning_effort: max`, and `kickoff.context_tier: long_context`; if the surface cannot enforce all three, do not dispatch through it. Model fallback attempts must remain explicit; never omit `model` to let the platform choose. Include the selected model in the spawn acknowledgment.

On VS Code, only use `runSubagent` after trusted runtime metadata verifies the exact session model ID is a compatible GPT and that `max` reasoning effort and `long_context` are enforced. A model-picker label is not verification; if any value is unknown, stop without dispatching.
