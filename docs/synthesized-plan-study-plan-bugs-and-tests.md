# Synthesized Plan: Study Plan Generation Bug Fixes & Test Suite

**GitHub Issue**: #161 — "Study plan includes known words incorrectly"  
**Date**: 2026-07-10  
**Methodology**: Comparative evaluation of three independent AI-generated plans (Opus, Codex, Goldeneye), verified against actual codebase, synthesized into one actionable plan.

---

## 1. Comparative Scorecard

| Criterion | Opus (A) | Codex (B) | Goldeneye (C) | Notes |
|-----------|----------|-----------|---------------|-------|
| **Bug Diagnosis Accuracy** | 5 | 4 | 4 | Opus provides exact line numbers; Codex has deeper root-cause thinking on pre-selection overlap; Goldeneye catches the `Compile Remove` issue |
| **Fix Specificity** | 5 | 3 | 3 | Opus gives copy-pasteable diffs; Codex/Goldeneye are more architectural |
| **Test Coverage** | 5 | 3 | 3 | 125 tests (Opus) vs 35-55 (Codex) vs 40-55 (Goldeneye). Opus is comprehensive but possibly over-scoped |
| **Test Infrastructure** | 5 | 3 | 4 | Opus's `IPlanDataProvider` is clean; Goldeneye's `TimeProvider` is the correct .NET 8+ approach; Codex's custom interfaces add maintenance |
| **Validation Rules** | 4 | 4 | 5 | Goldeneye's Critical vs Warning tier system is the most pragmatic |
| **Scope/Effort Realism** | 3 | 4 | 4 | Opus's 6-7 days and 125 tests risks scope creep; Codex/Goldeneye are tighter |
| **Pedagogical Correctness** | 5 | 3 | 4 | Opus correctly explains why near-known words need production review; Goldeneye's `RecommendedReviewInputMode` aligns mode to mastery |
| **Unique Insights** | 4 | 4 | 5 | Goldeneye's `PlanBuildContext` and invariant tiering are the strongest novel ideas |
| **TOTAL** | **36** | **28** | **32** | |

**Winner by points: Opus** — but the synthesized plan takes Opus's specificity and patches its scope weakness with Goldeneye's architecture and Codex's pragmatism.

---

## 2. Strengths & Weaknesses

### Plan A: Opus

**Strengths:**
- **Surgical precision**: Exact line numbers, method signatures, copy-paste code diffs. An implementer can follow this blindfolded.
- **Exhaustive test design**: 125 tests with tabular specifications including scenario + expected outcome for every test. The fluent builders (`VocabularyProgressBuilder`, `LearningResourceBuilder`) are production-quality.
- **Reconciliation step**: The post-selection vocab overlap reconciliation (lines 69-88 of the plan) with fallback-to-general-review-on-low-overlap is well-thought-out.

**Weaknesses:**
- **Over-scoped**: 125 tests across 7 files in 6-7 days is aggressive. Many tests are near-duplicates (e.g., 25 resource selection tests where 15 would suffice).
- **Post-hoc reconciliation**: Computes overlap AFTER resource selection, then patches. This is a bandaid — Codex's pre-selection approach is architecturally cleaner.
- **No `TimeProvider`**: Uses `new Random(today.GetHashCode())` instead of the .NET 8+ `TimeProvider` abstraction. Fine for now but misses the modern approach.

### Plan B: Codex

**Strengths:**
- **Pre-selection overlap**: `SelectResourcesForVocabularyAsync` computes overlap BEFORE choosing a resource. This eliminates the root cause instead of patching symptoms.
- **Deterministic hash tiebreaker**: Replacing `Guid.NewGuid()` with a hash of `(date, resourceId)` makes tests reproducible without injecting randomness.
- **Lean scope**: 3 test files is realistic for a focused fix-and-verify sprint.

**Weaknesses:**
- **Under-specified**: Based on the summary, lacks exact line references and code diffs. An implementer needs to do significant discovery work.
- **Custom `IDateTimeProvider`**: Reinvents the wheel when .NET 8+ has `TimeProvider`. Adds maintenance burden.
- **Set-cover algorithm complexity**: Greedy set-cover for multi-resource selection is a great idea but over-engineered for a codebase with 5-10 resources. Adds complexity without proportional benefit.

### Plan C: Goldeneye

