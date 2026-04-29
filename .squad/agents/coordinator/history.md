# Coordinator — Squad Routing & Handoff Log

> Squad routes work, enforces handoffs and reviewer gates. Does not generate domain artifacts.

---

## 2026-04-29 — iOS Build Recipe Verification Cycle

**Incident:** Coordinator reported 31 Razor errors when building iOS Release with net11p3 SDK per `docs/deploy-runbook.md` Step 2a.

**Captain's Response:** Pushback on root cause. Suspected obj/ contamination (Coordinator built with dirty build tree, no `dotnet clean` between SDK swaps).

**Verification Spawn:** Captain dispatched Wash to re-run build with proper hygiene (full `obj/` + `bin/` wipe, not just `dotnet clean`).

**Wash's Verdict:** Claim **VERIFIED**. With full wipe under net11p3, identical 31 errors reproduced. Razor SG regression in net11 Preview 3 is genuine, NOT contamination.

**Decision Outcome:** Recipe A (net11p3 swap) is broken. Recipe B (net10 GA + `-p:ValidateXcodeVersion=false`) is canonical. Documented in `.squad/decisions.md` 2026-04-29T14:32Z.

**Lesson Learned:**
- ✗ **Error:** Jump to conclusions about obj/ contamination without verifying hygiene first
- ✓ **Correction:** Full `obj/` + `bin/` wipe (not `dotnet clean`) is required between SDK swaps
- ✓ **Process Rule:** Wipe early, test, then wipe again — proper verification requires baseline repetition

**Process Improvement:** New hygiene rule added to decisions.md — when swapping SDKs via `global.json`, ALWAYS wipe `obj/` and `bin/` from affected projects. `dotnet clean` is not sufficient because Razor SG artifacts can collide.

---
