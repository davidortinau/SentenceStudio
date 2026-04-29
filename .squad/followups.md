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

**Status:** Discovered 2026-04-29 (import-content production deploy); **VERIFIED 2026-04-29 by Wash (clean obj/bin rebuild)**  
**Severity:** Medium (runbook actively misleading; first-time deployer following it will hit 31 build errors)  
**Scope:** `docs/deploy-runbook.md` Step 2a — iOS Release build instructions

### Problem

Step 2a tells the operator to swap `global.json` to `11.0.100-preview.3.26209.122` (net11 Preview 3) before building iOS Release, then restore net10 after. This was the workaround for Xcode 26.3 vs net10 GA SDK mismatch.

It is now broken: **the net11p3 Razor SDK genuinely cannot compile `src/SentenceStudio.UI/Pages/ImportContent.razor` and produces 31 errors.** The import-content feature is in main, so anyone following the runbook will fail.

### Verification (2026-04-29, Wash)

Captain suspected the 31-error report might be obj/ contamination from the prior net10 build (Coordinator did NOT `dotnet clean` between SDK swaps). Wash re-ran with proper hygiene:

1. Backup global.json → swap to net11p3 → confirm `dotnet --version` = `11.0.100-preview.3.26209.122`
2. **Wipe `obj/` and `bin/` from `src/SentenceStudio.UI/` and `src/SentenceStudio.iOS/`** (full nuke, not just `dotnet clean`)
3. Build iOS Release: same command as runbook Step 2a
4. **Result: 31 errors, 316 warnings, 8.66s.** Identical error count and identical error signatures to Coordinator's first attempt.
5. Restore global.json → confirm net10.

**Conclusion: net11p3 is genuinely incompatible with `ImportContent.razor`.** The contamination hypothesis is FALSE. Build log: `.squad/orchestration-log/2026-04-29-wash-net11p3-clean-build.log`.

### Verified error signatures (sampled from clean rebuild)

```
ImportContent.razor(4,9): error CS0246: type 'IContentImportService' could not be found
ImportContent.razor(5,9): error CS0246: type 'LearningResourceRepository' could not be found
ImportContent.razor(7,9): error CS0246: type 'NavigationManager' could not be found
ImportContent.razor(9,9): error CS0246: type 'ILogger<>' could not be found
ImportContent.razor(4,31): error CS9348: A compilation unit cannot directly contain members
ImportContent.razor(1124,79): error CS0102: type 'ImportContent' already contains a definition for ''
ImportContent.razor(1128,83): error CS0101: namespace 'SentenceStudio.WebUI.Pages' already contains a definition for ''
ImportContent.razor(1126,25): error CS0426: type name 'Phrase' does not exist in 'LexicalUnitType'
ImportContent.razor(11,13): error CS0535: 'ImportContent' does not implement 'IDisposable.Dispose()'
```

The CS9348 / CS0246 cluster on `@inject` directive lines (4–10) plus the duplicate-definition CS0101/CS0102 with empty type/member names is a Razor source generator regression in the net11p3 Razor SDK — `@inject` directives are being parsed as raw C# instead of being lifted into the generated component partial class.

### Correct Recipe (verified 2026-04-29 deploy + Wash re-verification)

Stay on net10 GA SDK (`10.0.101`); pass `-p:ValidateXcodeVersion=false` to skip the Xcode 26.3 version assertion:

```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
    -f net10.0-ios -c Release \
    -p:RuntimeIdentifier=ios-arm64 \
    -p:ValidateXcodeVersion=false
```

This is now the **canonical** iOS Release recipe.

### Required Edits to deploy-runbook.md (separate task — DO NOT do here)

1. Step 2a: remove the `cp global.json global.json.bak` / net11p3 swap / restore dance entirely
2. Document `-p:ValidateXcodeVersion=false` as the standard flag for the Xcode 26.3 mismatch
3. Update top-of-file "## .NET SDK Selection in This Repo" notes if they reference the swap; note that the swap is **broken on main** until the Razor SDK regression is resolved upstream
4. Add a hygiene rule: **if you ever do swap SDKs via `global.json`, always wipe `obj/` and `bin/` first** — `dotnet clean` is not sufficient because Razor source-gen artifacts can collide
5. Verify `global.json.bak` handling in `.gitignore`

### Cross-refs

- Orchestration log: `.squad/orchestration-log/2026-04-29T014444Z-publish-import-content.md`
- Wash verification log: `.squad/orchestration-log/2026-04-29-wash-net11p3-clean-build.log`
- Decision drop: `.squad/decisions/inbox/wash-ios-build-recipe-verified.md`
- Decision: `.squad/decisions.md` — 2026-04-29T01:44Z entry
- Agent history: `.squad/agents/kaylee/history.md` Learnings (2026-04-29); `.squad/agents/wash/history.md` (2026-04-29 entry)

---
