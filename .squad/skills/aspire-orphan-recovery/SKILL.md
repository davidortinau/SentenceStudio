# Aspire Orphan Recovery Skill

## Overview
This skill provides a systematic approach to recovering from Aspire AppHost failures caused by orphaned processes holding critical ports (especially port 22070, the dashboard port).

## Common Symptoms
When Aspire orphan processes are blocking a restart, you'll see one or more of these:

1. **Misleading error message**: `Cannot access a disposed object. Object name: 'IServiceProvider'`
   - This is a RED HERRING - not the actual root cause
   - The real error appears earlier in the log

2. **Actual root cause** (appears earlier in CLI log):
   ```
   Failed to bind to address 127.0.0.1:22070: address already in use
   ```

3. **Dashboard "starting" forever** - spinner never completes, no URL printed

4. **Service startup failures** - resources stuck in "Starting" state

## Root Cause Mechanism
The failure chain is:
1. Previous Aspire session crashed or was forcibly terminated
2. DCP (Distributed Application Controller) child processes didn't get cleaned up
3. Orphaned dcp process still holds port 22070 (dashboard) and/or service ports
4. New AppHost can't bind to those ports → fails to start
5. The disposed object error is a side effect of the bind failure, not the cause

## Diagnostic Procedure
Run these commands IN ORDER to identify orphan processes:

### 1. Check dashboard port (22070)
```bash
lsof -nP -iTCP:22070 -sTCP:LISTEN
```
**Expected when healthy**: Exit code 1 (port free)
**Problem indicator**: Shows a PID → orphaned process holding the port

### 2. Find all Aspire/SentenceStudio processes
```bash
ps aux | grep -E '(AppHost|aspire|dcp|SentenceStudio.Api|SentenceStudio.WebApp|SentenceStudio.Workers|SentenceStudio.Marketing)' | grep -v grep
```
**Filter out**: Lines containing `aspire agent mcp` (those are MCP servers, keep them)
**Look for**: AppHost, dcp, dotnet service processes

### 3. Find all listening dotnet/dcp ports
```bash
lsof -nP -iTCP -sTCP:LISTEN | grep -E 'dotnet|dcp'
```
**Check for**: Service ports in 52XXX range, PostgreSQL (60801), dashboard (22070)

## Two-Pass Cleanup Procedure

### Why Two Passes?
When you kill dcp monitor processes, they can re-orphan their child service binaries. The two-pass approach ensures complete cleanup.

### Pass 1: Kill AppHost + dcp tree
Target these IN ORDER (use specific PIDs from diagnostic output):

1. **AppHost process** (the `SentenceStudio.AppHost` binary)
   ```bash
   kill <apphost-pid>
   ```

2. **DCP start-apiserver** (the main dcp daemon)
   ```bash
   kill <dcp-apiserver-pid>
   ```

3. **DCP monitor-process** processes (multiple, often 4-5)
   ```bash
   kill <monitor-pid-1> <monitor-pid-2> <monitor-pid-3> ...
   ```

4. **dotnet-run wrappers** (if any, from `dotnet run --project AppHost`)
   ```bash
   kill <dotnet-run-pid>
   ```

**Wait 2-3 seconds** after Pass 1 for processes to terminate.

### Pass 2: Kill orphaned service binaries
After Pass 1, re-run diagnostic step 2 to find re-orphaned processes:

```bash
ps aux | grep -E 'SentenceStudio.Api|SentenceStudio.WebApp|SentenceStudio.Workers|SentenceStudio.Marketing' | grep -v grep
```

If you see service binaries still running (SentenceStudio.Api, etc.), kill them:
```bash
kill <service-pid-1> <service-pid-2> ...
```

## Verification Step
Before attempting to restart Aspire, verify the environment is clean:

### 1. Dashboard port must be free
```bash
lsof -nP -iTCP:22070 -sTCP:LISTEN
```
Should exit with code 1 (no output).

### 2. No orphan processes
```bash
ps aux | grep -E '(AppHost|dcp|SentenceStudio)' | grep -v 'aspire agent mcp' | grep -v grep
```
Should show ONLY currently running client apps (Mac Catalyst, iOS sim), NO backend processes.

### 3. Service ports free (if known)
```bash
lsof -nP -iTCP:60801 -sTCP:LISTEN  # PostgreSQL
lsof -nP -iTCP:5081 -sTCP:LISTEN   # API HTTP
```
All should exit with code 1.