**Strengths:**
- **`PlanBuildContext`**: Computing all decision inputs once and passing them through eliminates the "independent decisions" root cause at the architectural level. This is the deepest fix.
- **`TimeProvider` (built-in .NET 8+)**: The correct modern approach to time abstraction. Already tested and supported by the framework.
- **Invariant tiering**: Separating "Critical" (must fail tests/block plans) from "Warning" (log but allow) invariants is pragmatic and prevents false positives in production.
- **`Compile Remove` observation**: Goldeneye is the only plan that noticed `VocabularyProgressServiceTests.cs` is excluded from build, which affects test infrastructure decisions.

**Weaknesses:**
- **Architectural scope**: `PlanBuildContext` is a larger refactor than the other plans' approaches. Risk of touching too many methods.
- **`RecommendedReviewInputMode` as computed property**: Adding domain logic to a model class violates separation of concerns unless carefully scoped.
- **30-day window**: Extending from 14 to 30 days without profiling the query performance is a risk (though likely fine for small datasets).

---

## 3. What Each Plan Uniquely Contributes (Must-Adopt Ideas)

### From Opus (ADOPT):
- **Fluent test data builders** (`VocabularyProgressBuilder`, `LearningResourceBuilder`) — production-quality, reusable across all tests
- **`IPlanDataProvider` interface extraction** — eliminates service locator anti-pattern, enables clean mocking
- **Low-overlap fallback to general review** — pedagogically correct: if selected resource only has 2 of 20 due words, don't pretend it's contextual
- **Regression test per bug** — each bug gets at least 2 named regression tests

### From Codex (ADOPT):
- **Pre-selection overlap computation** — compute vocabulary overlap as a scoring input, not a post-hoc reconciliation
- **Deterministic hash tiebreaker** — `HashCode.Combine(today, resourceId)` replaces `Guid.NewGuid()` for reproducible test results
- **`PlanValidator` as a standalone class** — usable in both tests and production debug mode

### From Goldeneye (ADOPT):
- **`TimeProvider`** (built-in .NET 8+) — use this instead of custom `IDateTimeProvider`
- **Critical vs Warning invariant tiering** — critical invariants fail tests; warnings log but don't block
- **30-day query window** — correct for realistic spaced-repetition intervals (some cards have 21-day intervals)
- **Fix `Compile Remove`** — re-enable `VocabularyProgressServiceTests.cs` or document why it's excluded

---

## 4. Synthesized Recommendation

### Architecture: Opus's `IPlanDataProvider` + Codex's Pre-Selection Overlap

The core architectural change is a hybrid:

1. **Extract `IPlanDataProvider`** (from Opus) — this is the testing enabler. All DB calls in `DeterministicPlanBuilder` become mockable.
2. **Compute overlap BEFORE resource selection** (from Codex) — don't pick a resource then check if it matches vocabulary. Instead, make overlap a first-class scoring input.
3. **Use `TimeProvider`** (from Goldeneye) — for time-dependent logic and testing.

### Bug Fix Approach (per bug)

#### Bug 1: Resource–Vocabulary Misalignment

**Use Codex's approach (pre-selection) enhanced with Opus's specificity.**

Change `SelectPrimaryResourceAsync` signature to accept the full `VocabularyReviewBlock`:

```csharp
private async Task<SelectedResource?> SelectPrimaryResourceAsync(
    DateTime today, VocabularyReviewBlock? vocabReview, CancellationToken ct)
```

Inside the scoring loop, compute overlap per candidate BEFORE scoring:

```csharp
// For each candidate, count how many due words link to this resource
var overlapCount = 0;
if (vocabReview?.DueWords?.Any() == true)
{
    var resourceWordIds = await _planDataProvider
        .GetResourceVocabularyWordIdsAsync(candidate.Resource.Id, ct);
    var dueWordIds = vocabReview.DueWords
        .Select(w => w.VocabularyWordId).ToHashSet();
    overlapCount = dueWordIds.Intersect(resourceWordIds).Count;
}

// RULE 3: Vocabulary overlap bonus (replaces flat +75)
score += overlapCount * 15;  // 10 overlapping words = +150

// RULE 3b: Extra bonus if this IS the vocab resource (original intent)
if (candidate.IsVocabResource)
    score += 50;  // Small additional bonus, but overlap already dominates
```

This replaces Opus's post-hoc reconciliation with a scoring-integrated approach. The resource with the best vocabulary alignment wins during selection, not after.

**After selection, still reconcile** (from Opus) — set `VocabularyReviewBlock.OverlapWordIds` and `OverlapWordCount` for downstream use:

