# Today's Learning Plan - System Overview

## 📊 Current State Analysis (2025-11-19)

### Problem Identified
**Database shows:** `IsCompleted=0, MinutesSpent=20, EstimatedMinutes=15`
**Expected:** Activity should be marked complete when time requirement is met

### Root Cause
**IsCompleted flag is ONLY set by explicit user action**, not automatically when time threshold is reached.

---

## 🏗️ Plan Generation Architecture

### High-Level Flow
```
User Opens Dashboard
    ↓
GenerateTodaysPlanAsync() called
    ↓
[Check cache] → Return if exists
    ↓
[Generate New Plan]:
    1. Check vocab due count → Add VocabularyReview if >= 5 words
    2. Get recent activity history (last 7 days)
    3. Select optimal resource (variety-based)
    4. Select optimal skill (currently: first skill)
    5. Determine input activity (Reading vs Listening)
    6. Determine output activity (Shadowing/Cloze/Translation rotation)
    7. Add optional vocab game if < 4 items total
    ↓
Enrich with completion data from database
    ↓
Cache and return plan
```

---

## 🎯 Plan Generation Logic

### 1. Vocabulary Review Activity
**Trigger:** `vocabDueCount >= 5`
- **Estimated Minutes:** `Math.Min(vocabDueCount / 4, 15)` ← **15 MIN MAX**
- **Priority:** 1 (always first)
- **Route:** `/vocabulary-quiz`
- **Parameters:** `Mode=SRS, DueOnly=true`

**Example:** 81 words due → 81/4 = 20.25 → capped at 15 minutes

### 2. Resource Selection (`SelectOptimalResourceAsync`)
**Current Logic:** Simple variety algorithm
```csharp
1. Get all available resources
2. Filter OUT resources used in last 7 days
3. If candidates exist: return first
4. If no candidates: return oldest updated resource
```

**Not AI-based:** Uses rule-based filtering for variety

### 3. Skill Selection (`SelectOptimalSkillAsync`)
**Current Logic:** Returns first skill in database
```csharp
return skills.First();
```

**Not adaptive:** No personalization based on user progress

### 4. Input Activity Selection (`DetermineInputActivity`)
**Decision Logic:**
```csharp
IF last reading was NULL OR >= 2 days ago
    THEN Reading
ELSE
    Listening
```

**Goal:** Rotate input activities every ~2 days

### 5. Output Activity Selection (`DetermineOutputActivity`)
**Rotation Pattern:**
```
Shadowing → Cloze → Translation → Shadowing (cycle repeats)
```

**Logic:**
```csharp
IF no recent output activity
    THEN Shadowing (starting point)
ELSE
    Rotate to next in sequence
```

**Goal:** Varied output practice through 3 modalities

### 6. Estimated Minutes (Hardcoded)
- **VocabularyReview:** Dynamic (4-15 min based on due count)
- **Reading:** 10 minutes
- **Listening:** 12 minutes
- **Shadowing:** 10 minutes
- **Cloze:** 8 minutes
- **Translation:** 10 minutes
- **VocabularyGame:** 5 minutes

**Not adaptive:** Fixed time estimates regardless of skill level

---

## ⏱️ Time Tracking vs Completion

### Current Implementation

**Time Tracking (Works):**
1. User starts activity from plan
2. `ActivityTimerService` tracks elapsed time
3. Every minute: `UpdatePlanItemProgressAsync()` saves MinutesSpent to database
4. Progress bar updates: `28% complete 20/35 min`

**Completion Marking (Broken):**
1. `IsCompleted` flag ONLY set by `MarkPlanItemCompleteAsync()`
2. **Currently only called for:** Video activities (line 724 of DashboardPage.cs)
3. **NOT called for:** Regular activities (VocabularyQuiz, Reading, etc.)
4. **NOT automatically set** when `MinutesSpent >= EstimatedMinutes`

### The Gap
```
User spends 20 minutes on 15-minute activity
    ↓
✅ MinutesSpent=20 saved to database
❌ IsCompleted=0 (flag never set)
❌ Next activity stays locked
❌ "Resume" button returns to same activity
```

---

## 🔧 Recent Fixes Applied

### Fix #1: Time-Based Completion Detection
**File:** `TodaysPlanCard.cs`
**Added:** `IsItemComplete()` helper method
```csharp
bool IsItemComplete(DailyPlanItem item)
{
    // Check BOTH flag AND time requirement
    return item.IsCompleted || item.MinutesSpent >= item.EstimatedMinutes;
}
```

**Impact:**
- ✅ Activities unlock based on time spent
- ✅ Checkmark appears when time requirement met
- ✅ "Resume" button navigates to next activity
- ✅ UI reflects completion without database flag

