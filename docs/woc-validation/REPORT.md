# Word vs Phrase E2E Validation Report — MacCatalyst

**Date:** 2026-04-23
**Branch/Feature:** `LexicalUnitType` + `PhraseConstituent` + phrase-cascade exposure
**Target:** MacCatalyst (local SQLite `sstudio.db3`)
**Mode:** Option B — temporary `DevAuthService` DI swap, Steps 3–5 only, Steps 6–7 deferred to unit tests

---

## Verdict: **PARTIAL PASS**

| Step | Outcome | Evidence |
|---|---|---|
| 3. Shadowing with Word → AI-generated sentence | **PASS** (code + visual) | `ShadowingService.cs` L91–146 branches on `LexicalUnitType.Word` and calls AI; Shadowing page loaded successfully for a Word-heavy resource (`step3-shadowing.png`) |
| 4. Shadowing with Phrase → term used as-is | **PASS** (code path verified) | `ShadowingService.cs` L92–119 creates `ShadowingSentence { TargetLanguageText = word.TargetLanguageTerm }` for `Phrase/Sentence/Unknown` without AI round-trip |
| 5. "Phrases" smart resource appears & populates | **FAIL — data backfill gap** | Only 3 smart resources present in DB (`Daily Review`, `New Words Practice`, `Struggling Words`). Learning Resources UI shows 40 resources, **no "Phrases" entry** (`step5-resources.png`). Root cause below. |
| 6. Phrase attempt cascades exposure to constituents | **SKIPPED** — covered by `tests/SentenceStudio.UnitTests/Integration/PhraseCascadeIntegrationTests.cs` (10 tests) |
| 7. Word attempt does NOT cascade | **SKIPPED** — covered by `tests/SentenceStudio.UnitTests/Integration/WordOnlyNoCascadeRegressionTests.cs` (5 tests) |

---

## Steps 3 & 4 — Shadowing Word/Phrase branching

Verified by code inspection of the committed feature and live app launch.

`src/SentenceStudio.AppLib/Services/ShadowingService.cs`:
```csharp
var wordsNeedingAi = _words.Where(w => w.LexicalUnitType == LexicalUnitType.Word).ToList();
var wordsAsIs     = _words.Where(w => w.LexicalUnitType == LexicalUnitType.Phrase
                                   || w.LexicalUnitType == LexicalUnitType.Sentence
                                   || w.LexicalUnitType == LexicalUnitType.Unknown).ToList();
// ... wordsAsIs → new ShadowingSentence { TargetLanguageText = word.TargetLanguageTerm } (no AI)
// ... wordsNeedingAi → AI template prompt, parse ShadowingSentencesResponse
```

The app launched, authenticated via the dev swap, and rendered the Shadowing activity against a mixed-type Korean resource without exception (`step3-shadowing.png` shows a shadow sentence at 1/10). The branching is deterministic — if `LexicalUnitType == Word`, the item goes through `_aiService.SendPrompt`; if `Phrase/Sentence/Unknown`, the `TargetLanguageTerm` is passed through verbatim.

DB distribution confirms the branches are exercisable in this DB:
- `LexicalUnitType=1` (Word): 1910 rows
- `LexicalUnitType=2` (Phrase): 528 rows
- `LexicalUnitType=3` (Sentence): 40 rows
- `LexicalUnitType=0` (Unknown): 117 rows

---

## Step 5 — FAIL: "Phrases" smart resource missing

Only three smart resources exist in the local DB:

| Id | Title | SmartResourceType |
|---|---|---|
| 9cd3c461-… | Daily Review | `DailyReview` |
| c06a588c-… | New Words Practice | `NewWords` |
| 2182b531-… | Struggling Words | `Struggling` |

The `Phrases` smart resource (`SmartResourceType_Phrases = "Phrases"`, defined at `src/SentenceStudio.Shared/Services/SmartResourceService.cs:24`) is **never created** for this user.

### Root Cause
`SmartResourceService.InitializeSmartResourcesAsync` (L55–61) short-circuits when ANY smart resources already exist:

```csharp
var existingSmartResources = await _resourceRepo.GetSmartResourcesAsync();
if (existingSmartResources.Any())
{
    _logger.LogInformation("✅ Smart resources already exist, skipping initialization");
    return;
}
```

Because the 3 legacy smart resources pre-date the `Phrases` feature, the `if (existingSmartResources.Any()) return;` guard swallows the addition for every upgraded user. A fresh install creates all 4; an upgraded install keeps only the original 3.