```csharp
if (vocabReview != null && primaryResource != null)
{
    var resourceWordIds = await _planDataProvider
        .GetResourceVocabularyWordIdsAsync(primaryResource.Id, ct);
    var dueWordIds = vocabReview.DueWords
        .Select(w => w.VocabularyWordId).ToHashSet();
    vocabReview.OverlapWordIds = dueWordIds.Intersect(resourceWordIds).ToList();
    vocabReview.OverlapWordCount = vocabReview.OverlapWordIds.Count;

    // Opus's fallback: if overlap is too low, go general
    if (vocabReview.IsContextual && vocabReview.OverlapWordCount < 3)
    {
        vocabReview.IsContextual = false;
        vocabReview.ResourceId = null;
        _logger.LogInformation(
            "Low vocab overlap ({Count}) — falling back to general review",
            vocabReview.OverlapWordCount);
    }
}
```

**Files changed**: `DeterministicPlanBuilder.cs` (scoring logic + reconciliation), `VocabularyReviewBlock` (add `OverlapWordIds`, `OverlapWordCount` properties)

---

#### Bug 2: VocabReview WordCount Mismatch

**Use Opus's fix** — the reconciliation step above (Bug 1's post-selection overlap) already solves this. The `OverlapWordCount` replaces the inflated `WordCount` in activity descriptions.

Additionally, update `BuildActivitySequenceAsync` to use `OverlapWordCount` when available:

```csharp
var effectiveWordCount = vocabReview.IsContextual
    ? vocabReview.OverlapWordCount
    : vocabReview.WordCount;
```

**Files changed**: `DeterministicPlanBuilder.cs`, `VocabularyReviewBlock` (already modified in Bug 1)

---

#### Bug 3: Recency Bias in Resource Selection

**Use Opus's graduated scoring + Goldeneye's 30-day window + Codex's deterministic tiebreaker.**

1. **Extend to 30-day window** (Goldeneye — line 225):
```csharp
.Where(c => c.Date >= today.AddDays(-30) && ...)
```

2. **Graduated freshness** (Opus — replace step function):
```csharp
// RULE 2: Graduated freshness bonus (logarithmic)
if (candidate.DaysSinceLastUse >= 2)
    score += 50 + Math.Min(100, Math.Log(candidate.DaysSinceLastUse) * 25);
else
    score += candidate.DaysSinceLastUse * 10;
```
Result: 2d=+67, 5d=+90, 10d=+108, 30d=+135, never=+222.

3. **Deterministic tiebreaker** (Codex — replace Guid.NewGuid()):
```csharp
.ThenBy(c => HashCode.Combine(today.DayOfYear, today.Year, c.Resource.Id))
```

**Files changed**: `DeterministicPlanBuilder.cs`

---

#### Bug 4: Known Words in Recognition Mode

**Use Opus's diagnosis + Goldeneye's `RecommendedReviewInputMode` concept (but as a service, not a model property).**

The actual code at line 610-611 is:
```csharp
userMode = (currentItem.IsPromotedInQuiz || (currentItem.Progress?.MasteryScore ?? 0f) >= 0.50f)
    ? "Text" : "MultipleChoice";
```

This is correct for quiz-internal promotion. The real issue is:
1. `Progress` being null causes fallback to MultipleChoice
2. SRS-mode quiz words should start in the mode their mastery demands, not the quiz's internal promotion state

**Fix in `VocabQuiz.razor`**:
```csharp
// Ensure progress is loaded before quiz starts (guard)
var itemsWithoutProgress = quizItems.Where(qi => qi.Progress == null).ToList();
if (itemsWithoutProgress.Any())
{
    _logger.LogWarning("{Count} quiz items have no progress loaded", itemsWithoutProgress.Count);
}

// Mode selection: SRS-mode items use mastery, quiz-mode items use promotion
userMode = isSrsMode
    ? ((currentItem.Progress?.MasteryScore ?? 0f) >= 0.50f ? "Text" : "MultipleChoice")
    : (currentItem.IsPromotedInQuiz || (currentItem.Progress?.MasteryScore ?? 0f) >= 0.50f
        ? "Text" : "MultipleChoice");
```

**Also in `PlanConverter.cs`** (from Opus): add `ForceProductionForPromoted=true` param for SRS quiz routes.

**Files changed**: `VocabQuiz.razor`, `PlanConverter.cs`

---

#### Bug 5: Same Resource Chosen Repeatedly

**Use Opus's usage count penalty + Bug 3 fixes.**

