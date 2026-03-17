# Vocabulary Hierarchy Learning Design Analysis

**Author:** Language Learning Systems Expert  
**Date:** 2025-01-27  
**Context:** SentenceStudio vocabulary tracking design

---

## Problem Summary

Korean (and many languages) presents vocabulary in multiple related forms:
- Standalone words (대학교 = university)
- Phrases containing those words (대학교 때 = during university)
- Inflected forms (주문 = order, 주문하다 = to order)
- Full expressions (피자를 주문하는 게 어때요 = how about ordering pizza)

Currently, each is tracked independently. We need a nuanced approach that recognizes relationships while supporting effective learning.

---

## Current System Architecture

**Data Model:**
- `VocabularyWord`: Stores target/native terms, lemma field (partially used), tags, example sentences
- `VocabularyProgress`: Tracks mastery via streak-based scoring (0.0–1.0 scale)
- `VocabularyAttempt`: Records individual practice attempts with context
- `VocabularyLearningContext`: Historical attempt data for spaced repetition

**Learning Algorithm:**
- **Streak-based scoring**: Correct answers build `CurrentStreak` + `ProductionInStreak`
- **Mastery threshold**: 0.85 score + 2 production attempts = "Known"
- **Spaced repetition**: SM-2 algorithm adjusts review intervals (1–365 days)
- **Verification system**: User-declared "Familiar" → 14-day grace period → verification probe

---

## 1. UX Implications: How Users Experience Hierarchies

### Recommended Approach: **Progressive Disclosure with Contextual Grouping**

**Visual Representation:**
- Show vocabulary in **collapsible word families** in list views
- Root word as primary entry with expandable derived forms beneath
- Example:
  ```
  📚 주문 (order) ⬤⬤◯◯◯  [Known]
     ├─ 주문하다 (to order) ⬤◯◯◯◯  [Learning]
     └─ 피자를 주문하는 게 어때요 (how about ordering pizza) ◯◯◯◯◯  [Unknown]
  ```

**Key UX Principles:**
1. **Preserve cognitive hierarchy**: Users naturally think "I know 주문, but need work on the verb form"
2. **Avoid overwhelming learners**: Don't show all 12 forms of a verb simultaneously
3. **Make relationships discoverable**: Tapping a word shows related forms in a detail view
4. **Support intentional practice**: Let users choose "practice the root" vs. "practice all forms"

**Implementation in Current UI:**
- Vocabulary lists (VocabularyList model) could have a `DisplayMode` enum: `Flat` | `Hierarchical`
- Detail pages show "Related Forms" section (currently showing `ExampleSentences`, add related vocabulary)

---

## 2. Progress Tracking: Mastery with Relationships

### Recommended Approach: **Tiered Independence with Inheritance Boost**

**Why Pure Independence Fails:**
- Learner masters "자주" (often) but sees "자주 마시는" (often drinks) as 100% unfamiliar
- Demotivating: "I already know this word!"

**Why Full Sharing Fails:**
- Mastering a noun shouldn't auto-mark the verb form as mastered
- Collocations require separate practice (피자를 주문하다 is not just 주문 + grammar)

**Proposed System: Tiered Mastery with Root Boost**

```
Root Word (lemma):
  - Full independent mastery tracking
  - MasteryScore 0.0 → 1.0

Derived Forms (inflections, phrases):
  - Independent mastery tracking BUT:
    - Initial MasteryScore = MIN(0.30, RootMastery * 0.4)
    - "Head start" that recognizes existing knowledge
    - Still requires practice to reach Known threshold

Example:
  주문 (root): MasteryScore = 0.85 [Known]
  주문하다 (verb): Initial MasteryScore = 0.34 [Learning]
    → User gets credit for knowing the root, but must practice the verb form
```

**Database Changes:**
- Use existing `Lemma` field in `VocabularyWord` to link forms
- Add `ParentWordId` nullable FK for explicit hierarchy (optional)
- When creating progress for derived form, check root progress and apply boost

**Algorithm Integration:**
```csharp
// In VocabularyProgressService.GetOrCreateProgressAsync()
if (newVocabularyWord.Lemma != null) {
    var rootProgress = await GetProgressByLemmaAsync(lemma, userId);
    if (rootProgress != null && rootProgress.MasteryScore > 0) {
        newProgress.MasteryScore = Math.Min(0.30f, rootProgress.MasteryScore * 0.4f);
        newProgress.CurrentStreak = 1; // Small head start
    }
}
```

---

## 3. Review Presentation: What to Show in SRS

### Recommended Approach: **Adaptive Presentation Based on Knowledge Level**

**Core Principle:** Match review difficulty to learner's current mastery

**Strategy by Mastery Level:**

| Mastery Range | Present | Rationale |
|--------------|---------|-----------|
| 0.0–0.30 (Unknown/Early) | Standalone word + 1 simple sentence | Build foundation recognition |
| 0.30–0.60 (Learning) | Phrase or collocation | Test in realistic context |
| 0.60–0.85 (Advanced Learning) | Full expression or sentence | Push toward production fluency |
| 0.85+ (Known - maintenance) | Random form from hierarchy | Prevent regression, test flexibility |

