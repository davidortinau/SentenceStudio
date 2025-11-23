# Placement Test System - Research-Based Baseline Assessment

## Overview

The Placement Test establishes a learner's baseline vocabulary knowledge to prevent redundant practice and provide accurate content recommendations. Based on research by Laufer & Nation (VST) and Nation & Beglar's Productive Vocabulary Levels Test.

## The Problem

- New learners enter the app with existing knowledge from external study
- Self-reported CEFR level doesn't reveal specific vocabulary gaps
- Without baseline data, app treats known words as "unknown" → demotivating practice
- Need **fast, accurate assessment** to bootstrap VocabularyProgress table

## Academic Foundation

### Research-Based Principles

1. **Frequency-Band Sampling** (Nation & Beglar, 2007)
   - Vocabulary follows Zipfian distribution (most frequent 2000 words = ~80% of texts)
   - Test small samples from each frequency tier → infer total knowledge via statistics
   - 10-15 items per band gives 95% confidence intervals

2. **Recognition → Production Hierarchy** (Laufer & Nation, 1999)
   - **Receptive knowledge**: "I recognize 안녕하세요 means 'hello'"
   - **Productive knowledge**: "How do I say 'hello'? → 안녕하세요"
   - Recognition develops first, production lags behind
   - Testing both reveals **depth** of knowledge, not just breadth

3. **Statistical Inference**
   - If learner scores 75%+ on a frequency band → mark entire band as "known" (conservative)
   - Accounts for test error: use 80% discount factor (e.g., 90% accuracy → 0.72 MasteryScore)
   - Prevents false positives while avoiding hours of testing

4. **Computerized Adaptive Testing (CAT)** (Optional Enhancement)
   - Start mid-range, branch up/down based on performance
   - Converges to learner's "ceiling" in 20-30 items vs. 60+ in fixed tests
   - Used in TOEFL, Duolingo English Test

## Implementation: Three-Tier Strategy

### **Option 1: Quick Recognition Sweep (5-10 minutes)** ✅ FASTEST

**Format:**
- 12 items per frequency band (High: 1-1K, Mid: 1-3K, Low: 3-5K, Academic: 5-10K)
- Multiple choice: "What does '가다' mean?" → [go, come, eat, see]
- Pure recognition, no production

**Scoring Logic:**
```csharp
if (accuracyInBand >= 0.75f)
{
    // Mark all words in that frequency range as known
    BulkUpdateVocabularyProgress(
        frequencyMin: bandMin, 
        frequencyMax: bandMax,
        masteryScore: accuracyInBand * 0.8f,  // Conservative discount
        currentPhase: LearningPhase.Production  // Ready for next phase
    );
}
```

**Pros:**
- Fastest option (~48 items total)
- Low cognitive load
- Gets learner into app quickly

**Cons:**
- Recognition-only → may overestimate productive knowledge
- No differentiation between passive/active vocabulary

---

### **Option 2: Adaptive Recognition Test (10-15 minutes)** ⚡ MOST EFFICIENT

**Format:**
- Start with 1500-frequency words (mid-range)
- Branch logic:
  - 3 correct in a row → jump to 3000-frequency
  - 2 wrong in a row → drop to 500-frequency
- Converge when oscillation stabilizes

**Scoring Logic:**
```csharp
int ceilingFrequency = AdaptiveTestEngine.FindCeiling(responses);
// Mark all words below ceiling as known
BulkUpdateVocabularyProgress(
    frequencyMax: ceilingFrequency,
    masteryScore: 0.65f,  // Conservative for adaptive inference
    currentPhase: LearningPhase.Production
);
```

**Pros:**
- Fewest items needed (20-30 vs. 48)
- Precise identification of learner's frontier
- Engaging (feels personalized)

**Cons:**
- More complex algorithm
- Requires frequency metadata on all words

---

### **Option 3: Hybrid Assessment (15-20 minutes)** 🏆 RECOMMENDED

**Format:**
- **Phase 1 - Recognition** (12 items × 4 bands = 48 items)
  - Same as Option 1
- **Phase 2 - Production** (15 items from high-frequency)
  - Cloze format: "I need to _____ (사다) groceries." → type "buy"
  - Tests productive retrieval, not just recognition

**Scoring Logic:**
```csharp
foreach (var band in frequencyBands)
{
    if (band.RecognitionAccuracy >= 0.75f)
    {
        if (band.ProductionAccuracy >= 0.75f)
        {
            // DEEP knowledge → ready for application
            BulkUpdate(
                masteryScore: band.RecognitionAccuracy * 0.8f,
                currentPhase: LearningPhase.Application,
                productionCorrect: 3  // Simulate confidence
            );
        }
        else if (band.RecognitionAccuracy >= 0.75f)
        {
            // SHALLOW knowledge → needs production practice
            BulkUpdate(
                masteryScore: band.RecognitionAccuracy * 0.6f,
                currentPhase: LearningPhase.Production,
                recognitionCorrect: 3
            );
        }
    }
}
```

