### Phase 2 — Shared Extraction Pipeline + Translation Bug Fix

**Status:** IMPLEMENTED  
**Date:** 2026-07-25  
**Author:** Wash  
**Spec:** `docs/specs/cross-activity-mastery.md` §3.4, §4.1, §4.2, §5

**Context**

Phase 0 (scoring engine) and Phase 1 (quiz behavior) were complete. The next step was building the shared vocabulary extraction pipeline that all sentence-based activities (Writing, Translation, Scene, Conversation) will use, and fixing the Translation bug where `VocabularyProgressService` was injected but never called.

**Changes**

1. **`ExtractAndScoreVocabularyAsync`** added to `VocabularyProgressService` — deduplicates `VocabularyAnalysis` by `DictionaryForm`, matches against user vocabulary (case-insensitive), calls `RecordAttemptAsync` per word, collects verification probes and fires them AFTER the loop. Returns `List<VocabScoringResult>` for UI feedback.

2. **`PenaltyOverride`** added to `VocabularyAttempt`. When set, `RecordAttemptAsync` uses it instead of the computed scaled penalty. Enables Conversation's 0.8x softer penalty without duplicating scoring logic.

3. **Writing.razor** refactored: 30-line inline scoring loop replaced with single call to `ExtractAndScoreVocabularyAsync`. DifficultyWeight changed from 1.0 to 1.5 per Captain's decision.

4. **Translation.razor** fixed: ad-hoc AI prompt replaced with `TeacherSvc.GradeTranslation()` (proper Scriban template that already requests `vocabulary_analysis`). Added `IPreferencesService` and `ProgressCacheService` injections. Wired up `ExtractAndScoreVocabularyAsync` with DifficultyWeight=1.5.

5. **`RecordPassiveExposureAsync`** added to `VocabularyProgressService` for Reading word lookups. Creates `VocabularyLearningContext` entry, updates `LastExposedAt`/`ExposureCount` on progress record. Does NOT touch mastery, streak, or SRS.

6. **EF migrations** created for both SQLite and PostgreSQL (`AddPassiveExposureFields`). `PatchMissingColumnsAsync` updated for mobile SQLite compatibility.

**Remaining Work (Future Phases)**

- Scene.razor: needs template update + new injections + vocab loading (§4.3)
- Conversation.razor: needs Reply model extension + template update + softer penalty wiring (§4.4)
- Reading.razor: needs to call `RecordPassiveExposureAsync` on word tap (§5.3)

**Impact**

- Translation now records vocabulary mastery (was completely silent before)
- Writing uses correct DifficultyWeight of 1.5 (was under-weighting at 1.0)
- Any future activity can reuse `ExtractAndScoreVocabularyAsync` — no copy-paste needed
- All 401 tests pass
