# Decision: Phase 0 — Scoring Engine Foundation

**Author:** Wash (Backend Dev)
**Date:** 2026-04-15
**Status:** Implemented

## Summary

Implemented the Phase 0 scoring engine foundation that all other mastery system changes depend on. This replaces the flat wrong-answer penalty and hard streak reset with a nuanced, experience-aware system.

## Changes Made

### 1. CurrentStreak: int → float
- `VocabularyProgress.CurrentStreak` changed from `int` to `float` to support weighted difficulty increments
- Updated matching types in `VocabularyProgressResponse`, `VocabularyQuizItem`
- EF Core migrations created for both SQLite and PostgreSQL providers
- Audited all code referencing CurrentStreak — display code updated to format as `F1`

### 2. DifficultyWeight applied to streak increment
- `progress.CurrentStreak++` → `progress.CurrentStreak += weight` where weight comes from `attempt.DifficultyWeight` (defaults to 1.0f if unset)
- Scale: MC=1.0, Cloze=1.2, Text=1.5, Sentence=2.5 (already set by callers)

### 3. Temporal weighting (scaled wrong-answer penalty)
- Replaced flat `mastery *= 0.6` with logarithmic scaling based on `CorrectAttempts`
- Established words penalized less: `penaltyFactor = max(0.6, 1.0 - 0.4/(1 + log(1 + correctAttempts)))`
- A brand new word still gets the full 0.6 penalty; a word with 20+ correct attempts gets ~0.87

### 4. Partial streak preservation
- Replaced `CurrentStreak = 0` with fraction-based preservation
- `preserveFraction = min(0.5, log(1 + correctAttempts) / 8.0)`
- A new word still resets to ~0; a well-practiced word keeps up to 50% of its streak

### 5. Recovery-aware mastery (correct answers always show progress)
- Added `RECOVERY_BOOST = 0.02f` so a correct answer after a penalty always increases MasteryScore
- `MasteryScore = max(streakScore, currentMastery) + recoveryBoost`
- Prevents the frustrating case where a correct answer shows no visible progress

### 6. VocabQuiz deferred recording write-back fix
- Added `if (currentItem != null) currentItem.Progress = updatedProgress;` after `RecordAttemptAsync` in `RecordPendingAttemptAsync`
- This was the original bug: the quiz UI wasn't seeing updated progress after recording

### 7. New constants
- `WRONG_ANSWER_FLOOR`, `MAX_WRONG_PENALTY`, `MAX_STREAK_PRESERVE`, `STREAK_PRESERVE_DIVISOR`, `RECOVERY_BOOST`

## Test Impact
- 4 tests updated to reflect new scoring behavior (partial preservation, scaled penalty)
- All 391 passing tests continue to pass
- 1 pre-existing failure (`ResourceUsed15DaysAgo`) is unrelated

## Migration Notes
- Migrations created manually (EF tooling has a known issue with multi-target MAUI projects)
- SQLite: `INTEGER` → `REAL` (safe — SQLite has dynamic typing)
- PostgreSQL: `integer` → `real`
- Applied at runtime via `MigrateAsync()` — no manual steps needed
- No data loss: existing integer values are valid floats

## Dependencies
- This is the foundation for Phase 1 (cross-activity mastery) and Phase 2 (spaced repetition tuning)
- The StreakInfo record in IProgressService (daily practice streak) was NOT changed — it tracks consecutive *days*, not weighted correct answers
