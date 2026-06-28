# Scribe Archive — 2026-06-26

Archived by Scribe because decisions.md exceeded 51200 bytes. Entries older than 7 days (cutoff 2026-06-19) were moved out of the active decision log.

---

## Archived from decisions.md on 2026-06-26

### 2026-06-11 — Vocabulary Bootstrap Fix (Wash, Zoe Review, Jayne E2E)

**Session:** Vocab-less plan bootstrap fix (2nd bug from same morning as preview-mismatch fix)  
**Branch:** `squad/vocab-preview-quiz-mismatch-fix`  
**Commit landed:** `386b4550` — "Bootstrap vocabulary review for users with no SRS progress"  
**Status:** 🟢 Ready for Captain's pre-push approval. 583/583 unit tests passing. Mac Catalyst E2E verified 6/6 acceptance criteria.

#### Agents & Verdicts

- **Wash (Backend Dev)** — Diagnosed root cause, implemented bootstrap path + sentinel fix + wording fix. 🟢 **Implemented, logged, live-diagnosed, unit-tested.**
- **Zoe (Lead Reviewer)** — Code review bootstrap fix (🟢 after stale comment deleted), then wording-fix delta (🟢). **Minor fix required: delete stale "THIS WILL LIKELY FAIL" comment at lines 117–119 of ResourceSelectionTests.cs per AGENTS.md directive.**
- **Jayne (Tester)** — Initial E2E failed due to stale binary (running old code). After clean rebuild of all bin/obj, E2E PASSED all 6 acceptance criteria. **Key learning: partial cleans leave stale DLLs in MonoBundle; nuke AppLib/bin and AppLib/obj too.**

#### Core Decision

When deterministic planning finds fewer than 5 SRS-due vocabulary rows, it now queries user-scoped mapped vocabulary with no `VocabularyProgress` row and builds a bootstrap focus cohort. Bootstrap cohort capped at 15 words, gets a `VocabularyReview` activity plus normal focus-vocabulary propagation. Preserves Phase 1 contract: `PreviewWords` is exactly a projection of `FocusVocabularyIds`.

#### Rationale

Dashboard "New" vocabulary is computed from resource mappings without progress rows. Planner previously only read SRS-due progress rows, making first-time learners look vocabulary-less despite thousands of mapped words. Bootstrap path detects this gap and surfaces bootstrapped focus work. Capping at 15 keeps first session manageable.

#### Implementation Details

- `VocabularyProgressRepository.GetUnseenMappedVocabularyAsync` — new method returning synthetic, untracked `VocabularyProgress` objects (plan preview/selection only). Returns empty for empty userId, never unfiltered result. Multi-tenant safe per canonical post-May-2026 hotfix pattern.
- `DeterministicPlanBuilder` — new `BootstrapVocabularyTarget = 15`. When `DueWords < 5`, blends due + unseen mapped words up to bootstrap cap. `TotalDue` property represents selected cohort size during bootstrap, not full library.
- Resource last-use display — uses nullable `DaysSinceLastUse` (was `int` with 999 sentinel). Null = truly never used. Resources outside 30-day ranking lookback surface real age. Old "Fresh resource (not used for X days)" wording removed.
- Wording cascade for selection reason — four-branch: never used → "New resource for today's plan"; >30 days → "Resource not used recently"; 1–30 days → "Last used N days ago"; 0 → vocab-match or "Best available option".

#### Test & Coverage

- **New tests:** `PlanWithZeroDueAndManyUnseenMapped_SchedulesVocabActivityFromUnseenCohort`, `EmptyDueAndEmptyUnseenMapped_NoVocabActivity`, `ManyDueAndManyUnseen_PrefersDueNotBootstrap`, `PlanBootstrap_PreviewWordsEqualFocusVocabularyIds`, `ResourceUsed8DaysAgo_ShouldUseNeutralSelectionReason_NotFresh`, `ResourceUsed31DaysAgo_ShouldNotReportAs999Days`.
- **Regression:** Previously-known-failing `ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed` now passes (999-day sentinel bug fixed). Test count: 583/583 passing.
- **E2E verified:** Mac Catalyst dashboard shows 3 activities (VocabReview + Reading + VocabMatching). FocusVocabularyFacts populated with 15 word IDs. Preview words match activity focus set. Resource footer says "Last used 8 days ago" not "Fresh resource".

#### Files Changed (5 files, +430/-78 net)

