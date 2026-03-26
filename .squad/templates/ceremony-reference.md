# Ceremony Reference

> On-demand reference for ceremony configuration, facilitator spawn template, and execution rules.

## Config Format (ceremonies.md)

Each ceremony is a section with a config table:

| Field | Values |
|-------|--------|
| **Trigger** | `auto` (coordinator checks conditions) or `manual` (user requests) |
| **When** | `before` (runs before work batch) or `after` (runs after work completes) |
| **Condition** | When to auto-trigger (e.g., "multi-agent task involving 2+ agents") |
| **Facilitator** | Who runs it: `lead`, or a specific agent name |
| **Participants** | `all-relevant`, `all-involved`, or comma-separated names |
| **Time budget** | `focused` (tight), `normal`, or `open` (exploratory) |
| **Enabled** | `✅ yes` or `❌ no` |

## Facilitator Spawn Template

```
agent_type: "general-purpose"
model: "{lead_model}"
mode: "sync"
description: "🏗️ {Lead}: Facilitating {CeremonyName}"
prompt: |
  You are {Lead}, facilitating a {CeremonyName} ceremony.
  TEAM ROOT: {team_root}

  Read .squad/ceremonies.md for the agenda.
  Read .squad/decisions.md for context.

  PARTICIPANTS: {participant_list}
  TASK CONTEXT: {brief description of the work being planned/reviewed}

  Follow the agenda. For each participant, spawn them as sub-tasks to get input.
  Synthesize findings into decisions.

  Write ceremony summary to .squad/decisions/inbox/{lead}-{ceremony-slug}.md

  ⚠️ RESPONSE ORDER: After ALL tool calls, write plain text summary as FINAL output.
```

## Execution Rules

1. Before spawning a work batch, check `ceremonies.md` for auto-triggered `before` ceremonies
2. After a batch completes, check for `after` ceremonies
3. Manual ceremonies run only when the user asks
4. Spawn the facilitator (sync) — facilitator spawns participants as sub-tasks
5. Include ceremony summary in subsequent work batch spawn prompts
6. Spawn Scribe (background) to record ceremony outcomes
7. **Cooldown:** Skip auto-triggered checks for the immediately following step
8. Report: `📋 {CeremonyName} completed — facilitated by {Lead}. Decisions: {count} | Action items: {count}.`