```csharp
// RULE 6: Penalize frequently used resources
var usageCountInWindow = recentActivity.Count(a => a.ResourceId == candidate.Resource.Id);
score -= usageCountInWindow * 8;
```

Combined with Bug 3's graduated scoring and 30-day window, this provides natural resource rotation.

**Also add resource score logging** (from Opus) for debuggability:
```csharp
foreach (var c in candidates.Where(c => c.Score > -500).OrderByDescending(c => c.Score).Take(5))
{
    _logger.LogDebug("Resource {Title}: score={Score:F1}, lastUsed={Days}d, overlap={Overlap}",
        c.Resource.Title, c.Score, c.DaysSinceLastUse, c.OverlapCount);
}
```

**Files changed**: `DeterministicPlanBuilder.cs`

---

### Test Infrastructure

**Use Opus's `IPlanDataProvider` + Goldeneye's `TimeProvider` + Codex's `PlanValidator`.**

#### Testability Refactor (prerequisite)

1. **New interface**: `IPlanDataProvider` (from Opus)
   - File: `src/SentenceStudio.Shared/Services/PlanGeneration/IPlanDataProvider.cs`
   - Methods: `GetRecentCompletionsAsync`, `GetResourceVocabularyCountsAsync`, `GetResourceVocabularyWordIdsAsync`, `GetRecentSkillForResourceAsync`, `GetMostRecentSkillAsync`, `GetYesterdayActivityTypesAsync`

2. **New implementation**: `PlanDataProvider` wraps existing `IServiceProvider.CreateScope()` calls
   - File: `src/SentenceStudio.Shared/Services/PlanGeneration/PlanDataProvider.cs`

3. **Inject `TimeProvider`** (from Goldeneye) into `DeterministicPlanBuilder`:
   - Use `TimeProvider.System` in production, `FakeTimeProvider` in tests
   - Replace `DateTime.UtcNow` references with `_timeProvider.GetUtcNow()`

4. **Modified constructor**:
```csharp
public DeterministicPlanBuilder(
    UserProfileRepository userProfileRepo,
    LearningResourceRepository resourceRepo,
    SkillProfileRepository skillRepo,
    VocabularyProgressRepository vocabProgressRepo,
    IPlanDataProvider planDataProvider,    // replaces IServiceProvider
    TimeProvider timeProvider,             // replaces DateTime.UtcNow
    ILogger<DeterministicPlanBuilder> logger)
```

#### Test Structure

```
tests/SentenceStudio.UnitTests/
├── Services/
│   └── PlanGeneration/
│       ├── ResourceSelectionTests.cs           (~18 tests)
│       ├── VocabularyReviewTests.cs            (~15 tests)
│       ├── ActivitySequencingTests.cs          (~10 tests)
│       ├── PlanBuilderIntegrationTests.cs      (~15 tests)
│       ├── PlanConverterTests.cs               (~8 tests)
│       ├── PlanValidatorTests.cs               (~12 tests)
│       └── Builders/
│           ├── VocabularyProgressBuilder.cs    (fluent builder)
│           ├── LearningResourceBuilder.cs      (fluent builder)
│           └── PlanTestBase.cs                 (shared base class)
```

**Total: ~78 tests across 6 test files** — trimmed from Opus's 125 by removing near-duplicate scenarios. Enough coverage without scope creep.

#### Test Data Builders (from Opus, adopted directly)

Use Opus's `VocabularyProgressBuilder` and `LearningResourceBuilder` as specified. They are well-designed with methods like `.AsKnown()`, `.AsPromoted()`, `.AsLearning()`, `.LinkedToResource()`.

#### Mocking Strategy

| Dependency | Strategy | Source |
|------------|----------|--------|
| `IPlanDataProvider` | Moq mock | Opus |
| `UserProfileRepository` | Moq mock | Opus |
| `LearningResourceRepository` | Moq mock | Opus |
| `SkillProfileRepository` | Moq mock | Opus |
| `VocabularyProgressRepository` | Moq mock | Opus |
| `TimeProvider` | `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` | Goldeneye |
| `ILogger<T>` | `NullLogger<T>.Instance` | Opus |

---

### Validation Approach

**Use Codex's `PlanValidator` class + Goldeneye's invariant tiering.**