- `src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs` (+125/-14) — new `GetUnseenMappedVocabularyAsync`
- `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs` (+175/-73) — bootstrap logic, wording cascade, DaysSinceLastUse nullable
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderUnseenVocabBootstrapTests.cs` (new, 135 lines)
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderResourceSelectionTests.cs` (+28/-5) — new edge cases, stale comment removed per Zoe review
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderVocabularyReviewTests.cs` (+3/-3) — minor adjustments

#### Multi-Tenant Scoping Audit

`GetUnseenMappedVocabularyAsync` (line 354):
- Empty userId → log warning + return empty list ✓
- LearningResources join scoped `UserProfileId == userId` ✓
- Existence filter on VocabularyProgresses scoped `UserId == userId` ✓
- Pattern matches canonical post-May-2026 hotfix from `LearningResourceRepository` ✓

#### Zoe Notes (non-blocking)

- **Smart-resource inclusion:** `GetUnseenMappedVocabularyAsync` doesn't filter `!IsSmartResource`. Design question for Captain: should bootstrap ever include smart-resource vocab, or real-resources-only? No-op for Captain's current scenario (smart resources need SRS progress to get mapped). Worth a follow-up if direction changes.
- **`wordsByResource` semantic shift:** OLD grouped full due pool → NEW groups top-20 selected words. Same answer most time, occasionally different. Intentional (activity uses selected words, so resource chosen from them), but worth noting for archaeology.
- **Pre-existing NRE latent:** `LlmPlanGenerationService.cs:68,111` dereference `PrimaryResource.Title` without null check. Fallback path returns `PrimaryResource = null`. Not introduced here, but bootstrap makes it slightly more reachable. Follow-up issue worth filing; not blocking.
- **Grammar nit:** "Last used 1 days ago" (should be "day"). Cosmetic, Captain's call if to fix before ship.

#### Jayne E2E Findings (reference only)

- First E2E failed: running pre-bootstrap code. After clean-build (deleted MacCatalyst/bin, MacCatalyst/obj, Shared/bin, Shared/obj, **AppLib/bin, AppLib/obj**), produced correct Jun 11 13:49 binary with new code.
- Binary inspection (UTF-16 scan): old `"need 5+"` string absent, new `Bootstrapping vocab`, `unseen mapped`, `BootstrapVocabularyTarget` symbols present. ✓
- Catalyst squad-jayne test user profile not in local SQLite, but fallback pattern resolved to David's profile (ba20bcc5-ab2a…) which has 3253 unseen mapped words. Bootstrap triggered correctly. Test environment artifact, not a bug.
- VocabularyReview block built without matched resource (wordsByResource group didn't meet min-due threshold in test env). Doesn't affect acceptance criteria; Preview button in Study Insight may only appear when vocab block has resource — minor UX edge case.

#### Related Prior Work

- **Commit 750138b5** (2026-06-09): Phase 1 Focus Vocabulary — asymmetry fix between mobile (ProgressService) and API (PlanService) NarrativeFacts/RationaleFacts persistence. Preview button now renders stably. Bootstrap builds atop this foundation.
- **Commit 3c7a4cc** (2026-05-XX): Fallback plan rationale symmetry — Rationale: null, all 3 facts columns now protected by `?? planRow.X` coalesce.

#### Blockers & Gating

- **Required before commit:** Delete stale "THIS WILL LIKELY FAIL" comment at lines 117–119 of `DeterministicPlanBuilderResourceSelectionTests.cs` (Zoe review finding, 30-second fix per Reviewer Rejection Protocol).
- **E2E gate passed:** 6/6 acceptance criteria verified on Mac Catalyst clean-build binary.
- **Pre-push gate:** Awaiting Captain's explicit approval per `.github/copilot-instructions.md` pre-push review directive.


---

### 2026-06-10T03:35:00Z: Timezone fix routes plan dates through IPlanDateContext
**By:** Captain (davidortinau) (via Copilot, Claude Opus 4.7 xhigh)
**What:**
- `ProgressCacheService` plan methods (`GetTodaysPlan`, `SetTodaysPlan`, `InvalidateTodaysPlan`, `UpdateTodaysPlan`) now take an explicit `DateTime date` parameter instead of computing their own ambient "today". Cache TTL is computed as "expires at next UTC midnight after the keyed date" with a 1-minute floor to avoid negative/zero TTLs.
- `ProgressService.ResolveTodayKey()` resolves `IPlanDateContext` via `IServiceProvider.GetService` per-call. Returns `UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)` — the UTC midnight that opens the user's local day. Falls back to `DateTime.UtcNow.Date` (kinded Utc) with a debug log if IPlanDateContext is unavailable (older test rigs).
- All 4 `var today = DateTime.UtcNow.Date;` lines in plan methods (GenerateTodaysPlanAsync, MarkPlanItemCompleteAsync, UpdatePlanItemProgressAsync, StartAdHocSessionAsync) replaced with `ResolveTodayKey()`. All 12 internal `_cache.*` call sites pass an explicit date.
- `IProgressService.GetCachedPlanAsync` and `ClearCachedPlanAsync` now take `DateTime? date = null` — null delegates to internal `ResolveTodayKey()`. Index.razor calls these without a date arg.
- Regression-guarded by `ProgressCacheServiceTimezoneTests.cs` (7 tests, 3 of which fail on the simulated bug — proven non-vacuous).
- 574/574 unit tests pass (567 baseline + 7 new TZ guards).
**Why:** Captain reported Vocab Quiz showed different words than the Preview. The root cause is deeper than the May 2026 followup note suggested. `ProgressCacheService` was keying by `DateTime.Today` (LOCAL), while `ProgressService` was keying by `DateTime.UtcNow.Date`. Both violate the existing `IPlanDateContext` contract (PlanService/DeterministicPlanBuilder/GeneratedPlanValidator already use it). For a Pacific user at 9pm local, `DateTime.UtcNow.Date` = tomorrow's date, so `WHERE Date = @today` matched no row → fallback plan generated with different vocabulary → mismatch with the preview. The DailyPlan model docstring already says the Date column should be `IPlanDateContext.UserLocalDate.ToDateTime(MinValue, Utc)`. The fix aligns ProgressService and ProgressCacheService with that contract.
**Scope of this commit:** Separate PR from the FocusVocabularyIds contract + NarrativeFacts persistence + PlanFactsSerializer hoist work (those are uncommitted in the same checkout — Captain's pre-push /review gate hasn't run yet).
**Files touched:**
- src/SentenceStudio.Shared/Services/Progress/ProgressService.cs
- src/SentenceStudio.Shared/Services/Progress/ProgressCacheService.cs
- src/SentenceStudio.Shared/Services/Progress/IProgressService.cs
- src/SentenceStudio.UI/Pages/Index.razor
- src/SentenceStudio.Shared/Services/SmartResourceService.cs
- tests/SentenceStudio.UnitTests/Integration/ProgressCacheServiceTests.cs (signature updates)
- tests/SentenceStudio.UnitTests/Services/Progress/ProgressServiceFocusVocabularyContractTests.cs (signature updates)
- tests/SentenceStudio.UnitTests/Integration/ProgressCacheServiceTimezoneTests.cs (NEW)
**Known out-of-scope follow-ups:**
- ProgressService.GetStreakInfoAsync at line 1072: streak math still uses raw `DateTime.UtcNow.Date`. Display-layer drift only; defer.
- Index.razor:1090 (flashcard nav fallback): `todaysPlan?.GeneratedForDate.Date ?? DateTime.UtcNow.Date`. Defer.
- The "BannedSymbols.txt" referenced in the IPlanDateContext docstring doesn't actually exist in the repo. Would be a nice future build-time guard but is not blocking this PR.
# Duplicate merge decision criteria

## Decision

Vocabulary duplicate cleanup should distinguish merge safety from keeper selection:

- Learning progress is combined during merge and is not a user decision criterion.
- Resource associations are combined during merge and are not a user decision criterion.
- Automatic merge safety depends on stable semantic identity: normalized target term, native term, language, and lexical unit type must match.
- Keeper selection should prioritize encoding strength and memory aids, not resource count.
- Duplicate review UI should hide raw IDs and resource counts from the primary decision view.
- Batch merge and single-group merge use the same recommendation logic; they must not imply different "auto" versus "keep best" behavior.

## Rationale

The user needs to decide only when merge safety is uncertain or when retained memory/encoding content differs. Showing progress, resource counts, and raw IDs creates noise because those values are preserved or internal implementation details.

## Follow-up

If future duplicate merge work combines complementary memory aids field-by-field, update this decision to describe conflict handling for competing non-empty mnemonic, image, and audio fields.


---

# RCA: Mastery Calibration (Concern #1) and Plan Staleness (Concern #2)

Date: 2026-06-17T15:10:57-05:00
By: Zoe (Lead)
Status: Decision record. Investigation only -- no product code changed in this turn.
Surface: Concern #1 -- Production iOS (DX24), dave@ortinau.com. Concern #2 -- Production Webapp (Blazor InteractiveServer on Azure Container Apps, West US 3) plus iPhone DX24 via CoreSync.

References:
- .squad/decisions/inbox/lla-concern1-mastery-calibration.md
- .squad/decisions/inbox/wash-concern2-plan-staleness.md
- .squad/decisions/inbox/jayne-mastery-regression-tests.md

---

## Concern #1 -- Mastery Calibration Decoupled from SM-2 Interval

### Verdict

CALIBRATION MISAPPLICATION (bug to fix). The /12 divisor at VocabularyProgressService.cs:27 is correctly calibrated for the purpose it was originally tuned for -- in-session quiz rotation pacing (Issue #191, comment at lines 21-26). It is incorrectly governing a second, semantically different concern: the lifetime "do you know this word?" gate (IsKnown) and the learner-facing mastery percentage on the Learning Details sheet.

Adjudication of the LLA / Jayne framings:

- Jayne is correct that the /12 behavior is "by design as coded" -- the divisor and the IsKnown gates are both explicit and pinned by the existing tests in MasteryAlgorithmIntegrationTests.cs. Verified at VocabularyProgressService.cs:27 and VocabularyProgress.cs:11, 14, 109-113.
- LLA is correct that this same scalar is doing two jobs and only one of them was justified by the Issue #191 simulation. The mastered-override path at VocabularyProgressService.cs:162-174 (which exists specifically to align SM-2 with the Known gate by clamping interval to 60 when the word is mastered) cannot fire when 5/5-correct words sit at 0.625 mastery. That dead-code path is the structural tell that the calibration is misapplied, not by-design.

Consequence in production: a word with EffectiveStreak 7.5 (boda, mun) sits at 0.625 mastery and SM-2 interval 230, while a word with EffectiveStreak 7.0 (seonsaengnim) sits at 0.583 mastery and interval 230. SM-2 says "I will not ask again for 230 days because the learner clearly knows it" while the status model says "Learning, 62%". These signals contradict. Both LLA and Jayne agree on the math; the disagreement is purely about whether the contradiction is intended product behavior. It is not.

This is also a UX complaint (the Learning Details sheet shows a discouraging 62% despite mastery), but the UX symptom is downstream of the calibration misapplication. Fix the model and the UX symptom resolves.

### Chosen fix direction

LLA's Option 1: SRS-Interval-Aware Known Pathway + display srsBonus.

File and lines to change (recommendation only -- do NOT implement in this turn):

- VocabularyProgress.cs:109-113 -- add a third disjunct to IsKnown:

      || (ReviewInterval >= SRS_KNOWN_INTERVAL_THRESHOLD
          && Accuracy >= SRS_KNOWN_ACCURACY_THRESHOLD
          && ProductionInStreak >= 1);

  with new constants `SRS_KNOWN_INTERVAL_THRESHOLD = 60` and `SRS_KNOWN_ACCURACY_THRESHOLD = 0.80f`.

- VocabularyProgressService.cs:111-115 -- add an `srsBonus` to the displayed mastery calculation, using the interval value BEFORE the current attempt's SM-2 update:

      float srsBonus = priorReviewInterval >= 60
          ? MathF.Min(0.3f, priorReviewInterval / 365.0f)
          : 0f;
      float streakScore = MathF.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);
      float combinedScore = MathF.Min(streakScore + srsBonus, 1.0f);

  The "BEFORE-update" semantics is non-negotiable: capturing `priorReviewInterval` ahead of `UpdateSpacedRepetitionSchedule` (currently invoked at VocabularyProgressService.cs:155) is what prevents a fresh word's 5th attempt from leapfrogging straight into Known when SM-2 jumps interval 92 -> 230.

Concrete numbers under the chosen fix:

- EffectiveStreak 7.5, interval 230, accuracy 1.0, ProductionInStreak 3 (boda/mun):
  - streakScore = 7.5 / 12 = 0.625
  - srsBonus = min(0.30, 230/365) = 0.30
  - combinedScore = 0.925 -> displayed 92%
  - IsKnown via Pathway 3: interval(230) >= 60 AND accuracy(1.0) >= 0.80 AND ProductionInStreak(3) >= 1 -> TRUE
  - IsKnown via primary gate also passes after bonus is applied: 0.925 >= 0.85 AND ProductionInStreak(3) >= 2 -> TRUE
- EffectiveStreak 7.0, interval 230, accuracy 1.0, ProductionInStreak 2 (seonsaengnim):
  - streakScore = 7.0 / 12 = 0.583
  - srsBonus = 0.30
  - combinedScore = 0.883 -> displayed 88%
  - IsKnown via Pathway 3 fires; primary gate also passes after bonus.

Risk of false "known": low. The SRS pathway requires interval >= 60, which is reachable only via genuine progression through SM-2 (1 -> 6 -> 15 -> 37 -> 92 days inclusive of at least 4 correct retrievals across expanding gaps), AND lifetime accuracy >= 0.80, AND at least one production-mode answer in the current streak. The "use prior interval" guardrail blocks fresh-session gaming. The remaining edge is "high interval but low accuracy", which the accuracy floor explicitly excludes.

### Tests to update / add (Concern #1)

Update (existing assertions that pin /12 behavior must remain valid for fresh words but may need adjustment for the wrong-answer regression case):

- tests/SentenceStudio.UnitTests/Integration/MasteryAlgorithmIntegrationTests.cs -- review `WrongAnswer_AfterBuildingMastery_DropsBelowKnown`. Under the new gate, a word that has built up SRS interval but then gets a wrong answer needs to also reset the SRS pathway. Verify SM-2 reset semantics in `UpdateSpacedRepetitionSchedule` (VocabularyProgressService.cs:398-426) drop interval back below 60 on a wrong answer, otherwise the SRS pathway keeps the word Known. (Per LLA: "May need adjustment if SRS interval path keeps it Known".) The other tests in this file should remain valid because they exercise fresh words with no prior interval.

Add (new target tests, written to fail against current code, pass after fix):

- tests/SentenceStudio.UnitTests/Services/MasteryScoring/MasteryAlgorithmTargetTests.cs (new file)
  1. `SrsInterval_AboveThreshold_WithAccuracyAndProduction_IsKnown` -- screenshot scenario (CurrentStreak 6.0, Production 3, ReviewInterval 230, Accuracy 1.0) -> IsKnown TRUE
  2. `SrsInterval_BelowThreshold_RemainsLearning` -- ReviewInterval 37, Accuracy 1.0, ProductionInStreak 2 -> IsKnown FALSE via SRS path
  3. `SrsInterval_HighInterval_ButLowAccuracy_NotKnown` -- ReviewInterval 92, Accuracy 0.60, ProductionInStreak 2 -> IsKnown FALSE
  4. `SrsInterval_HighInterval_ButNoProduction_NotKnown` -- ReviewInterval 230, Accuracy 1.0, ProductionInStreak 0 -> IsKnown FALSE
  5. `MasteryScore_IncorporatesSrsBonus_ForLongIntervalWord` -- after recording one more correct on a word with prior interval 230, displayed mastery >= 0.85
  6. `FreshWord_InSingleSession_DoesNotGetSrsBonus` -- 5 corrects in rapid succession with no prior interval -> mastery stays governed by /12, NOT promoted to Known on the 5th turn

Test 6 is the critical guardrail that defends the Issue #191 rotation pacing. It must be in place before the bonus calculation flips on.

Characterization tests already added by Jayne (tests/SentenceStudio.UnitTests/Services/MasteryScoring/MasteryCalibrationCharacterizationTests.cs, 19 tests) accurately pin current behavior. Some of these will need to be retired or rewritten when the fix lands -- specifically `IsKnown_False_AtMastery625_BelowBothGates` and `IsKnown_False_AtMastery583_BelowBothGates` will become incorrect post-fix. Keep them in a "PRE-FIX PIN" group and remove them in the same PR that lands the fix, replaced by the target tests above.

---

## Concern #2 -- Plan Staleness / Non-Due Words Recycled

### Verdict

TIMEZONE / DATE-KEY BUG with high code-evidence confidence, pending production-data confirmation. The webapp's IPlanDateContext resolves "today" via `TimeZoneInfo.Local`, which on Azure Linux containers is UTC. Captain studies in CDT (UTC-5). Between 7pm-midnight CDT, the Azure server's "today" is the user's "tomorrow", so opening the dashboard during that window pre-generates the next-day plan keyed to vocabulary still due before the user's evening study session updates progress. The plan is then pinned in Postgres (DailyPlan + DailyPlanCompletion) and reconstructed verbatim by the morning short-circuit at ProgressService.cs:230-264, with no freshness check on FocusVocabularyIds.

Code citations verified:
- ProgressService.cs:57-69 -- ResolveTodayKey via IPlanDateContext, fallback to UTC.
- CoreServiceExtensions.cs:140-141 -- `services.AddTransient<IPlanDateContext>(sp => sp.GetRequiredService<DevicePlanDateContextProvider>().Current())` -- registered for both device and webapp via the shared core registration.
- DevicePlanDateContextProvider.cs:17 -- `public IPlanDateContext Current() => new PlanDateContext(TimeZoneInfo.Local);`
- src/SentenceStudio.WebApp/Program.cs:248-255 -- `RegisterSentenceStudioServices` calls `AddSentenceStudioCoreServices()` at line 250 with NO subsequent IPlanDateContext re-registration.
- ProgressService.cs:230-264 -- short-circuit returns reconstructed plan without re-evaluating vocabulary due dates.
- VocabQuiz.razor:717-740 -- DueOnly date filter is gated by `if (!usingFocusVocabularyIds)`; plan-driven launches bypass the due-date filter.

### Chosen fix direction

Two complementary fixes. Both should ship together; either alone is incomplete defense.

PRIMARY (root cause): Override IPlanDateContext in the webapp registration.

- File: src/SentenceStudio.WebApp/Program.cs (inside RegisterSentenceStudioServices, after the call at line 250)
- Replace the inherited transient registration with one that resolves a real user timezone instead of `TimeZoneInfo.Local`.
- Interim implementation (acceptable for the immediate fix while only Captain uses the webapp): default to `America/Chicago` and log when the fallback fires.
- Per-user implementation (the actual long-term solution, per Wash's Fix 3): add `IanaTimeZoneId` to UserProfile, populate via JS interop `Intl.DateTimeFormat().resolvedOptions().timeZone` on first Blazor circuit connect, and read it scoped per request. The decision record commits to BOTH steps; the interim ships first to unblock Captain, the per-user path follows in a separate migration PR.

DEFENSIVE (independent layer): Validate FocusVocabularyIds freshness in plan reconstruction.

- File: src/SentenceStudio.Shared/Services/Progress/ProgressService.cs, inside GetCachedPlanAsync after `ReconstructPlanFromDatabase` returns (per Wash's Fix 2 sketch).
- When a reconstructed plan's FocusVocabularyIds includes words whose current NextReviewDate is far in the future (i.e. they have been studied since the plan was created), invalidate the cache and force regeneration. Threshold: if more than 50% of focus IDs are no longer due, regenerate.
- This layer protects against any other source of plan staleness (sync race ordering, manual progress updates, server-clock skew, future timezone bugs) without requiring perfect timezone correctness everywhere.

### DueOnly bypass ruling

KEEP the bypass at VocabQuiz.razor:717-740 as designed. The bypass expresses the correct semantics for plan-driven launches: a plan item created with specific focus words should serve those focus words. Closing the bypass would break legitimate flows (e.g., a learner specifically wanting to drill the words their plan selected, even if some have just barely transitioned out of "due"). The right fix is to ensure the plan is fresh at the point of launch (PRIMARY + DEFENSIVE above), not to override what the plan says at the quiz layer.

The bypass DOES amplify the impact of any upstream staleness from "plan UI shows wrong words" to "plan actively quizzes wrong words." This is acknowledged as a tertiary amplifier in Wash's analysis and is the reason the DEFENSIVE layer is necessary even with the PRIMARY fix.

### Decisive production query

Before shipping the Concern #2 fix, Captain must run Wash's Query 4 against production Postgres (full SQL in `.squad/decisions/inbox/wash-concern2-plan-staleness.md`, section C). The single decisive comparison is:

    CASE WHEN dp."CreatedAt" < vp."UpdatedAt" THEN 'STALE' ELSE 'FRESH' END

joined across DailyPlan (today's row) and VocabularyProgress (the 3 words from the screenshot). If the verdict comes back STALE for all 3 rows AND `dp."CreatedAt"` falls in the 00:00-05:00 UTC window of June 17 (= 7pm-midnight CDT June 16), the timezone hypothesis is fully confirmed. If the verdict comes back FRESH (plan created after progress update), the hypothesis is wrong and the investigation must continue before any fix ships.

---

## Cross-cutting

### Priority and sequencing

P0 -- ship in parallel, neither blocks the other:

1. Concern #1 fix -- LLA Option 1 (SRS-aware IsKnown + srsBonus). Preconditions:
   - LLA Test 6 (`FreshWord_InSingleSession_DoesNotGetSrsBonus`) must be in the test suite and passing as a regression guard BEFORE the bonus calculation goes live.
   - Existing `WrongAnswer_AfterBuildingMastery_DropsBelowKnown` must be reviewed against the new SRS pathway and adjusted if needed.
   - Jayne's pre-fix characterization tests at MasteryCalibrationCharacterizationTests.cs (specifically the two `IsKnown_False_AtMastery625/583_BelowBothGates` cases) must be retired in the same PR that lands the fix.

2. Concern #2 fix -- WebApp IPlanDateContext override (interim America/Chicago) + GetCachedPlanAsync freshness check. Preconditions:
   - Captain runs Wash's Query 4 (the staleness verdict comparison). If verdict = STALE, ship. If verdict = FRESH, halt and reopen investigation.

P1 -- follow-up after P0 lands:

3. Concern #2 long-term -- Add `IanaTimeZoneId` to UserProfile (Wash's Fix 3). EF Core migration via `dotnet ef migrations add AddUserTimeZone --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj`. JS interop on first Blazor circuit connect to populate. Replace the interim America/Chicago default with the real per-user value.

4. CoreSync ordering (Wash's Fix 4) -- speculative, do not ship until P0 metrics show this is still a problem after PRIMARY + DEFENSIVE are in place. The DEFENSIVE freshness check should make sync ordering a non-issue.

Concern #2's PRIMARY fix and DEFENSIVE fix should be a single PR -- they are designed to work together and neither is complete protection without the other.

### Baseline discrepancy follow-up

Jayne verified 636/636 passing on tests/SentenceStudio.UnitTests, with the previously documented "intentional fail" (`ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed`) now passing. This obsoletes the baseline note in the global instructions.

Required follow-up tasks (do NOT edit in this investigation -- separate PR):

1. .github/copilot-instructions.md:133-135 -- update "Baseline: 534/535 passing" to "Baseline: 636/636 passing (verified 2026-06-17 by Jayne)" and remove or rewrite the line-117 reference. The original `ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed` test in tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderResourceSelectionTests.cs no longer carries the "THIS WILL LIKELY FAIL" comment near the assertion; the comment has migrated/replicated to other tests in the suite.

2. Delete the five remaining "// Assert -- THIS WILL LIKELY FAIL" comments per the documented convention ("If you fix the underlying bug, also delete the THIS WILL LIKELY FAIL comment so the next agent doesn't get confused"):
   - tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderResourceSelectionTests.cs:307
   - tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderVocabularyReviewTests.cs:149
   - tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderVocabularyReviewTests.cs:265
   - tests/SentenceStudio.UnitTests/PlanGeneration/StudyPlanIntegrationTests.cs:100
   - tests/SentenceStudio.UnitTests/PlanGeneration/StudyPlanIntegrationTests.cs:232

3. Before deleting the comments, do a manual diff sweep on each of the five tests to confirm the underlying bug each was documenting is actually fixed (not merely masked by a test-fixture change that side-stepped the assertion). Removing the comment without that verification risks losing the documentary signal that an old bug ever existed.

AGENTS.md does NOT contain a "534/535" reference in the current revision; the only place to update is `.github/copilot-instructions.md`.

### Production evidence status

Production was NOT reached during this investigation -- neither LLA nor Wash had access to the prod Postgres or the webapp logs.

- Concern #1 confidence is HIGH on code reading alone, because the contradiction (62% mastery with 230-day SRS interval) is reproducible from screenshots and matches the math of the verified code. No production query is needed to confirm Concern #1 -- the screenshots ARE the evidence and Jayne's characterization tests pin the math.

- Concern #2 confidence is HIGH on code reading but pending one decisive query. The single query that confirms or refutes the timezone-skew hypothesis is Wash's Query 4 (the CASE WHEN `dp."CreatedAt" < vp."UpdatedAt"` THEN 'STALE' comparison joined across DailyPlan and VocabularyProgress). Captain must run this against production before the Concern #2 fix ships. If the verdict is FRESH, the investigation must continue -- the alternative root causes to consider are (a) sync ordering pushing the stale plan AFTER progress was updated, (b) a separate plan-cache invalidation bug, (c) the FocusVocabularyFacts JSON column carrying stale word IDs from a different write path.

---

### 2026-06-17T15:10:57-05:00: Mastery Calibration Bug -- SM-2 Interval Decoupled from Status Model

**By:** language-learning-architect
**Surface:** Production iOS (iPhone DX24), account dave@ortinau.com
**Concern:** "Given the success I've had with these words, why are they presented like I don't know them, with such low mastery?"

---

## A. Verified File:Line Table

| Claim | File:Line | Verified Content |
|-------|-----------|-----------------|
| EFFECTIVE_STREAK_DIVISOR = 12.0 | src/SentenceStudio.Shared/Services/VocabularyProgressService.cs:27 | `private const float EFFECTIVE_STREAK_DIVISOR = 12.0f;` |
| MasteryScore calc | VocabularyProgressService.cs:111-115 | `float effectiveStreak = progress.CurrentStreak + (progress.ProductionInStreak * 0.5f); float streakScore = MathF.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f); ... progress.MasteryScore = MathF.Max(streakScore, progress.MasteryScore) + recoveryBoost;` |
| EffectiveStreak formula | src/SentenceStudio.Shared/Models/VocabularyProgress.cs:93 | `public float EffectiveStreak => CurrentStreak + (ProductionInStreak * 0.5f);` |
| IsKnown gate (primary) | VocabularyProgress.cs:109-110 | `(MasteryScore >= MASTERY_THRESHOLD && ProductionInStreak >= MIN_PRODUCTION_FOR_KNOWN)` -- MASTERY_THRESHOLD = 0.85f (:11), MIN_PRODUCTION_FOR_KNOWN = 2 (:10) |
| IsKnown gate (bypass) | VocabularyProgress.cs:111-113 | `(MasteryScore >= HIGH_CONFIDENCE_MASTERY_FLOOR && ProductionInStreak >= HIGH_CONFIDENCE_MIN_PRODUCTION && TotalAttempts >= HIGH_CONFIDENCE_MIN_ATTEMPTS)` -- 0.75f (:14), 4 (:15), 8 (:16) |
| SM-2 inline impl | VocabularyProgressService.cs:398-426 | Interval progression: 1->6->6*EF->... with EF starting at 2.5, +0.1 per correct (capped at 2.5). For 5 all-correct from scratch: 1->6->15->37->92->230. |
| Mastered override | VocabularyProgressService.cs:162-174 | Only fires when `progress.MasteryScore >= MASTERY_THRESHOLD && progress.ProductionInStreak >= MIN_PRODUCTION_FOR_KNOWN`. Sets interval=60, NextReviewDate=Now+60days. |
| UI display | src/SentenceStudio.UI/Pages/VocabQuiz.razor:440-449 | Displays EffectiveStreak, MasteryScore as percentage. ProductionInStreak shown as "{value} / 2". |

## B. Math Verification (matches screenshots exactly)

Word: "to see" (boda)
- CurrentStreak = 6.0, ProductionInStreak = 3, TotalAttempts = 5, Correct = 5
- EffectiveStreak = 6.0 + (3 * 0.5) = 7.5
- MasteryScore = min(7.5 / 12.0, 1.0) = 0.625 = 62% (screenshot: 62%) -- CONFIRMED
- IsKnown primary: 0.625 < 0.85 -- FAILS
- IsKnown bypass: 0.625 < 0.75 AND 3 < 4 AND 5 < 8 -- FAILS all three
- SM-2 interval with EF=2.5 after 5 corrects: 230 days -- CONFIRMED
- NextReviewDate: Feb 1, 2027 -- consistent with ~230 days from screenshot date

Word: mun (door)
- EffectiveStreak = 7.5, Mastery = 7.5/12 = 62.5% ~ 62% -- CONFIRMED

Word: seonsaengnim (teacher)
- EffectiveStreak = 7.0, Mastery = 7.0/12 = 58.3% ~ 58% -- CONFIRMED

All three: ReviewInterval = 230 days, EaseFactor = 2.50, IsDueForReview = No.

## C. The Decoupling Problem

SM-2 says: "I'm so confident this learner knows this word that I won't ask again for 230 days."
Status model says: "This word is only 62% mastered -- still Learning."

These two signals directly contradict each other from the learner's perspective.

## D. Design Verdict: CALIBRATION BUG (high confidence)

### Evidence from the codebase

The divisor was raised from 7.0 to 12.0 specifically to fix Issue #191 -- preventing
words from rotating out of a quiz SESSION too quickly (in 4-5 turns). The code comment
at VocabularyProgressService.cs:21-26 states:

> "Issue #191: divisor raised 7.0 -> 12.0 so a fresh, all-correct word doesn't reach
> mastery 1.0 in 5 turns. With 12.0, an all-correct fresh word lands at mastery ~0.583
> by turn 5 and ~0.917 by turn 7, aligning mastery growth with the rotation gate in
> VocabularyQuizItem.ReadyToRotateOut."

The decision doc (.squad/decisions/processed/2026-05-03/jayne-vocab-quiz-scoring-repro-189-191.md)
confirms the divisor was tuned for IN-SESSION rotation pacing. But the same MasteryScore
value is also used as the learner-facing "how well do I know this?" metric and as the gate
for the IsKnown status transition. These are different concerns:

1. Session rotation: "Has this word been practiced enough THIS session to cycle out?"
2. Lifetime mastery: "Given all evidence across sessions, does the learner know this word?"

The /12 divisor was calibrated for concern #1 but is now incorrectly governing concern #2.

### Learning-science grounding

In spaced-repetition research, the SM-2 interval is a direct proxy for estimated
retention strength. An interval of 230 days with ease factor 2.5 means:

- The algorithm expects >= 90% recall probability at that interval.
- 5 consecutive correct retrievals across increasing intervals (1, 6, 15, 37, 92 days)
  demonstrates durable long-term memory encoding.
- In SLA terms, a word recalled correctly at 92-day spacing has passed well beyond
  the threshold from short-term to long-term lexical knowledge.

The "minimum 10+ turns" requirement implied by the /12 divisor conflates:
- Number of within-session exposures (relevant for initial encoding)
- Evidence of long-term retention (demonstrated by recall at increasing intervals)

A word with 5/5 correct across expanding intervals is NOT the same as a word with
5/5 correct in a single session. The former demonstrates robust retrieval strength;
the latter may reflect only short-term priming. The current model cannot distinguish
these cases because MasteryScore is purely streak-count-based with no temporal signal.

### Why this is NOT intended design

- The code comment explicitly ties the divisor to rotation pacing, not to mastery status.
- The SM-2 mastered-override (lines 162-174) was clearly INTENDED to align the two systems
  but cannot fire because mastery < 0.85 -- creating a dead code path for these common cases.
- The high-confidence bypass (IsKnown with 0.75/4/8) was added as a patch but its
  thresholds (TotalAttempts >= 8, ProductionInStreak >= 4) are unreachable for words
  that the SRS has correctly spaced to 230 days (the learner will never see them 8 times
  because the algorithm stops asking).

---

## E. Ranked Fix Options

### Option 1 (RECOMMENDED): SRS-Interval-Aware Known Gate

Add a third pathway into IsKnown: if SM-2 has scheduled the word far out, it IS known.

Location: src/SentenceStudio.Shared/Models/VocabularyProgress.cs:109-113

Proposed addition to IsKnown:

    public bool IsKnown =>
        (MasteryScore >= MASTERY_THRESHOLD && ProductionInStreak >= MIN_PRODUCTION_FOR_KNOWN)
        || (MasteryScore >= HIGH_CONFIDENCE_MASTERY_FLOOR
            && ProductionInStreak >= HIGH_CONFIDENCE_MIN_PRODUCTION
            && TotalAttempts >= HIGH_CONFIDENCE_MIN_ATTEMPTS)
        || (ReviewInterval >= SRS_KNOWN_INTERVAL_THRESHOLD
            && Accuracy >= SRS_KNOWN_ACCURACY_THRESHOLD
            && ProductionInStreak >= 1);

New constants:
    private const int SRS_KNOWN_INTERVAL_THRESHOLD = 60;   // 60+ day interval = strong evidence
    private const float SRS_KNOWN_ACCURACY_THRESHOLD = 0.80f;  // At least 80% lifetime accuracy

Results for screenshot words:
- "to see": ReviewInterval=230 >= 60, Accuracy=1.0 >= 0.80, ProductionInStreak=3 >= 1 --> IsKnown = TRUE
- mun: Same pattern --> IsKnown = TRUE
- seonsaengnim: Same pattern --> IsKnown = TRUE

MasteryScore display would still show 62%/58% unless also updated (see complementary fix below).

Risk: Very low false-positive risk. A 60-day interval requires at minimum 3 consecutive
correct recalls across expanding gaps (1->6->15->37 = interval 37, one more correct = 92).
Combined with accuracy >= 80% and at least one production attempt, this is strong evidence.

Complementary: Also update MasteryScore to incorporate SRS evidence:

Location: VocabularyProgressService.cs:111-115

After computing streakScore, add:

    float srsBonus = progress.ReviewInterval >= 60
        ? MathF.Min(0.3f, progress.ReviewInterval / 365.0f)
        : 0f;
    float streakScore = MathF.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);
    float combinedScore = MathF.Min(streakScore + srsBonus, 1.0f);

Results with srsBonus for screenshot words (interval=230):
- srsBonus = min(0.3, 230/365) = 0.30
- "to see": streakScore = 0.625 + 0.30 = 0.925 --> displayed as 92%
- seonsaengnim: 0.583 + 0.30 = 0.883 --> displayed as 88%

Both would now pass the primary IsKnown gate (>= 0.85) independently.

Tests needing update: FullLifecycle_Unknown_To_Learning_To_Known,
FullLifecycle_KnownWord_GetsLongReviewInterval (would gain additional passing path).

### Option 2: Lower the Divisor (simpler but blunter)

Change EFFECTIVE_STREAK_DIVISOR from 12.0 to 8.0.

Location: VocabularyProgressService.cs:27

Results:
- "to see": 7.5 / 8.0 = 0.9375 = 94% --> passes 0.85 gate with ProductionInStreak=3 >= 2 --> IsKnown = TRUE
- mun: 7.5 / 8.0 = 94% --> IsKnown = TRUE (assuming ProductionInStreak >= 2)
- seonsaengnim: 7.0 / 8.0 = 0.875 = 88% --> passes 0.85, IsKnown depends on ProductionInStreak

Risk: MEDIUM. This re-introduces the Issue #191 problem partially -- fresh words
in a single session would reach mastery faster (5 MC = streak 5*0.8=4.0, eff=4.0,
mastery=4.0/8.0=0.50 vs the current 0.333). Session rotation gates would need re-tuning.

Tests needing update: ALL tests that assert specific mastery values for given streak counts.
The #191 rotation-pacing fix would be partially undone. At minimum:
- RecordAttempt_SingleCorrectMultipleChoice_IncreasesStreakAndMastery
- RecordAttempt_StreakOf12Recognition_ReachesMasteryCapButNotKnown (would cap earlier)
- FullLifecycle tests (fewer turns needed)
- IsPromoted threshold tests

### Option 3: Lower the IsKnown Threshold Only

Change MASTERY_THRESHOLD from 0.85 to 0.60.

Location: VocabularyProgress.cs:11 AND VocabularyProgressService.cs:19

Results:
- "to see": 0.625 >= 0.60, ProductionInStreak=3 >= 2 --> IsKnown = TRUE
- seonsaengnim: 0.583 < 0.60 --> still NOT Known (close miss)

Risk: HIGH. Lowering to 0.60 means a word with just EffectiveStreak 7.2 (e.g., 5 MC
attempts in a single session + 1 production) would be "Known" with no temporal evidence
of long-term retention. This defeats the purpose of the threshold entirely.

Tests needing update: FullLifecycle_Unknown_To_Learning_To_Known would pass sooner;
RecordAttempt_StreakOf12Recognition_ReachesMasteryCapButNotKnown would need new assertion.

### Option 4: Decouple Session Rotation Score from Lifetime Mastery

Introduce a separate `SessionScore` for rotation gating, keep MasteryScore as the lifetime
metric but lower the divisor for the lifetime metric only.

This is architecturally the cleanest solution but highest implementation cost:
- Add `SessionStreak` / `SessionScore` fields to VocabularyQuizItem (not persisted)
- Keep EFFECTIVE_STREAK_DIVISOR = 12.0 for session rotation
- Use a lower divisor (e.g., 8.0) or SRS-aware formula for persisted MasteryScore

Risk: LOW for false positives but HIGH implementation complexity. Touches both the quiz
session model and the persistence layer.

---

## F. Recommendation

**Option 1 (SRS-Interval-Aware Known Gate)** is the strongest fix because:

1. It uses the information already available (SM-2 interval) that is the most
   direct evidence of long-term retention strength.
2. It does not disturb the /12 divisor that was carefully tuned for session rotation.
3. The learning-science justification is clear: an interval of 60+ days with high
   accuracy IS knowledge by any reasonable SRS-theoretical definition.
4. The complementary srsBonus for display score resolves the UX complaint directly --
   the learner sees a mastery percentage that reflects their demonstrated retention.
5. Minimal test disruption -- existing tests remain valid because they test fresh words
   that have not yet built up SRS intervals.

---

## G. Existing Tests That Pin Current Behavior

File: tests/SentenceStudio.UnitTests/Integration/MasteryAlgorithmIntegrationTests.cs

| Test | What it pins | Would break under fix? |
|------|-------------|----------------------|
| RecordAttempt_SingleCorrectMultipleChoice_IncreasesStreakAndMastery | Mastery = 1.0/12.0 for 1 MC correct | No (no SRS interval yet) |
| RecordAttempt_SingleCorrectText_IncreasesProductionInStreak | Mastery = 1.5/12.0 for 1 Text correct | No |
| RecordAttempt_StreakOf12Recognition_ReachesMasteryCapButNotKnown | 12 MC -> mastery=1.0, IsKnown=FALSE (no production) | No (still no production) |
| FullLifecycle_Unknown_To_Learning_To_Known | 8 MC + 2 Text -> IsKnown=TRUE | No (same path works) |
| FullLifecycle_KnownWord_GetsLongReviewInterval | Known word gets interval=60 | No |
| WrongAnswer_AfterBuildingMastery_DropsBelowKnown | Wrong answer drops IsKnown | May need adjustment if SRS interval path keeps it Known |
| IsPromoted_SetAt50PercentMastery | 6 MC -> MasteryScore >= 0.50 | No |

The /12 divisor is EXPLICITLY tested (assertions compute expected values as N/12.0).
These tests pin the divisor as DESIGN but only for fresh-word-in-session scenarios.
No existing test covers the long-interval + perfect-accuracy case that Captain hit.
This is the gap.

The existing tests treat the divisor as intended for SESSION-LEVEL progression.
They do NOT assert that a word with 230-day SRS interval should remain at 62% mastery.
The absence of such a test confirms this is an untested edge case, not defended behavior.

---

## H. Target-Behavior Regression Tests (to add when fix lands)

File: tests/SentenceStudio.UnitTests/Integration/MasteryAlgorithmIntegrationTests.cs

### Test 1: SrsInterval_AboveThreshold_WithAccuracyAndProduction_IsKnown

    [Fact]
    public async Task SrsInterval_AboveThreshold_WithAccuracyAndProduction_IsKnown()
    {
        // Simulate Captain's scenario: 5/5 correct, interval grown to 230 days
        // After 5 correct answers (some production), SM-2 schedules 230 days out
        // Arrange: seed a progress record with the exact screenshot state
        // CurrentStreak=6.0, ProductionInStreak=3, TotalAttempts=5, CorrectAttempts=5,
        // ReviewInterval=230, EaseFactor=2.5
        // Assert: IsKnown == true (SRS interval pathway)
        // Assert: Status == LearningStatus.Known
    }

### Test 2: SrsInterval_BelowThreshold_RemainsLearning

    [Fact]
    public async Task SrsInterval_BelowThreshold_RemainsLearning()
    {
        // A word with interval < 60 days should NOT qualify via SRS path
        // Arrange: progress with ReviewInterval=37, Accuracy=1.0, ProductionInStreak=2
        // Assert: IsKnown == false (unless mastery >= 0.85 via primary path)
    }

### Test 3: SrsInterval_HighButLowAccuracy_NotKnown

    [Fact]
    public async Task SrsInterval_HighInterval_ButLowAccuracy_NotKnown()
    {
        // Edge case: somehow high interval but accuracy < 0.80 (e.g., early wrongs then recovery)
        // Arrange: ReviewInterval=92, Accuracy=0.60, ProductionInStreak=2
        // Assert: IsKnown == false via SRS path (accuracy guard prevents false positive)
    }

### Test 4: SrsInterval_HighButNoProduction_NotKnown

    [Fact]
    public async Task SrsInterval_HighInterval_ButNoProduction_NotKnown()
    {
        // Word only ever answered via MultipleChoice (recognition only)
        // Arrange: ReviewInterval=230, Accuracy=1.0, ProductionInStreak=0
        // Assert: IsKnown == false via SRS path (production guard prevents false positive)
    }

### Test 5: MasteryScore_IncorporatesSrsBonus_ForLongInterval

    [Fact]
    public async Task MasteryScore_IncorporatesSrsBonus_ForLongIntervalWord()
    {
        // After recording a correct attempt on a word with high existing interval,
        // the displayed MasteryScore should reflect SRS evidence
        // Arrange: word with ReviewInterval=230 before the attempt
        // Act: record one more correct attempt
        // Assert: MasteryScore > streakScore alone (srsBonus applied)
        // Assert: MasteryScore for EffStreak 7.5 + interval 230 >= 0.85
    }

### Test 6: FreshWord_InSingleSession_NoSrsBonus

    [Fact]
    public async Task FreshWord_InSingleSession_DoesNotGetSrsBonus()
    {
        // Ensure the fix does not help fresh words game the system
        // Arrange: brand new word, no prior ReviewInterval
        // Act: 5 correct answers in rapid succession
        // Assert: MasteryScore still governed by /12 divisor only
        // Assert: ReviewInterval grows via SM-2 but word is NOT yet Known
        //         (interval after 5 corrects from default start = 230, but
        //         this only matters on the NEXT attempt -- during the session
        //         the interval was building from 1)
    }

Note for Test 6: The implementation must be careful -- the srsBonus should use the
interval BEFORE the current attempt's SM-2 update, not after. Otherwise a fresh word
getting 5 correct in one session would immediately qualify on its 5th attempt when
the interval jumps to 230. The bonus should reflect PRIOR demonstrated retention.

---

## I. Summary

Root cause: The EFFECTIVE_STREAK_DIVISOR=12.0 was calibrated for in-session quiz
rotation pacing (Issue #191) but also governs the lifetime mastery display and the
IsKnown status gate. For words the SRS has correctly identified as well-known
(230-day interval, perfect accuracy), the mastery model contradicts the SRS model,
leaving the learner stuck at "Learning / 62%" indefinitely.

Verdict: CALIBRATION BUG, not intended design. High confidence.

Fix: Add SRS-interval-aware pathway into IsKnown (ReviewInterval >= 60 AND
Accuracy >= 0.80 AND ProductionInStreak >= 1) plus an srsBonus to the displayed
MasteryScore for words with long intervals. This preserves the /12 divisor for
session rotation while correctly reflecting long-term retention evidence.

---

# Wash Investigation: Plan Staleness -- Non-Due Words Recycled

Date: 2026-06-17
Surface: Production Webapp (Blazor InteractiveServer on Azure) + iPhone DX24 via CoreSync
Concern: Three words studied yesterday (NextReviewDate Feb 2027, ReviewInterval 230d) appeared in today's plan.

---

## A. Verified File:Line Table

| Claim | File:Line | Verified? | Actual Code |
|-------|-----------|-----------|-------------|
| Plan short-circuit | ProgressService.cs:230-264 | YES | `GenerateTodaysPlanAsync` calls `GetCachedPlanAsync(today)`; if non-null, returns immediately. Single-flight semaphore keyed on userId. |
| GetCachedPlanAsync reconstructs from DB | ProgressService.cs:451-510 | YES | Checks memory cache, then calls `ReconstructPlanFromDatabase(resolvedDate)`. If found, caches and returns. |
| ReconstructPlanFromDatabase | ProgressService.cs:1368-1520 | YES | Queries `DailyPlanCompletions` WHERE `Date == date.Date AND UserProfileId == profile.Id`. Reads `DailyPlan.FocusVocabularyFacts` for the focus word set. Reconstructs plan items with those focus IDs. |
| Due-word filter correct | VocabularyProgressRepository.cs:359-383 | YES | `GetDueVocabularyAsync` filters `NextReviewDate <= asOfDate` AND excludes mastered. Feb-2027 words would NOT pass. |
| DueOnly bypass when FocusVocabularyIds present | VocabQuiz.razor:717-740 | YES | The `if (DueOnly)` date filter is inside `if (!usingFocusVocabularyIds)`. When focus IDs are passed (plan-driven launch), all focus words are served regardless of NextReviewDate. |
| FocusVocabularyIds flow from DailyPlan row | ProgressService.cs:1398-1402, 1456-1484 | YES | `ReconstructPlanFromDatabase` reads `DailyPlan.FocusVocabularyFacts`, deserializes to IDs, and attaches them to each plan item that `ShouldUsePlanFocusVocabularyIds`. |
| VocabularyProgress syncs UploadAndDownload | SharedSyncRegistration.cs:27 | YES | Line 27: `Table<VocabularyProgress>("VocabularyProgress", syncDirection: SyncDirection.UploadAndDownload)` |
| DailyPlan syncs UploadAndDownload | SharedSyncRegistration.cs:31 | YES | Line 31: `Table<DailyPlan>("DailyPlan", ...)` |
| DailyPlanCompletion syncs UploadAndDownload | SharedSyncRegistration.cs:32 | YES | Line 32: `Table<DailyPlanCompletion>("DailyPlanCompletion", ...)` |
| ResolveTodayKey uses IPlanDateContext | ProgressService.cs:57-69 | YES | Resolves via DI. If present, returns `dateContext.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)`. Fallback: `DateTime.UtcNow.Date`. |
| Webapp registers DevicePlanDateContextProvider | CoreServiceExtensions.cs:140-141 | YES | `AddTransient<IPlanDateContext>(sp => sp.GetRequiredService<DevicePlanDateContextProvider>().Current())` |
| DevicePlanDateContextProvider uses TimeZoneInfo.Local | DevicePlanDateContextProvider.cs:17 | YES | `public IPlanDateContext Current() => new PlanDateContext(TimeZoneInfo.Local);` |
| Webapp is Blazor InteractiveServer | WebApp/Program.cs:51, App.razor:23 | YES | `.AddInteractiveServerComponents()` and `@rendermode="InteractiveServer"` |
| No timezone override in WebApp | WebApp/Program.cs (entire) | YES | No IPlanDateContext re-registration. No X-Timezone header handling. No JS interop for timezone. |
| Sync conflict resolution: ForceWrite on UPDATE | SyncService.cs:308-330 | YES | Both remote and local conflict funcs: INSERT => Skip; UPDATE/DELETE => ForceWrite. Last-write-wins semantics for updates. |
| ToDateKey in PlanService | PlanService.cs:419 | YES | `private static DateTime ToDateKey(DateOnly localDate) => localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);` |

---

## B. Root Cause Mechanism

### PRIMARY: Webapp timezone bug causes premature plan creation (HIGH confidence)

The production webapp (Blazor Server on Azure Container Apps, West US 3 region) resolves "today" via `DevicePlanDateContextProvider` which calls `TimeZoneInfo.Local`. On Azure Linux containers, `TimeZoneInfo.Local` defaults to UTC.

Captain is in CDT (UTC-5). The critical window:

- Between 7:00 PM CDT and 11:59 PM CDT, the Azure server believes it is the NEXT calendar day.
- If Captain opens the webapp dashboard during this window, `LoadPlanAsync()` (Index.razor:926-951) calls `GetCachedPlanAsync()` then `GenerateTodaysPlanAsync()`.
- The server computes `ResolveTodayKey()` as TOMORROW's date (from Captain's perspective).
- No plan exists for that "tomorrow" date yet, so `GenerateTodaysPlanAsync` generates a FRESH plan.
- The DeterministicPlanBuilder (DeterministicPlanBuilder.cs:101-106) calls `GetDueVocabularyAsync(today, userId)` where `today` is the server's idea of today.
- At that moment, the 3 words ARE still due (Captain hasn't studied them yet -- they're due for June 16/17).
- Those words get baked into the plan's `FocusVocabularyFacts` and the `DailyPlanCompletion` rows are written to Postgres.

Then Captain studies on the phone (June 16 CDT). Progress is updated locally (NextReviewDate -> Feb 2027). CoreSync pushes VocabularyProgress to Postgres. But the DailyPlan and DailyPlanCompletion rows for the server's "tomorrow" (Captain's actual today) already exist with those word IDs embedded.

The next morning (June 17 CDT), when Captain loads the dashboard:
- Phone: `ResolveTodayKey()` = June 17 CDT = `2026-06-17T00:00:00Z` (matching convention).
- Server: `ResolveTodayKey()` = June 17 UTC = `2026-06-17T00:00:00Z` (same value -- they align in the morning).
- `GetCachedPlanAsync` finds the DailyPlan/DailyPlanCompletion rows that were pre-created last night.
- `ReconstructPlanFromDatabase` (ProgressService.cs:1368) returns the stale plan with the 3 words.
- Short-circuit fires: the plan is returned without re-evaluating vocabulary due dates.

The FocusVocabularyIds bypass (VocabQuiz.razor:717) then serves those words even though they're no longer due, because the DueOnly filter is skipped for plan-driven launches.

### SECONDARY AMPLIFIER: CoreSync ForceWrite on UPDATE (MEDIUM confidence)

SyncService.cs:319,329: For UPDATE conflicts, both sides use `ConflictResolution.ForceWrite`. If the phone syncs AFTER the server created tomorrow's plan, the phone receives DailyPlan/DailyPlanCompletion rows for "tomorrow" from Postgres. These rows are written locally (ForceWrite). The phone then also short-circuits when it loads tomorrow's plan -- it finds the stale rows.

This means even if the phone generates its OWN correct plan (using local progress), if the Postgres plan was created first and syncs down, the server's stale plan wins via the DB-reconstruction path.

### TERTIARY: DueOnly bypass is by design but amplifies staleness

The bypass at VocabQuiz.razor:717-740 is intentional (plan items should serve their designated focus words). However, it transforms a plan-staleness bug from "wrong words listed in plan UI" to "wrong words actively quizzed," making the impact worse.

---

## C. Production Verification SQL

PROD REACHABILITY: I cannot reach production Postgres from this environment. Handing Captain the exact SQL.

All queries use the `"DailyPlan"`, `"DailyPlanCompletion"`, `"VocabularyProgress"`, `"VocabularyWord"`, `"UserProfile"` table names (singular, as configured in ApplicationDbContext.OnModelCreating).

### Query 1: Resolve Captain's UserProfile.Id

```sql
SELECT "Id", "Email", "DisplayName", "CreatedAt", "UpdatedAt"
FROM "UserProfile"
WHERE "Email" = 'dave@ortinau.com';
```

CONFIRM multi-tenant safety: result should be exactly 1 row.
REFUTE orphan issue: multiple rows with same email = cross-tenant risk (see Brot history).

### Query 2: VocabularyProgress for the 3 words

```sql
SELECT vp."Id", vw."TargetLanguageTerm", vw."NativeLanguageTerm",
       vp."MasteryScore", vp."CurrentStreak", vp."ProductionInStreak",
       vp."NextReviewDate", vp."ReviewInterval", vp."LastPracticedAt",
       vp."UpdatedAt", vp."CreatedAt"
