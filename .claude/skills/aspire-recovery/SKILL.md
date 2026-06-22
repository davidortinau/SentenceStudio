---
name: aspire-recovery
description: "Systematic recovery procedure for Aspire AppHost failures caused by orphaned processes holding critical ports (especially 22070). Covers diagnostics, two-pass cleanup (AppHost + dcp tree, then orphaned services), verification, and restart validation. USE FOR: \"aspire won't start\", \"cannot access disposed object\", \"address already in use\", \"aspire dashboard not loading\", \"port 22070 in use\", \"aspire restart failed\", \"orphaned dcp processes\", dashboard stuck on \"starting\", build succeeds but services won't start, previous Aspire session crashed and won't restart. DO NOT USE FOR: initial Aspire setup, configuration changes, deployment issues, or general Aspire CLI usage (use the aspire skill instead)."
---

# Aspire Orphan Recovery

**USE FOR**: "aspire won't start", "cannot access disposed object", "address already in use", "aspire dashboard not loading", "port 22070 in use", "aspire restart failed", "orphaned dcp processes"

**DO NOT USE FOR**: initial Aspire setup, configuration changes, deployment issues

## Summary
Systematic procedure for recovering from Aspire AppHost failures caused by orphaned processes holding critical ports (especially port 22070). Includes diagnostics, two-pass cleanup, verification, and restart validation.

## When to Use
- Aspire CLI reports "Cannot access a disposed object" error
- Dashboard doesn't load or shows "starting" indefinitely  
- Build succeeds but services won't start
- Previous Aspire session crashed and won't restart
- Port binding errors in Aspire logs

## Core Pattern
1. Diagnose: Check port 22070 and find all Aspire/dcp processes
2. Two-pass cleanup: Kill AppHost + dcp tree (pass 1), then orphaned services (pass 2)
3. Verify: Ports free, no orphan processes
4. Restart: `cd src/SentenceStudio.AppHost && aspire run`
5. Validate: Dashboard up, API /health returns Healthy

## Full Procedure
See `.squad/skills/aspire-orphan-recovery/SKILL.md` for:
- Complete diagnostic commands
- Why two passes are required
- Process identification guide (what to kill vs. keep)
- Verification steps
- Prevention tips and graceful shutdown patterns
- Decision tree for quick triage

## Key Commands
```bash
# Diagnostic
lsof -nP -iTCP:22070 -sTCP:LISTEN
ps aux | grep -E '(AppHost|dcp|SentenceStudio)' | grep -v 'aspire agent mcp'

# Cleanup (use specific PIDs from diagnostics)
kill <apphost-pid>
kill <dcp-apiserver-pid>
kill <monitor-pid-1> <monitor-pid-2> ...
# Wait 2-3 sec, then check for re-orphaned services and kill those

# Verify
lsof -nP -iTCP:22070 -sTCP:LISTEN  # should exit 1 (free)

# Restart
cd src/SentenceStudio.AppHost && aspire run

# Validate
curl -fsS https://localhost:7012/health --insecure  # → "Healthy"
```

## Common Mistakes
- Using `pkill`/`killall` instead of specific PIDs (NEVER do this)
- Skipping the two-pass cleanup → services re-orphan
- Killing MAUI client apps (Mac Catalyst, iOS sim) thinking they're orphans
- Not verifying ports are free before restart
- Using `kill -9` instead of graceful SIGTERM

## Related Documentation
- `.squad/skills/aspire-orphan-recovery/SKILL.md` - Full technical reference
- `.squad/decisions.md` - Aspire architectural decisions
- `docs/deploy-runbook.md` - Graceful Aspire workflows
