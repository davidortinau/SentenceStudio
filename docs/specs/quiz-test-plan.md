# Quiz Test Coverage Gap Analysis

**Author:** Jayne (Tester)
**Date:** 2026-04-13
**Status:** Gap analysis complete — implementation pending

---

## 1. Existing Test Inventory

### Unit Tests — Models

| File | Tests | What They Verify |
|------|-------|-----------------|
| `VocabularyProgressTests.cs` | 16 tests | `IsKnown` threshold (mastery >= 0.85 + production >= 2), `EffectiveStreak` formula, `Status` enum mapping, `Accuracy` computation, `IsDueForReview`, `HasConfidenceInMultipleChoice`, default values |
| `VocabularyAttemptAndProgressEdgeTests.cs` | 18 tests | `VocabularyAttempt.Phase` mapping (InputMode × ContextType → LearningPhase), `IsKnown` boundary cases, `EffectiveStreak` edge cases, `StreakToKnown`, `ProductionNeededForKnown`, `IsUserDeclared`/`VerificationState`, `IsInGracePeriod`, `IsDueForReview` edge cases, `IsLearning`/`IsUnknown` |

### Unit Tests — Services

| File | Tests | What They Verify |
|------|-------|-----------------|
| `VocabularyProgressServiceTests.cs` | 6 tests | `RecordAttemptAsync` correct → mastery increases, incorrect → mastery decreases, `GetProgressAsync`, `GetAllProgressAsync` user filtering, `GetReviewCandidatesAsync` due-word filtering, sentence context counts as production |
| `VocabularyProgressServiceUserIdTests.cs` | 9 tests | **Regression suite for userId="" bug.** `ResolveUserId` falls back to IPreferencesService, empty userId resolves from preferences, no cross-user data leakage, explicit userId overrides preferences, no-profile returns empty gracefully |
| `FuzzyAnswerMatcherTests.cs` | ~80 tests | `FuzzyMatcher.Evaluate` for typos, parenthetical stripping, to-prefix, tilde, slash alternatives, case insensitivity, Levenshtein threshold, null/empty inputs |

### Unit Tests — Quiz Filtering

| File | Tests | What They Verify |
|------|-------|-----------------|
| `VocabQuizFilteringTests.cs` | 13 tests | Known word exclusion (`IsKnown` vs stale `IsCompleted`), DueOnly filtering (due/not-due/unseen), mode selection (mastery >= 50% → Text, below → MC), quiz-local promotion, full pipeline filter simulation |

### Integration Tests

| File | Tests | What They Verify |
|------|-------|-----------------|
| `MasteryAlgorithmIntegrationTests.cs` | 12 tests | Full DB: streak increments on correct, production tracking on Text input, streak reset on wrong, 7 MC ≠ Known (needs production), full Unknown→Learning→Known lifecycle, Known → 60-day review, wrong answer drops below Known, `IsPromoted` at 50%, Voice counts as production, mastery caps at 1.0, penalties approach zero, new word creates record |
| `SpacedRepetitionIntegrationTests.cs` | 12 tests | SM-2 algorithm: interval progression (1→6→15→...), incorrect resets to 1, EaseFactor adjustments (min 1.3, max 2.5), interval cap at 365, recovery after failure, `GetDueVocabCountAsync` excludes known, `GetDueVocabularyAsync` excludes known, Known → 60-day review, learning context persisted |
| `ProgressCacheServiceTests.cs` | 13 tests | TodaysPlan CRUD, user-keyed isolation (no cross-bleed), VocabSummary cache, SkillProgress composite keys, InvalidateAll, ResourceProgress, PracticeHeat |
| `MultiDayLearningJourneyTests.cs` | 7 tests | Multi-day simulation: Day 1 MC practice, Day 2 wrong answers reset streaks, Day 3 production → Known, known words excluded from plan, resource rotation, plan reflects progress |
| `PlanToProgressLifecycleTests.cs` | 7 tests | Full cycle: plan→practice→progress→plan, practice history survives plan regen, VocabReview DueWords match reality, activity types, completion records affect resource selection |

### Summary: 193+ tests across 13 files

---

## 2. Captain's Reported Bugs — Coverage Map

### Bug 1: Session Summary WasCorrectThisSession
**The bug:** Session summary showed correct answers with X (wrong) icon.
**Root cause:** `WasCorrectThisSession` wasn't set in the sentence shortcut path.
**Test coverage:** NONE. No test exercises the session summary rendering or verifies `WasCorrectThisSession` is set after each answer path (MC, Text, sentence shortcut, override).

### Bug 2: userId="" (ResolveUserId)
**The bug:** `userId` defaulted to `""`, causing queries to return empty results → known words appeared as new.
**Test coverage:** COVERED. 9 regression tests in `VocabularyProgressServiceUserIdTests.cs` guard every public method. Strong coverage.

