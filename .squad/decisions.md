## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-2026-04-25.md`)

---

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

