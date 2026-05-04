# Training Log: MAUI AI Debugging Skill

## Session: 2025-07-16 — Device Data Extraction & Broker Resilience

**Trainer:** Skill Trainer (automated)
**Trigger:** User reported physical device debugging learnings and broker timeout issues

### Assessment

The maui-ai-debugging skill is large and comprehensive (~44KB) but had specific gaps:
1. ❌ No `xcrun devicectl device copy from/to` for physical device data extraction
2. ❌ No WAL file handling guidance for SQLite push/pull
3. ❌ No `PRAGMA wal_checkpoint(TRUNCATE)` guidance
4. ⚠️ No broker idle timeout warning — agents would silently fail after inactivity gaps
5. ✅ Broker section existed but lacked lifecycle details

### Changes Made

1. **Added "Physical Device Data Extraction" section** to `references/ios-and-mac.md`:
   - Full `xcrun devicectl device copy from/to` command examples
   - `--domain-type appDataContainer` and `--domain-identifier` parameter explanation
   - WAL File Handling subsection (CRITICAL warning):
     - `PRAGMA wal_checkpoint(TRUNCATE)` before pushing modified DB
     - Push empty WAL/SHM files after pushing modified DB to prevent stale reads
   - Updated table of contents

2. **Added summary "Device Data Extraction" section** to main SKILL.md:
   - Quick example of pulling SQLite from device
   - Cross-reference to full reference file for WAL handling details

3. **Added broker idle timeout warning** to main SKILL.md:
   - Warning paragraph in Broker & Discovery section explaining auto-shutdown behavior
   - Impact: previously connected agents need app restart to reconnect

4. **Added "Broker Idle Timeout" troubleshooting entry** to `references/troubleshooting.md`:
   - Symptom, cause, fix, and prevention guidance
   - Added to table of contents

### Evidence

- User confirmed `xcrun devicectl device copy from --domain-type appDataContainer` as the working command
- WAL corruption was discovered during actual data recovery (push without WAL handling → stale reads)
- Broker idle timeout caused unexpected failures in multi-hour debugging sessions

### Rationale

Physical device debugging is a distinct workflow from simulator debugging. The simulator can be inspected via `simctl get_app_container`, but physical devices require `devicectl` — a tool not mentioned anywhere in the skill. The WAL handling is particularly critical because skipping it causes silent data corruption that appears as "my changes didn't save" on the device.

The broker idle timeout is a subtle operational issue. The broker auto-restarts on CLI use, so simple commands still work. But the agent connection is lost — so `maui-devflow MAUI status` returns the connection error while `maui-devflow broker status` says "healthy." This mismatch is confusing without the context of what happened.

---

## Session: 2026-05-03 — Phantom Agent (Connected But Empty Tree)

**Trainer:** Skill Trainer (post-ship review)
**Trigger:** During auth-persistence ship, a final Catalyst E2E run had agent connected on
port 10223 but `ui tree` returned 0 windows, CDP "Not ready", `logs` 404 — even though
WebKit activity was clearly visible in `log stream`. Existing troubleshooting did not
cover this failure mode.

### Assessment

The existing "Broker Idle Timeout" entry covers the reverse case (CLI works, agent gone).
The new failure mode is the inverse: agent registered, CLI talks to broker successfully,
but the agent's own HTTP surface is wedged. None of the existing troubleshooting paths
(connection-refused, build-failures, CDP-not-connecting) match — they all assume either
the agent is missing or the app isn't running. Here the app *is* running, the agent *is*
connected, but the agent is a phantom.

Without coverage, agents (the AI kind) chase the symptom: re-running `wait`, restarting
the broker, rebuilding the app — burning 10-30 minutes. The recovery is "kill the app
process directly, then relaunch" — counter-intuitive because `list` says everything is fine.

### Changes Made

Added new troubleshooting entry "Phantom Agent (Connected But Empty Tree)" to
`references/troubleshooting.md`, including:
- Symptom checklist (4 specific command failures while system logs show app alive)
- Recovery ladder (diagnose → kill app → recycle broker → wipe state)
- "When to give up" guidance: if goal is behavior verification not UI introspection,
  fall back to API logs / DB queries / native screenshot. This is the path used to ship
  auth-persistence: zero 401s in Aspire logs accepted as sufficient evidence.
- Linked the entry into the TOC.

### Evidence

- Session 8c66d948, checkpoint 054, technical_details: "DevFlow tooling glitch" entry
- The actual workaround used in the ship (API log evidence) is now captured in the skill,
  so the next agent doesn't have to reinvent it

### Verdict

**MINOR-UPDATE.** Skill confidence remains **high**; this just fills a specific operational gap.
Suggested eval: drop the model into a session where `ui tree` returns 0 windows and `list`
shows agent connected — verify it reaches the "kill the app process" recovery within 3 commands.