### Bug 3: DueOnly filter not working
**The bug:** Non-due words appeared despite `DueOnly=true` query parameter.
**Test coverage:** PARTIAL. `VocabQuizFilteringTests.cs` tests the model-level filtering logic (IsDueForReview computed property). But NO test verifies that `VocabQuiz.razor`'s `LoadVocabulary()` actually applies the DueOnly filter correctly with real data.

### Bug 4: IsKnown / high-confidence bypass
**The bug:** Known words (high mastery + production) appeared in quiz because code checked stale `IsCompleted` instead of computed `IsKnown`.
**Test coverage:** PARTIAL. `VocabQuizFilteringTests.cs` tests this at the model level with `IsCompleted_IsStale_NotReliableForFiltering`. But NO integration test verifies the full `LoadVocabulary` → filter → `batchPool` pipeline.

### Bug 5: Progress not updating after text entry
**The bug:** After answering correctly via text input, the info panel still showed old CurrentStreak, TotalAttempts, etc.
**Root cause:** `RecordPendingAttemptAsync()` calls `ProgressService.RecordAttemptAsync()` but NEVER writes the returned `updatedProgress` back to `currentItem.Progress`. The sentence shortcut path (line 933) does, but the standard path (line 1206) doesn't.
**Test coverage:** NONE. No test verifies that `currentItem.Progress` reflects the just-recorded attempt.

### Bug 6: Known words appearing in quiz (combined)
**Test coverage:** PARTIAL via model tests. No integration test loads a quiz session with known words and verifies they're excluded from `batchPool`.

### Bug 7: Info panel showing stale data
**The bug:** Opening the info panel after answering shows pre-answer stats.
**Root cause:** Same as Bug 5 — `currentItem.Progress` is never updated in the standard path.
**Test coverage:** NONE. No component-level or integration test verifies info panel data reflects current progress.

---

## 3. Captain's Stated Expectations — Test Needs

| Expectation | Current Coverage | Gap |
|-------------|-----------------|-----|
| "My activity immediately is reflected in the learning details" | NONE | Need test: after RecordAttemptAsync, currentItem.Progress must equal returned value |
| "Immediately impacts how that word shows up in the current activity" | NONE | Need test: after correct answer, next encounter uses updated mastery for mode selection |
| "After 3 correct sentence productions, word should test out" | PARTIAL (MasteryAlgorithm integration tests cover Unknown→Known lifecycle) | Need test specific to sentence shortcut: 3 sentences → ProductionInStreak=3, IsKnown=true, word removed from pool |
| "Known words should stop appearing" | PARTIAL (model tests) | Need integration test: LoadVocabulary with known words → batchPool excludes them |
| "Each correct answer should increment CurrentStreak, TotalAttempts, CorrectAttempts" | COVERED at service level | Need test at quiz component level: after answer, currentItem.Progress counters match |

---

## 4. Missing Test Categories (Gaps)

### GAP A: Quiz Component Behavior Tests (HIGH PRIORITY)
No tests exercise `VocabQuiz.razor`'s actual methods. Everything is tested at the service/model level. The wiring between the component and the service is untested.

**Needed tests:**

1. **`CheckAnswer_Correct_MC_SetsWasCorrectThisSession`**
   - Setup: currentItem with word, call CheckAnswer with correct MC guess
   - Assert: `currentItem.WasCorrectThisSession == true`

2. **`CheckAnswer_Correct_Text_SetsWasCorrectThisSession`**
   - Setup: currentItem with word, call CheckAnswer with correct text input
   - Assert: `currentItem.WasCorrectThisSession == true`

3. **`OverrideAsCorrect_SetsWasCorrectThisSession`**
   - Setup: currentItem answered wrong, call OverrideAsCorrect
   - Assert: `currentItem.WasCorrectThisSession == true`, `pendingAttempt.WasCorrect == true`

4. **`RecordPendingAttemptAsync_UpdatesCurrentItemProgress`** ← Would have caught Bug 5
   - Setup: currentItem with Progress, pendingAttempt built from CheckAnswer
   - Act: call RecordPendingAttemptAsync
   - Assert: `currentItem.Progress.CurrentStreak` incremented, `currentItem.Progress.TotalAttempts` incremented

5. **`SentenceShortcut_UpdatesCurrentItemProgress`**
   - Setup: sentence shortcut with 2 correct sentences
   - Assert: `currentItem.Progress.ProductionInStreak == 2`

### GAP B: Round Rotation Tests (MEDIUM PRIORITY)
No test verifies that a word is removed from `batchPool` after reaching mastery within a quiz session.

**Needed tests:**

6. **`SetupNewRound_RemovesReadyToRotateOutItems`**
   - Setup: batchPool with item where `ReadyToRotateOut == true`
   - Act: call SetupNewRound
   - Assert: item no longer in batchPool

7. **`DueOnlySession_KnownWordRotatedOut`**
   - Setup: batchPool with item where `IsDueOnlySession=true` and `Progress.IsKnown=true`
   - Assert: `ReadyToRotateOut == true`

### GAP C: Session Summary Accuracy Tests (HIGH PRIORITY)
No test verifies the session summary displays correct information.

**Needed tests:**

