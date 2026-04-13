# Orchestration: Zoe — Mastery Bug Fix Code Review

**Date:** 2026-04-13T23:53:58Z  
**Spawn:** zoe-mastery-review  
**Mode:** Background  
**Charter:** Lead — Code Review & Architecture Governance  

## Task

Review Kaylee's implementation of 4 mastery/quiz bug fixes (Wash audit). Flag scope creep; approve core fixes; recommend refactoring strategy.

## Status

✅ COMPLETED

## Verdict

**CONDITIONAL APPROVE**

### Core Fixes: ✅ All Correct

**Fix 1 (WasCorrectThisSession)** — Correct. Flag set in all three paths.

**Fix 2 (DueOnly Filter)** — Correct. Three-branch logic is explicit and clear.

**Fix 3 (IsDueOnlySession)** — Correct. Propagation through items, rotation behavior safe.

**Fix 4 (High-Confidence Bypass)** — Correct. Alternative path to IsKnown is reasonable (75% mastery, 4+ production, 8+ attempts).

### Major Scope Creep Issues

1. **Blended Mastery Score** — Fundamental algorithmic change (not a bug fix)
   - Changes MasteryScore for EVERY word in system
   - Requires migration to recalculate existing records
   - Should be separate PR with A/B testing / gradual rollout

2. **Plan WordIds Threads** — Feature enhancement (not a bug fix)
   - Enables plan-driven word selection
   - Touches 8+ files across services/pages
   - Should be separate PR

3. **Unseen Words Logic** — Feature (not a bug fix)
   - Introduces new words automatically when due count < 20
   - Fundamental planning change
   - Requires product discussion

### Blocking Issues

**Missing Migration:** No recalculation of MasteryScore for existing records under new blended formula. Words will have stale scores until next attempt.

### Side Effects

IsKnown bypass affects:
- Daily plan generation
- Smart resource filtering
- Due word counts

**Impact:** Global behavior change, but conservative/safe. Document in commit message.

## Recommendation

**CONDITIONAL APPROVE:**
- Core 4 fixes are correct ✅
- Significant scope creep should be discussed with Captain before merge ⚠️
- Missing migration documentation ❌

**Captain should decide:**
1. Merge as-is (accepting scope creep) + add migration
2. Split into focused PR (core fixes only) + separate feature PRs

Either strategy is defensible. Code quality is high; it's change management strategy.

## Decision Record

Merged to decisions.md: Mastery/Quiz Bug Fixes Code Review
