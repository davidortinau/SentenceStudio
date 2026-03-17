# Vocabulary Hierarchy Tracking — Team Consensus Plan

**Date:** 2026-03-16  
**Requested by:** Captain David Ortinau  
**Contributors:** Zoe (Lead), River (AI/Prompt), Wash (Backend), SLA Expert, Learning Design Expert  
**Status:** PROPOSED — Awaiting Captain Approval

---

## Executive Summary

The team analyzed the vocabulary duplication problem where users must prove mastery of root words repeatedly across derived forms and phrases (e.g., 대학교 → 대학교 때, 주문 → 주문하다 → 피자를 주문하는 게 어때요). After examining data model, AI import, SRS scheduling, UX, and SLA research, we propose a **conservative MVP approach** that adds relationship tracking without disrupting existing mastery algorithms.

**Recommended Path:** Self-referential hierarchy with independent mastery, relationship-aware AI prompts, and UI hints. Delivers immediate value while preserving data integrity and proven SRS behavior.

---

## Problem Statement

### Current Behavior
- AI imports vocabulary as flat entries: each word/phrase is independent
- User masters "주문" (order - noun) → 100% complete
- Later encounters "주문하다" (to order - verb) → starts from 0% mastery
- Must re-prove knowledge of the same semantic core in different grammatical contexts

### Examples (Korean)

| Root Word | Derived Form | Full Expression | Current State |
|-----------|--------------|-----------------|---------------|
| 대학교 (university) | 대학교 때 (during university) | — | Each tracked separately |
| 자주 (often) | 자주 마시는 (often drinks) | — | No connection |
| 주문 (order - noun) | 주문하다 (to order) | 피자를 주문하는 게 어때요 (how about ordering pizza) | Three independent entries |

### Captain's Request
Move from "somewhat useful" to **"more nuanced"** — system should recognize linguistic relationships while still requiring meaningful practice.

---

## Team Analysis Summary

### Zoe (Lead): Architecture Framework
**Framed four design pillars:**

1. **Data Model** — How to represent relationships (FK, junction table, or lemma groups)
2. **Mastery Propagation** — Full credit, partial credit, or separate with hints
3. **AI Import Strategy** — Relationship detection during vocabulary extraction
4. **SRS Scheduling** — Coordinated review, weighted intervals, or independent

**Recommendation:** Start conservative (ParentWordId FK, independent mastery, no SRS changes) to validate UX before committing to complex mastery transfer or graph models.

---

### River (AI/Prompt): Import Strategy
**Proposed hierarchical vocabulary extraction:**

**New Response Schema:**
```json
{
  "vocabulary": [
    {
      "targetLanguageTerm": "대학교",
      "nativeLanguageTerm": "university",
      "lemma": "대학교",
      "relationshipType": "root",
      "relatedTerms": [],
      "linguisticMetadata": {
        "partOfSpeech": "noun",
        "frequency": "common",
        "difficulty": "beginner",
        "morphology": "standalone"
      }
    },
    {
      "targetLanguageTerm": "대학교 때",
      "nativeLanguageTerm": "during university",
      "lemma": "대학교",
      "relationshipType": "phrase",
      "relatedTerms": ["대학교"],
      "linguisticMetadata": {...}
    }
  ]
}
```

**Key Changes:**
- AI explicitly identifies relationship types (root, derived, inflected, phrase, compound, idiom)
- `relatedTerms` array links child to parent(s)
- Linguistic metadata supports future SRS enhancements (part of speech, frequency, difficulty)

**Prompt Enhancement:**
- Pre-fetch existing user vocabulary to detect overlaps
- Instruct AI to populate `relatedTerms` when extracting derived forms
- Return structured JSON with parent-child mappings

**Full specification:** [docs/vocabulary-hierarchy-prompt-design.md](vocabulary-hierarchy-prompt-design.md)

---

### Wash (Backend): Data Model
**Evaluated three schema options:**