```csharp
public static class PlanValidator
{
    public static PlanValidationResult Validate(PlanSkeleton plan, ValidationContext ctx)
    {
        var critical = new List<string>();
        var warnings = new List<string>();

        // CRITICAL invariants (must fail tests, block debug builds)
        if (plan.VocabularyReview?.DueWords?.Any(w => w.IsKnown) == true)
            critical.Add("V1: Known words found in review list");
        if (plan.VocabularyReview is { IsContextual: true, OverlapWordCount: < 3 })
            critical.Add("V2: Contextual review with <3 overlapping words");
        if ((plan.VocabularyReview?.WordCount ?? 0) > 20)
            critical.Add("V3: WordCount exceeds 20");
        if (plan.PrimaryResource != null && ctx.YesterdayResourceIds.Contains(plan.PrimaryResource.Id))
            critical.Add("V6: Yesterday's resource reused");

        // WARNING invariants (log but allow)
        if (plan.TotalMinutes != plan.Activities.Sum(a => a.EstimatedMinutes))
            warnings.Add("V8: TotalMinutes doesn't sum correctly");
        if (plan.Activities.Any(a => string.IsNullOrEmpty(a.Rationale)))
            warnings.Add("V10: Activity missing rationale");
        if (plan.TotalMinutes > (ctx.PreferredSessionMinutes + 3))
            warnings.Add("V15: Session time exceeded by >3 minutes");

        return new PlanValidationResult(critical, warnings);
    }
}

public record PlanValidationResult(List<string> CriticalViolations, List<string> Warnings)
{
    public bool IsValid => CriticalViolations.Count == 0;
}
```

