# Daily Plan Generation Strategy

## Overview: Architecture and Flow

When a user opens the app, here's what happens:

```
User Opens Dashboard
  ↓
Check for Cached Plan (memory → database)
  ↓ (if not found)
DeterministicPlanBuilder creates plan
  ├─ Determine vocabulary review needs (SRS algorithm)
  ├─ Select primary resource (scoring system)
  ├─ Determine skill level (from history)
  └─ Build activity sequence (pedagogical rules)
  ↓
Save to database + cache in memory
  ↓
Display plan with rationale
```

**Key Insight**: Despite the name "LlmPlanGenerationService," **no LLM is actually called**. The system uses deterministic algorithms with a hand-crafted rationale string.

---

## Deterministic vs LLM: The Strategic Pivot

### What Was Originally Built

The app initially used an LLM-based approach:
- Sent 14 days of activity history, available resources, skills, and vocab data to an LLM
- Asked it to generate a structured plan (1-5 activities with priorities, time estimates)
- Expected creative, personalized recommendations

### Why We Switched to Deterministic

**Problems with LLM approach:**
1. **Speed**: Network latency + inference = slow app startup (3-10 seconds)
2. **Reliability**: Network failures, API rate limits, costs
3. **Consistency**: Non-deterministic outputs, varying quality
4. **Pedagogical Control**: LLM might ignore established SLA principles
5. **Testability**: Impossible to unit test, hard to debug

**Benefits of deterministic approach:**
1. **Instant**: Pure computation (~50ms, works offline)
2. **Reliable**: Always works, no API dependencies
3. **Consistent**: Same inputs = same plan every time
4. **Pedagogically Sound**: Hardcoded research-backed rules
5. **Testable**: Can verify every decision path

### Current State

The "LLM service" is now just a wrapper that:
1. Calls the deterministic builder (does all real work)
2. Formats results
3. Generates a simple string rationale (no AI involved)

**Trade-off**: We chose speed, reliability, and pedagogical control over flexibility and personalization.

---

## Resource Selection: Spacing and Variety

### Core Philosophy

**Goal**: Maximize long-term retention through optimal spacing and variety.

### Scoring System

Resources are scored based on:

1. **Yesterday's resource = DISQUALIFIED** (hard constraint)
   - *Why*: Spacing effect requires time between exposures
   - *Research*: Memory consolidation happens during rest periods
   - *User Experience*: Same content daily kills motivation

2. **5+ days unused = STRONG PREFERENCE** (+100 points)
   - *Why*: Optimal spacing window for retention (1-7 days)
   - *Result*: Resources naturally rotate every 3-7 days

3. **Matches vocabulary due = BONUS** (+75 points)
   - *Why*: Contextual learning strengthens word-resource associations
   - *Example*: If 12 words are due from "Korean Podcast #5", prioritize that resource

4. **More vocabulary = PREFERENCE** (+log(count) * 5 points)
   - *Why*: Richer content provides more learning opportunities

5. **Has audio = PREFERENCE** (+20 points)
   - *Why*: Enables more activity types (listening, shadowing)

### Strategic Balance

**Variety Floor**: Never repeat yesterday (prevents burnout)
**Variety Ceiling**: 5+ days gets strong bonus (ensures rotation)
**Contextual Override**: Vocab due can override spacing (relevance wins)
**Tiebreaker**: Random selection prevents predictability

**Result**: Typical rotation is 3-7 days between seeing the same resource.

---

## Vocabulary Review: SRS Strategy

### When Vocab Review Happens

**Threshold**: Only if **5+ words are due** today
- *Why*: <5 words = too brief for dedicated activity (1-2 minutes)
- *Pedagogical*: Minimum viable practice session
- *Result*: Some days have no vocab review (intentional!)

### Why VocabularyQuiz (Not VocabularyMatching)

**VocabularyQuiz is used because:**
- Progressive difficulty: Multiple choice → text entry (scaffolded recall)
- Instant feedback reinforces learning
- Tracks individual word mastery scores
- Predictable time estimates (~3.5 words/minute)

**VocabularyMatching exists but is excluded from plans because:**
- Game mechanics prioritize speed over retention
- No individual word tracking
- Unpredictable time (competitive element)
- Better as standalone fun activity, not core curriculum

**Access**: VocabularyMatching is available via manual navigation for variety/fun.

### Contextual Learning Approach

**Best Case**: Words due from the same resource as today's plan
- *Example*: 8 words due from "Korean Stories Vol. 2" → plan uses that resource
- *Benefit*: Review words in their original learning context
- *Result*: Strengthens resource-word-meaning associations

**Fallback**: General pool if no resource has 5+ words due
- Still valuable, just less contextually rich

---

## Activity Sequencing: Pedagogical Progression