FROM "VocabularyProgress" vp
JOIN "VocabularyWord" vw ON vp."VocabularyWordId" = vw."Id"
WHERE vp."UserId" = '<CAPTAIN_USER_PROFILE_ID>'
  AND vw."TargetLanguageTerm" IN ('보다', '문', '선생님');
```

CONFIRM staleness hypothesis: NextReviewDate far in the future (Feb 2027), LastPracticedAt = yesterday.
REFUTE: if NextReviewDate <= today, words ARE legitimately due and the plan is correct.

### Query 3: DailyPlan + DailyPlanCompletion for today and yesterday

```sql
-- Plans for today (June 17 CDT = 2026-06-17T00:00:00Z) and yesterday
SELECT dp."Id", dp."Date", dp."GeneratedAtUtc", dp."Strategy",
       dp."FocusVocabularyFacts", dp."CreatedAt", dp."UpdatedAt"
FROM "DailyPlan" dp
WHERE dp."UserProfileId" = '<CAPTAIN_USER_PROFILE_ID>'
  AND dp."Date" IN ('2026-06-17T00:00:00Z'::timestamp, '2026-06-16T00:00:00Z'::timestamp)
ORDER BY dp."Date" DESC;

-- Completions for same dates
SELECT dpc."PlanItemId", dpc."Date", dpc."ActivityType",
       dpc."FocusVocabularyIds", dpc."Priority", dpc."IsCompleted",
       dpc."CreatedAt", dpc."UpdatedAt"