**Limitation:** Database still shows `IsCompleted=0` (cosmetic issue only)

### Fix #2: Resume Button Logic
**File:** `TodaysPlanCard.cs` (Lines 115-121)
**Changed:**
```csharp
// Before: Check next activity's progress
var buttonText = nextItem?.MinutesSpent > 0 ? "Resume" : "Start";

// After: Check ANY activity has progress
var hasAnyProgress = _plan.Items.Any(i => i.MinutesSpent > 0);
var buttonText = hasAnyProgress ? "Resume" : "Start";
```

**Impact:**
- ✅ Button says "Resume" when plan has any progress
- ✅ Button says "Start" only for brand new plan

---

## 🤖 AI vs Rule-Based System

### Current State: **Rule-Based Algorithm**

**What's NOT AI:**
- Resource selection (simple variety filter)
- Skill selection (first in list)
- Activity type rotation (hardcoded patterns)
- Time estimates (fixed durations)
- Completion criteria (time thresholds)

**What's Deterministic:**
- Vocab review triggers at 5+ due words
- Input activities rotate every 2 days
- Output activities cycle through 3 types
- Time allocations are constant
- Priority ordering is fixed

### Potential AI Integration Points

**Could be AI-powered:**
1. **Resource Selection:** ML model predicts optimal content based on:
   - User's vocabulary knowledge
   - Recent success rates
   - Content difficulty vs skill level
   - Engagement patterns

2. **Skill Focus:** Adaptive selection based on:
   - Weakest skill areas
   - Learning velocity per skill
   - Time since last practice
   - Spaced repetition optimization

3. **Activity Type:** Personalized based on:
   - Learning style preferences
   - Time-of-day effectiveness
   - Recent performance patterns
   - Optimal practice intervals

4. **Time Allocation:** Dynamic estimates using:
   - Historical completion times
   - Skill proficiency level
   - Content complexity analysis
   - Individual learning pace

5. **Plan Composition:** Holistic optimization:
   - Balance of input/output activities
   - Difficulty progression
   - Energy level management
   - Long-term learning goals

---

## 📊 Database Schema

### DailyPlanCompletion Table
```sql
CREATE TABLE DailyPlanCompletion (
    Id INTEGER PRIMARY KEY,
    Date TEXT NOT NULL,              -- UTC date (2025-11-19)
    PlanItemId TEXT NOT NULL,        -- Deterministic GUID from plan generation
    ActivityType TEXT,               -- "VocabularyReview", "Reading", etc.
    ResourceId INTEGER,              -- FK to LearningResource (nullable)
    SkillId INTEGER,                 -- FK to SkillProfile (nullable)
    IsCompleted INTEGER NOT NULL,    -- 0 or 1 (currently only set for videos)
    CompletedAt TEXT,                -- Timestamp when marked complete
    MinutesSpent INTEGER NOT NULL,   -- Updated every minute by timer
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
```

**Current Data Example:**
```
Date: 2025-11-18
PlanItemId: b904cb0e-3bfb-e34b-7dc4-85d42a3471cf
ActivityType: VocabularyReview
MinutesSpent: 20
EstimatedMinutes: 15 (from plan generation)
IsCompleted: 0 ← Never set!
```

---

## 🎯 Recommendations

### Short-Term (Fix Current Issues)
1. ✅ **DONE:** Add time-based completion detection in UI
2. **TODO:** Auto-set IsCompleted when timer reaches EstimatedMinutes
3. **TODO:** Add activity completion callback when user exits naturally

### Medium-Term (Improve Algorithm)
1. Add difficulty-based time estimates
2. Track average completion times per activity type
3. Implement skill weakness detection
4. Add resource effectiveness scoring

### Long-Term (AI Integration)
1. Build ML model for resource recommendation
2. Implement adaptive time allocation
3. Create personalized activity sequencing
4. Add predictive difficulty assessment
5. Optimize spaced repetition scheduling

---

## 🔍 Key Takeaways

**Current System Strengths:**
- ✅ Deterministic and predictable
- ✅ Variety through rotation
- ✅ Time tracking works reliably
- ✅ Sequential unlocking enforces structure

**Current System Weaknesses:**
- ❌ No personalization beyond variety
- ❌ Fixed time estimates ignore skill level
- ❌ Completion flag not automatically set
- ❌ No adaptation based on performance
- ❌ Resource selection is naive

**Next Evolution:**
- Replace rule-based selection with ML models
- Add user proficiency tracking
- Implement adaptive difficulty
- Create feedback loop from completion data
- Build recommendation engine

---

**Generated:** 2025-11-19T03:29:22Z
**Last Updated:** After implementing time-based completion detection