8. **`SessionSummary_CorrectCount_MatchesActual`**
   - Setup: run 5 items, 3 correct, 2 wrong
   - Assert: `correctCount == 3`, `totalTurns == 5`

9. **`SessionSummary_AllItemsHaveCorrectWasCorrectThisSession`** ← Would have caught Bug 1
   - Setup: run items with mix of MC correct, Text correct, override, sentence shortcut
   - Assert: each item's `WasCorrectThisSession` matches its actual outcome

10. **`SessionSummary_WordsMastered_CountsKnownAfterSession`**
    - Setup: drive items to Known during session
    - Assert: `wordsMastered` matches count of items where `Progress.IsKnown == true`

### GAP D: DueOnly Integration Tests (HIGH PRIORITY)

**Needed tests:**

11. **`LoadVocabulary_DueOnlyTrue_ExcludesNotDueWords`** ← Would have caught Bug 3
    - Setup: 10 words, 5 due (past NextReviewDate), 5 not due
    - Act: LoadVocabulary with DueOnly=true
    - Assert: batchPool contains only the 5 due words + any unseen words

12. **`LoadVocabulary_DueOnlyTrue_IncludesUnseenWords`**
    - Setup: words with no progress records
    - Act: LoadVocabulary with DueOnly=true
    - Assert: unseen words included

13. **`LoadVocabulary_DueOnlyFalse_IncludesAllNonKnownWords`**
    - Setup: mix of due/not-due/known words
    - Assert: all non-known words included regardless of NextReviewDate

### GAP E: Info Panel Live Update Tests (MEDIUM PRIORITY)

**Needed tests:**

14. **`InfoPanel_ShowsUpdatedProgressAfterMCAnswer`** ← Would have caught Bug 7
    - Setup: answer MC correctly, open info panel
    - Assert: displayed CurrentStreak, TotalAttempts, MasteryScore match post-answer values

15. **`InfoPanel_ShowsUpdatedProgressAfterTextAnswer`** ← Would have caught Bug 5
    - Setup: answer Text correctly
    - Assert: `currentItem.Progress.ProductionInStreak` incremented

16. **`InfoPanel_ShowsUpdatedProgressAfterOverride`**
    - Setup: answer wrong, override to correct
    - Assert: displayed values reflect the overridden (correct) result

### GAP F: Cross-Activity Progress Consistency (LOW PRIORITY)

**Needed tests:**

17. **`ProgressUpdatedInQuiz_ReflectsInDashboard`**
    - Setup: answer correctly in quiz, navigate to dashboard
    - Assert: vocab summary shows updated stats

18. **`ProgressUpdatedInQuiz_AffectsNextPlanGeneration`**
    - Setup: drive words to Known in quiz
    - Assert: next plan's due word count excludes newly-known words

---

## 5. Priority: Which Gaps Would Have Caught Tonight's Bugs

| Priority | Test # | Gap | Bug Prevented |
|----------|--------|-----|---------------|
| P0 | 4 | RecordPendingAttemptAsync writes back to currentItem.Progress | Bug 5 (progress not updating after text), Bug 7 (stale info panel) |
| P0 | 9 | Session summary WasCorrectThisSession accuracy | Bug 1 (correct answers marked wrong) |
| P0 | 11 | LoadVocabulary DueOnly integration | Bug 3 (non-due words appearing) |
| P0 | 14-16 | Info panel shows live data | Bug 5 + Bug 7 combined |
| P1 | 1-3 | WasCorrectThisSession set in all code paths | Bug 1 |
| P1 | 6-7 | Round rotation removes mastered words | Bug 6 variant |
| P1 | 8,10 | Session summary counters accurate | Bug 1 |
| P2 | 12-13 | DueOnly edge cases (unseen words, false mode) | Bug 3 edge cases |
| P2 | 5 | Sentence shortcut progress update | Variant of Bug 5 |
| P3 | 17-18 | Cross-activity consistency | Future regression prevention |

---

## 6. Code Bug Found During Analysis

**`RecordPendingAttemptAsync()` does NOT write `updatedProgress` back to `currentItem.Progress`.**

In `VocabQuiz.razor` line 1206, the service returns updated progress but it's used only for the `IsFamiliar` check and then discarded. Compare with the sentence shortcut path (line 933) which correctly does `currentItem.Progress = updatedProgress;`.

This is the root cause of both "progress not updating after text entry" and "info panel showing stale data." The fix is one line, but the gap it exposed — no component-level testing at all — is systemic.

---

## 7. Recommendations

1. **Immediate:** Fix the `RecordPendingAttemptAsync` write-back bug (add `currentItem.Progress = updatedProgress;`).
2. **This sprint:** Write tests 4, 9, 11, 14 (the P0 items). These are the exact tests that would have caught every bug the Captain reported tonight.
3. **Next sprint:** Write remaining P1 tests (1-3, 6-8, 10). These provide defense-in-depth for quiz component behavior.
4. **Ongoing:** Consider a Blazor component test harness (bUnit) to test VocabQuiz.razor methods directly without a running app.