FROM "DailyPlanCompletion" dpc
WHERE dpc."UserProfileId" = '<CAPTAIN_USER_PROFILE_ID>'
  AND dpc."Date" IN ('2026-06-17T00:00:00Z'::timestamp, '2026-06-16T00:00:00Z'::timestamp)
ORDER BY dpc."Date" DESC, dpc."Priority";
```

CONFIRM timezone hypothesis: if today's DailyPlan.CreatedAt is BEFORE Captain's study session (VocabularyProgress.UpdatedAt for the 3 words), the plan was pre-generated on stale progress.
REFUTE: if CreatedAt is AFTER the progress update, something else is happening.

### Query 4: THE DECISIVE COMPARISON (most diagnostic)

```sql
SELECT
    dp."Date" AS plan_date,
    dp."GeneratedAtUtc" AS plan_generated_at,
    dp."CreatedAt" AS plan_row_created,
    vp."LastPracticedAt" AS words_last_practiced,
    vp."UpdatedAt" AS progress_updated_at,
    vp."NextReviewDate",
    vw."TargetLanguageTerm",
    CASE
        WHEN dp."CreatedAt" < vp."UpdatedAt" THEN 'STALE: plan created BEFORE progress update'
        ELSE 'FRESH: plan created AFTER progress update'
    END AS staleness_verdict
FROM "DailyPlan" dp
CROSS JOIN (
    SELECT vp2."LastPracticedAt", vp2."UpdatedAt", vp2."NextReviewDate", vp2."VocabularyWordId"
    FROM "VocabularyProgress" vp2
    WHERE vp2."UserId" = '<CAPTAIN_USER_PROFILE_ID>'
      AND vp2."VocabularyWordId" IN (
          SELECT "Id" FROM "VocabularyWord" WHERE "TargetLanguageTerm" IN ('보다', '문', '선생님')
      )
) vp
JOIN "VocabularyWord" vw ON vp."VocabularyWordId" = vw."Id"
WHERE dp."UserProfileId" = '<CAPTAIN_USER_PROFILE_ID>'
  AND dp."Date" = '2026-06-17T00:00:00Z'::timestamp;
```

CONFIRM: `staleness_verdict = 'STALE'` for all 3 rows means the plan was pinned before study.
Additional CONFIRM for timezone bug: `plan_row_created` timestamp is between 00:00-05:00 UTC June 17 (= 7pm-midnight CDT June 16), proving the server generated "tomorrow's" plan during Captain's evening.

### Query 5: Check if the 3 words are in today's FocusVocabularyFacts

```sql
SELECT dp."FocusVocabularyFacts"
FROM "DailyPlan" dp
WHERE dp."UserProfileId" = '<CAPTAIN_USER_PROFILE_ID>'
  AND dp."Date" = '2026-06-17T00:00:00Z'::timestamp;
```

Cross-reference the VocabularyWordIds from Query 2 against the JSON array in FocusVocabularyFacts.
CONFIRM: if the 3 word IDs appear in FocusVocabularyFacts, the plan definitively carries stale selections.

---

## D. Fix Recommendations (Ranked)

### Fix 1 (CRITICAL): Webapp must resolve user's local timezone, not server timezone

File: src/SentenceStudio.WebApp/Program.cs (after line 250)
Also: new file src/SentenceStudio.WebApp/Platform/BlazorPlanDateContextProvider.cs

The webapp's `RegisterSentenceStudioServices` inherits `DevicePlanDateContextProvider` from `AddSentenceStudioCoreServices()`. On the server, `TimeZoneInfo.Local` = UTC, causing 5-hour date skew for CDT users.

Proposed fix: Override the IPlanDateContext registration after `AddSentenceStudioCoreServices()`:

```csharp
// In RegisterSentenceStudioServices, AFTER services.AddSentenceStudioCoreServices():
services.AddScoped<IPlanDateContext>(sp =>
{
    // Read user's timezone from their profile or circuit state.
    // Fallback to America/Chicago until per-user tz is implemented.
    var userState = sp.GetService<CircuitUserStateAccessor>();
    var tzId = userState?.Current?.TimeZoneId ?? "America/Chicago";
    TimeZoneResolver.TryResolve(tzId, out var zone);
    return new PlanDateContext(zone);
});
```

Long-term: store user's IANA timezone in UserProfile (populated via JS interop `Intl.DateTimeFormat().resolvedOptions().timeZone` on first Blazor circuit connect) and read it in the scoped IPlanDateContext.

### Fix 2 (HIGH): Plan generation must validate focus vocabulary freshness

File: src/SentenceStudio.Shared/Services/Progress/ProgressService.cs
Location: After line 465 (inside GetCachedPlanAsync, after reconstruction)

When a plan is reconstructed from the database, validate that its FocusVocabularyIds are still actually due. If the vocabulary has been studied since the plan was created, invalidate and regenerate:

```csharp
// After reconstructedPlan is built:
if (reconstructedPlan.FocusVocabularyIds?.Count > 0)
{
    var dueNow = await _vocabProgressRepo.GetDueVocabularyAsync(resolvedDate);
    var dueIds = new HashSet<string>(dueNow.Select(d => d.VocabularyWordId));
    var staleCount = reconstructedPlan.FocusVocabularyIds
        .Count(id => !dueIds.Contains(id));
    if (staleCount > reconstructedPlan.FocusVocabularyIds.Count / 2)
    {
        _logger.LogInformation("Plan focus vocabulary is stale ({Stale}/{Total} no longer due) -- regenerating", staleCount, reconstructedPlan.FocusVocabularyIds.Count);
        return null; // Forces fresh generation
    }
}
```

### Fix 3 (MEDIUM): Add timezone to UserProfile for multi-surface consistency

File: src/SentenceStudio.Shared/Models/UserProfile.cs
New property: `public string? IanaTimeZoneId { get; set; }`
Migration: `dotnet ef migrations add AddUserTimeZone --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj`

Both the webapp and the API can then read the user's stored timezone instead of relying on ambient system/request context.

### Fix 4 (LOW): Consider sync ordering guarantee

File: src/SentenceStudio.Shared/Services/SyncService.cs

Currently sync is bidirectional in one pass. If VocabularyProgress uploads AFTER DailyPlan/DailyPlanCompletion downloads, the phone receives a stale plan before the server sees updated progress. Consider:
- Sync VocabularyProgress UP first (ensure server has fresh progress)
- Then sync DailyPlan/DailyPlanCompletion DOWN (plan on server can be regenerated with fresh data)

This requires CoreSync to support ordered table sync or two-pass sync.

### Regression Tests to Add (Jayne's lane)

1. Test: WebApp IPlanDateContext resolves user-local date, not UTC (verify UserLocalDate matches user's timezone for CDT/PST/EST scenarios).
2. Test: GetCachedPlanAsync invalidates reconstructed plan when >50% focus vocabulary is no longer due.
3. Test: Plan generated during UTC-ahead window (server midnight but user's evening) does not carry words that become non-due overnight.
4. Test: CoreSync round-trip of DailyPlan preserves correct Date key across timezone boundaries.

---

## Summary

The root cause is a timezone impedance mismatch: the production webapp (Blazor Server on Azure, UTC) generates plans keyed to "today" from its own clock, which is 5 hours ahead of Captain's local time (CDT). Between 7pm-midnight CDT, the server pre-creates tomorrow's plan using progress that hasn't yet been updated by tonight's study. Once written to Postgres and synced, the plan is pinned by the short-circuit and never re-evaluated for vocabulary freshness.

The DueOnly bypass then serves those stale focus words during activities, even though they're no longer due per current progress.

---

# Jayne: Mastery Calibration Regression Tests

Date: 2026-06-17
Author: Jayne (Tester)
Status: DELIVERED — characterization tests merged, target tests documented

---

## A. Existing Test Inventory

### Already pinned by existing tests:

| File | What it pins |
|------|-------------|
| `tests/.../Services/MasteryScoring/ScoringEngineTests.cs` | DifficultyWeight acceleration (MC=1.0, Text=1.5, Sentence=2.5), temporal weighting (scaled penalty), partial streak preservation, recovery boost. Uses in-memory SQLite + real VocabularyProgressService. |
| `tests/.../Integration/MasteryAlgorithmIntegrationTests.cs` | Full lifecycle Unknown->Learning->Known; EffectiveStreak/12 divisor; 1/12 per MC correct; 1.5/12 per Text; IsKnown requires Mastery>=0.85 AND Prod>=2; 12 MC corrects saturate to 1.0 but IsKnown=false without production; wrong answer drops below Known. |
| `tests/.../Integration/SpacedRepetitionIntegrationTests.cs` | SM-2 interval progression (1->6->15->...), EF adjustments, interval cap at 365, reset on wrong, 60-day override for Known words, GetDueVocabularyAsync filters (due date + excludes Known). |
| `tests/.../PlanGeneration/VocabQuizFilteringTests.cs` | IsKnown filtering vs stale IsCompleted field. |

### What was NOT pinned before this work:

1. The specific /12 divisor producing exactly 62.5% at EffectiveStreak 7.5 — only approximations tested
2. The DECOUPLING between SM-2 interval and mastery (230 days interval while only 62% mastery)
3. The "to see" / "teacher" exact production values from screenshots
4. The 5-consecutive-correct SM-2 progression to exactly 230 days
5. IsKnown gate failure when mastery is below BOTH thresholds (primary 0.85 AND bypass 0.75)
6. The DueOnly bypass via FocusVocabularyIds (documented but untestable without refactor)

---

## B. Characterization Tests Added

File: `tests/SentenceStudio.UnitTests/Services/MasteryScoring/MasteryCalibrationCharacterizationTests.cs`

Tests (19 total):

1. `EffectiveStreak_Equals_CurrentStreak_Plus_ProductionTimesHalf` [Theory, 4 cases]
   - (6.0, 3) => 7.5 (boda "to see")
   - (5.5, 3) => 7.0 (seonsaengnim "teacher")
   - (6.0, 0) => 6.0 (pure recognition)
   - (0, 0) => 0.0 (fresh)

2. `MasteryScore_Divisor12_ProducesExpectedPercentage` [Theory, 5 cases]
   - 7.5 => 0.625 (62.5%)
   - 7.0 => 0.5833 (58.3%)
   - 12.0 => 1.0
   - 15.0 => 1.0 (capped)
   - 0.0 => 0.0

3. `IsKnown_False_AtMastery625_BelowBothGates` — pins "to see" scenario
4. `IsKnown_False_AtMastery583_BelowBothGates` — pins "teacher" scenario
5. `IsKnown_True_PrimaryGate_Mastery85_Production2` — minimum passing case
6. `IsKnown_True_HighConfidenceBypass_Mastery75_Production4_Attempts8` — bypass minimum
7. `IsKnown_False_HighConfidenceBypass_MissesOneCondition` — boundary
8. `Sm2_FiveConsecutiveCorrects_Produces230DayInterval` — pins exact progression
9. `Sm2_And_Mastery_Are_Decoupled_FiveCorrects` — THE key test proving the RCA
10. `Sm2_EaseFactor_StaysAtCap_WhenAlreadyAt2Point5`
11. `Sm2_EaseFactor_RecoverFromDecrease`
12. `NonDueWord_WithFuture_NextReviewDate_IsDueForReview_False` — documents Concern #2 model state

---

## C. Test Run Results

Command: `dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj --no-restore --verbosity minimal`

Result: **636/636 passed, 0 failed, 0 skipped**

NOTE: The previously-documented "intentional failure" (`ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed`) NOW PASSES against current code. The underlying bug was apparently fixed since the baseline doc (534/535) was written. Total test count grew from 535 to 636 (101 tests added by other work + my 19).

No new failures introduced.

---

## D. Do the Screenshots Match Current Code?

YES — fully explained:

- EffectiveStreak = CurrentStreak + ProductionInStreak * 0.5 matches 7.5 and 7.0
- MasteryScore = EffectiveStreak / 12.0 matches 62.5% and 58.3%
- IsKnown = false because 0.625 < 0.85 (primary) and 0.625 < 0.75 (bypass)
- ReviewInterval = 230 from SM-2: 1 -> 6 -> 15 -> 37 -> 92 -> 230 (5 corrects at EF 2.5)
- IsDueForReview = false because NextReviewDate (Feb 2027) > today

The DECOUPLING is by design: SM-2 and mastery are independent systems.
SM-2 grows exponentially (2.5x per correct). Mastery grows linearly (+1/12 per MC correct).
After 5 corrects SM-2 says "230 days until review" while mastery says "only 62%".

---

## E. Target-Behavior Regression Tests (post-fix)

Once a fix direction is decided, add these tests. They should FAIL against current code
and PASS after the fix.

### For Concern #1 (mastery calibration):

File: `tests/.../Services/MasteryScoring/MasteryCalibratedTargetTests.cs`

| Test Name | Assertion |
|-----------|-----------|
| `FiveCorrects_MixedMode_ShouldReachKnownOrHighMastery` | After 5 all-correct attempts with production mix, MasteryScore should be >= 0.75 (or whatever new threshold/divisor is chosen) |
| `Sm2IntervalAndMastery_ShouldNotContradict` | If SM-2 interval > 60 days, mastery should be >= some minimum floor (e.g., 0.70) to avoid the "Learning but won't review for 230 days" paradox |
| `KnownGate_ShouldBeReachableInReasonableAttempts` | A word with 5 all-correct (3 production) should either be Known or be reviewed within 30 days |

### For Concern #2 (plan staleness / DueOnly bypass):

File: `tests/.../PlanGeneration/DueOnlyBypassRegressionTests.cs`

| Test Name | Assertion |
|-----------|-----------|
| `FocusVocabularyPath_ShouldRespectDueDate` | When FocusVocabularyIds are populated by the plan, non-due words (NextReviewDate in future) should NOT be served in VocabQuiz |
| `PlanBuilder_ShouldNotIncludeNonDueWords` | DeterministicPlanBuilder FocusVocabularyIds output should only contain words where NextReviewDate <= today OR words with no progress record |
| `VocabReviewActivity_ShouldOnlyServeDueWords` | End-to-end: plan item with VocabularyReview activity type -> quiz loads -> only due words appear |

NOTE: The DueOnly bypass (VocabQuiz.razor:717-740) lives in Blazor rendering code.
Testing it properly requires either:
(a) Extracting the filtering logic into a testable service method, or
(b) Integration testing via the Blazor test host.
Recommend (a) as the cleaner path.

---

## F. Inbox File

`.squad/decisions/inbox/jayne-mastery-regression-tests.md`

---

## Concern #2 — Per-user Timezone Plan-Date Fix (2026-06-17 session, branch squad/per-user-timezone-plan-dates)

---

### 2026-06-17T16:08:31-05:00: Production confirmation + approved fix scope (Concern #2 only)

**By:** Captain (David Ortinau), recorded by Squad coordinator

- Production verified read-only on `sstudio-prod-biz` (temporary single-IP firewall rule, since removed). Profile `80227a51` = dave@ortinau.com (Korean). BOTH concerns confirmed.
- Concern #1 (mastery recalibration): CONFIRMED but **NOT approved for change now — parked.** All 3 words are 6/6 correct, ReviewInterval=365 (MAX), NextReviewDate 2027-06-17, yet MasteryScore 0.75-0.79 (< 0.85 Known gate) → still "Learning". EffectiveStreak/12 confirmed. Characterization tests committed on branch `squad/mastery-calibration-characterization-tests` (commit b16eb5c1). Leave the calibration code as-is.
- Concern #2 (plan staleness): CONFIRMED with three distinct defects:
  1. **STALE-PIN** — today's DailyPlan (Date=2026-06-17) CreatedAt 2026-06-17 00:07:56 UTC, but 3 words' VocabularyProgress.UpdatedAt = 2026-06-17 09:39-09:40 UTC (study happened 9.5h AFTER plan build). Plan never regenerated; same words in 5 consecutive plans (Jun 13-17).
  2. **CROSS-TABLE DATETIME INCONSISTENCY** — same generation event wrote DailyPlan.CreatedAt 2026-06-17 00:07:56.947 UTC and DailyPlanCompletion.CreatedAt 2026-06-16 19:07:56.952 (exactly 5h CDT offset apart, same millisecond). One path uses DateTime.UtcNow, the other DateTime.Now.
  3. **DUEONLY BYPASS (amplifier)** — VocabQuiz.razor:717-740 skips NextReviewDate filter on the FocusVocabularyIds path; stale focus words show even though IsDue=No.

**Approved fix scope (Concern #2 only):**
- A. `UserProfile.IanaTimeZoneId` (nullable). Dual-provider EF migration (Postgres + SQLite). Null fallback = UTC. Capture via JS interop on webapp interactive circuit; MAUI heads use TimeZoneInfo.Local. Register `WebAppPlanDateContext` in webapp DI.
- B. UTC normalization: fix DailyPlanCompletion.CreatedAt local-time write; audit for DateTime.Now / DateTime.UtcNow.Date inconsistencies.
- C. Plan freshness: drop focus words no longer due (NextReviewDate > now, TotalAttempts > 0); keep brand-new (0-attempt) words; honor due-ness for attempted words.
- D. Regression tests: date-key resolves to user-local near midnight; all writes UTC; studied word drops from same-day plan; recurrence guard against DateTime.Now in plan-date code.

**Why:** Production-confirmed recurrence of a known timezone defect class. Mastery recalibration deferred by Captain.

---

### 2026-06-17: Per-user timezone plan-date keying — implementation (Wash)

**By:** Wash (Backend Dev)
**Branch:** `squad/per-user-timezone-plan-dates` (local only, not pushed)
**Commits:** c7f192e5, 0cdda7ba, 7e7d67ef

**What shipped:**

1. `UserProfile.IanaTimeZoneId` (nullable string) — `src/SentenceStudio.Shared/Models/UserProfile.cs:33-43`. Null fallback = UTC (never America/Chicago).
2. Dual-provider EF migration `20260617211855_AddUserProfileIanaTimeZoneId` — Postgres (`type: text`) + SQLite (`type: TEXT`). Hand-written (see tooling friction entry below). Validated via `scripts/validate-mobile-migrations.sh` (exit 0).
3. `WebAppPlanDateContext` (scoped `IPlanDateContext` for webapp) — `src/SentenceStudio.WebApp/Platform/WebAppPlanDateContext.cs`. Resolves from `CircuitUserStateAccessor.Current.UserProfileId` (circuit) or IHttpContextAccessor claims (SSR). Fallback = UTC. Registered Scoped at `Program.cs:258-259`, overriding the Transient `DevicePlanDateContextProvider`.
4. `TimeZoneCaptureService` — `src/SentenceStudio.WebApp/Platform/TimeZoneCaptureService.cs`. Multi-tenant safe: refuses write on empty userId. Validates IANA id via TimeZoneResolver.TryResolve. No-ops on unchanged value.
5. `TimeZoneCapture.razor` (headless component) — `src/SentenceStudio.WebApp/Components/TimeZoneCapture.razor`. Placed by Kaylee (see Kaylee entry).
6. UTC normalization: `VocabQuiz.razor:725,1363` `DateTime.Now` → `DateTime.UtcNow`. ProgressService / PlanService / ProgressCacheService already use UtcNow consistently.
7. `ApplyFocusVocabularyFreshnessAsync` added to ProgressService — drops focus words where `NextReviewDate > UtcNow AND TotalAttempts > 0`; keeps brand-new (0-attempt) words; wired into all 3 return paths of `GetCachedPlanAsync`.

**Files changed:** 13 files, +471/-2. Full table in wash-impl-per-user-timezone-plan-dates.md.

---

### 2026-06-17: EF Core `dotnet ef` blocked on multi-targeted Shared project (tooling friction)

**By:** Wash (Backend Dev)

**Problem:** `dotnet ef migrations add --framework net10.0` fails with `MSB4057: The target "ResolvePackageAssets" does not exist in the project` against `SentenceStudio.Shared.csproj`. TFMs include `net10.0;net11.0-ios;net11.0-android;net11.0-maccatalyst;net11.0-macos`. The EF `--framework` flag cannot properly isolate TFM evaluation when the project uses conditional `<Compile Remove>` blocks (MAUI migration partition strategy).

**Workaround:** Hand-write dual-provider migrations following `.squad/skills/ef-dual-provider-migrations/SKILL.md`. Always validate with `bash scripts/validate-mobile-migrations.sh`.

**Documentation stale (follow-up needed, separate PR):**
- AGENTS.md line ~132: "The Shared project targets plain net10.0" — **incorrect** (multi-targeted since net11 MAUI heads).
- copilot-instructions.md (Database Migrations section): "The Shared project targets plain net10.0 and works fine with EF tooling. There is no TFM conflict" — **incorrect**.

**Upstream:** No matching issue found in dotnet/efcore. Recommend filing: `--framework` should properly isolate TFM evaluation in multi-targeted projects with conditional Compile Remove.

---

### 2026-06-17: Concern #2 timezone regression tests — first pass (Jayne)

**By:** Jayne (Tester/QA)
**Branch:** `squad/per-user-timezone-plan-dates`
**Commit:** 4bacc447
**Baseline before:** 617/617 passing.

**14 test cases added** to `tests/SentenceStudio.UnitTests/Services/Progress/Concern2TimezoneRegressionTests.cs`:

- Near-midnight CDT = next-day UTC pin (the exact production defect)
- Null/empty/invalid timezone falls back to UTC
- Valid IANA timezone resolves non-UTC zone
- TimeZoneCaptureService multi-tenant guard (empty userId refuses write) [Theory with 3 InlineData]
- Plan freshness drops studied-not-due, keeps brand-new, keeps due
- Plan freshness keeps all when all due
- Source-scan recurrence guards: `DateTime.Now` banned in VocabQuiz.razor and Services/Progress/*.cs

**Result after adding:** 631/631 passing (+14). Recurrence guards use source-scan pattern (matching existing PlanDateContextBannedSymbolsTests; no BannedSymbols.txt in repo).

---

### 2026-06-17: Review — REJECTED (two blockers) (Zoe)

**By:** Zoe (Lead / Reviewer)
**Commits reviewed:** c7f192e5, 0cdda7ba, 7e7d67ef, 4bacc447

**VERDICT: REJECT — two blockers:**

**BLOCKER #1 — TimeZoneCapture never rendered.**
`<TimeZoneCapture />` was not placed in any render tree. Component exists; DI registered; but zero instantiations. Consequence: `IanaTimeZoneId` never populated; `WebAppPlanDateContext` always sees null; falls back to UTC; production stale-pin defect persists end-to-end.
→ Owner: **Kaylee.** Placement must be in webapp-only interactive render tree (AppRoutes.razor or MainLayout equivalent).

**BLOCKER #2 — Cross-tenant freshness leak.**
`ApplyFocusVocabularyFreshnessAsync` called `GetByWordIdsAsync` (unscoped — returns all users' rows). On shared Postgres, `.GroupBy().First()` can pick another tenant's progress row, dropping a brand-new focus word from the wrong user's plan.
→ Owner: **Simon.** Fix: add `GetByWordIdsForUserAsync` with UserId filter, switch caller.

**PASSed items:** Migration dual-provider, WebAppPlanDateContext UTC fallback, freshness algorithm logic, UTC normalization sites, test adequacy.

**Non-blocking follow-ups:**
- (G1) WebAppPlanDateContext integration test (requires Blazor WebApp test host or extract helper to Shared)
- (G2) Cross-tenant freshness regression test (after Simon's fix)
- Extend recurrence guard to Services/Plans
- AGENTS.md / copilot-instructions.md TFM doc fix (separate PR)
- File dotnet/efcore upstream issue

---

### 2026-06-17: Fix cross-tenant freshness scoping (Simon)

**By:** Simon (Backend Specialist / Escalation)
**Branch:** `squad/per-user-timezone-plan-dates`
**Commit:** 03750fad
**Addresses:** Zoe blocker #2

**Root cause:** `GetByWordIdsAsync` filters only by `VocabularyWordId`. On shared Postgres, returns rows for ALL users.

**Fix:**
1. Added `VocabularyProgressRepository.GetByWordIdsForUserAsync(List<string> wordIds, string? userId = null)` at VocabularyProgressRepository.cs:119-152.
   - Resolves userId from `ActiveUserId` when null.
   - Empty/missing userId logs warning, returns empty list (never unfiltered, never throws).
   - `.Where(vp => vp.UserId == userId)` applied server-side BEFORE word-id filter.
   - Retains 500-row batch optimization for SQLite parameter limits.
   - Warning comment added to existing `GetByWordIdsAsync` (line 76-80) documenting the multi-tenant footgun.
2. `ProgressService.ApplyFocusVocabularyFreshnessAsync:985` switched from `GetByWordIdsAsync` to `GetByWordIdsForUserAsync`.

**Empty-userId safe default:** returns empty list → every focus word hits "no progress = brand new, keep" → plan returned unchanged. Freshness is a refinement, not a gate.

**Audit:** `VocabularyProgressService.cs:280` also calls unscoped method but post-filters at :284 (`.Where(p => p.UserId == resolvedUserId)`). Safe (no leak), but wasteful — switch to `GetByWordIdsForUserAsync` is a perf follow-up (not this PR).

**Build:** `dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj -f net10.0` — 0 errors, 172 pre-existing warnings.

---

### 2026-06-17: Wire TimeZoneCapture into webapp render tree — Blocker #1 resolved (Kaylee)

**By:** Kaylee (Full-stack Dev / Blazor / UI)
**Branch:** `squad/per-user-timezone-plan-dates`
**Commit:** fa2a25d4
**Addresses:** Zoe blocker #1

**Change:** Added `<TimeZoneCapture />` to `src/SentenceStudio.WebApp/Components/AppRoutes.razor`, immediately before the `<Router>` element.

**Why AppRoutes.razor (not shared MainLayout):**
- `MainLayout.razor` is in `SentenceStudio.UI` — compiled into MAUI heads. Placing a WebApp-project component there would break MAUI builds.
- AppRoutes.razor is webapp-only (`SentenceStudio.WebApp/Components/`). Inherits `@rendermode="InteractiveServer"` from App.razor:23.
- Wrapped in `<CascadingAuthenticationState>` from App.razor:22 — auth context available.
- TimeZoneCapture is headless, one-shot per circuit, no UI impact.

**Build:** `dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj -f net11.0` — 0 errors. MAUI heads unaffected.

---

### 2026-06-17: Concern #2 re-test after blocker fixes (Jayne)

**By:** Jayne (Tester/QA)
**Branch:** `squad/per-user-timezone-plan-dates`
**Commit:** 2b5eb73e
**Prior pass:** 631/631 (commit 4bacc447)

**Tests added (+2):**

1. `FocusVocabularyFreshness_MultiTenant_DoesNotLeakCrossTenantProgress` — pins Simon's `GetByWordIdsForUserAsync` fix. Uses real in-memory SQLite, two UserProfile rows, one shared word id; UserA has TotalAttempts=5/NextReviewDate=tomorrow; UserB has TotalAttempts=0. Asserts (a) exactly 1 row returned for UserB; (b) row owned by UserB; (c) freshness decision = KEEP. If anyone reverts to `GetByWordIdsAsync`, count becomes 2 and (a) fails.
2. `WebAppPlanDateContext_Integration_Skipped_RequiresBlazorTestHost` — marker test documenting that WebAppPlanDateContext lives in SentenceStudio.WebApp (not referenced by UnitTests project). Proper test requires a dedicated integration test project with Blazor WebApp test host or extract TZ-lookup to Shared helper. Marker always passes; gap stays visible.

**Recurrence guard extended:** `ProgressGatedPaths` in `Concern2TimezoneRegressionTests.cs` now includes `src/SentenceStudio.Shared/Services/Plans` (in addition to Services/Progress). Result: clean pass (only mentions are in XML doc comments, excluded by `///` filter).

