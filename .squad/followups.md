# Known Follow-Ups

**This file tracks non-blocking issues and discoveries that fall outside current tickets.**

---

## FU-1: PhraseConstituent Backfill Incomplete

**Status:** Discovered 2026-04-24 (Word vs Phrase batch)  
**Severity:** Low (design issue, not a bug)  
**Scope:** `VocabularyClassificationBackfillService.BackfillPhraseConstituentsAsync()`

### Problem

The 528 Phrases seeded in Captain's local SQLite are NOT linked to their constituent words. The `PhraseConstituent` join table has 0 rows. However:

- **Cascade code path is correct.** 27 integration tests pass (`VocabularyProgressService.RecordAttemptAsync` → passive exposure for constituents).
- **UI works.** Phrase mastery increments; constituents show correct exposure tally.
- **Tests comprehensive.** Classification priority, idempotency, boundaries, lemma-aware matching all verified.

### Root Cause

The constituent backfill was never called at startup. Only `VocabularyClassificationBackfillService` was wired into SyncService → `BackfillVocabularyCategoriesAsync()` runs for classification only. Constituents were intended to be backfilled at the same point but weren't hooked up.

### Why Not Fixed This Batch

- Doesn't block e2e feature validation (cascade works even with 0 constituent rows; constituents are added incrementally at import time).
- Separate backfill pass is needed; existing backfill pattern is single-pass-only.
- Needs decision: should this be a data-remediation ticket or left for next phrase-related work?

### Recommendation

Create follow-up ticket: "Backfill PhraseConstituent join rows for existing Phrases" (likely 1–2 hour task once decision made).

---

## FU-2: ~~Smart-Resource Idempotency~~ 

**Status:** ✅ RESOLVED 2026-04-24

Closed by two-layer fix:
- Per-type idempotent seed (`HashSet<SmartResourceType>` check in `InitializeSmartResourcesAsync`)
- Per-profile wire-up into `UserProfileRepository.GetAsync`

No follow-up needed; documented in `.squad/decisions.md`.

---

## FU-3: maui devflow Broker Daemon Mode (Silent Exit)

**Status:** Discovered 2026-04-24 (E2E tooling observation)  
**Severity:** Low (workaround available)  
**Scope:** `maui devflow` CLI v0.1.0-preview.4.26202.3

### Problem

Running `maui devflow broker start` in daemon mode silently exits with:
```
Missing file: Microsoft.Maui.DevFlow.Driver.runtimeconfig.json
```

No error logged; process just terminates.

### Workaround

Run in foreground + detach:
```bash
nohup maui devflow broker start --foreground &
```

This works reliably and is the current dev workflow for long-running maui devflow sessions (e.g., during e2e test batches).

### Recommendation

File issue upstream with maui devflow team (adospace/reactorui-maui or dotnet/maui). Include:
- Command: `maui devflow broker start`
- Expected: Daemon process persists
- Actual: Silent exit, no error
- Workaround: Foreground + nohup

Not critical for .NET 10 GA release; QoL improvement for future SDK updates.

---


## FU-4: Rewrite `docs/deploy-runbook.md` Step 2a — drop net11p3 global.json swap

**Status:** Discovered 2026-04-29 (import-content production deploy)  
**Severity:** Medium (runbook actively misleading; first-time deployer following it will hit 31 build errors)  
**Scope:** `docs/deploy-runbook.md` Step 2a — iOS Release build instructions

### Problem

Step 2a tells the operator to swap `global.json` to `11.0.100-preview.3.26209.122` (net11 Preview 3) before building iOS Release, then restore net10 after. This was the workaround for Xcode 26.3 vs net10 GA SDK mismatch.

It is now broken: the net11p3 Razor SDK cannot compile `src/SentenceStudio.UI/Pages/ImportContent.razor` and produces 31 errors (CS9348, CS0246, CS0426 on `@inject` directives and `LexicalUnitType.Phrase` references). The import-content feature is now in main, so anyone following the runbook will fail.

### Correct Recipe (verified 2026-04-29 deploy)

Stay on net10 GA SDK (`10.0.101`); pass `-p:ValidateXcodeVersion=false` to skip the Xcode version assertion:

```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
    -f net10.0-ios -c Release \
    -p:RuntimeIdentifier=ios-arm64 \
    -p:ValidateXcodeVersion=false
```

### Required Edits

1. Step 2a: remove the `cp global.json global.json.bak` / net11p3 swap / restore dance
2. Document `-p:ValidateXcodeVersion=false` as the standard flag for the Xcode 26.3 mismatch
3. Update top-of-file "## .NET SDK Selection in This Repo" notes if they reference the swap
4. Verify `global.json.bak` is not still in `.gitignore` for the wrong reason (or document why it remains)

### Cross-refs

- Orchestration log: `.squad/orchestration-log/2026-04-29T014444Z-publish-import-content.md`
- Decision: `.squad/decisions.md` — 2026-04-29T01:44Z entry
- Agent history: `.squad/agents/kaylee/history.md` Learnings (2026-04-29)

---