Runtime guard in `BuildPlanAsync` (from Opus, enhanced with Goldeneye's tiering):
```csharp
#if DEBUG
var result = PlanValidator.Validate(plan, validationContext);
if (result.CriticalViolations.Any())
    _logger.LogError("PLAN VALIDATION FAILED: {Violations}",
        string.Join("; ", result.CriticalViolations));
if (result.Warnings.Any())
    _logger.LogWarning("Plan validation warnings: {Warnings}",
        string.Join("; ", result.Warnings));
#endif
```

**Critical invariants (10)**:
| # | Rule |
|---|------|
| V1 | No known words in review |
| V2 | Contextual review has ≥3 overlapping words |
| V3 | WordCount ≤ 20 |
| V4 | WordCount ≤ TotalDue |
| V5 | PrimaryResource exists in available resources |
| V6 | Resource not used yesterday |
| V9 | Activities ordered by sequential priority |
| V11 | VocabularyReview is priority 1 if present |
| V13 | OverlapWordCount ≤ actual ResourceVocabularyMapping count |
| V14 | No duplicate activity types (except closing VocabularyGame) |

**Warning invariants (6)**:
| # | Rule |
|---|------|
| V7 | Input/output activity types not repeated from yesterday |
| V8 | TotalMinutes sums correctly |
| V10 | Every activity has a rationale |
| V15 | Session time ≤ preference + 3 min |
| V17 | Narrative present |
| V18 | ResourceSelectionReason present when resource is set |

---

### Implementation Order

| Phase | Task | Files | Tests | Days |
|-------|------|-------|-------|------|
| **1** | Extract `IPlanDataProvider` interface + implementation | 2 new, 1 modified | 0 | 0.5 |
| **2** | Add `TimeProvider` injection, add `OverlapWordIds`/`OverlapWordCount` to `VocabularyReviewBlock` | 2 modified | 0 | 0.5 |
| **3** | Test data builders + `PlanTestBase` | 3 new | 0 | 0.5 |
| **4** | Bug 1+2 fixes (pre-selection overlap scoring + post-selection reconciliation) | 1 modified | 0 | 0.5 |
| **5** | `ResourceSelectionTests.cs` | 1 new | 18 | 0.5 |
| **6** | Bug 3+5 fixes (graduated scoring, 30-day window, usage penalty, deterministic tiebreaker) | 1 modified | 0 | 0.5 |
| **7** | `VocabularyReviewTests.cs` | 1 new | 15 | 0.5 |
| **8** | Bug 4 fix (quiz mode + PlanConverter) | 2 modified | 0 | 0.5 |
| **9** | `PlanBuilderIntegrationTests.cs` | 1 new | 15 | 0.5 |
| **10** | `PlanValidator` + `PlanValidatorTests.cs` | 2 new | 12 | 0.5 |
| **11** | `ActivitySequencingTests.cs` + `PlanConverterTests.cs` | 2 new | 18 | 0.5 |
| **12** | Fix `VocabularyProgressServiceTests.cs` `Compile Remove` | 1 modified | ~7 existing | 0.5 |
| **TOTAL** | | ~13 files (8 new, 5 modified) | **~78 tests** | **~5 days** |

**Critical path**: Phases 1-3 must complete before any tests can be written. Phase 4 (Bug 1+2) has the highest user impact and should be prioritized after infrastructure.

---

### Key Tests (Selected Highlights)

#### Must-have regression tests (one per bug):

| Bug | Test | Assert |
|-----|------|--------|
| 1 | `ResourceSelection_VocabOverlap_DominatesRecency` | Resource with 15 overlapping due words beats unused resource with 0 overlap |
| 2 | `VocabReview_OverlapWordCount_MatchesActualResourceMapping` | `OverlapWordCount` ≤ resource's `ResourceVocabularyMapping` count |
| 2 | `VocabReview_LowOverlap_FallsBackToGeneral` | 10 due words, resource has 2 → `IsContextual = false` |
| 3 | `ResourceSelection_20DayOld_Beats5DayOld` | Graduated scoring differentiates 5-day from 20-day |
| 3 | `ResourceSelection_DeterministicTiebreaker_Reproducible` | Same date + resources → same selection |
| 4 | `QuizMode_NullProgress_DefaultsToMC_WithWarning` | `Progress = null` → MultipleChoice (not crash) |
| 4 | `QuizMode_HighMastery_ForcesTextMode` | `MasteryScore = 0.60` → Text mode |
| 5 | `ResourceSelection_7Days_AtLeast4UniqueResources` | Simulate 7 plans → ≥4 distinct resources |

#### Integration test (the "golden test"):

```
BuildPlan_HappyPath_Full30MinSession:
  Setup: 15 due words (10 linked to ResA, 5 to ResB), 5 resources, 30-min pref
  Assert:
    - PrimaryResource has highest vocab overlap
    - VocabReview.OverlapWordCount ≥ 3
    - 3-4 activities, ordered by priority
    - TotalMinutes ≈ 30
    - PlanValidator.Validate() returns IsValid = true
```

---

## 5. Risk Assessment

### Highest Risk: `IPlanDataProvider` Refactor Scope

The extraction of 6 DB calls from `DeterministicPlanBuilder` into `IPlanDataProvider` touches the most critical code path. If any query behavior changes subtly during extraction (e.g., different `Include()` chains, different filtering), it could introduce new bugs while fixing old ones.

**Mitigation**: Write a single "golden snapshot" integration test BEFORE the refactor that captures the current `BuildPlanAsync` output for a fixed dataset. Run it after extraction to confirm identical output.

### Second Risk: Pre-Selection Overlap Performance

Computing vocab overlap for every candidate resource during scoring means N additional DB queries (one per resource). With 10 resources this is fine; with 50+ resources this could be slow.

**Mitigation**: Batch the query — fetch all `ResourceVocabularyMapping` entries for the due word IDs in a single query, then compute overlaps in-memory.

### Third Risk: `TimeProvider` Availability

The project targets `net10.0`. `TimeProvider` requires `Microsoft.Extensions.TimeProvider.Testing` NuGet for test doubles. Verify it's compatible with the existing test project's TFM.

**Mitigation**: Check package compatibility before starting Phase 2. Fallback: use a simple `ITimeProvider` interface if the NuGet has issues.

### Fourth Risk: `Compile Remove` Re-enablement

`VocabularyProgressServiceTests.cs` was excluded for a reason (dependency on main SentenceStudio project which doesn't target net10.0). Re-enabling it may require restructuring test project references.

**Mitigation**: Make this Phase 12 (last). If it's blocked by TFM issues, document the reason and move on.

---

## Appendix: What Was Deliberately Excluded

| Idea | Source | Reason for Exclusion |
|------|--------|---------------------|
| Greedy set-cover algorithm for multi-resource | Codex | Over-engineered for 5-10 resources. Add later if resource count grows. |
| `PlanBuildContext` single-pass object | Goldeneye | Good architecture but too large a refactor for this sprint. `IPlanDataProvider` + pre-selection overlap achieves 80% of the benefit. |
| `VocabularyProgress.RecommendedReviewInputMode` computed property | Goldeneye | Violates model/service separation. Keep mode logic in the quiz page. |
| `IRandomProvider` custom interface | Codex | Deterministic hash tiebreaker eliminates the need for injectable randomness. |
| 125 tests | Opus | Trimmed to 78. The cut tests are near-duplicates or edge cases that can be added incrementally. |