## Restart Aspire
Once verified clean, restart Aspire:

```bash
cd src/SentenceStudio.AppHost
aspire run
```

**Alternative** (if `aspire run` has CLI bugs):
```bash
dotnet run --project src/SentenceStudio.AppHost/SentenceStudio.AppHost.csproj
```

**Initial wait**: ~30-60 seconds for build and startup.

## Monitoring Startup
While Aspire is starting:

### Check CLI log tail
```bash
tail -f ~/.aspire/logs/cli_*.log
```
**Look for**:
- Dashboard URL (usually https://localhost:17017)
- Resources transitioning: Starting → Running
- Health checks passing

### Verify dashboard port bound
```bash
lsof -nP -iTCP:22070 -sTCP:LISTEN
```
Should show the NEW AppHost PID.

### Query resources when dashboard is up
```bash
# Use Aspire MCP
aspire-list_resources

# Or curl (wait for dashboard URL in log)
curl -fsS http://localhost:22070/api/resources
```

## Final Health Check
Once dashboard shows resources running:

### API Health Endpoint
```bash
# Find API HTTPS URL from dashboard (usually https://localhost:7012)
curl -fsS https://localhost:7012/health --insecure
```
Should return: `Healthy`

### Dashboard UI
Open https://localhost:17017/ in browser - should show resources with green status.

## Prevention Tips

### Graceful Shutdown
When stopping Aspire:
- Use Ctrl+C in the terminal where `aspire run` is running
- Let it clean up (wait 2-3 seconds for "Application stopped" message)
- DO NOT force-kill the terminal or use `kill -9`

### Detached Shell Cleanup
If you started Aspire in a detached/async shell:
- Use `stop_bash` with the correct shellId (never just close the terminal)
- Verify cleanup with the diagnostic procedure above

### Signal Handling
If you must kill Aspire manually:
1. Send SIGTERM first (regular `kill`, no flags) - gives it time to cleanup
2. Wait 5 seconds
3. Only use `kill -9` as absolute last resort

## Decision Tree (Quick Reference)

```
START: Aspire won't start / "Cannot access disposed object" error
  ↓
Q: Is port 22070 in use?
  YES → Orphan detected, proceed to cleanup
  NO  → Different issue (check log for actual error)
  ↓
[Two-Pass Cleanup]
  Pass 1: Kill AppHost + dcp + monitors
  Wait 2-3 sec
  Pass 2: Kill re-orphaned services
  ↓
[Verify Clean]
  ✓ Port 22070 free
  ✓ No orphan processes
  ↓
[Restart]
  cd src/SentenceStudio.AppHost && aspire run
  ↓
[Validate]
  Dashboard at 22070 → Resources Running → API /health returns Healthy
  ↓
SUCCESS: Create .squad/aspire-ready.txt handoff
```

## Cross-References
- **Memories**: Check repo memories for "Cannot access a disposed object" and "address already in use"
- **Captain's Guide**: AGENTS.md - Wash charter section
- **Deploy Runbook**: docs/deploy-runbook.md - graceful Aspire workflows
- **Upstream Issues**: See `.squad/decisions/inbox/wash-aspire-upstream-triage.md`

## Common Gotchas

### Don't use pkill/killall
NEVER use `pkill dotnet`, `killall dcp`, or name-based killing:
- Can kill unrelated processes (other .NET tools, other users' processes)
- Violates the explicit PID requirement

### Don't skip verification
Always verify ports are free BEFORE restarting:
- Skipping verification = risk of cascading failures
- A 5-second check saves 5-minute debugging loops

### Don't confuse MAUI client apps with backend orphans
When running `ps aux | grep SentenceStudio`:
- `/Users/.../SentenceStudio.iOS.app/SentenceStudio.iOS` → iOS simulator, NOT orphan
- `.../maccatalyst-arm64/SentenceStudio.app/...` → Mac Catalyst app, NOT orphan
- These are EXPECTED and should NOT be killed

Only backend binaries (Api, WebApp, Workers, Marketing) running OUTSIDE an app bundle are orphans.

## Session-Specific Notes
This recovery was performed 2026-05-05:
- Environment was already clean (no orphans found)
- Aspire started successfully on first attempt
- No cleanup was required
- Final state: API healthy on https://localhost:7012