### The Standard Sequence

```
1. Vocabulary Review (if 5+ words due)  ← CONSOLIDATION
2. Input Activity (8-10 min)            ← COMPREHENSION
3. Output Activity (8-10 min)           ← PRODUCTION
4. Light Closer (5-8 min, optional)     ← REINFORCEMENT
```

### Why This Order: Research-Based Rationale

**Step 1: Vocab Review First**
- *Cognitive Load Theory*: Start with familiar material (low cognitive load)
- *Priming*: Activates relevant schema for upcoming content
- *SRS Timing*: Due words are time-sensitive (spacing intervals must be respected)

**Step 2: Input Before Output**
- *Krashen's Input Hypothesis*: Comprehensible input (i+1) must precede production
- *Lower Cognitive Load*: Passive processing easier than active generation
- *Model Building*: Internalize patterns before attempting to produce them

**Step 3: Output Activities**
- *Swain's Output Hypothesis*: Production reveals gaps and triggers noticing
- *Higher Cognitive Load*: Active recall, synthesis, generation
- *Transfer*: Apply patterns from input to new contexts

**Step 4: Light Closer (Optional)**
- *Spaced Repetition*: Quick review in low-pressure format
- *Positive Ending*: Game element maintains motivation
- *Conditional*: Only if session time budget allows

### Pedagogical Theories Referenced

1. **Cognitive Load Theory** (Sweller, 1988)
   - Manage intrinsic, extraneous, and germane cognitive load
   - Sequence from low → high complexity

2. **Input Hypothesis** (Krashen, 1985)
   - Comprehensible input (i+1) drives language acquisition
   - Must be slightly above current level

3. **Output Hypothesis** (Swain, 1985)
   - Pushed output triggers noticing of gaps
   - Production consolidates knowledge

4. **Spacing Effect** (Ebbinghaus, 1885; Cepeda et al., 2006)
   - Distributed practice superior to massed practice
   - Review timing follows forgetting curve

---

## Activity Selection: Variety Mechanisms

### Input Activities (Resource-Dependent)

**Available options depend on resource type:**
- Text only: `[Reading]`
- Audio: `[Listening, Reading]`
- Video: `[VideoWatching, Listening, Reading]`

**Selection logic:**
1. Filter out yesterday's activities (no consecutive days)
2. Prefer least recently used in past 3 days
3. Random tiebreaker

**Result**: Rotation through available input types over 3-4 day window.

### Output Activities (Skill-Dependent)

**Base options**: `[Translation, Cloze, Writing]`
**+Audio bonus**: `[Shadowing]` (highly effective for pronunciation)

**Selection logic:**
1. Filter out yesterday's activities
2. Prefer least recently used
3. Random tiebreaker

**Result**: Output activity types rotate, with shadowing appearing when resource has audio.

### Activity-Specific Rationales

Each activity type has a pedagogical purpose:

- **Reading**: Build comprehension and vocabulary recognition
- **Listening**: Train ear for natural speech patterns with transcript support
- **VideoWatching**: Authentic content with visual context for deeper understanding
- **Shadowing**: ⭐ Highly effective for pronunciation and speaking fluency
- **Translation**: Deep comprehension practice with active recall
- **Cloze**: Grammar and vocabulary recall in context
- **Writing**: Creative sentence construction for active vocabulary use
- **VocabularyGame**: Reinforcement in low-pressure, fun format

---

## Activity Coverage: What's In vs Out

### Activities in Daily Plans (8 core types)

**Always Available:**
- VocabularyReview (if 5+ words due)
- Reading (works with any resource)
- Translation (output practice)
- Cloze (fill-in-the-blank)
- Writing (creative production)

**Conditionally Available:**
- Listening (requires audio)
- VideoWatching (requires YouTube URL)
- Shadowing (requires audio)

**Sometimes Available:**
- VocabularyGame (only as "closer" if time remains)

### Activities Excluded from Plans (2 types)

**1. Scene Description (`DescribeAScenePage`)**
- *Purpose*: Describe photos in target language
- *Why excluded*:
  - Resource-independent (uses generic photos)
  - Open-ended creativity doesn't fit linear progression
  - No clear mastery tracking
  - Better as supplemental creative exercise
- *Access*: Manual navigation from dashboard

**2. HowDoYouSay**
- *Purpose*: Quick phrase translation lookup tool
- *Why excluded*:
  - Just-in-time learning (user has immediate need)
  - No long-term retention focus (lookup tool, not learning progression)
  - User-initiated queries, not curriculum-driven
- *Access*: Manual navigation from dashboard

### Design Philosophy

**Daily plans focus on**: Systematic, progressive, resource-based learning with clear mastery paths.

**Standalone tools serve**: Supplemental, exploratory, just-in-time needs without disrupting core curriculum.

