# Orchestration: Wash — Vocabulary Quiz Mastery & Filtering Audit

**Date:** 2026-04-13T23:53:58Z  
**Spawn:** wash-mastery-audit  
**Mode:** Background  
**Charter:** Backend & Integration Developer  

## Task

Deep audit of vocabulary quiz mastery scoring and word filtering bugs affecting session summary, DueOnly filter, rotation logic, and IsKnown threshold.

## Status

✅ COMPLETED

## Findings

**4 Critical Bugs Identified:**

1. **Session Summary Bug** — Words marked wrong despite correct performance
   - File: VocabQuiz.razor lines 55-56, 920-945
   - Cause: `WasCorrectThisSession` flag not set in sentence shortcut path
   - Fix: Set flag when updating production streak from shortcut results

2. **DueOnly Filter Bug** — Non-due words appearing despite filter active
   - File: VocabQuiz.razor lines 693-700
   - Cause: Filter applied once at load; `SetupNewRound()` rebuilds from `batchPool` without re-checking due dates
   - Fix: Re-apply DueOnly filter when building each round (SetupNewRound, line 728)

3. **Rotation Logic Bug** — Words not removed after mastery demonstrated
   - File: VocabularyQuizItem.cs line 17, VocabQuiz.razor line 688
   - Cause: Uses quiz-session streaks (3-correct required) instead of global mastery
   - Fix: Add `IsDueOnlySession` property; use global mastery to skip rotation in DueOnly mode

4. **IsKnown Threshold Bug** — Too strict (0.85 vs 0.80)
   - File: VocabularyProgress.cs line 11
   - Cause: 85% threshold creates false negatives (84% mastery + 13-streak marked "not known")
   - Fix: Lower threshold to 0.80; high-streak words align better with user perception

## Evidence

- Words with 13-streak, 84% mastery marked as "learning"
- Session summary showing ❌ for words with 3+ correct attempts
- Non-due words appearing in DueOnly sessions
- Screenshots attached in audit file

## Decision Record

Merged to decisions.md: Vocabulary Quiz Mastery & Filtering Audit
