### 2025-12-18 21:30 UTC: Vocab Quiz Scoring Repro Tests (#189, #191) — service-side disambiguation
**By:** Jayne (Tester) — Stream B Step 1
**Requested by:** David Ortinau (Captain)

**What:**
Added 4 integration tests in `tests/SentenceStudio.UnitTests/Integration/VocabQuizScoringRepro189And191Tests.cs` against real EF Core + in-memory SQLite via `PlanGenerationTestFixture`. Branch `test/vocab-quiz-scoring-repro-189-191`, draft PR #195.

**Outcome on `main` (2aab53d):**
- `Repro189_SingleCorrectRecognitionAttempt_ProducesExpectedPanelState` — **PASS**
- `Repro189_SingleCorrectRecognition_LegacyProductionFieldsRemainZero` — **PASS**
- `Repro191_NewWord_AllCorrect_DoesNotRotateOutBeforeFifthTurn` — **FAIL** (turn 4 trips `ReadyToRotateOut`)
- `Repro191_CharacterizeCurrentBehavior_FreshWordRotatesAtTurnN` — **PASS** (snapshot: turn=4)

**Decisions for the team to act on:**

1. **#189 root cause is NOT in `VocabularyProgressService`.** Service math is correct for a single correct MultipleChoice attempt: `TotalAttempts=1, CorrectAttempts=1, Accuracy=1.0, ProductionInStreak=0, ProductionAttempts=0`. Captain's "2 production attempts / 50% accuracy" panel readout originates in the UI layer.
   - **Action — Kaylee (Stream A):** audit Learning Details panel in `VocabQuiz.razor` (~lines 395–460) for reads of legacy obsolete fields; audit `RecordPendingAttemptAsync` call sites (~1245, ~1394, ~1490) for duplicate-fire.
   - **Action — Wash:** do NOT modify `VocabularyProgressService.RecordAttemptAsync` for #189. The two `Repro189_*` tests are now regression guards on its contract.

2. **#191 root cause IS in `VocabularyQuizItem.ReadyToRotateOut` Tier 2.** Captured failure trace: a brand-new word receiving 4 all-correct answers (3 MC + 1 Text — the quiz auto-flips to Text once `CurrentStreak>=3`) trips rotation at turn 4 because Tier 2's gates (`mastery>=0.50 OR streak>=3` plus only `SessionCorrectCount>=2 AND SessionTextCorrect>=1`) are met immediately. This explains Captain's 26 fresh words mastered in 58 turns / 8 rounds.
   - **Action — Wash (Stream B Step 2):** tighten Tier 2 (and/or the mode-flip threshold). **Pause for Captain alignment via decisions.md before picking a target curve** — this is a behavior policy decision, not a technical one. Wash's fix should turn `Repro191_NewWord_AllCorrect_DoesNotRotateOutBeforeFifthTurn` green; the characterization test will need its expected turn updated to the new value.

**Why:** Pinning expected post-state in code so Wash and Kaylee fix against an unambiguous target instead of paraphrased symptoms. The diagnostic split (#189 = UI; #191 = model) keeps the streams independent and avoids one fix masking the other.

**Files / refs:**
- New: `tests/SentenceStudio.UnitTests/Integration/VocabQuizScoringRepro189And191Tests.cs`
- PR: https://github.com/davidortinau/SentenceStudio/pull/195
- Builds clean against `tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj`. No production code touched.