---

## Key Trade-offs and Strategic Decisions

### 1. Deterministic vs Flexible
- **Trade-off**: Rigid rules vs adaptive personalization
- **Decision**: Deterministic (speed, reliability, testability win)
- **Future**: Could add LLM override for advanced users wanting experimentation

### 2. Resource-Centric vs Skill-Centric
- **Trade-off**: Focus on content vs competencies
- **Decision**: Resource-centric (resources contain rich context)
- **Rationale**: Skills are metadata on resources, not standalone curriculum

### 3. Variety vs Mastery
- **Trade-off**: Broad exposure vs deep practice
- **Decision**: Balanced (5-day rotation + contextual vocab review)
- **Rationale**: Spacing requires variety, but context deepens mastery

### 4. Fixed Sequence vs Dynamic Ordering
- **Trade-off**: Pedagogically optimal order vs user preference
- **Decision**: Fixed (vocab → input → output → game)
- **Rationale**: Cognitive load theory suggests novices benefit from structure

### 5. Plan Completeness vs User Freedom
- **Trade-off**: Comprehensive plans vs learner autonomy
- **Decision**: Guided plans + manual access to all activities
- **Rationale**: Default path for consistency, freedom for exploration

---

## Current Limitations and Opportunities

### Why Plans Feel Repetitive

**The Problem**: Despite variety mechanisms, learners report seeing:
- Same activity pairs (VocabularyQuiz → Reading frequently)
- Limited use of available activities
- Predictable patterns

**Root Causes**:
1. **Fixed Sequence**: Always vocab → input → output (rigid)
2. **Resource Constraints**: If most resources are text-only, always Reading
3. **VocabularyGame Underused**: Only appears as "closer" if time permits
4. **No Conversation Activity**: Not generated in plans despite being available
5. **Recency Window Too Narrow**: 3-day lookback may be insufficient

### Opportunities for Iteration

**Potential improvements without losing pedagogical soundness:**

1. **Vary the sequence occasionally**
   - Some days: Input → Output → Vocab review (vocab as reinforcement)
   - Some days: Vocab → Output → Input (flipped for variety)

2. **Promote VocabularyGame to core rotation**
   - Make it a valid "input" activity option
   - Not just a closer, but a primary vocabulary practice method

3. **Add Conversation to active rotation**
   - Include in output activity pool
   - Great for speaking practice, currently unused

4. **Widen recency detection**
   - Look back 5-7 days instead of 3
   - Prevents same activity appearing weekly

5. **Add "wildcard" days**
   - 1 in 5 plans could break the pattern
   - Use Scene Description or other standalone activities

6. **Resource capability awareness**
   - If user has mostly text resources, suggest adding audio content
   - Display variety metrics in dashboard

---

## Summary: Philosophy in One Paragraph

The plan generation strategy prioritizes **pedagogical soundness over flexibility**, using **deterministic algorithms grounded in Second Language Acquisition research** (Krashen, Swain, cognitive load theory, spacing effect) to create **fast, reliable, testable daily plans**. Resources are selected to **maximize spacing (5+ day rotation) while enabling contextual vocabulary review**, activities sequence from **low → high cognitive load (input → output)** with **variety enforced through recency filtering**, and the system **optimizes for long-term retention over short-term engagement**. The LLM was removed because **speed, consistency, and pedagogical control** matter more than adaptability for novice learners following a structured curriculum.

---

## Appendix: Activity Type Quick Reference

| Activity Type | Category | Requires | In Plans? | Purpose |
|--------------|----------|----------|-----------|---------|
| VocabularyReview | Consolidation | 5+ words due | Yes (priority 1) | SRS flashcard review |
| Reading | Input | Text resource | Yes (always) | Comprehension + vocab recognition |
| Listening | Input | Audio resource | Yes (conditional) | Audio comprehension with transcript |
| VideoWatching | Input | YouTube URL | Yes (conditional) | Authentic content with visual context |
| Shadowing | Output | Audio resource | Yes (conditional) | Pronunciation + fluency |
| Translation | Output | Any resource | Yes (always) | Active recall + comprehension |
| Cloze | Output | Any resource | Yes (always) | Grammar + vocab in context |
| Writing | Output | Any resource | Yes (always) | Creative production |
| VocabularyGame | Reinforcement | Skill | Yes (closer only) | Low-pressure review game |
| SceneDescription | Standalone | None | **No** | Open-ended creativity |
| Conversation | Output | Skill | **No** (but could be!) | AI chat practice |
| HowDoYouSay | Tool | None | **No** | Just-in-time lookup |

**Key Insight**: Several valuable activities exist but aren't used in plans, creating an opportunity to increase variety while maintaining pedagogical quality.
