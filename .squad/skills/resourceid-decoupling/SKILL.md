# Skill: 4-Layer ResourceId Decoupling for Vocabulary-Driven Activities

**Pattern Status:** Proven (4 confirmations: VocabQuiz, VocabularyMatching, Cloze, NumberDrill)  
**Confidence:** High  
**First Applied:** Commit 0c8e197 (VocabQuiz + VocabularyMatching)  
**Documented:** 2026-05-04 (.squad/decisions.md), 2026-05-05 (this skill)  
**Context:** SentenceStudio DailyPlan system — vocabulary-driven activities must ignore ResourceId scope

---

## Problem Statement

**Symptom:** Vocabulary-driven activities (Quiz, Matching, Cloze, NumberDrill) accidentally inherit ResourceId from DailyPlan, filtering their vocab pool to a single LearningResource instead of the full user vocabulary.

**Root cause:** DailyPlan generator creates plan items from LearningResources (e.g., "Reading → Cloze from Resource X"). PlanConverter blindly passes `ResourceId` to all activities via query params. Vocabulary-driven activities must draw from the FULL user vocab pool (SRS-scheduled words across all resources), not just one resource.

**Consequence if not applied:**
- VocabQuiz shows only 3 words (from Resource X) instead of 47 due words (across all resources)
- Matching game has insufficient pairs (needs 6+ words, Resource X has 3)
- NumberDrill shows 0 items (numbers aren't associated with resources at all)

---

## The 4-Layer Defense-in-Depth Pattern

Each layer independently enforces `ResourceId = null` for vocabulary-driven activities. Redundant by design — if Layer 1 is buggy, Layer 2 catches it; if Layer 2 is buggy, Layer 3 catches it; if all fail, Layer 4 rejects at page load.

### Layer 1: Plan Builder (Source of Truth)

**File:** `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs`  
**Location:** Activity creation (e.g., STEP 1 vocab review, STEP 4 closer)  
**Action:** Set `ResourceId = null` when creating PlannedActivity

**Example (STEP 1 vocab review):**
```csharp
activities.Add(new PlannedActivity
{
    ActivityType = "VocabularyReview",
    ResourceId = null,  // ← Layer 1: VocabularyReview is vocabulary-driven, NOT resource-scoped
    SkillId = vocabReview.SkillId,
    EstimatedMinutes = vocabReview.EstimatedMinutes,
    // ...
});
```

**Example (STEP 4 closer — VocabularyGame or NumberDrill):**
```csharp
activities.Add(new PlannedActivity
{
    ActivityType = closerActivity,  // "VocabularyGame" or "NumberDrill"
    ResourceId = null,  // ← Layer 1: Both are vocab-driven
    SkillId = closerActivity == "VocabularyGame" ? skill?.Id : null,
    // ...
});
```

**Rationale:** If the plan item never has a ResourceId, downstream layers never see one. This is the primary fix.

---

### Layer 2: PlanConverter (Route Parameter Builder)

**File:** `src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs`  
**Method:** `BuildRouteParameters(PlanActivityType activityType, string? resourceId, string? skillId)`  
**Action:** Omit ResourceId from route parameters for vocabulary-driven activities, even if passed

**Code:**
```csharp
public static Dictionary<string, object> BuildRouteParameters(
    PlanActivityType activityType, string? resourceId, string? skillId)
{
    var parameters = new Dictionary<string, object>();

    if (activityType == PlanActivityType.VocabularyReview)
    {
        parameters["Mode"] = "SRS";
        parameters["DueOnly"] = true;
        // ← Layer 2: ResourceId intentionally NOT added
    }
    else if (activityType == PlanActivityType.VocabularyGame)
    {
        parameters["DueOnly"] = true;
        // ← Layer 2: ResourceId intentionally NOT added
        if (!string.IsNullOrEmpty(skillId))
            parameters["SkillId"] = skillId;
    }
    else if (activityType == PlanActivityType.Cloze)
    {
        parameters["DueOnly"] = true;
        // ← Layer 2: ResourceId intentionally NOT added
        if (!string.IsNullOrEmpty(skillId))
            parameters["SkillId"] = skillId;
    }
    else if (activityType == PlanActivityType.NumberDrill)
    {
        parameters["DueOnly"] = true;
        // ← Layer 2: ResourceId intentionally NOT added (numbers aren't tied to resources)
    }
    else
    {
        // Resource-based activities (Reading, Listening, etc.)
        if (!string.IsNullOrEmpty(resourceId))
            parameters["ResourceId"] = resourceId;
        if (!string.IsNullOrEmpty(skillId))
            parameters["SkillId"] = skillId;
    }

    return parameters;
}
```

**Rationale:** Even if Layer 1 accidentally sets a ResourceId, this layer strips it. The route params dictionary never includes `"ResourceId"` key for vocab-driven activities.

---

### Layer 3: Dashboard Navigation Guard (UI Launch Point)

**File:** `src/SentenceStudio.UI/Pages/Index.razor`  
**Method:** `LaunchPlanItem(DailyPlanItem item)`  
**Action:** Do NOT append ResourceId query param for vocabulary-driven activities

**Code:**
```csharp
private void LaunchPlanItem(DailyPlanItem item)
{
    var route = MapActivityRoute(item.ActivityType);
    var query = new List<string>();
    var multiResourceRoutes = new HashSet<string> { "vocab-quiz", "vocab-matching" };
    
    // ← Layer 3: CRITICAL guard — vocabulary-driven activities skip ResourceId
    if (item.ActivityType != PlanActivityType.VocabularyReview 
        && item.ActivityType != PlanActivityType.VocabularyGame 
        && item.ActivityType != PlanActivityType.Cloze 
        && item.ActivityType != PlanActivityType.NumberDrill 
        && !string.IsNullOrEmpty(item.ResourceId))
    {
        if (multiResourceRoutes.Contains(route))
            query.Add($"resourceIds={item.ResourceId}");
        else
            query.Add($"resourceId={item.ResourceId}");
    }
    
    if (!string.IsNullOrEmpty(item.SkillId))
        query.Add($"skillId={item.SkillId}");
    
    // Add PlanItemId for completion tracking
    query.Add($"PlanItemId={item.Id}");
    
    var url = query.Any() ? $"{route}?{string.Join("&", query)}" : route;
    NavManager.NavigateTo(url);
}
```

**Rationale:** Even if a persisted DailyPlanItem (from DB or cache) has a stale ResourceId, the UI navigation layer refuses to pass it. The URL query string never includes `resourceId=...` for vocab-driven activities.

---

### Layer 4: Page-Level Defense (Razor Component)

**Files:** 
- `src/SentenceStudio.UI/Pages/VocabQuiz.razor`
- `src/SentenceStudio.UI/Pages/VocabularyMatching.razor`
- `src/SentenceStudio.UI/Pages/Cloze.razor`
- `src/SentenceStudio.UI/Pages/NumberDrill.razor`

**Action:** Reject ResourceId if present, log warning, set to null

**Code pattern:**
```csharp
[Parameter]
[SupplyParameterFromQuery]
public string? ResourceId { get; set; }

protected override void OnParametersSet()
{
    // ← Layer 4: Defense-in-depth — this activity ignores ResourceId
    if (!string.IsNullOrEmpty(ResourceId))
    {
        Logger.LogWarning("{ActivityName} received ResourceId '{ResourceId}' — ignoring (activity is vocabulary-driven)", 
            "VocabQuiz", ResourceId);  // Replace "VocabQuiz" with activity name
        ResourceId = null;
    }
    
    base.OnParametersSet();
}
```

**Rationale:** Even if a user manually edits the URL (e.g., `/vocab-quiz?resourceId=abc123`), the page rejects it. This is the final firewall.

---

## When to Apply This Pattern

**Use this pattern for activities that:**
1. **Select content from the full user vocabulary pool** (not a single resource)
2. **Use SRS scheduling** (DueDate, Interval, EaseFactor from VocabularyProgress or equivalent)
3. **Can be launched from DailyPlan** (plan slot integration)

**Examples:**
- ✅ VocabularyReview (VocabQuiz) — SRS-scheduled words across all resources
- ✅ VocabularyGame (VocabularyMatching) — due vocab words, needs 6+ for pairs
- ✅ Cloze — fill-in-the-blank from due vocab pool
- ✅ NumberDrill — Korean number buckets (not tied to resources at all)

**Do NOT use for:**
- ❌ Reading — must read from a specific LearningResource.Content
- ❌ Listening — must play a specific LearningResource.AudioUrl
- ❌ Translation — translates a specific resource's content
- ❌ SceneDescription — describes a specific resource's scene

---

## Checklist for New Vocabulary-Driven Activities

When adding a new activity that draws from the user's full vocab pool:

- [ ] **Layer 1:** Set `ResourceId = null` in DeterministicPlanBuilder when creating PlannedActivity
- [ ] **Layer 2:** Add activity type to PlanConverter.BuildRouteParameters(), omit ResourceId, set `DueOnly = true`
- [ ] **Layer 3:** Add activity type to Index.razor ResourceId guard (line ~817)
- [ ] **Layer 4:** Add OnParametersSet() guard in page component, reject ResourceId if present
- [ ] **Localization:** Add PlanItemXxxTitle/Desc keys using PascalCase (avoid AI snake_case)
- [ ] **Tests:** Verify PlanConverter produces route without ResourceId, verify page ignores manual ResourceId query param

---

## Historical Context

**First application:** Commit 0c8e197 (2026-05-04)  
**Activities covered:** VocabQuiz, VocabularyMatching  
**Documentation:** `.squad/decisions.md` (2026-05-04 NumberDrill Phase 1 decision)

**Second application:** Cloze activity (date unknown, existing in codebase as of 2026-05-05)

**Third application:** NumberDrill Phase 2 (2026-05-05 by Wash)  
**Decision:** `.squad/decisions/inbox/wash-numberdrill-plan-integration-impl.md`  
**Validation:** 519/520 unit tests passing, backend build clean

**Pattern extracted as skill:** 2026-05-05 (this document)

---

## Known Gotchas

### Gotcha 1: DB-Persisted ResourceId
If a DailyPlanItem is persisted to DB with a stale ResourceId (e.g., from before Layer 1 was fixed), Layers 3 and 4 catch it. Layer 2 is only invoked during plan generation, not during plan deserialization.

**Mitigation:** Layers 3 and 4 are mandatory even after Layer 1 is correct.

### Gotcha 2: Manual URL Editing
Users or tests might navigate to `/vocab-quiz?resourceId=abc123` manually. Layer 4 catches this.

**Mitigation:** Always implement Layer 4 in the page component.

### Gotcha 3: AI Localization Key Mismatch
AI-generated plan items sometimes use snake_case keys (`"plan_item_vocab_review_title"`) while resource files use PascalCase (`"PlanItemVocabReviewTitle"`).

**Mitigation:** Use enum-driven key mapping in PlanConverter (compile-time), not runtime string fields. See `.github/copilot-instructions.md` localization guidelines.

---

## Related Patterns

- **Single-Flight Async:** `.squad/skills/single-flight-async/SKILL.md` — prevents duplicate async operations
- **SM-2 Scheduler:** `src/SentenceStudio.AppLib/Services/Spaced/Sm2Scheduler.cs` — reusable spaced repetition math
- **Enum-Driven Localization:** `.github/copilot-instructions.md` — avoid snake_case/PascalCase mismatches

---

## References

- `.squad/decisions.md` — 2026-05-04 entry (NumberDrill Phase 1)
- `.squad/decisions/inbox/zoe-numberdrill-plan-integration-arch.md` — Phase 2 architecture decision
- Commit 0c8e197 — initial Quiz/Matching 4-layer implementation