**Implementation:**
- `VocabularyQuizItem` model already supports `Context` field
- Review scheduler (`GetReviewCandidatesAsync`) selects appropriate form:
  ```csharp
  if (progress.MasteryScore < 0.30) {
      // Show root word only
      quizItem.WordToPresent = vocabularyWord.TargetLanguageTerm;
  } else if (progress.MasteryScore < 0.60) {
      // Show related phrase (requires linking)
      quizItem.WordToPresent = GetRelatedPhrase(vocabularyWord);
  } else {
      // Show full sentence
      quizItem.WordToPresent = vocabularyWord.ExampleSentences.FirstOrDefault()?.TargetSentence;
  }
  ```

**Edge Case: Multiple Forms Due for Review**
- **Don't show all at once** (creates review fatigue)
- **Prioritize harder forms** (e.g., show verb before noun if verb has lower mastery)
- **Interleave reviews** across multiple sessions

---

## 4. Import Deduplication: Handling Overlapping Vocabulary

### Recommended Approach: **Smart Merge with Relationship Linking**

**Scenario:** AI imports transcript containing "주문하다" (to order), but user already has "주문" (order)

**Decision Tree:**

```
1. Exact match found (same TargetLanguageTerm)?
   → Update existing record (add new ExampleSentence if applicable)
   
2. Lemma match found (same root but different form)?
   → Create NEW record
   → Link to root via Lemma field
   → Apply mastery boost (see Section 2)
   → Add to same VocabularyList as parent (optional)
   
3. Substring overlap but different words (e.g., 대학교 vs 대학교 때)?
   → Suggest relationship to user for confirmation
   → AI should provide confidence score for relationship
   → User approves → create with link
   → User rejects → create as independent
```

**Implementation Strategy:**
- Extend AI import logic to detect lemmas (language-specific, may require Korean NLP library)
- For Korean: Use `KoreanLemmatizer` or regex patterns for common suffixes (하다, 되다)
- Store relationship metadata: `RelationshipType` enum (Root, Inflection, Collocation, Phrase)

**User Control:**
- Settings toggle: "Auto-link related vocabulary" (default: ON)
- Manual linking UI: "Link to existing word" button in vocabulary detail page

---

## 5. Gamification: Motivational Impact of Hierarchies

### Recommended Approach: **Flexible Counting with Depth Bonuses**

**The Motivation Problem:**
- Pure flat counting: User has 1000 vocabulary items (overwhelming)
- Root-only counting: User has 200 roots (feels like not learning much)

**Proposed: Dual Metrics**

**1. Word Families Mastered (Primary Metric)**
- Count = # of root lemmas where at least one form is Known
- Example: Know "주문" OR "주문하다" → 1 word family
- **UX:** "You've mastered 247 word families 🎉"

**2. Total Forms Learned (Secondary Metric)**
- Count = # of Known forms (all variants)
- Example: Know "주문" AND "주문하다" → 2 forms
- **UX:** "547 vocabulary forms mastered across 247 words"

**Progress Bar Behavior:**
- Main progress bar shows **word families** (feels achievable)
- Detail view shows **forms within family** (shows depth of mastery)

**Streak Tracking:**
- Current system (consecutive correct answers) unchanged
- Add **Family Mastery Streak**: Days with at least 1 word family completed

**Vocabulary Count Display:**
```
📊 Your Progress
   🏆 247 Word Families Mastered
   📝 547 Total Forms Learned
   🔥 12-day learning streak
```

**Badges/Achievements:**
- "Root Master" (100 root words at Known)
- "Form Fanatic" (Master 5+ forms of a single word)
- "Phrase Power" (Master 50 phrase-based entries)

---

## Implementation Roadmap

### Phase 1: Foundation (Minimal Changes)
1. ✅ Already have `Lemma` field in `VocabularyWord` (unused)
2. Start populating `Lemma` during AI import (detect Korean verb/noun roots)
3. Apply mastery boost in `GetOrCreateProgressAsync()` (Section 2 logic)
4. Update vocabulary detail page to show "Related Forms" section

### Phase 2: Enhanced UX
5. Add collapsible hierarchy display in vocabulary lists
6. Implement adaptive review presentation (Section 3)
7. Build smart deduplication flow with user confirmation UI

### Phase 3: Gamification
8. Add dual metrics dashboard (word families vs. total forms)
9. Implement depth-based badges
10. A/B test motivational messaging

---

## Open Questions for Team Discussion

1. **Korean-specific NLP**: Should we integrate `korean-lemmatizer` npm package, or use simpler regex-based detection?
2. **User control vs. automation**: How much should users manage hierarchies manually vs. AI auto-linking?
3. **Phrase boundaries**: When is "대학교 때" a phrase vs. just "대학교" + grammar? (Linguistic complexity)
4. **Migration strategy**: Existing vocabulary has no hierarchy data—backfill via AI batch job or gradual enrichment?

---

## Conclusion

**Recommended Mental Model for Users:**
- **"I'm learning words and how to use them"**
- Words have forms, and forms live in context
- Progress is measured by word families (not overwhelmed by variants)
- Reviews intelligently test appropriate complexity for current skill level

**Key Success Metrics:**
- Reduced redundancy complaints ("I already know this!")
- Increased engagement with phrase-based learning
- Higher mastery scores on derived forms (shows boost is working)
- Maintained or improved retention rates (hierarchy doesn't confuse learners)
