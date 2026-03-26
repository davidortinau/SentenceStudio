# Ralph — Work Monitor Reference

> On-demand reference for Ralph's work-check cycle, idle-watch, and board format.

## Work-Check Cycle

Ralph runs a continuous loop: scan → categorize → act → repeat.

### Step 1 — Scan (parallel)

```bash
# Untriaged issues
gh issue list --label "squad" --state open --json number,title,labels,assignees --limit 20

# Member-assigned issues
gh issue list --state open --json number,title,labels,assignees --limit 20

# Open PRs
gh pr list --state open --json number,title,author,labels,isDraft,reviewDecision --limit 20

# Draft PRs
gh pr list --state open --draft --json number,title,author,labels,checks --limit 20
```

### Step 2 — Categorize

| Category | Signal | Action |
|----------|--------|--------|
| Untriaged | `squad` label, no `squad:{member}` label | Lead triages |
| Assigned unstarted | `squad:{member}` label, no PR | Spawn assigned agent |
| Draft PRs | Draft from squad member | Check if stalled |
| Review feedback | `CHANGES_REQUESTED` | Route to PR author |
| CI failures | Checks failing | Notify assigned agent |
| Approved PRs | Approved + CI green | Merge and close issue |
| Empty | All clear | Report idle |

### Step 3 — Act

Process one category at a time, highest priority first. Spawn agents as needed.

**⚡ CRITICAL:** After results collected, DO NOT stop. Go back to Step 1 immediately.

### Step 4 — Periodic Check-in (every 3-5 rounds)

```
🔄 Ralph: Round {N} complete.
   ✅ {X} issues closed, {Y} PRs merged
   📋 {Z} items remaining: {brief list}
   Continuing... (say "Ralph, idle" to stop)
```

## Board Display Format

```
🔄 Ralph — Work Monitor
━━━━━━━━━━━━━━━━━━━━━━
📊 Board Status:
  🔴 Untriaged:    N issues need triage
  🟡 In Progress:  N issues assigned, N draft PRs
  🟢 Ready:        N PRs approved, awaiting merge
  ✅ Done:         N issues closed this session
```

## Idle-Watch Mode

When the board is clear, Ralph enters idle-watch. For persistent polling, use:

```bash
npx github:bradygaster/squad watch                    # polls every 10 minutes
npx github:bradygaster/squad watch --interval 5       # polls every 5 minutes
```

## Triggers

| User says | Action |
|-----------|--------|
| "Ralph, go" | Activate work-check loop |
| "Ralph, status" | One cycle, report, don't loop |
| "Ralph, check every N minutes" | Set polling interval |
| "Ralph, idle" / "stop" | Fully deactivate |

## Ralph State (session-scoped)

- Active/idle toggle
- Round count
- Scope (default: all categories)
- Stats: issues closed, PRs merged, items processed
