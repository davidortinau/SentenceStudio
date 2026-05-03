# ResourceId Decoupling Pattern for Vocabulary-Driven Activities

## When to Use This Pattern

Apply when an activity is **vocabulary-driven** (should load from full user vocab pool based on SRS/due status) but can be incorrectly filtered by LearningResource scope.

**Trigger symptoms:**
- Activity launched from Today's Plan shows "no words available" error
- Same activity works when launched from resource detail page
- User has many due words globally but activity fails on specific resource
- Insights dashboard shows X due words, but activity claims 0 available

**Vocabulary-driven activities:**
- VocabularyReview (Vocab Quiz)
- VocabularyGame (Vocab Matching)
- Cloze (when launched from plan — generates contextual sentences for due vocab)

**Resource-driven activities (DO NOT apply this pattern):**
- Reading, Listening, VideoWatching, Shadowing, Translation, Writing
- These require resource.Transcript, resource.AudioUrl, etc.

## The 4-Layer Fix

### Layer 1: DeterministicPlanBuilder.cs

Set `ResourceId = null` when creating the plan activity. This prevents NEW plans from carrying ResourceId.

**Example (VocabReview, line 460):**
```csharp
activities.Add(new PlannedActivity
{
    ActivityType = "VocabularyReview",
    ResourceId = null,  // VocabularyReview is vocabulary-driven, NOT resource-scoped
    SkillId = vocabReview.SkillId ?? skill?.Id,
    // ...
});
```

**Example (VocabGame, line 520):**
```csharp
activities.Add(new PlannedActivity
{
    ActivityType = "VocabularyGame",
    ResourceId = null, // Vocabulary games use skill context
    SkillId = skill.Id,
    // ...
});
```

### Layer 2: PlanConverter.cs

Add activity-specific handling in `BuildRouteParameters` to:
1. Set `DueOnly = true` (SRS filter)
2. Skip passing ResourceId to route params

**Example (VocabReview, lines 126-131):**
```csharp
if (activityType == PlanActivityType.VocabularyReview)
{
    parameters["Mode"] = "SRS";
    parameters["DueOnly"] = true;
    // VocabularyReview is vocabulary-driven, NOT resource-driven
    // ResourceId is intentionally NOT passed to allow quiz to load from full user vocab pool
}
```

**Example (VocabGame, lines 133-139):**
```csharp
else if (activityType == PlanActivityType.VocabularyGame)
{
    parameters["DueOnly"] = true;
    // VocabularyGame is vocabulary-driven, NOT resource-driven (like VocabularyReview)
    // ResourceId is intentionally NOT passed to allow matching to load from full user vocab pool
    if (!string.IsNullOrEmpty(skillId))
        parameters["SkillId"] = skillId;
}
```

### Layer 3: Index.razor LaunchPlanItem Guard

Prevent **persisted old plan items** from leaking ResourceId into the URL. This is defense-in-depth for plans generated before Layer 1 fix.

**Location:** `src/SentenceStudio.UI/Pages/Index.razor` lines 984-992

**Pattern:**
```csharp
// CRITICAL: For vocabulary-driven activities (VocabularyReview, VocabularyGame),
// NEVER pass ResourceId (even if persisted on the plan item)
// These activities are vocabulary-driven, NOT resource-driven
if (item.ActivityType != PlanActivityType.VocabularyReview 
    && item.ActivityType != PlanActivityType.VocabularyGame 
    && !string.IsNullOrEmpty(item.ResourceId))
{
    if (multiResourceRoutes.Contains(route))
        query.Add($"resourceIds={item.ResourceId}");
    else
        query.Add($"resourceId={item.ResourceId}");
}
```

**Why this matters:** Even after Layer 1 fix ships, DB already contains persisted DailyPlan rows with the old ResourceId. Index.razor reading `item.ResourceId` directly bypasses PlanConverter entirely. The guard stops the leak at the UI boundary.

### Layer 4: Page-Level Defense (DueOnly Check)

Activity page ignores ResourceIds when `DueOnly=true`, even if upstream leak bypassed Layer 3.

**Example (VocabQuiz.razor, line 643):**
```csharp
// DEFENSE IN DEPTH: When DueOnly=true (SRS mode), ignore ResourceIds even if passed
// This ensures VocabularyReview always loads from full user vocabulary pool
var resourceIds = DueOnly ? Array.Empty<string>() : ParseResourceIds();
```

**Example (VocabMatching.razor, similar pattern):**
```csharp
// DEFENSE IN DEPTH: When DueOnly=true (plan-initiated SRS mode), ignore ResourceIds
// and load from full user vocabulary pool, same pattern as VocabQuiz
var resourceIds = DueOnly ? Array.Empty<string>() : ParseResourceIds();
```

**Required page changes:**
1. Add query parameter: `[SupplyParameterFromQuery(Name = "DueOnly")] public bool DueOnly { get; set; }`
2. Check DueOnly flag in load method
3. When DueOnly=true: call `GetAllVocabularyWordsWithResourcesAsync()` or `GetDueWordsAsync()` (full user pool)
4. When DueOnly=false: preserve existing resource-filtered behavior (user-initiated path)

## Testing Checklist

After applying all 4 layers:

- [ ] Generate new plan → activity launches successfully from Today's Plan
- [ ] Old persisted plan item → activity still launches successfully (Layer 3 guard prevents leak)
- [ ] Direct resource-filtered launch → still works (DueOnly=false path)
- [ ] Activity loads due words globally when launched from plan
- [ ] Activity respects resource filter when launched from resource detail page
- [ ] No regression for other vocabulary-driven activities

## References

- VocabQuiz decoupling: commits 88a0272 (builder+converter), c081a63 (Index guard + page defense)
- VocabMatching decoupling: commit 0c8e197 (all 4 layers)
- Decisions log: `.squad/decisions.md` lines 7-16
- Memory: "DailyPlan items persisted in DB retain their original ResourceId field. UI code that reads item.ResourceId directly (e.g., Index.razor LaunchPlanItem) bypasses PlanConverter and can leak stale resource filters."

## Anti-Pattern: ResourceId Field Removal

**DO NOT** remove the ResourceId field from DailyPlanItem schema. Resource-driven activities (Reading, Shadowing, etc.) legitimately use it. The fix is selective filtering, not schema change.