**Final result:** 633/633 passing / 0 failed / 0 skipped. Build: 0 errors, 212 pre-existing warnings.

---

### 2026-06-17: Re-review — APPROVED (Zoe)

**By:** Zoe (Lead / Reviewer)
**Commits reviewed:** 03750fad (Simon), fa2a25d4 (Kaylee), 2b5eb73e (Jayne)

**VERDICT: APPROVED for Captain's pre-push /review gate.**

Both blockers genuinely resolved. All previously-PASSed items still hold. Test suite 633/0/0.

**Blocker #1 (TimeZoneCapture wiring):** PASS. AppRoutes.razor placement is correct (not MainLayout — MAUI-build safety). InteractiveServer inheritance confirmed. One-shot-per-circuit guard and multi-tenant guard both in place. Zoe retracts "MainLayout" from prior review; AppRoutes is the right webapp-only placement.

**Blocker #2 (cross-tenant freshness):** PASS. `GetByWordIdsForUserAsync` applies UserId filter server-side before word-id filter. Empty-userId safe default returns plan unchanged. Warning comment on `GetByWordIdsAsync` documents the footgun for future callers. `VocabularyProgressService.cs:280` post-filters correctly (safe, perf follow-up).

**Non-blocking carry-forward follow-ups:**

| # | Item | Owner |
|---|------|-------|
| 1 | Banned-symbol guard for `GetByWordIdsAsync` callers in Services/Progress (with `// allow:multi-tenant-safe` marker) | Jayne |
| 2 | Switch `VocabularyProgressService.cs:280` to `GetByWordIdsForUserAsync` (perf, not correctness) | Simon |
| 3 | WebAppPlanDateContext integration test (Blazor WebApp test host or Shared helper refactor) | Jayne |
| 4 | Doc fix: AGENTS.md / copilot-instructions.md re Shared TFM matrix | Scribe (separate docs PR) |
| 5 | File dotnet/efcore upstream issue (`--framework` + multi-targeted projects + conditional Compile Remove) | Wash |

---

## Archived processed inbox: captain-rca-datarecoveryservice-cross-tenant-corruption.md

# RCA: DataRecoveryService silently retagged Captain's 8 months of work to a test account

**Date:** 2026-06-11  
**Author:** Captain (David Ortinau) + Coding Agent  
**Severity:** Critical — data corruption + cross-tenant data leak  
**Branch:** `squad/vocab-preview-quiz-mismatch-fix` (data recovery committed in same PR)  
**Status:** Recovery executed. Code prevention fix pending (separate work item).

---

## Symptom

Captain logged into the local webapp as `dave@ortinau.com`, expected to see his ~3,000 vocab words from earlier today, saw an empty profile with the name "Jayne Test" displayed instead.

## What was actually wrong

Three layers:

1. **Data corruption** — All 166 of Captain's LearningResources (back to October 2025), 27 SkillProfiles, 2,202 UserActivity rows, 165 DailyPlanCompletions, etc. had been silently retagged from his real UserProfileId to the test account `squad-jayne@sentencestudio.test` (UserProfileId `5b999582-27d2-4646-9642-f4e4d35be8e8`). CoreSync had faithfully propagated this corruption from Mac Catalyst SQLite up to Postgres.

2. **Cross-tenant leak (UserProfileRepository.GetAsync)** — When `dave@ortinau.com` logged in but `dave`'s UserProfile row didn't exist in Postgres, the repository fell back to `db.UserProfiles.FirstOrDefaultAsync()` and returned any row — usually the oldest one (squad-jayne). The Profile.razor page used this codepath, so Captain saw "Jayne Test" as the displayed name while JWT-scoped data queries correctly showed dave's empty content. The name-vs-data mismatch was the first visible symptom.