**Pros:**
- Distinguishes receptive vs. productive knowledge
- Maps directly to app's `LearningPhase` enum
- Prevents false sense of mastery
- Aligns with research (Laufer & Nation's Productive VST)

**Cons:**
- Longest duration (15-20 min)
- Production items require typing (mobile friction)

---

## Data Model

### Database Tables

```csharp
PlacementTests
- Id, UserId, TestType, Status
- StartedAt, CompletedAt
- EstimatedVocabularySizeMin/Max
- EstimatedCEFRLevel
- WordsMarkedAsKnown, WordsMarkedAsLearning

PlacementTestItems
- Id, PlacementTestId, VocabularyWordId
- ItemType (Recognition | Production)
- FrequencyRank (1-10000)
- IsCorrect, UserAnswer
- PresentedAt, AnsweredAt, ResponseTimeMs
```

### Frequency Bands (CEFR Alignment)

| Band | Frequency Range | CEFR Approx | Expected Mastery |
|------|----------------|-------------|------------------|
| High Frequency | 1-1,000 | A1-A2 | Beginners: 50-80% |
| Mid Frequency | 1,001-3,000 | A2-B1 | Intermediate: 40-70% |
| Low Frequency | 3,001-5,000 | B1-B2 | Upper-Int: 30-60% |
| Academic | 5,001-10,000 | B2-C1 | Advanced: 20-40% |

### CEFR Inference Logic

```csharp
if (Academic.Accuracy >= 0.70f) → "C1"
if (LowFreq.Accuracy >= 0.70f) → "B2"
if (MidFreq.Accuracy >= 0.70f) → "B1"
if (HighFreq.Accuracy >= 0.70f) → "A2"
else → "A1"
```

---

## UI/UX Flow

### 1. Entry Points

- **First-time users**: Prompt after profile creation
- **Settings menu**: "Retake Placement Test"
- **Smart triggers**: If app detects high accuracy (>90% in recent quizzes) → suggest placement test

### 2. Instructions Screen

```
📋 Vocabulary Placement Test

This quick assessment will help us understand your current Korean vocabulary level.

⏱️ Duration: 15-20 minutes
📊 Tests recognition and production
🎯 Establishes your baseline
✅ Automatically marks words you know

When to take this test:
• First time using the app
• After studying elsewhere
• If content feels too easy/hard
```

### 3. Testing Phase

- Progress bar: "Item 23 / 63"
- **Recognition items**: 4-option multiple choice
- **Production items**: Text entry with placeholder
- No skip button (ensures statistical validity)
- Auto-save progress (can resume if interrupted)

### 4. Results Screen

```
🎉 Assessment Complete!

Estimated Vocabulary: ~2,400 words
CEFR Level: B1

✅ 1,850 words marked as known
📚 450 words marked for review

[Frequency Band Breakdown]
High Frequency (1-1K): 85% recognition, 72% production
Mid Frequency (1-3K): 68% recognition, 45% production
Low Frequency (3-5K): 42% recognition, —
Academic (5-10K): 18% recognition, —
```

---

## Next Steps for Implementation

### Prerequisites

1. **Add FrequencyRank to VocabularyWord**
   ```csharp
   public int FrequencyRank { get; set; } // 1-10000, based on corpus data
   ```

2. **Import Korean Frequency Data**
   - Source: National Institute of Korean Language corpus
   - Or: Use existing word lists with frequency annotations
   - Map to existing VocabularyWord IDs

3. **Distractor Generation**
   - For multiple-choice items, need semantically plausible wrong answers
   - Simple approach: Same frequency band + different meaning
   - Advanced: Use word embeddings to find near-synonyms

4. **Migration**
   ```bash
   dotnet ef migrations add AddPlacementTestTables
   dotnet ef database update
   ```

### Service Registration

```csharp
// In MauiProgram.cs
services.AddScoped<PlacementTestService>();
```

### Localization Strings Needed

```
PlacementTestTitle
PlacementTestInstructions
PlacementTestWhenToTake
RecognitionItemPrompt (e.g., "What does '{word}' mean?")
ProductionItemPrompt (e.g., "How do you say '{word}'?")
Start, Submit, Continue
```

---

## Research References

1. **Laufer, B., & Nation, P. (1999).** A vocabulary-size test of controlled productive ability. *Language Testing*, 16(1), 33-51.

2. **Nation, I. S. P., & Beglar, D. (2007).** A vocabulary size test. *The Language Teacher*, 31(7), 9-13.

3. **Nation, I. S. P. (2006).** How large a vocabulary is needed for reading and listening? *Canadian Modern Language Review*, 63(1), 59-82.

4. **Schmitt, N. (2010).** *Researching Vocabulary: A Vocabulary Research Manual.* Palgrave Macmillan.

5. **Webb, S., & Nation, P. (2017).** How Vocabulary Is Learned. Oxford University Press.

---

## Future Enhancements

### Short-term
- Add response time tracking → detect lucky guesses (too fast) or confusion (too slow)
- Implement typo tolerance for production items (Levenshtein distance ≤ 2)
- Add option to skip production phase (faster test, less accurate)

### Medium-term
- **Adaptive engine** (Option 2) with Bayesian item selection
- **Semantic distractors** using word2vec/fastText embeddings
- **Multi-modal testing**: Include audio recognition ("Listen and choose meaning")

### Long-term
- **Grammar placement test** using same statistical sampling approach
- **Listening comprehension placement** with graded audio clips
- **Continuous calibration**: Re-run micro-tests quarterly to adjust baseline as learner progresses

---

## Learning Science Rationale

This design applies:

1. **Efficient assessment via sampling** → respects learner's time
2. **Recognition vs. production testing** → accurate depth measurement
3. **Statistical inference** → avoids testing every word individually
4. **Conservative scoring** → prevents overconfidence from test luck
5. **Immediate application** → baseline data instantly improves content recommendations

**Result:** Learners skip redundant practice, see relevant content faster, and feel the app "understands" their level → higher engagement and retention.
