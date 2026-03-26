# PRD Intake Reference

> On-demand reference for ingesting PRDs and decomposing into work items.

## Triggers

| User says | Action |
|-----------|--------|
| "here's the PRD" / "work from this spec" | Expect file path or pasted content |
| "read the PRD at {path}" | Read the file at that path |
| "the PRD changed" / "updated the spec" | Re-read and diff against previous decomposition |
| (pastes requirements text) | Treat as inline PRD |

## Intake Flow

1. **Detect source:** File path, pasted text, or URL
2. **Store PRD ref** in `team.md` under `## PRD`:
   ```markdown
   ## PRD
   | Field | Value |
   |-------|-------|
   | Source | {path or "inline"} |
   | Ingested | {date} |
   | Status | Active |
   ```
3. **Spawn Lead** (sync, premium model bump) to decompose:

```
agent_type: "general-purpose"
model: "{premium_model}"
mode: "sync"
description: "🏗️ {Lead}: Decomposing PRD into work items"
prompt: |
  You are {Lead}. Read the PRD below and decompose it into work items.
  TEAM ROOT: {team_root}

  PRD CONTENT:
  {prd_content}

  For each work item, specify:
  - Title
  - Description (enough detail for an agent to implement)
  - Assigned to (which squad member based on routing.md)
  - Dependencies (which items must complete first)
  - Priority (P0/P1/P2)
  - Estimated complexity (S/M/L)

  Write decomposition to .squad/decisions/inbox/{lead}-prd-decomposition.md
```

4. **Present work items** as table for user approval
5. **Route approved items** respecting dependencies

## Work Item Table Format

```
| # | Title | Agent | Priority | Size | Depends On |
|---|-------|-------|----------|------|------------|
| 1 | Set up data models | Wash | P0 | M | — |
| 2 | Build list page | Kaylee | P0 | M | 1 |
| 3 | Add grading prompt | River | P1 | S | 1 |
```

## Mid-Project Updates

When the PRD changes:
1. Re-read the PRD
2. Spawn Lead to diff against previous decomposition
3. Present changes: new items, removed items, modified items
4. Route approved changes
