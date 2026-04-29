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


## FU-4: ~~Rewrite `docs/deploy-runbook.md` Step 2a — drop net11p3 global.json swap~~

**Status:** ✅ RESOLVED 2026-04-29T21:00Z (root cause identified, filed upstream, workaround applied)
**Severity:** Was Medium; now informational
**Scope:** `docs/deploy-runbook.md` Step 2a — iOS Release build instructions

### Resolution summary

Earlier framing (below) treated this as "net11p3 broken on main, runbook actively misleading." **That framing was wrong.** Corrected facts:

1. **net11p3 is not broadly broken.** Captain verified with a clean `dotnet new maui-blazor` project at `~/work/PeeThreeRegression` — builds and deploys fine.
2. **Narrow Razor SG regression** — switch expressions returning `RenderFragment` lambdas with inline Razor markup; SG emits synthetic members with empty names → CS0101/CS0102, cascading CS0246/CS9348 on `@inject` lines.
3. **Filed upstream:** https://github.com/dotnet/razor/issues/13117 (Wash, 2026-04-29) — repro zip: `~/work/peethree-repro-artifacts/peethree-net11p3-repro.zip` (252 KB).
4. **The only file in the repo that triggered the bug was `src/SentenceStudio.UI/Pages/ImportContent.razor`.** Kaylee refactored it (2026-04-29) — replaced two `RenderTypeBadge`/`RenderStatusBadge` switch-expression-of-RenderFragment helpers with tuple-meta helpers (`GetTypeBadgeMeta`, `GetStatusBadgeMeta`) returning `(CssClass, IconClass, Label)` consumed inline.
5. **Net result:** Step 2a (the `global.json` swap to net11p3 for iOS device publish) **is no longer needed** for SentenceStudio because (a) we use **net10 + `-p:ValidateXcodeVersion=false`** as the canonical iOS Release recipe, AND (b) the only file that triggered the bug has been refactored, so the swap path would also succeed if anyone needed it.
6. **Hygiene rule still valid:** if you ever swap SDKs via `global.json`, wipe `obj/` AND `bin/` first — `dotnet clean` is not sufficient.

### Verified path forward

**Canonical iOS Release recipe** (no SDK swap needed):

```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
    -f net10.0-ios -c Release \
    -p:RuntimeIdentifier=ios-arm64 \
    -p:ValidateXcodeVersion=false
```

### Required edits to deploy-runbook.md (still a separate task)

1. Step 2a: remove the `cp global.json global.json.bak` / net11p3 swap / restore dance.
2. Document `-p:ValidateXcodeVersion=false` as the standard flag for the Xcode 26.3 mismatch.
3. Update top-of-file ".NET SDK Selection" notes to reflect that the swap is **optional** (not broken) and only relevant if upstream guidance changes.
4. Add the obj/bin wipe hygiene rule.
5. Reference the upstream issue (https://github.com/dotnet/razor/issues/13117) so future readers understand the historical context.

### Cross-refs

- **Upstream issue:** https://github.com/dotnet/razor/issues/13117
- **Corrected decision:** `.squad/decisions.md` — 2026-04-29T21:00Z entry (and corrections to 2026-04-29T14:32Z entry)
- **Archived decisions:** `.squad/decisions/archive/kaylee-renderfragment-switch-pattern-banned.md`, `.squad/decisions/archive/wash-net11p3-razor-sg-repro.md`
- **Repro artifacts:** `~/work/peethree-repro-artifacts/peethree-net11p3-repro.zip`
- Orchestration log: `.squad/orchestration-log/2026-04-29T014444Z-publish-import-content.md`
- Wash verification log: `.squad/orchestration-log/2026-04-29-wash-net11p3-clean-build.log`

### Original entry (preserved for history)

The original framing called this "net11p3 broken on main, runbook actively misleading; first-time deployer following it will hit 31 build errors." That was technically true at the time the entry was written, but the framing missed the narrowness of the regression. Wash's clean rebuild correctly falsified the obj/-contamination hypothesis but stopped short of isolating the trigger pattern; the team did that next, and Captain's clean `PeeThreeRegression` project provided the counter-evidence that net11p3 itself is fine.

---
