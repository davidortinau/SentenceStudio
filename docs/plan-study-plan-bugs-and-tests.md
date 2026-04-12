# Plan: Study Plan Generation Bug Fixes & Test Suite

**GitHub Issue**: #161 — "Study plan includes known words incorrectly"
**Date**: 2026-07-09
**Scope**: `DeterministicPlanBuilder`, `VocabularyProgressRepository`, `PlanConverter`, `VocabQuiz.razor`

---

## Table of Contents

- [A. Bug Fix Plan (5 bugs)](#a-bug-fix-plan)
- [B. Test Suite Design](#b-test-suite-design)
- [C. Plan Validation Invariants](#c-plan-validation-invariants)
- [D. Test Infrastructure](#d-test-infrastructure)
- [E. Scope Estimate](#e-scope-estimate)

---

## A. Bug Fix Plan

### Bug 1: Resource–Vocabulary Misalignment (THE CORE BUG)

**Root Cause**: `DetermineVocabularyReviewAsync()` and `SelectPrimaryResourceAsync()` make independent decisions. Vocab is selected by SRS due dates, resource is selected by recency/scoring. Result: the plan says "Review 20 words from Resource X" but Resource X may contain only 3 of those words.

The flow today:
1. `DetermineVocabularyReviewAsync` → picks due words, groups by resource, sets `VocabularyReviewBlock.ResourceId` = resource with most overlap
2. `SelectPrimaryResourceAsync(vocabResourceId)` → scores all resources, gives +75 bonus to vocabResourceId, but **another resource can still outscore it**

The +75 "contextual" bonus is too weak. A never-used resource gets +100 (freshness) + log(vocabCount)*5, easily beating +75. So the selected primary resource often differs from the vocab resource.

**Fix** (file: `DeterministicPlanBuilder.cs`):

1. **Strengthen coupling in `SelectPrimaryResourceAsync`** (lines 291-293):
   - Increase the `IsVocabResource` bonus from +75 to +200 (make it decisive unless the resource was used yesterday)
   - Add a new scoring factor: **vocab overlap score**. For each candidate, count how many of the due words (from `VocabularyReviewBlock.DueWords`) have a `LearningContext` linking to that resource. Score: `overlapCount * 15`. This rewards ANY resource that shares due vocabulary, not just the exact vocab-resource.

2. **Pass `DueWords` into `SelectPrimaryResourceAsync`** — Change the signature from:
   ```csharp
   private async Task<SelectedResource?> SelectPrimaryResourceAsync(
       DateTime today, string? vocabResourceId, CancellationToken ct)
   ```
   to:
   ```csharp
   private async Task<SelectedResource?> SelectPrimaryResourceAsync(
       DateTime today, VocabularyReviewBlock? vocabReview, CancellationToken ct)
   ```
   This passes the full vocab block, allowing overlap calculation.

3. **Reconcile VocabularyReviewBlock.WordCount after resource selection** in `BuildPlanAsync()` (after line 65):
   After primary resource is selected, recompute `vocabReview.WordCount` to only count due words that overlap with the primary resource's vocabulary. Add `vocabReview.OverlapWordCount` as a new property that reflects the actual count. The activity rationale should use `OverlapWordCount` when contextual.

4. **Add `OverlapWordIds` to `VocabularyReviewBlock`** (new property):
   ```csharp
   public List<string> OverlapWordIds { get; set; } = new();
   ```
   Populated after resource selection with the intersection of due word IDs and ResourceVocabularyMapping word IDs for the selected resource.

**Verification**: After fix, write a test that creates due words linked to Resource A, but Resource B has higher recency score. Assert the plan's selected resource is Resource A (because overlap bonus dominates), and `WordCount` matches the actual overlap.

---

### Bug 2: VocabReview WordCount Mismatch

**Root Cause**: `VocabularyReviewBlock.WordCount` is set to `Math.Min(20, dueWords.Count)` (line 152) which counts ALL due words, not words in the selected resource. The plan then says "Review 20 words" but the quiz may only find 6 relevant words.

**Fix** (file: `DeterministicPlanBuilder.cs`, method `BuildPlanAsync`, after resource selection):

Add a reconciliation step between lines 113-114 (after `BuildActivitySequenceAsync` returns):
```csharp
// Reconcile vocab review word count with selected resource
if (vocabReview != null && primaryResource != null)
{
    var resourceWordIds = await GetResourceVocabularyWordIdsAsync(primaryResource.Id, ct);
    var dueWordIds = vocabReview.DueWords.Select(w => w.VocabularyWordId).ToHashSet();
    var overlapIds = dueWordIds.Intersect(resourceWordIds).ToList();

    vocabReview.OverlapWordIds = overlapIds;
    vocabReview.OverlapWordCount = overlapIds.Count;

    // If contextual and overlap is too low, fall back to general review
    if (vocabReview.IsContextual && overlapIds.Count < 3)
    {
        vocabReview.IsContextual = false;
        vocabReview.ResourceId = null;
        _logger.LogInformation("⚠️ Low vocab overlap ({Count}) — falling back to general review",
            overlapIds.Count);
    }
}
```

Add a new private helper:
```csharp
private async Task<HashSet<string>> GetResourceVocabularyWordIdsAsync(
    string resourceId, CancellationToken ct)
{
    using var scope = _serviceProvider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var ids = await db.ResourceVocabularyMappings
        .Where(rvm => rvm.ResourceId == resourceId)
        .Select(rvm => rvm.VocabularyWordId)
        .ToListAsync(ct);
    return ids.ToHashSet();
}
```

Also update `VocabularyReviewBlock` to add:
```csharp
public int OverlapWordCount { get; set; }
public List<string> OverlapWordIds { get; set; } = new();
```

**Verification**: Test that `VocabularyReviewBlock.OverlapWordCount` never exceeds the actual number of due words in the selected resource's vocabulary mappings.

---

### Bug 3: Recency Bias in Resource Selection

**Root Cause**: The 14-day `DailyPlanCompletions` window (line 225) means any resource used >14 days ago gets `DaysSinceLastUse = 999`, identical to never-used. With only ~5-10 resources, this creates a small rotation of 3-4 "fresh" resources repeatedly selected.

Additionally, the scoring curve is discontinuous:
- Yesterday: -1000 (hard block)
- 0 days ago (today): 0 points
- 2 days: +50
- 5+ days: +100
- 999 (never or >14 days): +100

So a resource used 5 days ago gets the same score (+100) as one used 14+ days ago. There's no incentive to pick the truly least-recently-used.

**Fix** (file: `DeterministicPlanBuilder.cs`, method `SelectPrimaryResourceAsync`):

1. **Extend lookup window to 30 days** (line 225):
   ```csharp
   .Where(c => c.Date >= today.AddDays(-30) && ...)
   ```

2. **Add graduated scoring for DaysSinceLastUse** (replace lines 283-289):
   ```csharp
   // RULE 2: Graduated freshness bonus (logarithmic, not step function)
   if (candidate.DaysSinceLastUse >= 2)
       score += 50 + Math.Min(100, Math.Log(candidate.DaysSinceLastUse) * 25);
   else
       score += candidate.DaysSinceLastUse * 10;
   ```
   This gives: 2 days = +67, 5 days = +90, 10 days = +108, 30 days = +135, never = +222.
   Never-used resources always win over merely "fresh" ones.

3. **Replace `Guid.NewGuid()` tiebreaker with deterministic shuffle** (line 309):
   Use a seeded random based on today's date so results are reproducible for debugging:
   ```csharp
   var rng = new Random(today.GetHashCode());
   // ...
   .ThenBy(c => rng.Next())
   ```

**Verification**: Test with 6 resources: 3 used 3 days ago, 3 used 15 days ago. Assert one of the 15-day resources is selected. Run the same test 10 times — verify variety across the 3 old resources, not just the first one alphabetically.

---

### Bug 4: Known Words Shown in Recognition Mode (Quiz Page Bug)

**Root Cause**: This is actually TWO sub-issues:

**4a. Near-known words appearing in review**: A word with `MasteryScore = 0.90, ProductionInStreak = 1` is NOT `IsKnown` (needs `ProductionInStreak >= 2`), so `GetDueVocabularyAsync` correctly returns it. This is **by design** — the word needs more production practice. The user perceives it as "known" because they can recognize it, but the system correctly demands production evidence. **No code change needed**, but the UI should explain WHY the word is being reviewed.

**4b. Quiz mode not respecting IsPromoted for SRS words**: In `VocabQuiz.razor` line 610-611, the mode check is:
```csharp
userMode = (currentItem.IsPromotedInQuiz || (currentItem.Progress?.MasteryScore ?? 0f) >= 0.50f)
    ? "Text" : "MultipleChoice";
```

This DOES check global mastery. So words with MasteryScore >= 0.50 DO get Text mode. **The existing logic appears correct.** However, there may be a timing issue: if `Progress` is null (not loaded), it falls back to `0f >= 0.50f = false`, defaulting to MultipleChoice.

**Fix** (file: `src/SentenceStudio.UI/Pages/VocabQuiz.razor`, method `LoadVocabulary` around lines 481-556):

1. Add a guard that ensures `Progress` is loaded for every quiz item before the quiz starts:
   ```csharp
   // After creating VocabularyQuizItem list:
   var itemsWithoutProgress = quizItems.Where(qi => qi.Progress == null).ToList();
   if (itemsWithoutProgress.Any())
   {
       _logger.LogWarning("⚠️ {Count} quiz items have no progress loaded — they'll default to MultipleChoice",
           itemsWithoutProgress.Count);
   }
   ```

2. Add metadata to `VocabularyReviewBlock` so PlanConverter can pass the expected mode:
   In `PlanConverter.cs`, method `BuildRouteParameters` (line 123-129), add:
   ```csharp
   parameters["ForceProductionForPromoted"] = true;
   ```
   Then in `VocabQuiz.razor`, read this parameter and use it to ensure promoted words start in Text mode from the first encounter, not after 3 MC correct.

**Verification**: Create a test word with MasteryScore=0.60, ProductionInStreak=0. Load it in quiz. Assert `userMode == "Text"`. Then test with MasteryScore=0.30 → assert `userMode == "MultipleChoice"`.

---

### Bug 5: Same Resource Chosen Repeatedly

**Root Cause**: Combination of Bug 3 (recency bias) and the Guid.NewGuid() tiebreaker producing non-deterministic but non-uniform distribution. With a small resource pool (5-8 resources), the scoring function produces many ties at +100, and the GUID tiebreaker doesn't guarantee uniform distribution.

**Fix**: Addressed primarily by Bug 3 fixes (graduated scoring, extended window). Additionally:

1. **Add usage count penalty** in `SelectPrimaryResourceAsync` (new scoring rule after line 299):
   ```csharp
   // RULE 6: Penalize frequently used resources (round-robin encouragement)
   var usageCountLast14Days = recentActivity.Count(a => a.ResourceId == candidate.Resource.Id);
   score -= usageCountLast14Days * 8;
   ```
   A resource used 3 times in 14 days gets -24, one used once gets -8, never used gets 0.

2. **Log resource scores** for debugging (new, after scoring loop, before selection):
   ```csharp
   foreach (var c in candidates.Where(c => c.Score > -500).OrderByDescending(c => c.Score).Take(5))
   {
       _logger.LogDebug("📊 Resource {Title}: score={Score:F1}, lastUsed={Days}d ago, vocabCount={Vocab}, isVocabResource={IsVocab}",
           c.Resource.Title, c.Score, c.DaysSinceLastUse, c.VocabCount, c.IsVocabResource);
   }
   ```

**Verification**: Create 5 resources, simulate 7 days of completions. Generate 7 more plans. Assert that at least 4 of 5 resources are selected across the 7 plans (no resource selected more than 3 times in 7 days).

---

## B. Test Suite Design

### File Structure

```
tests/SentenceStudio.UnitTests/
├── Services/
│   └── PlanGeneration/
│       ├── DeterministicPlanBuilderTests.cs          (~35 tests)
│       ├── ResourceSelectionTests.cs                  (~25 tests)
│       ├── VocabularyReviewDeterminationTests.cs     (~20 tests)
│       ├── ActivitySequencingTests.cs                 (~15 tests)
│       ├── PlanConverterTests.cs                      (~12 tests)
│       ├── PlanValidationTests.cs                     (~18 tests)
│       └── Builders/
│           ├── TestPlanDataBuilder.cs                 (test data factory)
│           ├── VocabularyProgressBuilder.cs           (fluent builder)
│           ├── LearningResourceBuilder.cs             (fluent builder)
│           └── MockRepositoryFactory.cs               (mock setup helpers)
```

**Total estimated tests: ~125**

---

### B.1 Test Data Builders

#### `VocabularyProgressBuilder.cs`
```csharp
public class VocabularyProgressBuilder
{
    private VocabularyProgress _progress = new();
    private List<VocabularyLearningContext> _contexts = new();

    public static VocabularyProgressBuilder Create() => new();

    public VocabularyProgressBuilder WithWordId(string wordId)
    { _progress.VocabularyWordId = wordId; return this; }

    public VocabularyProgressBuilder WithMastery(float score)
    { _progress.MasteryScore = score; return this; }

    public VocabularyProgressBuilder WithProductionInStreak(int count)
    { _progress.ProductionInStreak = count; return this; }

    public VocabularyProgressBuilder DueOn(DateTime date)
    { _progress.NextReviewDate = date; return this; }

    public VocabularyProgressBuilder AsKnown()
    { _progress.MasteryScore = 0.90f; _progress.ProductionInStreak = 3; return this; }

    public VocabularyProgressBuilder AsPromoted()
    { _progress.MasteryScore = 0.60f; _progress.ProductionInStreak = 0; return this; }

    public VocabularyProgressBuilder AsLearning()
    { _progress.MasteryScore = 0.30f; return this; }

    public VocabularyProgressBuilder AsNew()
    { _progress.MasteryScore = 0f; _progress.TotalAttempts = 0; return this; }

    public VocabularyProgressBuilder LinkedToResource(string resourceId)
    {
        _contexts.Add(new VocabularyLearningContext
        { LearningResourceId = resourceId });
        return this;
    }

    public VocabularyProgressBuilder WithWord(VocabularyWord word)
    { _progress.VocabularyWord = word; return this; }

    public VocabularyProgress Build()
    {
        _progress.LearningContexts = _contexts;
        return _progress;
    }
}
```

#### `LearningResourceBuilder.cs`
```csharp
public class LearningResourceBuilder
{
    private LearningResource _resource = new()
    {
        Id = Guid.NewGuid().ToString(),
        Title = "Test Resource",
        MediaType = "Video",
        Language = "Korean"
    };

    public static LearningResourceBuilder Create() => new();

    public LearningResourceBuilder WithId(string id)
    { _resource.Id = id; return this; }

    public LearningResourceBuilder WithTitle(string title)
    { _resource.Title = title; return this; }

    public LearningResourceBuilder AsVideo(string youtubeUrl = "https://youtube.com/watch?v=test")
    { _resource.MediaType = "Video"; _resource.MediaUrl = youtubeUrl; return this; }

    public LearningResourceBuilder AsPodcast()
    { _resource.MediaType = "Podcast"; return this; }

    public LearningResourceBuilder AsArticle()
    { _resource.MediaType = "Article"; return this; }

    public LearningResourceBuilder WithTranscript(string transcript = "test transcript content")
    { _resource.Transcript = transcript; return this; }

    public LearningResourceBuilder WithNoTranscript()
    { _resource.Transcript = null; return this; }

    public LearningResource Build() => _resource;
}
```

#### `MockRepositoryFactory.cs`

This factory creates pre-configured Moq mocks. The key challenge is that `DeterministicPlanBuilder` accesses `ApplicationDbContext` directly through `IServiceProvider.CreateScope()`. This means we need to either:

**Option A (Recommended): Extract DB queries into injectable interfaces**

Create a new interface:
```csharp
// File: src/SentenceStudio.Shared/Services/PlanGeneration/IPlanDataProvider.cs
public interface IPlanDataProvider
{
    Task<List<DailyPlanCompletion>> GetRecentCompletionsAsync(DateTime since, CancellationToken ct);
    Task<Dictionary<string, int>> GetResourceVocabularyCountsAsync(CancellationToken ct);
    Task<HashSet<string>> GetResourceVocabularyWordIdsAsync(string resourceId, CancellationToken ct);
    Task<string?> GetRecentSkillForResourceAsync(string resourceId, CancellationToken ct);
    Task<string?> GetMostRecentSkillAsync(CancellationToken ct);
}
```

Then refactor `DeterministicPlanBuilder` to inject `IPlanDataProvider` instead of using `IServiceProvider.CreateScope()` + `ApplicationDbContext` directly. This makes the class fully testable with mocks.

**Option B (If refactor is deferred): Mock IServiceProvider chain**

Create a helper that sets up the `IServiceProvider → IServiceScope → ApplicationDbContext` mock chain with an in-memory `DbContext`:
```csharp
public static class MockServiceProviderFactory
{
    public static (Mock<IServiceProvider>, ApplicationDbContext) CreateWithInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider.GetRequiredService<ApplicationDbContext>())
            .Returns(db);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactoryMock.Object);

        return (providerMock, db);
    }
}
```

**Recommendation**: Implement Option A first. The refactor is surgical (extract ~6 DB calls to an interface + implementation class) and dramatically simplifies all tests. Keep the existing `DeterministicPlanBuilder` constructor accepting both old and new dependencies for backward compatibility during migration.

---

### B.2 Unit Tests: Resource Selection (`ResourceSelectionTests.cs`)

All tests target `SelectPrimaryResourceAsync` logic. Use extracted interface or mocked DB.

| # | Test Name | Scenario | Expected |
|---|-----------|----------|----------|
| 1 | `SelectResource_NeverUsedResource_ScoredHigherThanRecentlyUsed` | Res A: used 3 days ago. Res B: never used. | B selected |
| 2 | `SelectResource_YesterdayResource_NeverSelected` | Res A: used yesterday. No other resources. | Returns null |
| 3 | `SelectResource_YesterdayResource_SkippedWhenOthersAvailable` | Res A: yesterday. Res B: 3 days ago. | B selected |
| 4 | `SelectResource_VocabOverlapBonus_PreferResourceWithDueWords` | Res A: 0 overlap, 10 days fresh. Res B: 15 overlap words, 3 days ago. | B selected (overlap wins) |
| 5 | `SelectResource_FreshnessBonus_GraduatedNotStepFunction` | Res A: 5 days. Res B: 20 days. Same vocab. | B scores higher than A |
| 6 | `SelectResource_AudioBonus_PreferVideoOverArticle` | Res A: Article, 5 days. Res B: Video, 5 days. | B (has audio bonus) |
| 7 | `SelectResource_UsageCountPenalty_FrequentlyUsedPenalized` | Res A: used 5 times in 14 days. Res B: used 1 time. Same freshness. | B selected |
| 8 | `SelectResource_AllUsedYesterday_ReturnsNull` | All resources used yesterday. | null |
| 9 | `SelectResource_EmptyResourceList_ReturnsNull` | No resources in DB. | null |
| 10 | `SelectResource_OtherMediaType_Excluded` | Res A: MediaType="Other". Res B: MediaType="Video". | B selected, A excluded |
| 11 | `SelectResource_14DayWindowExpanded_OldResourcesStillTracked` | Res A: used 20 days ago. Res B: used 3 days ago. | A selected (not treated as "never used" with 30-day window) |
| 12 | `SelectResource_TiedScores_ProduceVariety` | 3 resources with identical scores. Run 20 times. | At least 2 different resources selected |
| 13 | `SelectResource_VocabCountBonus_LogarithmicScale` | Res A: 5 words. Res B: 500 words. Same freshness. | B scores higher, but not 100x higher |
| 14 | `SelectResource_SingleResource_NotUsedYesterday_Selected` | 1 resource, used 2 days ago. | That resource selected |
| 15 | `SelectResource_SingleResource_UsedYesterday_ReturnsNull` | 1 resource, used yesterday. | null |
| 16 | `SelectResource_VocabResourceBonus_StrongerThanFreshness` | Vocab resource used 2 days ago. Non-vocab resource unused for 10 days. | Vocab resource wins with +200 bonus |
| 17 | `SelectResource_NullVocabReview_NoOverlapScoring` | null vocabReview passed. | Selection works without crash, uses freshness only |
| 18 | `SelectResource_ResourceWithNoId_Filtered` | Resource with Id = "". | Excluded from candidates |
| 19 | `SelectResource_ResourceWithNoTitle_Filtered` | Resource with Title = null. | Excluded from candidates |
| 20 | `SelectResource_SelectionReason_AccuratelyDescribesChoice` | Various scenarios. | SelectionReason string matches actual reason |
| 21 | `SelectResource_HasTranscript_SetCorrectly` | Video with transcript. Article without. | HasTranscript reflects reality |
| 22 | `SelectResource_YouTubeUrl_OnlySetForVideos` | Video with MediaUrl, Podcast with MediaUrl. | YouTubeUrl only non-null for Video |
| 23 | `SelectResource_DaysSinceLastUse_CorrectForNewlyUsed` | Resource used today. | DaysSinceLastUse = 0 |
| 24 | `SelectResource_DaysSinceLastUse_CorrectFor30PlusDays` | Resource used 40 days ago (outside new 30-day window). | DaysSinceLastUse = 999 |
| 25 | `SelectResource_MultipleFreshResources_PreferHigherVocabCount` | 3 resources all 10+ days old. A: 10 words, B: 50, C: 100. | C selected (vocab count tiebreaker via log scale) |

---

### B.3 Unit Tests: Vocabulary Review Determination (`VocabularyReviewDeterminationTests.cs`)

All tests target `DetermineVocabularyReviewAsync` logic.

| # | Test Name | Scenario | Expected |
|---|-----------|----------|----------|
| 1 | `VocabReview_FewerThan5DueWords_ReturnsNull` | 3 due words. | null (skip review) |
| 2 | `VocabReview_Exactly5DueWords_ReturnsReview` | 5 due words. | VocabularyReviewBlock with WordCount=5 |
| 3 | `VocabReview_20PlusDueWords_CappedAt20` | 30 due words. | WordCount=20, TotalDue=30 |
| 4 | `VocabReview_WordsGroupedByResource_BestResourceChosen` | 8 words linked to ResA, 3 to ResB. | ResourceId = ResA |
| 5 | `VocabReview_NoResourceGroupWith5Plus_GeneralReview` | 3 words→ResA, 3→ResB, 3→ResC. | ResourceId = null, IsContextual = false |
| 6 | `VocabReview_WordsWithNoLearningContext_HandledGracefully` | 10 due words, none linked to resources. | ResourceId = null |
| 7 | `VocabReview_EstimatedMinutes_CalculatedFromWordCount` | 14 due words. | EstimatedMinutes = ceiling(14/3.5) = 4 |
| 8 | `VocabReview_KnownWordsExcluded_ByRepository` | Words with MasteryScore≥0.85 AND ProductionInStreak≥2. | Not in due list |
| 9 | `VocabReview_NearKnownWordsIncluded` | Word: MasteryScore=0.90, ProductionInStreak=1. | IS in due list (needs production) |
| 10 | `VocabReview_ContextualReview_SetsSkillId` | Words linked to ResA. ResA has skill in completions. | SkillId = that skill |
| 11 | `VocabReview_NoDueWords_ReturnsNull` | 0 due words. | null |
| 12 | `VocabReview_DueWords_ProperlyStoredInBlock` | 10 due words. | DueWords list has all 10 VocabularyProgress objects |
| 13 | `VocabReview_WordsWithMultipleContexts_CountedOnce` | Word linked to ResA AND ResB. | Word counted once per resource group, not duplicated |
| 14 | `VocabReview_ContextualReview_SkillIdNull_WhenNoCompletions` | Resource has no DailyPlanCompletions. | SkillId = null |
| 15 | `VocabReview_NewWordsIncluded_WhenDue` | Word with MasteryScore=0, NextReviewDate=today. | Included |
| 16 | `VocabReview_FutureReviewDate_Excluded` | Word with NextReviewDate = tomorrow. | Excluded |
| 17 | `VocabReview_NullNextReviewDate_Behavior` | Word with NextReviewDate = null. | Check: is it included or excluded by repo? |
| 18 | `VocabReview_OverlapWordCount_SetAfterReconciliation` | 10 due words, resource has 7 of them. | OverlapWordCount = 7 |
| 19 | `VocabReview_LowOverlap_FallsBackToGeneral` | 10 due words, resource has 2 of them. | IsContextual flips to false |
| 20 | `VocabReview_HighOverlap_StaysContextual` | 10 due words, resource has 8 of them. | IsContextual = true, OverlapWordCount = 8 |

---

### B.4 Unit Tests: Activity Sequencing (`ActivitySequencingTests.cs`)

| # | Test Name | Scenario | Expected |
|---|-----------|----------|----------|
| 1 | `Activities_VocabReviewFirst_WhenPresent` | Vocab review + input + output activities. | First activity is VocabularyReview |
| 2 | `Activities_NoVocabReview_StartsWithInput` | No vocab review. | First activity is input type |
| 3 | `Activities_InputBeforeOutput_CognitiveLoadProgression` | Full session. | Input activity Priority < Output activity Priority |
| 4 | `Activities_VocabGameCloser_AddedWhenTimeRemains` | 30 min session, 15 min used. | VocabularyGame added as closer |
| 5 | `Activities_ShortSession_OnlyVocabReview` | 5 min session, 10 words due. | Only VocabularyReview, no input/output |
| 6 | `Activities_YesterdayActivities_Avoided` | Yesterday: Reading + Translation. | Today: NOT Reading, NOT Translation |
| 7 | `Activities_AllActivitiesUsedYesterday_FallsBackGracefully` | All input types used yesterday. | Selects from available (no crash) |
| 8 | `Activities_LeastRecentlyUsed_Preferred` | Reading used 5 times recently, Listening 1 time. | Listening selected over Reading |
| 9 | `Activities_VideoWatching_RequiresYouTubeUrl` | Resource: Video, no MediaUrl. | VideoWatching NOT in candidates |
| 10 | `Activities_Reading_RequiresTranscript` | Resource: no transcript. | Reading NOT in candidates |
| 11 | `Activities_Shadowing_RequiresAudio` | Resource: Article (no audio). | Shadowing NOT in candidates |
| 12 | `Activities_NoCompatibleInput_SkipsInputStep` | Resource: no transcript, no audio, no YouTube. | Input step skipped entirely |
| 13 | `Activities_EstimatedMinutes_NeverExceedSession` | 15 min session. | Sum of all activities ≤ 15 |
| 14 | `Activities_Priority_IsSequential` | Full plan. | Priorities are 1, 2, 3, ... |
| 15 | `Activities_Rationale_SetForEveryActivity` | Full plan. | No activity has null/empty Rationale |

---

### B.5 Integration Tests: Full Plan Generation (`DeterministicPlanBuilderTests.cs`)

These test `BuildPlanAsync()` end-to-end with realistic data.

| # | Test Name | Scenario | Expected |
|---|-----------|----------|----------|
| 1 | `BuildPlan_NoUserProfile_ReturnsNull` | No user profile in DB. | null |
| 2 | `BuildPlan_NoResources_NoVocab_ReturnsNull` | User exists, no resources, no due words. | null |
| 3 | `BuildPlan_NoResources_WithVocab_ReturnsFallback` | No resources, 10 due words. | Vocab-only plan |
| 4 | `BuildPlan_HappyPath_Full30MinSession` | 10 due words, 5 resources, 30 min pref. | Complete plan with 3-4 activities |
| 5 | `BuildPlan_VocabOnly_ShortSession` | 10 due words, resources, 8 min session. | Vocab review only |
| 6 | `BuildPlan_NoVocabDue_SkipsReview` | 0 due words. | No VocabularyReview activity |
| 7 | `BuildPlan_ResourceMatchesVocab_Coherent` | 15 due words linked to ResA. | PrimaryResource IS ResA (or has high overlap) |
| 8 | `BuildPlan_NarrativeGenerated_NotEmpty` | Full plan. | Narrative.Story is non-empty string |
| 9 | `BuildPlan_TotalMinutes_SumsCorrectly` | Full plan. | TotalMinutes == sum of activity EstimatedMinutes |
| 10 | `BuildPlan_AllActivities_HaveResourceOrSkillId` | Full plan. | Every activity has ResourceId OR SkillId set |
| 11 | `BuildPlan_ResourceSelectionReason_Populated` | Full plan. | ResourceSelectionReason is not null/empty |
| 12 | `BuildPlan_ConsecutiveDays_NeverSameResource` | Generate plan Day1, record completion, generate Day2. | Day2 resource ≠ Day1 resource |
| 13 | `BuildPlan_ConsecutiveDays_NeverSameActivityTypes` | Generate Day1 + Day2. | Day2 input activity ≠ Day1 input activity |
| 14 | `BuildPlan_FallbackPlan_HasNarrative` | Vocab-only fallback. | Narrative still present |
| 15 | `BuildPlan_AllSkillsAvailable_SkillSelected` | Multiple skills in DB. | PrimarySkill is not null |
| 16 | `BuildPlan_NoSkills_PlanStillGenerated` | No skills. | Plan generated (skill = null), no crash |
| 17 | `BuildPlan_MassiveVocabLoad_CappedAt20` | 200 due words. | VocabularyReview.WordCount ≤ 20 |
| 18 | `BuildPlan_SingleResourceUsedToday_StillSelected` | 1 resource, used today. | Selected (only blocked if yesterday, per rules) |

Plus 17 more regression tests below.

---

### B.6 Regression Tests (per bug)

| # | Test | Bug | Description |
|---|------|-----|-------------|
| R1 | `Regression_Bug1_ResourceVocabAligned` | 1 | Due words linked to ResA. Assert selected resource = ResA OR overlap is logged. |
| R2 | `Regression_Bug1_OverlapBonusDominates` | 1 | Vocab resource 2 days old beats non-vocab 10 days old. |
| R3 | `Regression_Bug2_WordCountMatchesOverlap` | 2 | VocabReview.OverlapWordCount ≤ resource's actual word count. |
| R4 | `Regression_Bug2_LowOverlapFallsBackToGeneral` | 2 | 10 due words, resource has 1. IsContextual=false. |
| R5 | `Regression_Bug3_NeverUsedBeats5DayOld` | 3 | Never-used resource scores higher than 5-day-old. |
| R6 | `Regression_Bug3_20DayOldBeats5DayOld` | 3 | 20-day resource scores higher than 5-day. |
| R7 | `Regression_Bug3_FrequentUsePenalized` | 3 | Resource used 4 times in 14 days penalized vs 1-time use. |
| R8 | `Regression_Bug4a_NearKnownInTextMode` | 4 | MasteryScore=0.90, ProductionInStreak=1 → quiz shows Text mode. |
| R9 | `Regression_Bug4b_PromotedWordInTextMode` | 4 | MasteryScore=0.60 → Text mode in quiz. |
| R10 | `Regression_Bug4b_LowMasteryInMCMode` | 4 | MasteryScore=0.30 → MultipleChoice mode. |
| R11 | `Regression_Bug4b_NullProgressDefaultsMC` | 4 | Progress = null → MultipleChoice (logged). |
| R12 | `Regression_Bug5_7DaysUses4PlusResources` | 5 | Simulate 7 daily plans, assert ≥ 4 unique resources. |
| R13 | `Regression_Bug5_DeterministicSameDay` | 5 | Same date seed → same plan (reproducible). |
| R14 | `Regression_Bug2_VocabCountNeverExceedsResourceWords` | 2 | WordCount in activity ≤ resource's actual mapped word count. |
| R15 | `Regression_Bug1_IndependentSelectionsFixed` | 1 | Vocab from ResA, resource selection WITHOUT overlap bonus → might pick ResB (demonstrates old bug). WITH fix → picks ResA. |
| R16 | `Regression_NarrativeIncludesVocabOverlapInfo` | 1,2 | Narrative mentions how many vocab overlap with resource. |
| R17 | `Regression_SelectionReasonExplainsVocabMatch` | 1 | SelectionReason includes "Matches X vocabulary words" when applicable. |

---

### B.7 PlanConverter Tests (`PlanConverterTests.cs`)

| # | Test Name | Description |
|---|-----------|-------------|
| 1 | `Convert_VocabularyReview_RoutesToVocabQuiz` | Activity "VocabularyReview" → route "/vocabulary-quiz" |
| 2 | `Convert_VocabularyReview_HasSRSMode` | Route params include Mode=SRS, DueOnly=true |
| 3 | `Convert_VocabularyReview_IncludesResourceId` | ResourceId passed when present |
| 4 | `Convert_Reading_RoutesToReadingPage` | "Reading" → "/reading" |
| 5 | `Convert_UnknownActivityType_ThrowsArgException` | "InvalidType" → ArgumentException |
| 6 | `Convert_PlanItemId_Deterministic` | Same inputs → same ID every time |
| 7 | `Convert_PlanItemId_DifferentDates_DifferentIds` | Different date → different ID |
| 8 | `Convert_VocabDueCount_UsedWhenAvailable` | Activity has VocabWordCount=15 → VocabDueCount=15 |
| 9 | `Convert_VocabDueCount_FallbackToTotal` | Activity has null VocabWordCount → uses vocabDueCount param |
| 10 | `Convert_AllActivityTypes_HaveRoutes` | Loop all PlanActivityType enum values → no exceptions |
| 11 | `Convert_Priority_PreservedFromInput` | Activities ordered by Priority in output |
| 12 | `Convert_ForceProductionForPromoted_Param` | VocabularyReview params include ForceProductionForPromoted=true (new) |

---

### B.8 Plan Validation Invariant Tests (`PlanValidationTests.cs`)

These tests define invariants that EVERY generated plan must satisfy. They're written as a `PlanValidator` static class that can be called in any test and also wired into production code as a debug assertion.

```csharp
public static class PlanValidator
{
    public static List<string> Validate(PlanSkeleton plan, ValidationContext ctx);
}

public class ValidationContext
{
    public List<VocabularyProgress> DueWords { get; set; }
    public List<LearningResource> AvailableResources { get; set; }
    public Dictionary<string, HashSet<string>> ResourceVocabMap { get; set; }
    public DateTime Today { get; set; }
    public HashSet<string> YesterdayActivityTypes { get; set; }
    public HashSet<string> YesterdayResourceIds { get; set; }
}
```

**Invariants to validate:**

| # | Invariant | Rule |
|---|-----------|------|
| V1 | **No known words in review** | For every word in VocabReview.DueWords: `!word.IsKnown` |
| V2 | **Vocab overlap ≥ 3 if contextual** | If VocabReview.IsContextual, then OverlapWordCount ≥ 3 |
| V3 | **WordCount ≤ 20** | VocabReview.WordCount ≤ 20 |
| V4 | **WordCount ≤ TotalDue** | VocabReview.WordCount ≤ VocabReview.TotalDue |
| V5 | **Resource exists** | PrimaryResource.Id is in AvailableResources |
| V6 | **Resource not used yesterday** | PrimaryResource.Id NOT in YesterdayResourceIds |
| V7 | **Activity types not repeated from yesterday** | Input/Output activities NOT in YesterdayActivityTypes (soft: warn if violated) |
| V8 | **TotalMinutes consistent** | plan.TotalMinutes == plan.Activities.Sum(a.EstimatedMinutes) |
| V9 | **Activities ordered by Priority** | Priorities are sequential: 1, 2, 3, ... |
| V10 | **Every activity has a Rationale** | All activities have non-null, non-empty Rationale |
| V11 | **VocabReview is first if present** | If any activity is VocabularyReview, it has Priority=1 |
| V12 | **Resource-based activities reference valid resource** | Activities with ResourceId: that resource exists |
| V13 | **VocabCount matches resource** | VocabReview.OverlapWordCount ≤ actual ResourceVocabularyMapping count for that resource |
| V14 | **No duplicate activity types** | No two activities share the same ActivityType (except closing VocabularyGame) |
| V15 | **Session time respected** | TotalMinutes ≤ UserProfile.PreferredSessionMinutes + 3 (small tolerance) |
| V16 | **Promoted words flagged for production** | DueWords with MasteryScore ≥ 0.50 should NOT be in recognition-only mode |
| V17 | **Narrative present** | plan.Narrative is not null |
| V18 | **ResourceSelectionReason present** | Non-empty when PrimaryResource is set |

Test file `PlanValidationTests.cs` should:
1. Generate plans with various data configurations
2. Run `PlanValidator.Validate()` on each
3. Assert zero violations

```csharp
[Fact]
public async Task GeneratedPlan_PassesAllInvariants()
{
    // Arrange: set up realistic data
    var plan = await _sut.BuildPlanAsync();

    // Act
    var violations = PlanValidator.Validate(plan, _validationContext);

    // Assert
    violations.Should().BeEmpty(
        $"Plan violated invariants: {string.Join(", ", violations)}");
}
```

---

## C. Plan Validation Invariants (Production Guardrails)

Beyond tests, add runtime validation in `BuildPlanAsync()` as a debug-mode guard:

```csharp
// At end of BuildPlanAsync, before return:
#if DEBUG
var violations = PlanValidator.Validate(plan, new ValidationContext { ... });
if (violations.Any())
{
    _logger.LogWarning("⚠️ Plan validation failures: {Violations}",
        string.Join("; ", violations));
}
#endif
```

This catches regressions in development without impacting production performance.

---

## D. Test Infrastructure

### D.1 Refactoring for Testability

**The single most impactful change**: Extract `ApplicationDbContext` access from `DeterministicPlanBuilder` into `IPlanDataProvider`.

**New file**: `src/SentenceStudio.Shared/Services/PlanGeneration/IPlanDataProvider.cs`
```csharp
public interface IPlanDataProvider
{
    Task<List<DailyPlanCompletion>> GetRecentCompletionsAsync(DateTime since, CancellationToken ct);
    Task<Dictionary<string, int>> GetResourceVocabularyCountsAsync(CancellationToken ct);
    Task<HashSet<string>> GetResourceVocabularyWordIdsAsync(string resourceId, CancellationToken ct);
    Task<string?> GetRecentSkillForResourceAsync(string resourceId, CancellationToken ct);
    Task<string?> GetMostRecentSkillAsync(CancellationToken ct);
    Task<List<string>> GetRecentActivityTypesAsync(DateTime since, CancellationToken ct);
    Task<HashSet<string>> GetYesterdayActivityTypesAsync(DateTime yesterday, CancellationToken ct);
}
```

**New file**: `src/SentenceStudio.Shared/Services/PlanGeneration/PlanDataProvider.cs`
Implementation that wraps the existing `IServiceProvider.CreateScope() → ApplicationDbContext` calls.

**Modified**: `DeterministicPlanBuilder.cs` — Replace all `CreateScope() / GetRequiredService<ApplicationDbContext>()` with calls to `IPlanDataProvider`. Constructor changes:
```csharp
public DeterministicPlanBuilder(
    UserProfileRepository userProfileRepo,
    LearningResourceRepository resourceRepo,
    SkillProfileRepository skillRepo,
    VocabularyProgressRepository vocabProgressRepo,
    IPlanDataProvider planDataProvider,  // NEW
    ILogger<DeterministicPlanBuilder> logger)
```

This removes `IServiceProvider` from the constructor, eliminating the service locator anti-pattern.

### D.2 Mocking Strategy

| Dependency | Mock Strategy | Why |
|------------|---------------|-----|
| `IPlanDataProvider` | Moq mock | Core data access, fully mockable |
| `UserProfileRepository` | Moq mock | Returns simple UserProfile |
| `LearningResourceRepository` | Moq mock | Returns List<LearningResource> |
| `SkillProfileRepository` | Moq mock | Returns SkillProfile objects |
| `VocabularyProgressRepository` | Moq mock | Returns due vocabulary lists |
| `ILogger<T>` | `Mock<ILogger<T>>()` or `NullLogger<T>.Instance` | Log verification optional |

### D.3 Test Base Class

```csharp
public abstract class PlanBuilderTestBase
{
    protected Mock<UserProfileRepository> MockUserProfileRepo;
    protected Mock<LearningResourceRepository> MockResourceRepo;
    protected Mock<SkillProfileRepository> MockSkillRepo;
    protected Mock<VocabularyProgressRepository> MockVocabProgressRepo;
    protected Mock<IPlanDataProvider> MockPlanDataProvider;
    protected DeterministicPlanBuilder Sut;

    protected PlanBuilderTestBase()
    {
        MockUserProfileRepo = new Mock<UserProfileRepository>();
        MockResourceRepo = new Mock<LearningResourceRepository>();
        MockSkillRepo = new Mock<SkillProfileRepository>();
        MockVocabProgressRepo = new Mock<VocabularyProgressRepository>();
        MockPlanDataProvider = new Mock<IPlanDataProvider>();

        // Default: user with 30-minute sessions
        MockUserProfileRepo.Setup(r => r.GetAsync())
            .ReturnsAsync(new UserProfile { PreferredSessionMinutes = 30 });

        // Default: empty completions
        MockPlanDataProvider.Setup(p => p.GetRecentCompletionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DailyPlanCompletion>());

        Sut = new DeterministicPlanBuilder(
            MockUserProfileRepo.Object,
            MockResourceRepo.Object,
            MockSkillRepo.Object,
            MockVocabProgressRepo.Object,
            MockPlanDataProvider.Object,
            NullLogger<DeterministicPlanBuilder>.Instance);
    }

    // Helper: quickly add resources
    protected void SetupResources(params LearningResource[] resources)
    {
        MockResourceRepo.Setup(r => r.GetAllResourcesLightweightAsync(null, null))
            .ReturnsAsync(resources.ToList());
    }

    // Helper: quickly add due vocab
    protected void SetupDueVocabulary(params VocabularyProgress[] words)
    {
        MockVocabProgressRepo.Setup(r => r.GetDueVocabularyAsync(It.IsAny<DateTime>(), It.IsAny<string>()))
            .ReturnsAsync(words.ToList());
    }

    // Helper: set resource vocab mappings
    protected void SetupResourceVocabMapping(string resourceId, params string[] wordIds)
    {
        MockPlanDataProvider.Setup(p => p.GetResourceVocabularyWordIdsAsync(resourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(wordIds.ToHashSet());

        MockPlanDataProvider.Setup(p => p.GetResourceVocabularyCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { [resourceId] = wordIds.Length });
    }
}
```

### D.4 Running Tests

Add to existing `tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj`:
- No new package references needed (Moq, FluentAssertions, xUnit already present)
- The project already references `SentenceStudio.Shared` which contains all the types under test

Verify with:
```bash
dotnet test tests/SentenceStudio.UnitTests/ --verbosity normal
```

---

## E. Scope Estimate

| Component | Files | Estimated Tests | Effort |
|-----------|-------|-----------------|--------|
| **Bug Fixes** | 3 modified files + 2 new files | — | 1-2 days |
| `IPlanDataProvider` interface + impl | 2 new files | — | 0.5 day |
| `DeterministicPlanBuilder` refactor | 1 modified file | — | 0.5 day |
| Test data builders | 3 new files | — | 0.5 day |
| `ResourceSelectionTests` | 1 new file | 25 tests | 1 day |
| `VocabularyReviewDeterminationTests` | 1 new file | 20 tests | 0.5 day |
| `ActivitySequencingTests` | 1 new file | 15 tests | 0.5 day |
| `DeterministicPlanBuilderTests` | 1 new file | 35 tests | 1 day |
| `PlanConverterTests` | 1 new file | 12 tests | 0.5 day |
| `PlanValidationTests` + `PlanValidator` | 2 new files | 18 tests | 1 day |
| **TOTAL** | ~14 files (7 new, 4 modified, 3 builder files) | **~125 tests** | **~6-7 days** |

### Recommended Implementation Order

1. **Extract `IPlanDataProvider`** — Enables all testing (blocks everything)
2. **Test data builders** — Foundation for all tests
3. **Resource selection bug fixes** (Bugs 1, 3, 5) — Highest user impact
4. **Resource selection tests** — Verify fixes
5. **Vocab review bug fixes** (Bug 2) — WordCount reconciliation
6. **Vocab review tests** — Verify fixes
7. **Quiz mode fix** (Bug 4) — UI layer fix
8. **Activity sequencing tests** — Lower priority but catches edge cases
9. **PlanValidator + invariant tests** — Safety net for future changes
10. **PlanConverter tests** — Completeness
