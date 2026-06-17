### 2026-06-17: Fix cross-tenant data leak in ApplyFocusVocabularyFreshnessAsync

**Author:** Simon (Backend Specialist / Escalation)
**Branch:** squad/per-user-timezone-plan-dates
**Rejection addressed:** Zoe's finding #3b (BLOCKER) in zoe-review-per-user-timezone.md

---

## Root cause

`VocabularyProgressRepository.GetByWordIdsAsync` (src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs:80) filters only by `VocabularyWordId` -- it has no `UserId` clause. On per-device SQLite (MAUI heads) this is harmless because only one user's rows exist. On shared Postgres (webapp + API), the query returns rows for ALL tenants.

`ProgressService.ApplyFocusVocabularyFreshnessAsync` (src/SentenceStudio.Shared/Services/Progress/ProgressService.cs:985) called this unscoped method and then used `.GroupBy(p => p.VocabularyWordId).First()` to pick a single row per word. On multi-tenant Postgres, `.First()` can arbitrarily select another tenant's progress row. If User A has studied a word (TotalAttempts=3, NextReviewDate=tomorrow) and User B has the same word with 0 attempts, B's brand-new word could be erroneously DROPPED from B's plan based on A's progress. This is a cross-tenant data leak.

## Fix (approach a -- scoped repo method)

Added `VocabularyProgressRepository.GetByWordIdsForUserAsync(List<string> wordIds, string? userId = null)` at VocabularyProgressRepository.cs:119-152.

Pattern matches the established `GetByWordIdAsync` at line 56:
- Resolves userId from `ActiveUserId` when null
- Empty/missing userId logs a warning and returns an empty list (never unfiltered, never throws)
- Applies `.Where(vp => vp.UserId == userId)` server-side BEFORE the word-id filter
- Retains the 500-row batch optimization for SQLite parameter limits

Changed `ProgressService.ApplyFocusVocabularyFreshnessAsync` at line 985 to call `GetByWordIdsForUserAsync` instead of `GetByWordIdsAsync`.

When no active user is available, the method returns an empty progress list, which means `progressByWordId` is empty, every focus word hits the "no progress record = brand new, keep it" branch, and the plan is returned unchanged. This is the correct safe behavior: freshness is a refinement, not a gate.

## Files changed

1. src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs
   - Lines 76-79: added warning comment on existing `GetByWordIdsAsync` noting it returns rows for all users
   - Lines 119-152: new `GetByWordIdsForUserAsync` method (tenant-safe)

2. src/SentenceStudio.Shared/Services/Progress/ProgressService.cs
   - Lines 984-988: changed `GetByWordIdsAsync` call to `GetByWordIdsForUserAsync`

## Same-tenant freshness outcomes (unchanged)

The freshness logic itself (lines 990-1019) is not modified. For the correct (same-tenant) rows:
- No progress record -> KEEP (brand new) -- unchanged
- TotalAttempts == 0 -> KEEP (never studied) -- unchanged
- TotalAttempts > 0 and NextReviewDate > UtcNow -> DROP (studied, not yet due) -- unchanged
- TotalAttempts > 0 and NextReviewDate <= UtcNow or null -> KEEP (still due) -- unchanged

## Empty-userId safety

Empty userId -> `GetByWordIdsForUserAsync` logs warning, returns empty list -> `progressByWordId` dictionary is empty -> every focus word hits the "no progress" branch -> all kept -> plan returned unchanged. No throw, no circuit break, no erroneous drops.

## Audit: VocabularyProgressService.cs:280

VocabularyProgressService.GetProgressForWordsAsync (src/SentenceStudio.Shared/Services/VocabularyProgressService.cs:272-286) calls the unscoped `GetByWordIdsAsync` at line 280 but immediately post-filters at lines 283-284:

    .Where(p => p.UserId == resolvedUserId)

This is SAFE. The unscoped fetch is wasteful (loads rows for other tenants into memory before discarding), but it does not leak cross-tenant data to the caller. It would benefit from switching to `GetByWordIdsForUserAsync` for efficiency, but that is a separate optimization -- not a correctness fix and not in scope for this PR.

## Recommended regression test for Jayne

Test name: `FocusVocabularyFreshness_MultiTenant_DoesNotLeakCrossTenantProgress`

Setup:
- Two user profiles (UserA, UserB) in an in-memory Postgres or SQLite database
- A shared focus vocabulary word ID (e.g. "word-123")
- UserA: VocabularyProgress with TotalAttempts=5, NextReviewDate=tomorrow (studied, not due)
- UserB: VocabularyProgress with TotalAttempts=0 (brand new, or no record at all)

Action: Run ApplyFocusVocabularyFreshnessAsync for UserB's plan containing "word-123"

Assert: "word-123" is KEPT in UserB's plan (not dropped based on UserA's progress)

This pins the cross-tenant isolation guarantee and will break if anyone reverts to the unscoped method.

## Compile result

`dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj -f net10.0` -- 0 errors, 172 pre-existing warnings.