| Option | Schema | Pros | Cons |
|--------|--------|------|------|
| **A: Self-Referential FK** | `ParentVocabularyWordId` + `RelationType` enum | Simple, fast queries, CoreSync-friendly, non-destructive migration | Single parent only (can't express multi-word compounds) |
| **B: Junction Table** | `VocabularyRelationship` (many-to-many) | Handles complex relationships, explicit relationship types per edge | Join complexity, separate sync table, harder queries |
| **C: Lemma Groups** | `LemmaGroupId` + `VocabularyLemmaGroup` table | Linguistically accurate, scales to large vocab | Major refactor, requires AI lemma assignment |

**Recommendation: Option A (Self-Referential)**

**Why:**
- Covers 90% of linguistic relationships (most words have single parent)
- Minimal migration (two NULLable columns, one migration, no new sync table)
- Fast traversal (direct FK lookup, no joins)
- Existing vocabulary unaffected (all default to `ParentWordId = null`)
- CoreSync-compatible (single table, existing conflict resolution works)

**Migration:**
```sql
ALTER TABLE VocabularyWord ADD COLUMN ParentVocabularyWordId TEXT NULL;
ALTER TABLE VocabularyWord ADD COLUMN RelationType TEXT NULL;
CREATE INDEX IX_VocabularyWord_Parent ON VocabularyWord(ParentVocabularyWordId);
```

**Key Decision:** Keep `VocabularyProgress` tracking **independent per word**. Hierarchy is metadata for UX (show related words), NOT for aggregating mastery scores.

**Rationale:**
- Phrases ARE distinct learning targets (e.g., "during university" ≠ "university")
- Context matters — production in isolation ≠ production in expression
- SRS intervals differ per difficulty level
- Aggregating scores would confuse CoreSync and violate current architecture

**Full specification:** [.squad/decisions/inbox/wash-vocabulary-hierarchy-proposal.md](.squad/decisions/inbox/wash-vocabulary-hierarchy-proposal.md)

---

### SLA Expert: Research Perspective
**Applied second-language acquisition theory to the design:**

#### 1. Word Families vs. Phrases: Distinct Challenges
**Principle:** Usage-based learning (Tomasello, 2003) — form-meaning connections are context-dependent.

- **대학교** (standalone noun) vs **대학교 때** (temporal phrase) activate different syntactic frames
- High-frequency collocations become **entrenched as units** independent of components (Ellis, 2002)
- Formulaic sequences stored **holistically**, not compositionally (Wray, 2002)

**Design Implication:** ✅ Treat root words and phrases as **separate items** with independent mastery.

#### 2. Receptive vs. Productive Knowledge
**Current system already distinguishes:**
- Recognition attempts (multiple-choice, matching)
- Production attempts (text entry, recall)
- `IsKnown` threshold requires BOTH recognition (0.85 mastery) AND production (2+ attempts)

**SLA validation:** This aligns with research showing production is stronger evidence of mastery (Laufer & Nation, 1999).

**Enhancement opportunity:** Add visual distinction in UI:
- 👁️ "Known (receptive)" — can recognize but not produce
- 🎯 "Known (productive)" — can recognize AND produce

#### 3. Spaced Repetition Coordination
**Problem with merged scheduling:** Reviewing "주문" (noun) does NOT strengthen memory for "주문하다" (verb) — forgetting curves are form-specific (Ellis & Sinclair, 1996).

**Recommended:** ✅ Independent schedules per form with coordination mechanisms:
1. **Difficulty bonus:** If root is Known, reduce initial difficulty for derived (faster first review)
2. **Leeching detection:** Flag if user fails derived form but knows root → suggest morphology practice
3. **Batch review:** Surface related items together for comparison/contrast (elaborative encoding)

#### 4. Chunking and Formulaic Sequences
**When is a phrase a "chunk"?**
- **Low-frequency phrase:** Track components separately (자주, 마시다)
- **High-frequency collocation:** Track as both chunk AND components (자주 마시는)
- **Fixed expression/idiom:** Track ONLY as chunk (피자를 주문하는 게 어때요)

**Full analysis:** [docs/sla-vocabulary-hierarchies-analysis.md](sla-vocabulary-hierarchies-analysis.md)

---

### Learning Design Expert: UX & Motivation
**Applied learning systems expertise to user experience:**

#### 1. UX: Progressive Disclosure
**Recommended:** Collapsible word families in list views
```
📚 주문 (order) ⬤⬤⬤⬤⬤ [Known]
   ├─ 주문하다 (to order) ⬤⬤◯◯◯ [Learning]
   └─ 피자를 주문하는 게 어때요 (...) ◯◯◯◯◯ [Unknown]
```

**Principles:**
- Preserve cognitive hierarchy (users think "I know 주문, but need work on the verb")
- Avoid overwhelming (don't show all 12 forms simultaneously)
- Make relationships discoverable (tap word → see related forms in detail)

#### 2. Progress Tracking: Tiered Independence with Inheritance Boost
**Problem with pure independence:** User masters "자주" → sees "자주 마시는" as 100% unfamiliar → demotivating.

**Problem with full sharing:** Mastering noun shouldn't auto-mark verb as mastered.

**Proposed:** Derived forms get **head start** but still require practice:
```csharp
if (rootProgress.IsKnown) {
    newProgress.MasteryScore = Math.Min(0.30f, rootProgress.MasteryScore * 0.4f);
    newProgress.CurrentStreak = 1;
}
```

**Example:**
- 주문 (root): MasteryScore = 0.85 [Known]
- 주문하다 (verb): Initial MasteryScore = 0.34 [Learning]
- User gets credit for knowing root, but must practice verb to reach Known (0.85)

#### 3. Review Presentation: Adaptive by Mastery Level

| Mastery Range | Present | Rationale |
|--------------|---------|-----------|
| 0.0–0.30 (Unknown) | Standalone word + simple sentence | Build foundation |
| 0.30–0.60 (Learning) | Phrase or collocation | Test in context |
| 0.60–0.85 (Advanced) | Full expression | Push toward production fluency |
| 0.85+ (Known) | Random form from hierarchy | Prevent regression |

#### 4. Import Deduplication: Smart Merge with Relationship Linking

**Decision Tree:**
1. **Exact match?** → Update existing record (add new example sentence)
2. **Lemma match?** → Create NEW record, link via `ParentWordId`, apply mastery boost
3. **Substring overlap?** → AI suggests relationship → user confirms

#### 5. Gamification: Dual Metrics
**Primary:** "Word families mastered" (# of lemmas where at least one form is Known)
**Secondary:** "Total forms learned" (shows depth)
**Badges:** "Root Master" (10 families), "Form Fanatic" (50 derived forms)

**Mental model:** "I'm learning words and how to use them" — forms live in context, progress measured by families.

**Full analysis:** [docs/vocabulary-hierarchy-learning-design.md](vocabulary-hierarchy-learning-design.md)

---

## Team Consensus: MVP Architecture

### 1. Data Model (Wash)
✅ **Option A: Self-Referential Hierarchy**

**Add to `VocabularyWord`:**
```csharp
public string? ParentVocabularyWordId { get; set; }
public VocabularyWordRelationType? RelationType { get; set; }

// Navigation properties
[JsonIgnore]
public VocabularyWord? ParentWord { get; set; }
[JsonIgnore]
public List<VocabularyWord> ChildWords { get; set; } = new();
```

**New Enum:**
```csharp
public enum VocabularyWordRelationType
{
    Inflection,    // 주문 → 주문하다 (verb conjugation)
    Phrase,        // 대학교 → 대학교 때 (word + particle)
    Idiom,         // Fixed expression
    Compound,      // Two words merged
    Synonym,       // Alternative expression
    Antonym        // Opposite (learning contrast)
}
```

**Migration:**
- EF Core migration with NULLable columns
- Existing vocabulary defaults to `ParentWordId = null` (independent)
- No data loss, fully reversible

---

### 2. Mastery Propagation (All Agents)
✅ **Independent Mastery with Inheritance Boost**

**Implementation:**
```csharp
// In VocabularyProgressService.GetOrCreateProgressAsync()
if (newVocabularyWord.ParentVocabularyWordId != null) {
    var parentProgress = await GetProgressAsync(userId, newVocabularyWord.ParentVocabularyWordId);
    if (parentProgress?.IsKnown == true) {
        // Derived word starts with 30-40% of parent's mastery
        newProgress.MasteryScore = Math.Min(0.30f, parentProgress.MasteryScore * 0.4f);
        newProgress.CurrentStreak = 1; // Small head start
        newProgress.NextReviewDate = DateTime.UtcNow.AddDays(1); // Earlier first review
    }
}
```

**Key Principle:** Hierarchy is metadata for UX, NOT for aggregating scores. Each word tracks separately.

---

### 3. AI Import Strategy (River)
✅ **Relationship-Aware Prompt**

**New Prompt Template:** `GenerateVocabularyWithHierarchy.scriban-txt`

**Instructs AI to:**
1. Identify relationship types (root, derived, inflected, phrase, compound, idiom)
2. Populate `relatedTerms` when extracting derived forms
3. Provide linguistic metadata (part of speech, frequency, difficulty, morphology)
4. Use existing user vocabulary to detect overlaps

**Import Flow:**
1. Pre-fetch user's existing vocabulary for target language
2. Pass to AI as `existing_vocabulary` parameter
3. AI returns hierarchical JSON with parent-child links
4. Service checks if parent exists → creates with `ParentVocabularyWordId`
5. Apply mastery boost if parent is Known

**Response Model:**
```csharp
public class VocabularyImportResponse {
    public List<VocabularyImportItem> Vocabulary { get; set; }
}

public class VocabularyImportItem {
    public string TargetLanguageTerm { get; set; }
    public string NativeLanguageTerm { get; set; }
    public string Lemma { get; set; }
    public string RelationshipType { get; set; } // root, derived, phrase, etc.
    public List<string> RelatedTerms { get; set; }
    public LinguisticMetadata LinguisticMetadata { get; set; }
}
```

---

### 4. SRS Scheduling (Conservative)
✅ **Independent Scheduling (No Changes to SM-2)**

**Rationale:**
- Current SM-2 algorithm is proven and stable
- SLA research validates independent schedules per form
- Avoid complexity risk in critical learning path

**Future Enhancement (Phase 2):**
- Batch review suggestions (surface related items together)
- Coordinated intervals (root mastery → boost derived form intervals)
- Leeching detection across word families

---

### 5. UI Visualization (Kaylee's Domain — Not Specified Here)
**Recommended patterns from Learning Design Expert:**
- Vocabulary list: Collapsible word families (root + expandable children)
- Detail page: "Related Words" section with mastery badges
- Import preview: Show relationships ("Adding '자주 마시는' — related to known word '자주'")
- Progress view: Dual metrics (word families + total forms)

**Deferred to Kaylee for implementation design.**

---

## Implementation Phases

### Phase 1: Prompt & Schema (2-3 days)
**Owner:** River + Wash

- [ ] Create `GenerateVocabularyWithHierarchy.scriban-txt` prompt template
- [ ] Add response models: `VocabularyImportResponse`, `VocabularyImportItem`, `LinguisticMetadata`
- [ ] Add `ParentVocabularyWordId`, `RelationType` to `VocabularyWord` model
- [ ] Create EF Core migration (NULLable columns, index on ParentWordId)
- [ ] Update `ResourceEdit.GenerateVocabulary()` to call new prompt
- [ ] Process hierarchical response: create parent → create child with FK
- [ ] Test with Korean transcript samples

**Acceptance Criteria:**
- AI extracts root words + derived forms with relationship types
- Parent words exist before children are created
- Existing vocabulary unaffected (all ParentWordId = null)

---

### Phase 2: Mastery Inheritance (1-2 days)
**Owner:** Wash

- [ ] Update `VocabularyProgressService.GetOrCreateProgressAsync()`
- [ ] Check for parent progress when creating child progress
- [ ] Apply mastery boost: `MasteryScore = min(0.30, parentMastery * 0.4)`
- [ ] Add `CurrentStreak = 1` for head start
- [ ] Set earlier `NextReviewDate` (1 day vs. default)
- [ ] Test mastery propagation with known root words

**Acceptance Criteria:**
- Derived word starts with 30-40% mastery if parent is Known
- Still requires 2+ production attempts to reach Known threshold
- Independent mastery tracking preserved

---

### Phase 3: Repository Methods (1 day)
**Owner:** Wash

- [ ] Add `GetUserVocabularyForLanguageAsync(string language)` (for import pre-fetch)
- [ ] Add `GetRelatedWordsAsync(string wordId)` (for UI display)
- [ ] Add `GetWordsByLemmaAsync(string lemma)` (for deduplication)
- [ ] Add CoreSync validation tests for new FK relationships

**Acceptance Criteria:**
- Repository methods work cross-platform (MAUI + WebApp)
- CoreSync handles parent-child relationships correctly
- Cascading deletes don't break sync state

---

### Phase 4: UI Visualization (2-3 days)
**Owner:** Kaylee

- [ ] Add "Related Words" section to vocabulary detail page
- [ ] Show parent → child relationships in list views
- [ ] Add mastery badges for word families
- [ ] Update import preview UI to show relationships
- [ ] Add filter/group by relationship type (optional)

**Acceptance Criteria:**
- Users can see word family hierarchies
- Relationships discoverable via UI (not hidden)
- Accessibility: keyboard navigation, screen reader support

---

### Phase 5: Testing (1-2 days)
**Owner:** Jayne

- [ ] Test AI prompt accuracy on Korean transcript samples (target: 90%+ precision)
- [ ] Verify mastery inheritance with known root words
- [ ] Test CoreSync with hierarchical vocabulary across devices
- [ ] Verify no duplicate entries created during import
- [ ] Test with multiple languages (Korean, Spanish, Japanese)
- [ ] E2E test: import → sync → cross-device display

**Acceptance Criteria:**
- AI relationship detection accurate (90%+ on manual review)
- Mastery boost works correctly (derived words start with credit)
- Sync conflict resolution works for hierarchical data

---

## Success Metrics

**Definition of Done:**

1. **AI Relationship Detection:** 90%+ precision on Korean transcript samples (manually verified)
2. **Mastery Inheritance:** Derived words start with 30-40% of root mastery (verified via progress records)
3. **No Duplication:** Zero duplicate vocabulary entries created during import (E2E test passes)
4. **User Validation:** Positive feedback on relationship tracking ("The app knows I already learned the root!")
5. **Data Integrity:** No data loss during migration, existing vocabulary preserved
6. **CoreSync Stability:** Hierarchical vocabulary syncs correctly across MAUI + WebApp

---

## Open Questions for Captain

### 1. Mastery Boost Aggressiveness
**Current proposal:** 30-40% of parent mastery (conservative)

**Options:**
- **30%** — More practice required (conservative, safer)
- **40%** — Balanced (team recommendation)
- **50%** — Faster progression (optimistic)

**Question:** Which feels right for your learning philosophy?

---

### 2. Multi-Parent Relationships
**Current proposal:** Single-parent model (covers 90% of cases)

**Edge case:** "자주 마시는" (often drinks) derives from BOTH "자주" (often) AND "마시다" (to drink)

**Options:**
- **Phase 1:** Assign single most relevant parent (AI chooses "마시다" as primary)
- **Phase 2:** Upgrade to junction table for multi-parent support

**Question:** Is single-parent acceptable for MVP, or must we handle multi-parent from day one?

---

### 3. Backfill Historical Vocabulary
**Current proposal:** New imports only (existing vocabulary stays flat)

**Alternative:** Run AI over existing vocabulary to detect relationships retrospectively

**Trade-offs:**
- **Backfill:** Users immediately see relationships for all vocabulary (better UX, higher risk)
- **New only:** Safe, incremental, but existing vocab feels second-class

**Question:** Backfill now, or wait until Phase 2 after validating accuracy?

---

### 4. User Control Over Relationships
**Current proposal:** AI-assigned relationships (no user editing)

**Alternative:** Add "Edit Relationship" UI for manual correction

**Trade-offs:**
- **AI-only:** Simpler implementation, but mistakes can't be fixed
- **User editable:** More control, but adds UI complexity

**Question:** Trust AI or enable manual override?

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| AI prompt returns wrong relationships | Users get incorrect mastery credit | Validate on test corpus (90%+ target); add manual review UI in Phase 2 |
| Existing vocabulary breaks with schema change | Data loss or corruption | NULLable FK migration (additive only); test on DB copy before production |
| Performance degrades with FK lookups | Slow import/quiz generation | Add index on `ParentVocabularyWordId`; cache related words in memory |
| Users confused by partial mastery | "Why does this word start at 40%?" | Add tooltip: "You already know '주문', so you're starting with credit" |
| CoreSync conflicts with hierarchical data | Orphaned children, broken FKs | Validate FK consistency in sync conflict resolution; test cross-device |

---

## Team Recommendations

### Zoe (Lead)
**Start conservative.** Phase 1 only — prove AI can detect relationships and UX is useful before adding mastery transfer logic or graph models. If prompt works, rest is wiring. If not, iterate on prompt first.

### River (AI/Prompt)
**Prompt quality is critical.** Test on 10-20 real Korean transcripts before declaring success. 90%+ precision is achievable with structured output + linguistic examples in prompt.

### Wash (Backend)
**Schema is sound.** Self-referential FK handles 90% of cases, minimal migration risk, CoreSync-compatible. Can upgrade to junction table later if multi-parent becomes common.

### SLA Expert
**Independent mastery is correct.** SLA research validates separate tracking per form. Coordination mechanisms (difficulty bonus, batch review) are enhancements, not requirements.

### Learning Design Expert
**UX is the unlock.** Hierarchy only delivers value if users can SEE relationships. Collapsible word families + mastery boost tooltips are minimum viable visualization.

---

## Next Steps

1. **Captain Decision (You):**
   - Approve MVP architecture (Phase 1-3)
   - Answer open questions (mastery boost %, multi-parent, backfill, user editing)
   - Green-light implementation start

2. **River:** Prototype prompt, test on 10 Korean transcripts, report accuracy

3. **Wash:** Create EF Core migration, coordinate with River on response schema

4. **Kaylee:** UI mockups for word family display (detail page + list view)

5. **Jayne:** Define E2E test scenarios for hierarchical vocabulary

6. **Team:** Reconvene after Phase 1 to review UX and decide on Phase 2 (mastery transfer refinement, SRS coordination)

---

## Appendix: Related Documents

- [Zoe's Architecture Framing](.squad/decisions/inbox/zoe-vocab-relationships-architecture.md)
- [River's AI Prompt Design](vocabulary-hierarchy-prompt-design.md)
- [Wash's Data Model Proposal](.squad/decisions/inbox/wash-vocabulary-hierarchy-proposal.md)
- [SLA Research Analysis](sla-vocabulary-hierarchies-analysis.md)
- [Learning Design Analysis](vocabulary-hierarchy-learning-design.md)

---

**Status:** PROPOSED — Awaiting Captain David Ortinau's approval to proceed with Phase 1 implementation.
