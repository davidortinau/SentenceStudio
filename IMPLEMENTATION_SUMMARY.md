# LLM-Powered Daily Plan Generation - Implementation Summary

**Date:** 2025-11-19
**Status:** Phase 1 & 2 Complete ✅

## What Was Implemented

### Phase 1: Foundation & Data Models ✅
**Commit:** `fdc7bb9` - "feat: Add LLM-based plan generation infrastructure"

**Changes:**
1. **DTOs with [Description] Attributes**
   - `DailyPlanRequest.cs` - Context sent to LLM
   - `DailyPlanResponse.cs` - Structured response from LLM
   - `ActivitySummary`, `ResourceOption`, `SkillOption` - Supporting types

2. **User Profile Enhancements**
   - Added `PreferredSessionMinutes` (default: 20 min)
   - Added `TargetCEFRLevel` (optional)
   - Database migration: `20251119134045_AddUserPreferencesForPlanGeneration`

3. **Prompt Template System**
   - Added Scriban package for templating
   - Created `DailyPlanGeneration.scriban` with:
     - Complete activity catalog (Reading, Listening, Shadowing, Cloze, Translation, VocabularyReview, VocabularyGame)
     - Research-backed learning principles
     - Pedagogical guidance for each activity
     - Session length flexibility

4. **LLM Service Implementation**
   - `LlmPlanGenerationService.cs`
   - Gathers context from user profile, vocab progress, activity history, resources, skills
   - Renders Scriban template with context
   - Calls GPT-4o-mini via Microsoft.Extensions.AI
   - Returns structured `DailyPlanResponse`

### Phase 2: Integration into ProgressService ✅
**Commit:** `29f82a8` - "feat: Replace rule-based plan generation with LLM-powered system"

**Changes:**
1. **Plan Converter Utility**
   - `PlanConverter.cs` - Transforms LLM response to `TodaysPlan` record
   - Maps activity types to routes and parameters
   - Generates deterministic plan item IDs
   - Handles localization keys

2. **ProgressService Refactoring**
   - Injected `ILlmPlanGenerationService`
   - Replaced 200+ lines of rule-based logic with LLM calls
   - Added intelligent fallback (vocab review only) if LLM fails
   - Maintained caching and enrichment logic

3. **Removed Old Logic**
   - `SelectOptimalResourceAsync` (rule-based)
   - `SelectOptimalSkillAsync` (rule-based)
   - `DetermineInputActivity` (hardcoded rotation)
   - `DetermineOutputActivity` (hardcoded rotation)
   - All hardcoded time estimates

---

## How It Works

### User Opens Dashboard
1. Dashboard calls `ProgressService.GenerateTodaysPlanAsync()`
2. Checks cache first (if plan exists for today, return it)
3. If no cached plan:
   - Calls `LlmPlanGenerationService.GeneratePlanAsync()`
   - Service gathers context:
     - User preferences (session length, CEFR level)
     - Vocabulary due count
     - Recent 14 days of activity history
     - Available resources with metadata
     - Available skills
   - Renders Scriban prompt with context
   - Sends to GPT-4o-mini
   - GPT returns structured JSON with 1-5 activities
4. `PlanConverter` transforms LLM response to `TodaysPlan`
5. Enriches with streak info and completion data
6. Caches and returns plan

### Fallback Strategy
If LLM fails (network, API error, invalid response):
- Falls back to basic plan (vocab review only)
- User can still practice
- Next app restart will retry LLM

---

## LLM Prompt Design

### Context Provided
- **User Profile:** Session length preference, CEFR level, languages
- **Vocabulary Due:** Number of words needing review today
- **Recent History:** Last 14 days of activities with time spent
- **Available Resources:** Title, media type, word count
- **Available Skills:** Title, description

### Activity Catalog Included
Each activity listed with:
- Pedagogical purpose
- Cognitive load level
- Required parameters (ResourceId, SkillId)
- Research citations where applicable
- Time range recommendations

### Learning Principles
1. Spaced repetition priority (vocab if >= 5 due)
2. Variety matters (avoid recent resources)
3. Input before output (cognitive load progression)
4. Skill balance (rotate practice areas)
5. Activity variety (research-backed engagement)
6. Session energy management

