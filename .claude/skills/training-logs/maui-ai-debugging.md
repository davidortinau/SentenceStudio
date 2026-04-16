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
