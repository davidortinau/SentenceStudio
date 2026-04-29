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

## 2026-04-29 — Issue #179 Fix + Pre-Deploy Check Rewrite Publish

**Scope:** Production publish (Issue #179 fix, PR #181) + pre-deploy check migration to Flex Server (PR #182)

**Orchestration Arc:**

1. **Pre-Deploy Gate** — Wash's rewritten script (Flex Server validation) PASS (4/4 checks green)
2. **Azure Deploy** — `azd deploy` to production SUCCESS in 2:28
3. **Post-Deploy Validation** — 16 passed / 0 failed / 2 skipped / 2 warnings
4. **iOS Device Build (DX24)** — **New simplified recipe executed successfully:**
   - Stay on net10 GA SDK (no SDK swap needed)
   - Use `-p:ValidateXcodeVersion=false` flag
   - Build succeeded
   - Install to DX24 (CF4F94E3-A1C9-5617-A089-9ABB0110A09F) succeeded
   - Launch succeeded

**Key Innovation:**
- **Before:** Documented workaround required net11p3 SDK swap (`global.json` toggle) to suppress Xcode 26.3 mismatch
- **After:** Pass `-p:ValidateXcodeVersion=false` to net10 build; no swap needed
- **Impact:** Simplified iOS publish workflow. Xcode version assertion is a warning, not a blocker.

**What is Live:**
- Issue #179 fix deployed to production
- Pre-deploy validation now guarding every future deploy
- iOS DX24 running latest code

**New Canonical iOS Release Recipe:**
```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
    -f net10.0-ios -c Release \
    -p:RuntimeIdentifier=ios-arm64 \
    -p:ValidateXcodeVersion=false
```

**Documented:** `.squad/orchestration-log/2026-04-29T13-26-41Z-coordinator-publish.md`