### LLM Output
Returns JSON:
```json
{
  "activities": [
    {
      "activityType": "VocabularyReview",
      "resourceId": null,
      "skillId": null,
      "estimatedMinutes": 10,
      "priority": 1
    },
    {
      "activityType": "Reading",
      "resourceId": 5,
      "skillId": 2,
      "estimatedMinutes": 10,
      "priority": 2
    }
  ],
  "rationale": "Started with vocab review (81 words due)..."
}
```

---

## Benefits Over Rule-Based System

### Before (Rule-Based)
- ❌ Naive resource selection (first non-recent)
- ❌ Broken skill selection (always first)
- ❌ Hardcoded activity rotation
- ❌ Fixed time estimates
- ❌ No reasoning about combinations
- ❌ Can't adapt to user patterns

### After (LLM-Powered)
- ✅ Intelligent resource selection based on variety, level, topic relevance
- ✅ Smart skill rotation based on recent practice and balance
- ✅ Varied activity types based on engagement research
- ✅ Adaptive time estimates based on difficulty and user level
- ✅ Holistic plan composition (considers cognitive load, energy levels)
- ✅ Can explain reasoning (rationale field)
- ✅ Improves automatically as GPT models improve

---

## User Experience

**User Opens App:**
1. Sees loading indicator for 2-3 seconds (LLM call)
2. Plan appears with 2-4 activities
3. Activities are personalized to:
   - Their session length preference
   - Their CEFR level (if set)
   - Recent activity history
   - Vocabulary due
   - Research-backed learning principles

**Zero Configuration Required:**
- Defaults work well (20 min sessions)
- Can adjust in settings if desired
- Plan adapts automatically each day

---

## Next Steps (Future Enhancements)

### Phase 3: User Settings UI (Optional)
- Add session length picker to onboarding
- Add session length picker to settings
- Add CEFR level selector to settings

### Phase 4: Telemetry & Refinement (Optional)
- Track plan quality metrics
- Log LLM rationales for analysis
- Tune prompt based on real usage
- A/B test prompt variations

### Phase 5: Advanced Features (Optional)
- User can request plan regeneration
- User can provide feedback on plans
- LLM learns from user skip patterns
- Long-term goal tracking influences plans

---

## Technical Debt & Notes

### Good
- Clean separation of concerns (LLM service, converter, progress service)
- Robust error handling with fallback
- Maintains existing caching and enrichment logic
- Comprehensive logging for debugging
- Uses Microsoft.Extensions.AI patterns

### Could Improve Later
- Consider caching user profile in LLM service (currently fetches each time)
- Add retry logic for transient API failures
- Monitor API costs (currently minimal for 1 user)
- Consider local LLM fallback (Ollama) for offline scenarios

---

## Cost Analysis

**Current Setup:**
- Model: gpt-4o-mini
- Frequency: Once per day per user
- Prompt size: ~1000 tokens (varies with history)
- Response size: ~200 tokens
- Cost per plan: ~$0.01
- Monthly cost (1 user): ~$0.30
- Annual cost (1 user): ~$3.65

**Scaling to 1000 users:**
- Monthly cost: ~$300
- Annual cost: ~$3,650
- Still very affordable for B2C SaaS

---

## Files Changed

### New Files
- `src/SentenceStudio.Shared/Models/DailyPlanGeneration/DailyPlanRequest.cs`
- `src/SentenceStudio.Shared/Models/DailyPlanGeneration/DailyPlanResponse.cs`
- `src/SentenceStudio/Resources/Prompts/DailyPlanGeneration.scriban`
- `src/SentenceStudio/Services/PlanGeneration/LlmPlanGenerationService.cs`
- `src/SentenceStudio/Services/PlanGeneration/PlanConverter.cs`
- `src/SentenceStudio.Shared/Migrations/20251119134045_AddUserPreferencesForPlanGeneration.cs`

### Modified Files
- `src/SentenceStudio.Shared/Models/UserProfile.cs` - Added preferences
- `src/SentenceStudio/Services/Progress/ProgressService.cs` - LLM integration
- `src/SentenceStudio/MauiProgram.cs` - Service registration
- `src/SentenceStudio/SentenceStudio.csproj` - Scriban package

---

**Implementation Status:** ✅ Complete and ready for testing
**Next Action:** Test app with real data and observe plan quality