3. **WebPreferencesService is a server-wide Singleton** (deferred — Bug #3, not addressed in this PR). It persists to a single shared file on disk, so concurrent webapp users would clobber each other's preferences.

## Root cause of Layer 1 (the data corruption)

`DataRecoveryService.RecoverOrphanedDataAsync(string newUserProfileId)` was introduced by commit `b466985d` (2026-04-15, "feat: add automatic orphan data recovery after server wipe"). Its purpose: when production Postgres is wiped and a returning user re-registers, their local-only records carry the old UserProfileId and become invisible. The service was designed to retag them to the new id.

It is invoked unconditionally from `IdentityAuthService.StoreTokens()` line 421 — **on every successful login**.

The algorithm:
1. Scan every user-scoped table (LearningResource, SkillProfile, UserActivity, DailyPlan, DiaryEntry, MonitoredChannel, VideoImport, WordAssociationScore, VocabularyProgress, MinimalPair*, etc.) for any `UserProfileId`/`UserId` value that differs from the new login's `UserProfileId`.
2. `UPDATE` every such row to the new `UserProfileId`.
3. In `RetagUserProfileAsync`: if the new id's UserProfile row already exists locally, `DELETE` every other UserProfile row.

When squad-jayne signed in on Captain's Mac Catalyst this morning (~13:29 PDT) to do E2E testing, `DataRecoveryService` did exactly what its code says — it scanned the device, found Captain's real `UserProfileId`'s data (~2,584 rows total), and retagged ALL of it to squad-jayne. CoreSync then synced the corruption to Postgres.

The smoking gun is in the local SQLite `__CORE_SYNC_CT` table — the `UserProfile|D|f452438c-b0ac-4770-afea-0803e2670df5|` event with empty SRC (local-origin delete) shows Captain's UserProfile row being removed.

## The fundamental design flaw

`DataRecoveryService` answers the wrong question. It asks: "What's on this device that doesn't match the user who just signed in?" — and assumes the answer is always "orphans that need a new home."

In reality, the answer can also be:
- "Data belonging to a real previous user on a shared/testing device" ← what bit us
- "Test fixtures another developer left behind"  
- "Records from when I was logged in as a different account 5 minutes ago"

The service does **no checks** for:
- **Account email continuity** — orphan UserProfile.Email ≠ new account's email → strong "different person" signal, but ignored.
- **Temporal sanity** — server account created today vs orphan records from October 2025 → impossible if the same human. Not checked.
- **User confirmation** — silently UPDATEs production rows; no prompt, no preview, no log warning that's visible to the user.
- **Sync state** — records already uploaded under another id are not "local-only orphans"; they're synced data belonging to that other user. Ignored.
- **Run frequency** — runs on every login forever; should run at most once after install (or only when explicitly invoked for the recovery scenario).

## Prevention — recommended fix (separate PR)

In priority order:

1. **Refuse to recover when orphan UserProfile rows have a different `Email` value.** Strongest signal that we're dealing with a different human, not the same human with a regenerated id.

2. **Refuse to recover when orphan records pre-date the new account's `CreatedAt`.** Server account created today + orphan rows from October 2025 = impossible by construction.

3. **Run only on first login after a fresh install.** Track via preference flag `_data_recovery_complete`. The legitimate "server wipe + re-register" scenario doesn't recur.

4. **Require explicit opt-in** for the actual recovery action — UI prompt: "We found N unsynced records from a previous session. Adopt them under your new account, or leave them as orphan?"

5. **At minimum (interim mitigation)**: gate the entire service behind a `enableAutomaticDataRecovery` config flag, default `false`. Turn it back on per-incident only when a real server wipe occurs.

## Recovery execution log

- **Backup**: Postgres dumped to `.squad/backups/postgres-pre-retag-20260611-190624.sql` (7.9 MB), Mac Catalyst SQLite snapshot to `.squad/backups/sstudio-pre-retag-20260611-190624.db3` (12 MB).
- **Postgres**: dave's 5 auto-seeded smart resources + 2 plans deleted (dedupe); 11 UserProfileId tables retagged (5b999582 → ba20bcc5); 4 UserId tables retagged (no rows); squad-jayne AspNetUsers row + 8 RefreshTokens deleted; squad-jayne UserProfile row deleted. Verified: 0 rows under 5b999582; dave owns 166 resources, 2,202 activities, 27 skills, 1 plan.
- **Mac Catalyst SQLite**: same retag applied (incl. UserProfile PK rename 5b999582 → ba20bcc5 with Name/Email update to dave). DailyPlan unique-constraint conflict resolved by deleting dave's auto-seeded plans first. CoreSync `__CORE_SYNC_CT` change-tracking entries created by the retag suppressed (no redundant push to server). AspNetUsers squad-jayne row + dependents deleted locally.
- **Mac Catalyst preferences**: `active_profile_id` set to `ba20bcc5…`; auth JWT/refresh/expires keys deleted; `app_is_authenticated` = false; next launch will require fresh login as dave@ortinau.com.
- **WebApp prefs file**: already targets ba20bcc5; left as-is.

## What we're committing in this PR

- Bug #2 fix (Wash): removed `UserProfileRepository.GetAsync()` silent fallback + extended the cross-tenant guard pattern to LearningResource/DiaryEntry/SkillProfile/UserActivity repositories (already-established pattern per `AGENTS.md` non-negotiable rule). 3 new regression tests for the GetAsync codepath.
- This RCA decision doc.
- The recovery itself was a one-shot DB operation, not in source.

## What's deferred to a separate PR

- **Prevention fix in DataRecoveryService** — apply at minimum mitigations #1, #2, and #3 above. New regression tests covering the "different real user signs in on the device" scenario. Likely best-handled by Wash or River on a dedicated branch.
- **Bug #3 (WebPreferencesService Singleton)** — needs per-request scoping or migration to the JWT-claim-based pattern used by `HttpUserScopeProvider`.
- **Other 9 dangling AspNetUsers FKs in Postgres** — need a separate sweep to either resurrect their UserProfiles or clean up the orphan AspNetUsers rows.

---

## Archived processed inbox: copilot-e2e-caught-di-captive-dependency.md

# Decision: Live e2e caught a DI captive-dependency crash in the per-user-timezone fix

**By:** Copilot CLI (Claude Opus 4.8), for Captain
**Date:** 2026-06-18
**Branch:** squad/per-user-timezone-plan-dates (commit f4f4c85a)

## What
The Concern #2 per-user-timezone fix registered the webapp's `IPlanDateContext`
(`WebAppPlanDateContext`) as **Scoped**. The build succeeded and all 633 unit tests
passed, but the webapp **crashed on startup** under Aspire:

```
Cannot consume scoped service 'IPlanDateContext' from singleton
'GeneratedPlanValidator'  (DI ValidateOnBuild) — WebApp/Program.cs:201
```

## Why it happened
`AddSentenceStudioCoreServices` (AppLib) registers the plan services as **singletons**:
`GeneratedPlanValidator` captures `IPlanDateContext` in its constructor;
`ProgressService`/`DeterministicPlanBuilder` resolve it from the **root** provider.
A Scoped `IPlanDateContext` therefore (a) fails `ValidateOnBuild` at startup and
(b) would throw on root resolution at runtime. The API avoids this because it does
NOT call `AddSentenceStudioCoreServices` and re-registers `DeterministicPlanBuilder`
et al. as Scoped (Api/Program.cs:288-296); the webapp does call it.

## Fix
Register `WebAppPlanDateContext` as **Transient** (matches AppLib's device
registration `CoreServiceExtensions.cs:140`). Safe because the current user is
resolved via `CircuitUserStateAccessor` (static `AsyncLocal`, ambient across
scopes), not the DI scope — so a transient instance built from any provider still
reads the right user's `IanaTimeZoneId`.

## Verified end-to-end (Aspire + Playwright, local Postgres)
- Webapp boots clean (no DI crash) after the fix.
- Migration `AddUserProfileIanaTimeZoneId` applied to real Postgres (column present).
- Browser login as squad-jayne wrote `IanaTimeZoneId = America/Chicago` (TZ capture works).
- Today's Plan generated (runtime `IPlanDateContext` resolution works).
- Freshness filter: marked 5 of 15 focus words studied+not-due → served plan dropped
  exactly those 5 (Vocabulary Review URL carried 10 FocusVocabularyIds, none the 5).
  Synthetic test rows cleaned up afterward.

## Follow-up (recommended, not done)
Add a DI-validation regression guard for the webapp: build the webapp's
`IServiceCollection` and call `BuildServiceProvider(validateScopes:true,
validateOnBuild:true)` in a test so captive-dependency regressions fail CI instead
of at deploy/startup. (Pairs with Jayne's existing "WebAppPlanDateContext
integration test" follow-up.)

---

## Archived processed inbox: datarecovery-prevention-implementation.md

# Decision: DataRecoveryService Prevention Implementation

**Date:** 2026-06-11  
**Author:** Coding Agent (acting as Wash)  
**Branch:** `squad/datarecovery-prevention-fix`  
**Relates to:** `.squad/decisions/inbox/captain-rca-datarecoveryservice-cross-tenant-corruption.md`

---

## Context

Following the June 11 cross-tenant data corruption incident (full RCA in the file above), this PR
implements the prevention fix: three new safeguards in `DataRecoveryService.RecoverOrphanedDataAsync`
plus a config flag in `IdentityAuthService.StoreTokens` that disables automatic recovery by default.

## What was implemented

### 1. Email mismatch safeguard (`DataRecoveryService`)

Before retagging anything, the service now queries orphan `UserProfile` rows and compares their
`Email` to the email of the user who just signed in. If any orphan profile has a non-empty email
that differs (case-insensitive) from the new user's email, the service logs:

```
[OrphanRecovery] ABORTED — Email mismatch: orphan profile(s) belong to a different user.
NewUserId=..., OrphanUserIds=..., OrphanEmails=dave@ortinau.com
```

This is the primary safeguard and covers the exact production scenario (squad-jayne signing in on
Captain's device).

### 2. Temporal sanity safeguard (`DataRecoveryService`)

Using EF Core LINQ queries (not raw SQL to avoid DateTime format issues), the service:
- Fetches the new user's `UserProfile.CreatedAt` from local SQLite.
- Fetches the earliest `CreatedAt` across orphan `LearningResource` and `UserActivity` rows.
- If any orphan record predates the new account by more than 1 day, logs:
  ```
  [OrphanRecovery] ABORTED — Temporal mismatch: orphan data (...) predates new account (...).
  ```

### 3. First-run gate (`DataRecoveryService` + `IPreferencesService`)

The service checks `_preferences.Get("_data_recovery_complete", "") == "true"` at the top of
`RecoverOrphanedDataAsync`. If set, it returns immediately (Debug log only). The flag is written
as `"true"` at the end of any run that either found no orphans or completed a successful retag.
This makes the service one-shot: it runs at most once per install, which matches the only
legitimate use case (one-time recovery after a server wipe).

The `IPreferencesService` is now injected into `DataRecoveryService` as a constructor parameter.
Since it was already registered in DI (`MauiPreferencesService` in `SentenceStudioAppBuilder`),
no DI registration changes were needed.

### 4. `enable_automatic_data_recovery` config flag (`IdentityAuthService`)

`IdentityAuthService.StoreTokens` now checks:
```csharp
if (_dataRecovery != null && _preferences.Get("enable_automatic_data_recovery", false))
```
Default is `false`, so automatic recovery is **disabled by default**. The service can still be
invoked manually in a future UI flow. When flipped to `true`, the email and token are extracted
from JWT and passed to `RecoverOrphanedDataAsync`.

### 5. Structured ABORTED logging

All abort paths log at `Warning` level with the format:
```
[OrphanRecovery] ABORTED — {Reason}. NewUserId={NewUserId}, OrphanUserIds={OrphanCount}, OrphanEmails={OrphanEmails}
```

### 6. Tests (`tests/SentenceStudio.UnitTests/Data/DataRecoveryServiceTests.cs`)

Four regression tests covering:
1. **Exact production scenario** — orphan profile `dave@ortinau.com`, new login `squad-jayne@...`
   → ABORT, zero mutations, Warning logged with "ABORTED" and the orphan email.
2. **Temporal abort** — orphan data 6 months old, new account created today → ABORT.
3. **First-run gate** — `_data_recovery_complete="true"` → exits immediately, no Set calls.
4. **Legitimate recovery** — same email on old+new profile, orphan data 30 min old → retag
   succeeds, first-run flag is set.

Test count delta: 534 → 538 passing (4 new tests on the current main baseline).

### 7. Documentation

- `AGENTS.md` `## Data Preservation Rules` — new bullet about DataRecoveryService safeguards with
  cross-references to the RCA and regression tests.
- `.github/copilot-instructions.md` `## Multi-tenant data scoping rule` — new "DataRecoveryService
  gates (NON-NEGOTIABLE)" bullet documenting the three safeguards and the config flag.

## Decisions made

- **EF Core LINQ over raw SQL for DateTime** — raw `SqlQueryRaw<string>` + `DateTime.TryParse` with
  `RoundtripKind` failed in tests because SQLite stores dates as "yyyy-MM-dd HH:mm:ss.fffffff" (no T
  separator). Switched `GetNewUserCreatedAtAsync` and `GetEarliestOrphanCreatedAtAsync` to EF Core
  LINQ queries (`UserProfiles.Where(...).Select(p => (DateTime?)p.CreatedAt).FirstOrDefaultAsync()`,
  `LearningResources.Where(...).MinAsync()`). Raw SQL is kept only for CollectOrphanIdsAsync and the
  retag/delete operations (as before).

- **Email check requires email to be passed** — the email is not present in `AuthResponseDto`; it
  must be extracted from the JWT. The new `ExtractEmailFromJwt` static helper in
  `IdentityAuthService` does this, reusing the same claim-type fallback chain as
  `BackfillProfileFromJwtAsync`. If the token can't be parsed, `null` is passed and the email
  safeguard is skipped (the temporal and first-run gates still protect).

- **Temporal safeguard is best-effort** — if the new user's profile doesn't exist in local SQLite
  yet (no sync has run yet), `GetNewUserCreatedAtAsync` returns `null` and the temporal check is
  skipped. This is intentional: the email safeguard is the primary blocker; the temporal check is
  belt-and-suspenders. A future improvement could pass `newUserCreatedAt` explicitly from the server
  profile download.

## Follow-up items

- A future UI flow could let the user explicitly trigger recovery ("We found N records from a
  previous session — adopt them?") rather than relying on the automated path with the flag.
- The `enable_automatic_data_recovery` flag could be moved to a proper `IConfiguration` key if the
  app ever adds structured configuration. For now, `IPreferencesService` is consistent with how
  similar runtime toggles work in this codebase.

---

## Archived processed inbox: kaylee-env-bar.md

### 2026-06-24T21:35: Replace corner ribbon with full-width env bar
**By:** Kaylee (Full-stack Dev)

**What:** Replaced the `position:fixed` rotated-corner ribbon (`EnvironmentBadge`) with a thin full-width horizontal bar pinned to the very top of the UI, in normal document flow (not fixed-position overlay). Component name kept as `EnvironmentBadge` to avoid touching 4 call sites.

**Changes:**
- `src/SentenceStudio.UI/Components/EnvironmentBadge.razor` — new bar markup (`env-bar`, `env-bar-pill`, `env-bar-apihost`, `env-bar-version`); adds API hostname and `v1.1.0` version from assembly reflection. Production detection logic unchanged (`azurecontainerapps.io` → render nothing).
- `src/SentenceStudio.UI/wwwroot/css/app.css` — replaced `.env-badge*` styles with `.env-bar*` styles. Bar is in normal flow (no `position:fixed`), subtly tinted background (8% opacity of env accent color), colored pill for env label, monospace host+version. Safe-area `@supports` block expands `padding-top` for iOS notch.
- `src/SentenceStudio.UI/Layout/MainLayout.razor` — outer wrapper changed from `d-flex vh-100` (implicit row) to `d-flex flex-column vh-100`; all former siblings of `<EnvironmentBadge />` wrapped in a new `d-flex flex-row flex-grow-1 overflow-hidden` inner div so the bar occupies real layout space and pushes content down rather than overlapping it.

**Why:** The fixed corner ribbon was hard to read, covered UI elements in the top-right corner, and showed no actionable dogfooding info. The GitHub-style top bar is immediately visible, shows `SentenceStudio | [LOCAL] localhost:7211 ... v1.1.0`, and stays out of the way by occupying its own flex row (~26px) above the app content. `vh-100` math preserved: outer column is 100vh, inner row is `flex-grow-1`.

**Follow-ups:**
- None required. The auth pages (Login, Register, ForgotPassword) render the bar standalone without the MainLayout column wrapper; it renders fine as a block element at the top of those pages.
- The `pulse-production` keyframe and `.env-badge-production` CSS class have been removed (production never rendered a badge anyway — `envLabel` stayed empty for `azurecontainerapps.io`).

---

## Archived processed inbox: kaylee-ingest-indicator.md

# Decision: Shared-Ingest UI Activity Indicator

**Date:** 2026-06-25  
**Author:** Kaylee (Full-stack Dev)  
**Status:** Implemented

## Context

The iOS Share Extension queues shared text/URLs; on app foreground `SharedIngestProcessor.DrainAsync` runs ~20s AI extraction and saves vocab to a "Shared Inbox" resource. The flow was entirely silent — the user returned to the app with no feedback that content was received, processing, or completed.

## Decision

Introduce a thin, cross-platform `SharedIngestNotifier` singleton that bridges the drain to the Blazor UI without any platform or MAUI types in Shared.

## What Was Built

### `SharedIngestNotifier` (Shared/Services)
- Thread-safe singleton with `lock` protecting state mutations.
- States: `Idle` → `Processing` → `Completed` → `Idle` (via `ClearCompleted`).
- Public surface: `SetProcessing(itemCount)`, `SetCompleted(created, skipped, resourceId)`, `ClearCompleted()`, `Changed` event.
- `Raise()` swallows subscriber exceptions so a UI bug can never crash the drain.
- No MAUI/iOS types — lives safely in `SentenceStudio.Shared`.

### Hook points in `SharedIngestProcessor.DrainCoreAsync`
- **SetProcessing** fires after the items snapshot (line ~187), only when `items.Count > 0` (never on the zero-items early return or the Deferred/no-user early return).
- **SetCompleted** fires just before `return drainResult` in the normal path, after all items are processed.

### DI Registration (`CoreServiceExtensions.cs`)
- `services.AddSingleton<SharedIngestNotifier>()` added alongside the other ingest supporting services.

### `SharedIngestIndicator.razor` (UI/Components)
- Injects `SharedIngestNotifier`, `NavigationManager`, `BlazorLocalizationService`.
- **Processing state:** slim blue `alert-primary` banner with `spinner-border spinner-border-sm` + localized text. No dismiss button (auto-clears when completed).
- **Completed state:** slim green `alert-success` banner with `bi-check-circle-fill` icon, `string.Format(Localize["SharedIngest_AddedFormat"], count)`, a "View" button (`NavigateTo("/vocabulary")`), and an X dismiss button.
- **Idle:** renders nothing.
- Auto-dismisses Completed banner after 6 seconds via `System.Threading.Timer`.
- Race-condition guard: `OnInitialized` checks if `State == Completed && LastCompletedAtUtc < 30s` and starts the auto-dismiss immediately (handles foreground-while-drain-already-finished case).
- `IDisposable` unsubscribes and disposes the timer.

### Placement in `MainLayout.razor`
`<SharedIngestIndicator />` placed immediately after `<UpdateAvailableBanner .../>` inside the main content column flex container, so it appears as a non-blocking top banner above `<main>` content on every page.

### Localization Keys
| Key | English | Korean |
|-----|---------|--------|
| `SharedIngest_Processing` | Processing shared content… | 공유된 콘텐츠 처리 중… |
| `SharedIngest_AddedFormat` | {0} items added from sharing | 공유에서 {0}개 항목이 추가되었습니다 |
| `SharedIngest_View` | View | 보기 |
| `SharedIngest_Dismiss` | Dismiss | 닫기 |

Added to both `AppResources.resx` and `AppResources.ko.resx`.

## Build Results
- `SentenceStudio.Shared` — 0 errors
- `SentenceStudio.UI` — 0 errors
- `SentenceStudio.iOS` (iossimulator-arm64 Debug) — 0 errors

## Alternatives Considered
- **Toast only:** A toast disappears before the user notices; a persistent banner is more appropriate for a ~20s async operation.
- **Event on ISharedIngestProcessor:** Would require changing the interface contract; the notifier is simpler and injectable from either side.
- **`IProgress<T>`:** Works but less observable from Blazor's subscription model; event-based `Changed` matches the existing `ThemeService.ThemeChanged` / `BlazorLocalizationService.CultureChanged` patterns in this codebase.

---

## Archived processed inbox: kaylee-ref-sentence-mobile.md

### 2026-06-24T21:53: Kebab overflow menu for reference-sentence cards
**By:** Kaylee (Full-stack Dev)
**What:** Replaced the 4-button `btn-group` on each saved-sentence card in `ReferenceSentencesSection.razor` with a float-right kebab (`bi-three-dots-vertical`) that opens a Blazor-state-driven dropdown. Sentence text is now full-width inside the card; badges row is full-width. Layout applies at all widths (no responsive dual-layout).
**Why:** On narrow/mobile widths the right-side btn-group was squeezing the Korean text to ~3 words per line. A single unified layout (kebab-everywhere) is cleaner, less code, and works well at any width. No Bootstrap JS dependency — the `.show` class is toggled purely by `openMenuId` state, with a transparent fixed backdrop to close on outside-click.
**Files changed:**
- `src/SentenceStudio.UI/Components/ReferenceSentencesSection.razor` — markup, `openMenuId` state, `ToggleMenu`/`CloseMenu`, `openMenuId = -1` in four action handlers.
- `src/SentenceStudio.UI/wwwroot/css/app.css` — clearfix on `.ref-sentence-card`, float/position for `.ref-sentence-actions`, absolute dropdown, backdrop z-index stacking.
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` — added `RefSentences_Actions` = "Actions".
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx` — added `RefSentences_Actions` = "작업".

---

## Archived processed inbox: wash-drain-gate.md

# Decision: Replace static _draining field with injectable SharedIngestDrainGate singleton

**Author:** Wash (Backend Dev)
**Date:** 2026-06-26
**Status:** Applied

## Problem

`SharedIngestProcessor.DrainAsync` used a `private static int _draining` field (Interlocked CAS) as a process-wide single-flight guard. This was correct for production but caused flaky cross-test contamination in the full unit-test suite: the single-flight test holds the static guard while its slow parse completes, and if the async release races with the next test's execution, `YouTubeUrlItem_VideoImportKickedOff_ItemRemoved_NotifierVideoImportStarted` would call `DrainAsync` while `_draining` was still 1, causing it to return a no-op and fail its assertions. Full-suite result was 722/723; the `--filter SharedIngest` subset passed by timing luck.

## Decision

Replace the `static int _draining` field with `SharedIngestDrainGate`, a small injectable type that encapsulates the same `Interlocked.CompareExchange` / `Interlocked.Exchange` logic:

```csharp
public sealed class SharedIngestDrainGate
{
    private int _busy;
    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    public void Exit() => Interlocked.Exchange(ref _busy, 0);
}
```

`SharedIngestProcessor.DrainAsync` calls `_gate.TryEnter()` on entry; returns a no-op result immediately if it returns false (gate already held); calls `_gate.Exit()` in a `finally` block on the normal path. The early-return path does NOT call `Exit()` (gate was never entered).

## DI Registration

`SharedIngestDrainGate` is registered as a **Singleton** in `CoreServiceExtensions.cs` (alongside `SharedIngestNotifier`):

```csharp
services.AddSingleton<SharedIngestDrainGate>();
```

`ISharedIngestProcessor` / `SharedIngestProcessor` remain Scoped. All scoped instances share one gate in production, preserving the process-wide single-flight semantics. The iOS head already registers `ISharedIngestProcessor` as Scoped; no change needed there.

## Test Isolation

`BuildProcessor` in `SharedIngestProcessorTests` now accepts an optional `SharedIngestDrainGate? gate` parameter (defaults to `new SharedIngestDrainGate()`). Every normal test gets its own fresh gate with no shared state between tests — no static field to leak.

The single-flight test (`SingleFlight_ConcurrentDrainOnSeparateInstances_ReturnedNoOp`) explicitly creates one `sharedGate` and passes it to both processor A and processor B, matching the production singleton pattern. It also explicitly verifies the gate is idle after A completes (`sharedGate.TryEnter()` returns true), then releases it — removing the implicit static-state assumption in the original test.

## Full-suite result

- Before: 722/723 (YouTubeUrlItem test failed intermittently in full suite)
- After: 723/723 — all tests pass, zero static leakage

The previously documented intentional failure (`DeterministicPlanBuilderResourceSelectionTests.ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed`) was already passing in the current suite (fixed separately); baseline is now 723/723.

## Files Changed

- `src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs` — added `SharedIngestDrainGate` type, replaced static field + Interlocked calls with gate injection
- `src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs` — `AddSingleton<SharedIngestDrainGate>()`
- `tests/SentenceStudio.UnitTests/Services/SharedIngestProcessorTests.cs` — `BuildProcessor` gate param, single-flight test rewritten to use shared gate instance

---

## Archived processed inbox: wash-drain-hang-diag.md

# Decision: SharedIngestProcessor drain-hang diagnostics + timeout

**Author:** Wash  
**Date:** 2026-06-25  
**Status:** Instrumentation only — no logic changes

## Context

After switching `ContentType.Auto` to `ContentType.Vocabulary` the drain pipeline began actually executing `ParseContentAsync` (AI call) and `CommitImportAsync` (DB write). The OnActivated handler logs "calling DrainAsync" but never produces a result or exception line, even after 4+ minutes. Root cause is unconfirmed: the hang is inside DrainAsync, either at the AI parse call or the DB commit.

idevicesyslog does not work on DX24; breadcrumbs are pulled via devicectl from `{Documents}/app-drain-debug.txt`.

## What was instrumented

### New file: `src/SentenceStudio.Sharing/IngestDiagnostics.cs`

Thin cross-platform static sink. `IngestDiagnostics.Sink` is an `Action<string>?` wired at startup by the platform head. `IngestDiagnostics.Log(msg)` invokes the sink inside a try/catch so it can never crash the app.

### `src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs`

Added `IngestDiagnostics.Log(...)` calls at the following points (text-item path and URL-item path):

- After `_queue.List()`: `drain: items={count}` — confirms items were visible at drain time.
- Before `ParseContentAsync`: `item {id}: parse start` — if nothing follows, the AI call is the hang.
- After `ParseContentAsync`: `item {id}: parsed rows={n}` — confirms AI returned.
- Before `CommitImportAsync`: `item {id}: commit start (target=existing|new)` — if nothing follows after parse logged, the DB write is the hang.
- After `CommitImportAsync`: `item {id}: commit done created=N skipped=N resource=X` — confirms commit returned.
- In the per-item catch block: `item {id}: EXCEPTION TypeName: message` (existing logger call preserved).
- URL-item path gets identical parse/commit breadcrumbs with `(url)` suffix.

### `src/SentenceStudio.iOS/MauiProgram.cs`

- Promoted `AppendDrainDebug(filePath, text)` and `DrainDocsDir()` to module-level static helpers (outside the Task.Run lambda) so both the `IngestDiagnostics.Sink` assignment and the OnActivated handler write to the same file with the same timestamp format.
- Wired `IngestDiagnostics.Sink = msg => AppendDrainDebug(diagFile, $"[diag] {msg}")` BEFORE `ConfigureLifecycleEvents`, so sink is live before any drain can start.
- Added `CancellationTokenSource(TimeSpan.FromSeconds(60))` wrapping the `DrainAsync` call. A 60-second timeout means a hung AI call or deadlocked DB write surfaces as `TaskCanceledException` in the catch block instead of hanging the background thread indefinitely.
- Exception catch block now logs `GetType().Name`, `Message`, `InnerException`, and `StackTrace` so a timeout shows `TaskCanceledException` with the cancellation stack.

## Drain logic

Unchanged. `ContentType.Vocabulary` fix from prior session stays. No changes to parse request, commit logic, queue management, or error handling beyond the added `IngestDiagnostics.Log` calls.

## Build result

`dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj -f net11.0-ios -c Release -p:RuntimeIdentifier=ios-arm64`  
**0 errors, 664 warnings (all pre-existing IL2026/EF1002/CS0618 trim and nullable warnings).**

## How to read the breadcrumb file after next share

Pull via devicectl:
```bash
xcrun devicectl device copy from --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  --source /path/to/app/Documents/app-drain-debug.txt ./app-drain-debug.txt
```

Look for the last `calling DrainAsync` entry and then:
- `[diag] drain: items=N` — if missing, queue was empty at drain time (extension wrote to wrong path).
- `[diag] item X: parse start` — if missing after `items=N`, hanging before the AI call (auth, scope, service resolution).
- `[diag] item X: parsed rows=N` — if missing after `parse start`, **AI call is the hang**. Timeout will fire after 60s and log `TaskCanceledException`.
- `[diag] item X: commit start` — if missing after `parsed rows=N`, hanging between parse return and commit (should not happen).
- `[diag] item X: commit done` — if missing after `commit start`, **DB write is the hang**.

---

## Archived processed inbox: wash-ingest-processor.md

### 2026-06-25T12:40: Phase 3-Core — SharedIngestProcessor + unit tests
**By:** Wash

**What:**
Added the platform-agnostic drain/orchestration layer for "Share to SentenceStudio" Phase 3-Core.

Files changed:
- `src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` — added `<ProjectReference>` to `SentenceStudio.Sharing`
- `src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs` — new file containing:
  - `ISharedInboxResourceFinder` — tiny injectable interface for locating the "Shared Inbox" resource (enables unit testing without pulling in the full EF/DI stack)
  - `LearningResourceSharedInboxFinder` — production wrapper around `LearningResourceRepository.GetAllResourcesLightweightAsync`
  - `SharedIngestDrainResult` — result record (ProcessedCount, CreatedVocabCount, SkippedVocabCount, EmptyCount, DeferredUrlCount, FailedCount, Deferred, ResourceId)
  - `ISharedIngestProcessor` interface
  - `SharedIngestProcessor` implementation — full drain orchestration logic
- `tests/SentenceStudio.UnitTests/Services/SharedIngestProcessorTests.cs` — 7 xUnit tests covering all required scenarios

**Why:**
Phase 3-Core of "Share to SentenceStudio". The processor is the bridge between the `ISharedIngestQueue` (iOS Share Extension side) and `IContentImportService` (vocab save engine). Kept fully unit-testable with fakes per the task spec. No iOS/NSFileManager/DI-registration/app-lifecycle code introduced in this slice.

**Key design decisions:**

1. **Single-flight guard** uses `Interlocked.CompareExchange` on an `int _draining` field rather than `SemaphoreSlim` — avoids blocking, returns a zero-count no-op result instantly if a drain is already running.

2. **ISharedInboxResourceFinder interface** introduced to decouple the resource-lookup from `LearningResourceRepository`'s heavy constructor (`IServiceProvider`, `IFileSystemService`). The interface has one method; the concrete wrapper `LearningResourceSharedInboxFinder` is provided for production DI wiring (a later slice).

3. **Language resolution** uses `IPreferencesService.Get("target_language", "Korean")` and `IPreferencesService.Get("native_language", "English")`. The Import Content UI pages do not exist in `src/SentenceStudio.UI` (empty results on search), so the `UserProfile.TargetLanguage`/`NativeLanguage` DB fields could not be sourced without adding `UserProfileRepository` as a dependency. The preferences-key approach matches what `ContentImportRequest` uses as its own defaults and is the most testable option. To align with the UserProfile model in a future slice, replace with a `UserProfileRepository.GetAsync()` call during DI-registration time, once the full lifecycle hook is wired.

4. **Queue snapshot** — `_queue.List().ToList()` is called once to snapshot items before the loop. This avoids `InvalidOperationException` from concurrent modification when `Remove()` is called mid-iteration on implementations (like `InMemorySharedIngestQueue` in tests) that return a live view.

5. **URL items** — skipped with `DeferredUrlCount++`, left in queue; a clear `// Phase 4: URL fetch` TODO is in the code.

6. **Partial-failure isolation** — each item is wrapped in `try/catch (Exception ex) when (ex is not OperationCanceledException)`. A failure logs an error, leaves the item queued, and continues.

7. **Multi-tenant safety** — `active_profile_id` check at entry: empty userId → log + return `Deferred=true`, items stay queued.

**Test results:** 7/7 new tests pass. Full suite: 694/694.

---

## Archived processed inbox: wash-ios-drain-wiring.md

# iOS Shared Ingest Drain Wiring (Phase 3)

**Author:** Wash (Backend Dev)  
**Date:** 2026-06-25  
**Phase:** 3 — iOS DI registration + foreground drain

---

## What was wired

### iOS head (`src/SentenceStudio.iOS/MauiProgram.cs`)

- **`ISharedIngestQueue` (Singleton)**: Constructs `FileSystemSharedIngestQueue` pointing at the App Group container path (`NSFileManager.DefaultManager.GetContainerUrl(SharingConstants.AppGroupId)` + `share-inbox` subdirectory). Falls back to `FileSystem.AppDataDirectory/share-inbox` if the App Group container URL is null (sim without entitlement, tests).
- **`ISharedIngestProcessor` (Scoped)**: `SharedIngestProcessor`. Scoped to match `IContentImportService` and `LearningResourceRepository` (both Scoped deps). Resolved inside a created scope at drain time.
- **Lifecycle drain** (`ConfigureLifecycleEvents` → `AddiOS` → `OnActivated`): Fire-and-forget `Task.Run` resolves `ISharedIngestProcessor` from a fresh scope via `IPlatformApplication.Current.Services.CreateScope()` and calls `DrainAsync()`. Exceptions are caught and written to `Console`. Does not block the UI thread. Safe to call on every activation: processor single-flights concurrent calls and auth-gates on no active user.

### Shared (`src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs`)

- **`IWebArticleFetcher` → `WebArticleFetcher`** via `AddHttpClient<IWebArticleFetcher, WebArticleFetcher>()`. Registered cross-platform since `WebArticleFetcher` has no iOS-specific deps.
- **`ISharedInboxResourceFinder` → `LearningResourceSharedInboxFinder`** (Scoped). Registered cross-platform; requires `LearningResourceRepository` (already Singleton in core services).

---

## Language-source fix

**Finding**: `SharedIngestProcessor` was reading language from `IPreferencesService.Get("target_language", ...)` / `IPreferencesService.Get("native_language", ...)`. These preference keys are **never written** anywhere in the codebase — every call would always return the "Korean"/"English" defaults, regardless of the user's actual profile settings.

**Established pattern**: `TranslationService`, `DiaryService`, `ClozureService`, `ShadowingService`, and `ConversationService` all resolve languages from `UserProfileRepository.GetAsync()` — `userProfile.TargetLanguage` / `userProfile.NativeLanguage` — with Korean/English fallbacks.

**Fix**: Replaced `IPreferencesService` in `SharedIngestProcessor`'s constructor with `SentenceStudio.Data.UserProfileRepository`. `DrainCoreAsync` now calls `await _userProfileRepo.GetAsync()` for both the auth gate (null profile → defer) and language resolution. This also makes `userId` come from `userProfile.Id` rather than a raw preferences read, which is the correct multi-user source.

---

## What stays iOS-only

- `ISharedIngestQueue` registration and the lifecycle drain hook are guarded with `#if IOS` in `SentenceStudio.iOS/MauiProgram.cs`. Other heads have no App Group container and no share extension; they never register the queue or the processor.

---

## Archived processed inbox: wash-share-breadcrumbs.md

# Decision: Share-to-Vocabulary breadcrumb instrumentation (device diagnostics)

**Date:** 2026-06-25  
**Author:** Wash (Backend Dev)  
**Status:** Merged to main pending Captain deploy

## Context

iOS Share Extension → App drain pipeline is silently failing on DX24.
`idevicesyslog` does not work on this iOS version. Codesign confirms
both processes have the correct `com.apple.security.application-groups`
entitlement. Neither the App Group container nor the app container have
a `share-inbox` directory, indicating: (1) the extension never enqueued
anything, and (2) the app drain never ran far enough to create the queue
directory. We need to know whether `GetContainerUrl` returns null in
each process, and whether `OnActivated` fires at all.

## Decision

Add plaintext breadcrumb files written to each process's own
`Documents/` directory (always retrievable via
`xcrun devicectl device copy from --domain-type appDataContainer`).
Instrumentation is additive only — no feature logic changed. All
instrumentation wrapped in try/catch to prevent crashes.

## Files written (on device)

### Extension breadcrumb
- **Process bundle id:** `com.simplyprofound.sentencestudio.shareextension`
- **File path:** `Documents/ext-debug.txt` (appended each `DidSelectPost` invocation)
- **Also written to:** App Group container root (`group.com.simplyprofound.sentencestudio/ext-debug.txt`) if `GetContainerUrl` returns non-null
- **Records:**
  - UTC timestamp
  - `containerUrl=` — raw result of `GetContainerUrl(AppGroupId)`, or "NULL"
  - `queueDir=` — resolved path or "NULL"
  - `inputItems=` — count of `ExtensionContext.InputItems`
  - Per provider: conformance to `public.plain-text`, `public.url`, `public.text`, `public.file-url`
  - `ContentText=` — compose sheet text (truncated 120 chars)
  - `capturedCount=` and Kind+Payload (truncated 80 chars) per item
  - Full exception if any

### App drain breadcrumb
- **Process bundle id:** `com.simplyprofound.sentencestudio`
- **File path:** `Documents/app-drain-debug.txt` (appended each `OnActivated` event)
- **Records (sequential steps so we see how far it gets):**
  - "OnActivated fired"
  - `appGroupContainer=` — `GetContainerUrl` result or "NULL"
  - `servicesNull=` — whether `IPlatformApplication.Current?.Services` is null
  - "calling DrainAsync" (emitted only if services resolved)
  - `DrainResult:` all fields of `SharedIngestDrainResult`
    (ProcessedCount, CreatedVocabCount, SkippedVocabCount, EmptyCount,
    FailedCount, Deferred, ResourceId)
  - Full exception + stack if any

## How to retrieve after a share attempt

```bash
# Extension documents
xcrun devicectl device copy from \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  --domain-type appDataContainer \
  --domain-identifier com.simplyprofound.sentencestudio.shareextension \
  Documents/ext-debug.txt ./ext-debug.txt

# App documents
xcrun devicectl device copy from \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  --domain-type appDataContainer \
  --domain-identifier com.simplyprofound.sentencestudio \
  Documents/app-drain-debug.txt ./app-drain-debug.txt

# App Group container (belt-and-suspenders copy from extension)
xcrun devicectl device copy from \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  --domain-type groupContainer \
  --domain-identifier group.com.simplyprofound.sentencestudio \
  ext-debug.txt ./ext-debug-group.txt
```

## Build result

Release build: `net11.0-ios -c Release -p:RuntimeIdentifier=ios-arm64`
Outcome: **0 errors, 113 warnings** (all pre-existing, no new warnings
from this change).

## Revert plan

This is diagnostic-only. Once root cause is identified, revert both
files to remove the breadcrumb code before next production ship.

---

## Archived processed inbox: wash-share-extension.md

# Decision: Share Extension Scaffold — Phase 2

**Date:** 2026-06-25  
**Author:** Wash (Backend Dev)  
**Status:** Done

## Context

Phase 2 of the iOS Share-to-Vocabulary feature. Captain completed Apple Developer Portal
steps (App Group `group.com.simplyprofound.sentencestudio` registered on both app and
extension App IDs). This task scaffolds the managed C# extension project and gets both
the extension and the main iOS app building for `iossimulator-arm64` (Debug, no signing).

## Files Created / Modified

| File | Action |
|------|--------|
| `src/SentenceStudio.iOS/Platforms/iOS/Entitlements.plist` | Created — app-group entitlement for main app |
| `src/SentenceStudio.ShareExtension/SentenceStudio.ShareExtension.csproj` | Created — minimal iOS extension project (`net11.0-ios`, `IsAppExtension=True`, `Registrar=static`) |
| `src/SentenceStudio.ShareExtension/Entitlements.plist` | Created — same app-group entitlement for extension |
| `src/SentenceStudio.ShareExtension/Info.plist` | Created — extension bundle info (NSExtensionPointIdentifier: com.apple.share-services) |
| `src/SentenceStudio.ShareExtension/MainInterface.storyboard` | Created — minimal storyboard wiring ShareViewController |
| `src/SentenceStudio.ShareExtension/ShareViewController.designer.cs` | Created — generated outlets stub |
| `src/SentenceStudio.ShareExtension/ShareViewController.cs` | Created — SLComposeServiceViewController subclass; captures text/URL from NSItemProvider, enqueues via FileSystemSharedIngestQueue into the App Group container |
| `src/SentenceStudio.iOS/SentenceStudio.iOS.csproj` | Modified — added CodesignEntitlements + extension ProjectReference |
| `src/SentenceStudio.sln` | Modified — added SentenceStudio.ShareExtension project + build configs |

## Build Results

Extension alone:  
`dotnet build src/SentenceStudio.ShareExtension ... -f net11.0-ios -p:RuntimeIdentifier=iossimulator-arm64 -c Debug`  
**Result: Build succeeded. 0 Warning(s). 0 Error(s). 40s.**

Full iOS app (bundles extension):  
`dotnet build src/SentenceStudio.iOS ... -f net11.0-ios -p:RuntimeIdentifier=iossimulator-arm64 -c Debug`  
**Result: Build succeeded. 110 Warning(s). 0 Error(s). 80s.**

## Dogfooding Friction (net11-p5 SDK regression — filed here for upstream)

**SDK:** Microsoft.iOS.Sdk.net11.0_26.5 26.5.11546-net11-p5  
**Platform:** iossimulator-arm64 Debug  

### Symptom

Linker fails with undefined symbols:

```
Undefined symbols for architecture arm64:
  "_xamarin_r2r_module_count", referenced from:
      xamarin_register_r2r_modules() in r2r_modules.o
  "_xamarin_r2r_modules", referenced from:
      xamarin_register_r2r_modules() in r2r_modules.o
ld: symbol(s) not found for architecture arm64
clang++: error: linker command failed with exit code 1
```

This is a **pre-existing regression** in the main iOS project unrelated to the extension.
Confirmed by stashing the extension changes and reproducing identically.

### Root Cause

The static registrar generates `r2r_modules.o` (a ReadyToRun module registration table)
that references `_xamarin_r2r_module_count` and `_xamarin_r2r_modules`. With
`Registrar=dynamic`, those symbols are not defined by `registrar.o` (which is not
generated in dynamic mode), leaving `r2r_modules.o` with dangling external references.
The SDK bug: `r2r_modules.o` gets linked even when `Registrar=dynamic` is in effect,
and stale obj artifacts from a prior static-registrar invocation perpetuate the issue
across incremental builds.

### Workaround Applied

Added to `SentenceStudio.iOS.csproj`:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <Registrar>dynamic</Registrar>
</PropertyGroup>
```

Also requires a clean of `obj/Debug/net11.0-ios/iossimulator-arm64/` once after switching.
After the clean, incremental Debug builds work fine.

**Static registrar is preserved for Release/device builds — no impact on production.**

### Upstream Action Needed

This is a regression in net11-preview5 vs net10 GA. In net10, simulator Debug builds
do not invoke crossgen2 / emit `r2r_modules.o` at all. In net11-preview5, they do, but
the dynamic registrar path doesn't define the symbols `r2r_modules.o` depends on.

Recommend filing against dotnet/maui or dotnet/runtime-assets with SDK version
`26.5.11546-net11-p5` and TFM `net11.0-ios` + RID `iossimulator-arm64`.

## What Is NOT Done (Next Slices)

- App-side drainer / DI registration / lifecycle (`ISharedIngestProcessor` wired into MAUI startup)
- Device signing / provisioning profile — Captain will drive
- ShareExtension does NOT reference SentenceStudio.AppLib/UI (intentional — extensions must stay thin)

---

## Archived processed inbox: wash-share-whole-utterance.md

# Decision: Share-to-Vocab Preserves Full Utterance

**Date:** 2026-06-25  
**Author:** Wash (Backend Dev)  
**Status:** Implemented

## Context

When Captain shares a Korean sentence or phrase from another app, the existing `SharedIngestProcessor` path called `ParseContentAsync` with `ContentType=Vocabulary`, which routed through `ParseFreeTextContentAsync` and the `FreeTextToVocab.scriban-txt` template. That template only extracts constituent vocabulary items in dictionary form — it never records the full shared utterance as a vocabulary item. The learner loses the sentence they actually encountered in the wild.

## Decision

Add a share-specific extraction path that is separate from the Import UI path (`FreeTextToVocab` + `ParseFreeTextContentAsync`). The share path always preserves the verbatim input as the first vocabulary item, then extracts constituents.

### What was added

1. **`ShareTextToVocab.scriban-txt`** (`src/SentenceStudio.AppLib/Resources/Raw/`)  
   New Scriban template. Rule 1 mandates including the entire input verbatim as the first item, classified (Sentence/Phrase/Idiom/Word), with a full native-language translation and `confidence=high`. Rules 2+ are the existing constituent-extraction rules from `FreeTextToVocab`. Skip rules apply only to constituents; the whole-input item is never skipped. Dedup rule clarified to keep the whole-input item even if it overlaps a constituent.

2. **`IContentImportService.ParseSharedTextAsync`** + implementation in `ContentImportService`  
   New interface method: `Task<ContentImportPreview> ParseSharedTextAsync(string text, string targetLanguage, string nativeLanguage, CancellationToken ct)`. Implementation loads `ShareTextToVocab.scriban-txt`, renders with `source_text/target_language/native_language` (no `format_hint`), calls `_aiService.SendPrompt<FreeTextVocabularyExtractionResponse>`, maps results using the same confidence→RowStatus, `ResolveLexicalUnitType`, IsAiTranslated=true mapping as `ParseFreeTextContentAsync`. Returns `ContentImportPreview` with `DetectedFormat="Shared text"`. Empty AI response returns an empty preview (no throw).

3. **`SharedIngestProcessor.DrainCoreAsync` — text-item path**  
   Replaced the `ParseContentAsync(ContentImportRequest)` call for `Kind=Text` items with `ParseSharedTextAsync(item.Payload, targetLanguage, nativeLanguage, ct)`. All surrounding logic (empty-preview check, CommitImportAsync, Shared Inbox target resolution, diagnostics breadcrumbs, partial-failure isolation) is unchanged. URL items continue to call `ParseContentAsync` so articles are not stored as a single giant Sentence.

## What was NOT changed

- `FreeTextToVocab.scriban-txt` — untouched.
- `ParseContentAsync` / `ParseFreeTextContentAsync` — untouched; Import UI behavior is identical.
- All harvest flags (`HarvestWords=true`, `HarvestPhrases=true`, `HarvestSentences=true`) on the `ContentImportCommit` for shared items remain in place; the whole-utterance first item will be classified as Sentence/Phrase/Idiom/Word and committed accordingly.

## Test impact

`SharedIngestProcessorTests`: all existing text-item tests updated to mock/verify `ParseSharedTextAsync` instead of `ParseContentAsync`. New test `TextItem_UsesParseSharedTextAsync_UrlItem_UsesParseContentAsync` asserts routing: text goes to `ParseSharedTextAsync`, URL goes to `ParseContentAsync`.

Result: **59/59 tests passing**.

---

## Archived processed inbox: wash-shared-url-resource.md

# Decision: Shared URL items become Learning Resources, not Shared Inbox vocab

**Author**: Wash (Backend Dev)
**Date**: 2026-06-25
**Status**: Implemented

## Context

The Share-to-Vocabulary feature previously routed ALL shared content through the Shared Inbox
vocabulary pipeline. Captain re-scoped: a shared URL must become a **LearningResource**, not
a Shared Inbox vocab entry. Text items (the original use-case) are unchanged.

## Decision

Two distinct URL routing paths replace the old single "fetch → vocab-parse → Shared Inbox" path:

### YouTube URL
- Detection: `YoutubeExplode.Videos.VideoId.Parse(url)` succeeds → it's YouTube.
- Action: kick off `IVideoImportPipeline.ImportFromUrlAsync(url, userId, language)` detached
  via `Task.Run` (fire-and-forget). The drain removes the queue item immediately and does
  NOT await the import.
- Rationale: video import is multi-minute; blocking the drain would time-out the app's
  foreground window. The service manages its own DI scope internally.
- Toast: "Video import started — track in Media Import" with "Track" button → `/media-import`.

### Article / non-YouTube URL
- Action: `IWebArticleFetcher.FetchReadableTextAsync` → `ParseContentAsync(ContentType.Transcript,
  HarvestTranscript=true)` → `CommitImportAsync(Mode=New, title=article.Title or host)`.
- Creates a brand-new LearningResource (not appended to Shared Inbox).
- Toast: "Imported {title}" with "View" → `/vocabulary`.

### Text items
Unchanged: `ParseSharedTextAsync` → `CommitImportAsync` into Shared Inbox resource.

## Notifier shape change

`SharedIngestNotifier` was generalized:
- Old: `SetProcessing(int count)` / `SetCompleted(int created, int skipped, string? resourceId)`
- New: `SetProcessing(SharedIngestNotificationKind kind)` / `SetCompleted(kind, count, title, actionRoute)`
- `SharedIngestNotificationKind` enum: `Vocabulary | VideoImportStarted | ResourceImported`
- Named `SharedIngestNotificationKind` (not `SharedIngestKind`) to avoid collision with the
  existing `SentenceStudio.Sharing.SharedIngestKind { Text, Url }` queue-item enum.

## DI change

`IVideoImportPipeline` interface introduced over `VideoImportPipelineService` for testability.
Registration in `CoreServiceExtensions`: singleton for both the concrete class and the interface
(forwarded via `sp.GetRequiredService<VideoImportPipelineService>()`).

## Files changed

- `src/SentenceStudio.Shared/Services/VideoImportPipelineService.cs` — added `IVideoImportPipeline` interface
- `src/SentenceStudio.Shared/Services/SharedIngestNotifier.cs` — new shape
- `src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs` — rewritten URL path
- `src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs` — `IVideoImportPipeline` registration
- `src/SentenceStudio.UI/Components/SharedIngestIndicator.razor` — kind-aware toast messages
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` — 5 new keys
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx` — 5 new Korean translations
- `tests/SentenceStudio.UnitTests/Services/SharedIngestProcessorTests.cs` — updated + 2 new tests

## Test results

20/20 SharedIngest tests passing (18 prior + 2 new: YouTube detached kick-off, article resource import).

---

## Archived processed inbox: wash-sharing-queue.md

### 2026-06-25T12:34:42-05:00: Phase 1 — Cross-process shared ingest queue contract

**By:** Wash (Backend Dev)

**What:**
Created `src/SentenceStudio.Sharing` (netstandard2.0) containing the full cross-process shared queue contract for the iOS Share Extension feature:
- `SharedIngestKind` enum (Text=0, Url=1)
- `SharedIngestItem` POCO (Id, Kind, Payload, SourceAppBundleId, CapturedAtUtc, SchemaVersion)
- `SharingConstants` (AppGroupId, QueueDirectoryName, CurrentSchemaVersion)
- `ISharedIngestQueue` interface (Enqueue / List / Remove)
- `FileSystemSharedIngestQueue` implementation — atomic write via temp+move, resilient List (skips malformed files), ordered by CapturedAtUtc ascending
- 9 unit tests in `tests/SentenceStudio.UnitTests/Sharing/SharedIngestQueueTests.cs` — all pass
- Added `System.Text.Json 8.0.5` to `Directory.Packages.props` (CPM)
- Added project to `src/SentenceStudio.sln` and `<ProjectReference>` in the UnitTests csproj

**Why:**
The iOS Share Extension runs in a separate process that cannot reference MAUI/EF/AI. Putting the shared-queue contract in a dependency-light netstandard2.0 library lets both the extension and the main app heads reference the same types. The directory path is injected at construction time so Phase 2 (App Group / NSFileManager resolution) can be added in the iOS head without touching this library.

**CPM friction note:**
`System.Text.Json` was not previously in `Directory.Packages.props`. Added version `8.0.5` under the Utilities section. No other friction encountered; netstandard2.0 + CPM + implicit usings worked cleanly.

**Phase 2/3 reminders:**
- Phase 2: iOS App Group container resolution via NSFileManager — platform-specific, lives in the iOS head
- Phase 3: App-side drainer — calls `ISharedIngestQueue.List()` on foreground, runs AI extraction, calls `Remove` per item

---

## Archived processed inbox: wash-singleflight-fix.md

# Decision: Make SharedIngestProcessor single-flight guard static

**Author:** Wash (Backend Dev)
**Date:** 2026-06-26
**Status:** Accepted

## Context

`SharedIngestProcessor` is registered as `AddScoped<ISharedIngestProcessor, SharedIngestProcessor>()`.
On iOS, `OnActivated` creates a new DI scope on every app foreground, producing a **new instance** each time.
Two rapid foreground events therefore produce two instances, each starting fresh with `_draining = 0`.
The per-instance guard does not prevent both drains from running concurrently against the same queue,
resulting in duplicate "Shared Inbox" resource creation and duplicate YouTube/article imports.

## Change

`src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs` line 134:

```csharp
// Before
private int _draining;

// After
private static int _draining;
```

`static` means the field is allocated once per process rather than once per instance, so the
`Interlocked.CompareExchange` acquire and the `finally`-guarded `Interlocked.Exchange` release in
`DrainAsync` operate on the same memory regardless of which scope/instance calls them.

The `finally` block that resets the guard was already correct; no change was needed there.

## Why static fixes it

A static field lives on the type, not on the object. All `SharedIngestProcessor` instances in the
same process share the single `_draining` slot. The first instance to win the CAS (compare-exchange
0 → 1) holds the guard; any other instance that attempts the same CAS sees `1` and returns a no-op
immediately, exactly as intended.

## Test update

`tests/SentenceStudio.UnitTests/Services/SharedIngestProcessorTests.cs`:

- Renamed `SingleFlight_ConcurrentDrainReturnedNoOp` to
  `SingleFlight_ConcurrentDrainOnSeparateInstances_ReturnedNoOp`.
- Now creates **two separate `SharedIngestProcessor` instances** (A and B) backed by the same
  queue but independent mocks.
- Instance A's `ParseSharedTextAsync` blocks on a `TaskCompletionSource`-style semaphore so the
  drain is genuinely in-flight when B attempts its drain.
- Asserts B's `ParseSharedTextAsync` and `CommitImportAsync` are **never called** (Times.Never),
  confirming the static guard repelled B entirely.
- Releases A and asserts A processed exactly 1 item normally.
- Static-state leakage is prevented structurally: the test always lets A's drain complete, so the
  `finally` block clears `_draining` back to 0 before the test exits. No explicit teardown is
  required.

## Build / test results

```
dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj
  -> 0 errors, 980 warnings (pre-existing)

dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj --filter "FullyQualifiedName~SharedIngest"
  -> Failed: 0, Passed: 20, Skipped: 0 (all 20 SharedIngest tests green)
```

---

## Archived processed inbox: wash-strip-debug-breadcrumbs.md

# Decision: Strip DEBUG breadcrumb instrumentation from Share-to-Vocabulary feature

**Date:** 2026-06-26  
**Author:** Wash (Backend Dev)  
**Requested by:** Captain (David Ortinau)

## Context

During development of the Share-to-Vocabulary feature, on-device diagnostic breadcrumb instrumentation was added to three files to trace the share extension → app group queue → drain pipeline end-to-end. This instrumentation was never intended to ship.

## What was removed

### 1. `src/SentenceStudio.ShareExtension/ShareViewController.cs`

Removed:
- `ExtDocsDir()` helper method
- `AppendBreadcrumb(string, string)` helper method
- `Truncate(string?, int)` helper method (used only by diagnostics)
- `System.Text.StringBuilder extDiag` accumulation in `CaptureAndComplete()`
- All `extDiag.AppendLine(...)` calls logging UTI conformance, container path, queue dir, item details
- `ext-debug.txt` file writes to both the extension's Documents directory and the App Group container

`CaptureAndComplete()` now does only: capture items, enqueue to App Group queue, complete request (with a clean try/catch/finally).

### 2. `src/SentenceStudio.iOS/MauiProgram.cs`

Removed:
- `DrainDocsDir()` local static function
- `AppendDrainDebug(string, string)` local static function
- `diagFile` variable and its initialization
- `IngestDiagnostics.Sink = ...` wiring
- All `AppendDrainDebug(...)` calls inside the `OnActivated` Task.Run lambda (OnActivated fired, container path, servicesNull, calling DrainAsync, DrainResult summary, exception breadcrumb)

Kept intact: `#if IOS` DI registrations for `ISharedIngestQueue` and `ISharedIngestProcessor`, the `ConfigureLifecycleEvents` → `OnActivated` drain hook, the `CancellationTokenSource(TimeSpan.FromSeconds(60))` timeout, and the `Console.WriteLine` error log in the catch block.

### 3. `src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs`

Removed all 17 `IngestDiagnostics.Log(...)` calls across `DrainCoreAsync` and `ProcessUrlItemAsync` (text item parse/commit path, YouTube path, article path, and all exception catch blocks). No real logic was touched.

### 4. `src/SentenceStudio.Sharing/IngestDiagnostics.cs`

Deleted. This file was debug-only infrastructure (`static Action<string>? Sink` + `Log(string)` forwarder). It had no role in production behavior.

## Reference checks

After edits, `grep -r "IngestDiagnostics|ext-debug\.txt|app-drain-debug\.txt|AppendDrainDebug|DrainDocsDir|AppendBreadcrumb|ExtDocsDir"` across the repo returned zero matches in production code. (Unrelated `Truncate` methods in `FeedbackEndpoints.cs`, `HelpKitService.cs`, and test files are independent — different classes, no shared symbol.)

## Build and test results

All builds sequential (never parallel) per project conventions:

| Project | Result |
|---------|--------|
| `SentenceStudio.Sharing` | 0 errors, 0 warnings |
| `SentenceStudio.ShareExtension` (-f net11.0-ios, iossimulator-arm64) | 0 errors, 0 warnings |
| `SentenceStudio.Shared` | 0 errors |
| `SentenceStudio.UI` | 0 errors |
| Unit tests `--filter SharedIngest` | 20/20 passed |
| `SentenceStudio.iOS` (-f net11.0-ios, iossimulator-arm64, Debug) | 0 errors |

## Decision

Remove all debug breadcrumb instrumentation before shipping. Feature code (queue, processor, notifier, UI indicator) is unchanged.

---

## Archived processed inbox: wash-url-ingest.md

### 2026-06-25T12:49: Phase 4 URL ingest — fetch + reduce + parse+commit pipeline
**By:** Wash
**What:**
Added Phase 4 URL item processing to the "Share to SentenceStudio" ingest pipeline.

New files:
- `src/SentenceStudio.Shared/Services/WebArticleFetcher.cs` — `WebArticleText` record, `IWebArticleFetcher` interface, `WebArticleFetcher` implementation, and `HtmlReadability` static reducer.
- `tests/SentenceStudio.UnitTests/Services/WebArticleReadabilityTests.cs` — 14 pure reducer tests.
- `tests/SentenceStudio.UnitTests/Services/WebArticleFetcherTests.cs` — 10 fetch + handler tests.

Modified files:
- `src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs` — injected `IWebArticleFetcher`, replaced URL-deferral branch with fetch→reduce→parse→commit path, removed `DeferredUrlCount` from `SharedIngestDrainResult`, added `UrlItemOutcome` private record struct (workaround for C# async-method / ref-param restriction).
- `tests/SentenceStudio.UnitTests/Services/SharedIngestProcessorTests.cs` — replaced the old `UrlItem_IsSkipped_DeferredUrlCount_Incremented_NotRemovedFromQueue` test with three new URL tests (success, fetch-failure, empty-after-fetch), added `IWebArticleFetcher` mock to `BuildProcessor` helper.

**Why:**
- URL items now flow through the same parse→commit pipeline as text items instead of being deferred indefinitely.
- HTML dependency decision: no HTML parser (`HtmlAgilityPack`, `AngleSharp`) exists in the solution. Rather than add a new dependency, used a pure regex + `System.Net.WebUtility.HtmlDecode` reducer (`HtmlReadability`). Sufficient for downstream AI vocabulary extraction (which tolerates noise) and keeps the reducer IO-free and deterministic in tests.
- Fetch seam: `WebArticleFetcher` accepts a plain `HttpClient` constructor parameter so tests can inject a `FakeHttpMessageHandler` without hitting the network. No `IHttpClientFactory` required.
- `DeferredUrlCount` removed from `SharedIngestDrainResult` — folded into `FailedCount` (transient error, left in queue) and `EmptyCount` (no usable text, removed). The property was only referenced in the processor and its test file; both updated cleanly.
- URL failure modes are symmetric with text-item failures: a bad URL leaves the item queued for retry, never blocks the rest of the drain.

**Test results:** 721/721 (full suite, no regressions).

---

## Archived processed inbox: wash-webapp-identity-fix.md

# Decision: Fix cross-tenant identity leak in UserProfileRepository.GetAsync()

**Author:** Wash (Backend Dev)  
**Date:** 2026-06-11  
**Branch:** `squad/vocab-preview-quiz-mismatch-fix` (staged, not committed)  
**Status:** Staged — awaiting Zoe review + Jayne E2E before Captain commits

---

## Root cause (confirmed)

`src/SentenceStudio.Shared/Data/UserProfileRepository.cs` line 133 (old):

```csharp
profile ??= await db.UserProfiles.FirstOrDefaultAsync();
```

When `active_profile_id` was set in preferences but no matching `UserProfile` row existed, this fallback returned an arbitrary row (no `ORDER BY`) — in Captain's production DB that was squad-jayne's profile because her row was created first.

---

## Changes made

### File 1: `src/SentenceStudio.Shared/Data/UserProfileRepository.cs`

**Lines changed:** 124–136 (replacing old 124–133)

Removed the cross-tenant fallback. The `if/else` block now:

- `activeId` empty → `LogWarning("GetAsync: no active_profile_id set — returning null")`, returns null
- `activeId` set, row found → returns that profile (unchanged behavior)
- `activeId` set, row **not** found → `LogWarning("GetAsync: active_profile_id '{ActiveId}' not found in UserProfiles — returning null")`, returns null

The `profile ??= await db.UserProfiles.FirstOrDefaultAsync();` line is **deleted**.

### File 2: `tests/SentenceStudio.UnitTests/Data/UserProfileRepositoryGetAsyncTests.cs` (new)

Three regression tests:

| Test | Seed | active_profile_id | Expected |
|------|------|-------------------|----------|
| `GetAsync_WhenActiveIdEmpty_ReturnsNull` | 2 profiles | `""` | null + warning |
| `GetAsync_WhenActiveIdDoesNotMatchAnyRow_ReturnsNull` | 2 profiles (jayne first) | `"ghost-id-not-in-db"` | null + warning naming the id |
| `GetAsync_WhenActiveIdMatches_ReturnsThatProfile` | 2 profiles (jayne first) | captain's id | captain's profile (not jayne) |

Test B is the exact production bug: jayne is the first row, captain's id is set as active but his row is missing. The test asserts null is returned, not jayne.

---

## Caller audit

All callers of `UserProfileRepository.GetAsync()` (zero-arg) were inspected. None required changes — all were already null-safe:

| Caller | Null handling | Status |
|--------|--------------|--------|
| `PostLoginRouter.cs:59` | `profile is not null &&` check → routes to `/onboarding` on null | ✓ safe |
| `Profile.razor:244` `LoadProfile` | explicit `if (profile != null)` | ✓ safe |
| `Profile.razor:275` `SaveProfile` | `?? new UserProfile()` — creates a blank row, but this is the deferred Bug #3 pattern | ✓ no regression |
| `ProgressService.cs` (8 call sites) | explicit `if (userProfile == null) { _logger.LogWarning; return; }` | ✓ safe |
| `DeterministicPlanBuilder.cs:65` | explicit null check, returns null | ✓ safe |
| `LocalizationInitializer.cs:52` | explicit `if (profile is null) return;` | ✓ safe |
| `IdentityAuthService.cs:509,571` | `if (profile is null) return;` and `ApplyLocaleFromProfile(profile?)` which accepts nullable | ✓ safe |
| `ClozureService, ShadowingService, DiaryService, ConversationService, TeacherService, StorytellerService, TranslationService` | all use `profile?.TargetLanguage ?? "Korean"` null-conditional pattern | ✓ safe |
| `VideoImportPipelineService.cs:293` | explicit `if (userProfile != null)` | ✓ safe |
| All razor pages (`NumberDrill`, `HowDoYouSay`, `VocabQuiz`, `MediaImport`, `DiaryEditor`, `ImportContent`, `Conversation`, `Scene`, `ResourceEdit`, `ChannelDetail`, `Shadowing`, `Index`, `Onboarding`) | all use `?.` null-conditional or explicit null checks | ✓ safe |
| `UserProfileRepository.GetOrCreateDefaultAsync()` | checks `profile == null` and constructs a new default | ✓ safe (existing behavior, not changed) |
| `UserProfileRepository.SaveDisplayCultureAsync()` | checks `profile is null` and creates a new UserProfile | ✓ safe |

**No caller changes required.**

---

## Test results

- Baseline: 583/583 passing  
- After fix: 586/586 passing (+3 new tests, 0 regressions)

---

## What was NOT done (deferred per Captain's instructions)

- Bug #3 (architectural): injecting the AspNetUsers claim id into `GetAsync()` so it never relies on `IPreferencesService` at all. That's a separate PR scope.
- Data recovery: seeding dave's missing `UserProfile` row in Postgres. Captain is handling that manually.
- `WebPreferencesService` replacement: also Bug #3 scope.

---

## Plain text summary

Removed the one-line fallback `profile ??= await db.UserProfiles.FirstOrDefaultAsync()` in `UserProfileRepository.GetAsync()`. That line was the cross-tenant leak: when a user's `active_profile_id` pref was set but their DB row was missing, the code silently returned the first row it found — whoever happened to be oldest in the table. The fix replaces the fallback with two warn-log paths (empty id vs. id-not-found) and returns null. All 27+ callers were audited and were already null-safe. Three targeted regression tests reproduce the exact production scenario (jayne as first row, captain's id set as active but row absent) and assert null is returned. 583 → 586 tests, all green.