### Fix (owner follow-up, not this validation's scope)
Replace the blanket early-return with a per-type idempotent check:

```csharp
var existingTypes = existingSmartResources.Select(r => r.SmartResourceType).ToHashSet();
// create + refresh each of DailyReview / NewWords / Struggling / Phrases only if missing
```

---

## Known Follow-Ups

### FU-1: PhraseConstituent backfill not populated in local dev DB
- **Finding:** `PhraseConstituent` table has **0 rows** despite 528 phrases seeded with `LexicalUnitType=2`. Cascade logic (`VocabularyProgressService.cs:214–258`) queries `PhraseConstituents WHERE PhraseWordId=…` and finds nothing, so no constituent exposures fire.
- **Impact:** The cascade silently no-ops for all 528 phrases on this device. Feature code is correct and covered by unit tests (`PhraseCascadeIntegrationTests`, `WordOnlyNoCascadeRegressionTests`), but real-world phrase attempts produce zero passive exposures until the constituency graph is populated.
- **Next:** The `VocabularyClassificationBackfillService` (present in the working tree — untracked file `src/SentenceStudio.Shared/Services/VocabularyClassificationBackfillService.cs`) needs a companion constituent-builder, or an AI-driven extractor must run over existing phrases to produce `(PhraseWordId, ConstituentWordId)` rows.

### FU-2: "Phrases" smart resource not created for upgraded users
- **Finding:** Described above. Affects every user whose DB existed before the Phrases smart resource shipped.
- **Impact:** Users cannot filter to a phrase-only practice list through the smart-resource picker on existing installs. The feature is invisible.
- **Next:** Per-type idempotent creation in `InitializeSmartResourcesAsync`, plus a call path that runs on every app start (already guaranteed since `InitializeSmartResourcesAsync` is invoked on profile load).

---

## Final confirmations

| Check | Result |
|---|---|
| (a) DI swap reverted (`ServiceCollectionExtentions.cs`) | ✅ `git diff` clean; file reports no change |
| (a') DevAuthService reverted (temp JWT helper removed) | ✅ `git diff` clean; file reports no change |
| (b) No feature files staged | ✅ Only working-tree (unstaged) changes remain — all are pre-existing feature edits, none mine |
| (c) Both DB backups present | ✅ `sstudio.db3.bak.20260423-202814` (6758400 B), `sstudio.db3.bak2.20260423-205117` (6791168 B) |
| (d) Row counts unchanged | ✅ VocabularyWord=2595, VocabularyProgress=1745, Challenge=197, UserProfile=1 |
| (e) PhraseConstituent still 0 rows | ✅ No seeding occurred |

## Artifacts
- `docs/woc-validation/01-initial.png` — pre-swap Sign In page (blocked state)
- `docs/woc-validation/02-after-devauth.png` — dashboard reached after DevAuth swap + JWT patch
- `docs/woc-validation/03-dashboard.png` — (attempted dashboard snapshot; replaced by step3-shadowing)
- `docs/woc-validation/step3-shadowing.png` — Shadowing activity rendering (Korean resource, mixed Word/Phrase/Sentence pool)
- `docs/woc-validation/step5-resources.png` — Learning Resources list; **no Phrases entry visible**
- `docs/woc-validation/tree1.txt`, `dom1.txt`, `logs1.txt` — raw DevFlow + log captures

---

## Step 5 re-verify (Jayne, post-Wash fix)

**Verdict: ❌ FAIL — Phrases smart resource still absent on upgraded DB.**

### What was checked

Wash's fix replaced the blanket early-return in `SmartResourceService.InitializeSmartResourcesAsync` with a per-type idempotency check (creates only missing smart resource types — `src/SentenceStudio.Shared/Services/SmartResourceService.cs:49-131`). The change is present in source and compiled into the running binary.

### Build + launch

- Rebuilt MacCatalyst with the updated `SmartResourceService.cs` (source mtime `Apr 23 21:27`, bundle `SentenceStudio.AppLib.dll` mtime `Apr 23 21:33` — fix is in the live binary).
- Temp DI swap: `ServiceCollectionExtentions.cs` → `DevAuthService`.
- Additional temp patch to `MauiAuthenticationStateProvider.GetAuthenticationStateAsync` (fast path) so a null DevAuth token falls back to `CreateOptimisticPrincipal()` — same "JWT patch" category noted in the prior Step 5 session. Without it, `AuthorizeRouteView` rejects the empty `ClaimsIdentity` and redirects to Sign In.
- App launched (PID 56234, MacCatalyst, agent on port 10223). Dashboard reached.

### Evidence

**DB after launch (unchanged from before the Wash fix):**

```
sqlite> SELECT lr.Title, lr.SmartResourceType FROM LearningResource
        WHERE IsSmartResource=1 ORDER BY CreatedAt;
Daily Review     | DailyReview
New Words Practice | NewWords
Struggling Words | Struggling

sqlite> SELECT COUNT(*) FROM LearningResource WHERE SmartResourceType='Phrases';
0

sqlite> SELECT COUNT(*) FROM ResourceVocabularyMapping rvm
        JOIN LearningResource lr ON rvm.ResourceID=lr.Id
        JOIN VocabularyWord vw ON rvm.VocabularyWordID=vw.Id
        WHERE lr.SmartResourceType='Phrases' AND vw.LexicalUnitType NOT IN (2,3);
0   (vacuously — resource does not exist)
```

**UI (Vocabulary → "All Resources" dropdown, via CDP):**

```
["All Resources","Olivia 쌤",…,"Struggling Words","New Words Practice","Daily Review",…]
```

No `Phrases` entry. Same 3-legacy-smart-resources state as the original Step 5 failure.

**Screenshot:** `docs/woc-validation/step5-phrases-resource.png` (Learning Resources list — smart resources don't surface here either, consistent with prior `step5-resources.png`).

**Screenshot of Phrases contents:** not produced — the resource does not exist, so there is nothing to open.

### Root cause — deeper than the blanket early-return

Wash's per-type idempotency change is correct in isolation (unit test `SmartResourcePhrasesTests` exercises it). **But no production code path calls `InitializeSmartResourcesAsync`.**

```
$ grep -rn "InitializeSmartResourcesAsync" src/ --include="*.cs"
src/SentenceStudio.Shared/Services/SmartResourceService.cs:49:  public async Task InitializeSmartResourcesAsync(...)
(no other matches)

$ grep -rn "InitializeSmartResourcesAsync" tests/ --include="*.cs"
tests/SentenceStudio.UnitTests/Services/SmartResourcePhrasesTests.cs: (test-only)
```

`SmartResourceService` is registered in DI (`CoreServiceExtensions.cs:80 — services.AddSingleton<SmartResourceService>();`) but never resolved or invoked from app startup, profile load, sign-in, or the resources page. The three existing smart-resource rows were seeded on 2025-11-26 and have persisted unchanged since — they were not created by this method running in production.

So the fix Wash delivered is a **necessary but insufficient** condition. Making the Phrases smart resource appear on an upgraded install requires **both**:
1. ✅ Per-type idempotency inside `InitializeSmartResourcesAsync` (done — Wash).
2. ❌ A startup / profile-load call site that actually invokes it (**still missing**).

### Row-count sanity (unchanged, DB intact)

| Table            | Count |
|------------------|-------|
| `UserProfile`    | 1     |
| `VocabularyWord` | 2595  |
| `VocabularyWord` where `LexicalUnitType=2` (Phrase) | 528 |

### Final confirmations

| Check | Result |
|---|---|
| DI swap reverted (`ServiceCollectionExtentions.cs`) | ✅ `git diff` clean |
| Auth-state-provider patch reverted | ✅ `git diff` clean |
| No new staged/unstaged changes from this run | ✅ `git status --porcelain` reports nothing for the two touched files |
| DB not wiped / app not uninstalled | ✅ Captain's sandbox container untouched, both backups still present |
| Row counts match pre-run | ✅ UserProfile=1, VocabularyWord=2595, PhraseWords=528 |
| App killed | ✅ PID 56234 terminated |

### Recommendation for Coordinator

Loop back to Wash (or whichever agent owns the startup/profile-load path) to add an invocation of `InitializeSmartResourcesAsync` during app boot — e.g. after sign-in or during first profile load per user/target-language. Candidates:
- `UserProfileRepository.GetAsync` right after `MigrateAsync()` / `BackfillUserProfileIdsAsync()`.
- A `MauiProgram` / `CoreServiceExtensions` startup hook that runs once per session.
- Inside the existing auth success callback that also triggers sync registration.

Unit test coverage already exists (`SmartResourcePhrasesTests`) — the miss is purely a wiring gap.

### Artifacts

- `docs/woc-validation/step5-phrases-resource.png` — Learning Resources page (no smart resources surfaced; dropdown evidence captured via CDP into this report).

---

## Step 5 re-verify #2 (post-wire-up) — 2026-04-23 (Jayne)

**Verdict: PASS**

### What changed since last run
Wash wired `SmartResourceService.EnsureSmartResourcesAsync(profile)` into `UserProfileRepository.GetAsync`, guarded per-profile and non-fatal on error. Per-type idempotency fix from the first pass is still in place. Build was clean (0 errors) after re-applying auth bypass patches for UI verification.

### Evidence

**DB — smart resources after app launch:**
```
Daily Review|DailyReview       (pre-existing, 2025-11-26)
New Words Practice|NewWords    (pre-existing, 2025-11-26)
Struggling Words|Struggling    (pre-existing, 2025-11-26)
Phrases|Phrases                (NEW, created 2026-04-23 21:49:41) ← wire-up fired
```

**Off-type cross-check (MUST be 0):**
```sql
SELECT COUNT(*) FROM ResourceVocabularyMapping rvm 
  JOIN LearningResource lr ON rvm.ResourceID=lr.Id 
  JOIN VocabularyWord vw ON rvm.VocabularyWordID=vw.Id 
  WHERE lr.SmartResourceType='Phrases' AND vw.LexicalUnitType NOT IN (2,3);
→ 0  ✅
```

**Mapping rows for Phrases = 0** — this is *by design*. Phrases is a **dynamic smart resource**: items are resolved at query time via `SmartResourceService.GetPhrasesVocabularyIdsAsync()`, which enforces `LexicalUnitType IN (Phrase, Sentence)` at the LINQ level (SmartResourceService.cs lines 325-326). Predicted dynamic payload: **403 items** (from `VocabularyProgress JOIN VocabularyWord WHERE LexicalUnitType IN (2,3)`).

Sanity: the Resource *edit* page shows "Vocabulary (0 words)" for **all four** smart resources (including NewWords which has 1368 mapping rows), confirming the edit view simply doesn't render smart-resource contents — it is not Phrases-specific and not a regression.

### UI evidence
- `docs/woc-validation/step5-phrases-resource-v2.png` — Learning Resources page shows **Phrases** card (top-left) with "Smart Vocabulary List • Korean", dated 4/23/2026. CDP scan confirms all 4 smart resource names present in DOM.
- `docs/woc-validation/step5-phrases-contents-v2.png` — Phrases detail page shows green "Smart Resource — Auto-updated by the system" banner, title "Phrases", description "Practice all your phrase and sentence vocabulary", tags `system-generated,dynamic,phrases`.
- `docs/woc-validation/step5-after-bypass-v2.png` — Dashboard after DevAuthService bypass (context shot).
- `docs/woc-validation/step5-phrases-vocab-filter-v2.png` — Vocabulary page with `resource:Phrases` chip (stored-mapping filter → 0, expected for dynamic resource).

### Row-count sanity
| Table | Count |
|---|---|
| UserProfile | 1 |
| VocabularyWord | 2595 |
| VocabularyWord where LexicalUnitType=2 (Phrase) | 528 |
| VocabularyWord where LexicalUnitType=3 (Sentence) | 40 |

### Final confirmations
| Check | Result |
|---|---|
| DI swap reverted (`ServiceCollectionExtentions.cs`) | ✅ no diff |
| Auth-state-provider patch reverted | ✅ no diff |
| No stray `TEMP-JAYNE-AUTH-BYPASS` markers | ✅ `grep -c` = 0 in both files |
| Only expected files modified | ✅ UserProfileRepository.cs, SmartResourceService.cs, migration renames, squad bookkeeping |
| DB not wiped / app not uninstalled | ✅ Captain's container sandbox intact |
| Row counts match baseline | ✅ UserProfile=1, VocabularyWord=2595, Phrase=528 |
| App killed | ✅ PID 60367 terminated |

### Decision-tree resolution
"Phrases resource exists in DB AND UI, populated only with type 2/3 → **PASS**"

Off-type mapping count = 0 requirement is satisfied. Dynamic resolution path is the canonical consumer of Phrases items and is strictly type-gated in code. Wash's wire-up fix resolved the gap identified in the previous run.

— Jayne
