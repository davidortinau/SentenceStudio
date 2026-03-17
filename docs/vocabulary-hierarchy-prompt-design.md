# Vocabulary Hierarchy Tracking — AI Prompt Design

**Author:** River (AI/Prompt Engineer)  
**Date:** 2025-03-15  
**Status:** PROPOSED  

---

## Problem Statement

When importing vocabulary from transcripts, the AI returns flat vocabulary entries. This creates overlap and duplication:

- A user masters "대학교" (university) as a standalone word
- Later, "대학교 때" (during university) appears in a transcript
- The system treats these as completely separate items, forcing the user to re-prove mastery

This violates SLA principles: learners build from known roots to complex expressions. Our current import strategy ignores linguistic relationships.

---

## Proposed Solution

Restructure the vocabulary import prompt and response schema to capture **linguistic hierarchy** during AI generation. This allows the system to:

1. **Link derived forms to root words** — "주문하다" (to order) links to "주문" (order)
2. **Identify phrases containing known words** — "피자를 주문하는 게 어때요" contains "주문하다"
3. **Detect inflected forms** — "마시는" (drinks) links to "마시다" (to drink)
4. **Credit prior mastery** — If the user knows "자주" (often), don't start "자주 마시는" (often drinks) from zero

---

## 1. Relationship Types

Define explicit vocabulary relationships the AI should detect:

| Type | Description | Example (Korean → English) |
|------|-------------|----------------------------|
| `root` | Base/standalone word | 대학교 (university) |
| `derived` | Grammatical transformation | 주문 → 주문하다 (order → to order) |
| `inflected` | Conjugated/inflected form | 마시다 → 마시는 (to drink → drinks) |
| `phrase` | Multi-word expression containing root | 대학교 때 (during university, contains 대학교) |
| `compound` | New meaning from multiple roots | 자주 + 마시다 → 자주 마시는 (often + drink → often drinks) |
| `idiom` | Non-compositional phrase | 고생 끝에 낙이 온다 (after hardship comes ease) |

---

## 2. Structured JSON Response Schema

### Current Response (Flat)
```json
[
  { "TargetLanguageTerm": "대학교", "NativeLanguageTerm": "university" },
  { "TargetLanguageTerm": "대학교 때", "NativeLanguageTerm": "during university" }
]
```

**Problem:** No indication that "대학교 때" contains "대학교".

---

### Proposed Response (Hierarchical)

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
      "linguisticMetadata": {
        "partOfSpeech": "noun_phrase",
        "frequency": "common",
        "difficulty": "intermediate",
        "morphology": "noun + time_particle"
      }
    },
    {
      "targetLanguageTerm": "주문",
      "nativeLanguageTerm": "order",
      "lemma": "주문",
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
      "targetLanguageTerm": "주문하다",
      "nativeLanguageTerm": "to order",
      "lemma": "주문",
      "relationshipType": "derived",
      "relatedTerms": ["주문"],
      "linguisticMetadata": {
        "partOfSpeech": "verb",
        "frequency": "common",
        "difficulty": "beginner",
        "morphology": "noun + -하다 (do)"
      }
    },
    {
      "targetLanguageTerm": "피자를 주문하는 게 어때요",
      "nativeLanguageTerm": "how about ordering pizza",
      "lemma": "주문하다",
      "relationshipType": "phrase",
      "relatedTerms": ["주문하다", "피자"],
      "linguisticMetadata": {
        "partOfSpeech": "sentence",
        "frequency": "common",
        "difficulty": "intermediate",
        "morphology": "full_expression"
      }
    }
  ]
}
```

---

## 3. Revised AI Prompt Template

**File:** `src/SentenceStudio.AppLib/Resources/Raw/GenerateVocabularyWithHierarchy.scriban-txt`

```scriban
You are a language learning assistant specializing in {{target_language}}. Analyze this transcript and extract ALL vocabulary words, phrases, and expressions that would be useful for a {{native_language}}-speaking learner.

## CRITICAL INSTRUCTIONS

1. **Identify Linguistic Relationships:**
   - Mark standalone words as "root"
   - Mark grammatical transformations as "derived" (e.g., noun → verb)
   - Mark inflected forms as "inflected" (e.g., verb stem → conjugated)
   - Mark multi-word expressions as "phrase" or "compound"
   - Mark idioms with non-compositional meaning as "idiom"

2. **Link Related Terms:**
   - When extracting "대학교 때", include "대학교" in `relatedTerms`
   - When extracting "주문하다", include "주문" in `relatedTerms`
   - When extracting "자주 마시는", include both "자주" and "마시다" in `relatedTerms`

3. **Provide Linguistic Metadata:**
   - `partOfSpeech`: noun, verb, adjective, adverb, noun_phrase, verb_phrase, sentence, etc.
   - `frequency`: common, uncommon, rare
   - `difficulty`: beginner, intermediate, advanced
   - `morphology`: Brief description of word structure (e.g., "noun + -하다", "verb stem + -는")

4. **Field Definitions (DO NOT SWAP):**
   - `targetLanguageTerm`: The word/phrase in {{target_language}}
   - `nativeLanguageTerm`: The {{native_language}} translation
   - `lemma`: Dictionary/base form (same as targetLanguageTerm for roots)
   - `relationshipType`: One of: root, derived, inflected, phrase, compound, idiom
   - `relatedTerms`: Array of {{target_language}} terms this word relates to (empty for roots)

5. **Completeness:**
   - Extract ALL words/phrases from the transcript
   - Include basic words even if they seem simple
   - Prefer dictionary forms for `lemma` (e.g., "마시다" not "마시는")

## Response Format

Return ONLY valid JSON matching this schema:

```json
{
  "vocabulary": [
    {
      "targetLanguageTerm": "string",
      "nativeLanguageTerm": "string",
      "lemma": "string",
      "relationshipType": "root|derived|inflected|phrase|compound|idiom",
      "relatedTerms": ["string"],
      "linguisticMetadata": {
        "partOfSpeech": "string",
        "frequency": "common|uncommon|rare",
        "difficulty": "beginner|intermediate|advanced",
        "morphology": "string"
      }
    }
  ]
}
```

## Transcript

{{transcript}}

## Target Language

{{target_language}}

## Native Language

{{native_language}}
```

---

## 4. Detection of Existing Vocabulary

**Problem:** When importing, we need to know if the user already has "주문" in their vocabulary before adding "주문하다".

**Solution:** Pre-query strategy before calling AI.

### Implementation in `ResourceEdit.razor`

```csharp
private async Task GenerateVocabulary()
{
    // ... existing setup code ...

    // NEW: Pre-fetch user's existing vocabulary for this language
    var existingVocabulary = await ResourceRepo.GetUserVocabularyForLanguageAsync(targetLanguage);
    var existingTermsJson = JsonSerializer.Serialize(existingVocabulary.Select(v => new {
        term = v.TargetLanguageTerm,
        lemma = v.Lemma ?? v.TargetLanguageTerm
    }));

    // Inject existing vocabulary into prompt
    string prompt = await RenderScribanTemplate("GenerateVocabularyWithHierarchy", new {
        transcript = resource.Transcript,
        target_language = targetLanguage,
        native_language = nativeLanguage,
        existing_vocabulary = existingTermsJson
    });

    var response = await AiSvc.SendPrompt<VocabularyImportResponse>(prompt);

    // ... process response ...
}
```

### Updated Prompt Section

Add to the prompt template:

```scriban
## Existing User Vocabulary

The user already knows these {{target_language}} terms. When extracting new vocabulary, populate `relatedTerms` if any new term contains or derives from these:

{{existing_vocabulary}}

**Important:** If a term is already known, you may still extract it if it appears in a new context (e.g., a phrase or expression). The system will link them automatically.
```

---

## 5. SLA-Aware Metadata for Review Scheduling

The `linguisticMetadata` field enables **spacing algorithm enhancements**:

### Metadata Fields

| Field | Purpose | Example Use in Scheduling |
|-------|---------|---------------------------|
| `partOfSpeech` | Grammatical category | Nouns are easier to retain than verbs → longer intervals |
| `frequency` | How common the word is | Common words reviewed more often (higher exposure) |
| `difficulty` | Learner complexity level | Beginner words get shorter intervals initially |
| `morphology` | Word structure description | Derived forms inherit partial mastery from root |

### Example: Inherited Mastery Score

When "주문하다" (to order) is imported and "주문" (order) is already Known:

```csharp
var rootWord = await ResourceRepo.GetWordByTargetTermAsync("주문");
var rootProgress = await ProgressRepo.GetProgressAsync(userId, rootWord.Id);

if (rootProgress != null && rootProgress.IsKnown)
{
    // Bootstrap new derived word with 40% of root's mastery
    newProgress.MasteryScore = rootProgress.MasteryScore * 0.4f;
    newProgress.CurrentStreak = 2; // Start with credit
    newProgress.FirstSeenAt = DateTime.UtcNow;
    newProgress.NextReviewDate = DateTime.UtcNow.AddDays(1); // Earlier first review
}
```

**Rationale:** If the user knows "주문", they already understand the semantic core of "주문하다". They only need to learn the "-하다" grammatical pattern.

---

## 6. Database Schema Changes

### Add to `VocabularyWord` table

Current schema already has:
- `Lemma` (string, nullable) ✅

**NEW columns required:**

```sql
ALTER TABLE VocabularyWord ADD COLUMN RelationshipType TEXT; -- root, derived, inflected, phrase, compound, idiom
ALTER TABLE VocabularyWord ADD COLUMN PartOfSpeech TEXT; -- noun, verb, adjective, etc.
ALTER TABLE VocabularyWord ADD COLUMN Frequency TEXT; -- common, uncommon, rare
ALTER TABLE VocabularyWord ADD COLUMN Difficulty TEXT; -- beginner, intermediate, advanced
ALTER TABLE VocabularyWord ADD COLUMN Morphology TEXT; -- "noun + -하다", "verb stem + -는", etc.
```

### New junction table: `VocabularyWordRelations`

```sql
CREATE TABLE VocabularyWordRelations (
    Id TEXT PRIMARY KEY,
    SourceWordId TEXT NOT NULL, -- FK to VocabularyWord (e.g., "주문하다")
    RelatedWordId TEXT NOT NULL, -- FK to VocabularyWord (e.g., "주문")
    RelationType TEXT NOT NULL,  -- "contains", "derived_from", "inflected_from", "compound_of"
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY (SourceWordId) REFERENCES VocabularyWord(Id) ON DELETE CASCADE,
    FOREIGN KEY (RelatedWordId) REFERENCES VocabularyWord(Id) ON DELETE CASCADE
);

CREATE INDEX idx_vocab_relations_source ON VocabularyWordRelations(SourceWordId);
CREATE INDEX idx_vocab_relations_related ON VocabularyWordRelations(RelatedWordId);
```

**Purpose:** Track explicit relationships between vocabulary items for:
- Mastery inheritance (root → derived)
- Quiz generation (avoid testing "대학교" and "대학교 때" in the same session)
- Progress visualization (show word families)

---

## 7. Response Model Updates

### New C# Models

**File:** `src/SentenceStudio.Shared/Models/VocabularyImportResponse.cs`

```csharp
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class VocabularyImportResponse
{
    [JsonPropertyName("vocabulary")]
    public List<VocabularyImportItem> Vocabulary { get; set; } = new();
}

public class VocabularyImportItem
{
    [JsonPropertyName("targetLanguageTerm")]
    public string TargetLanguageTerm { get; set; } = string.Empty;

    [JsonPropertyName("nativeLanguageTerm")]
    public string NativeLanguageTerm { get; set; } = string.Empty;

    [JsonPropertyName("lemma")]
    public string Lemma { get; set; } = string.Empty;

    [JsonPropertyName("relationshipType")]
    public string RelationshipType { get; set; } = "root"; // root, derived, inflected, phrase, compound, idiom

    [JsonPropertyName("relatedTerms")]
    public List<string> RelatedTerms { get; set; } = new();

    [JsonPropertyName("linguisticMetadata")]
    public LinguisticMetadata LinguisticMetadata { get; set; } = new();
}

public class LinguisticMetadata
{
    [JsonPropertyName("partOfSpeech")]
    public string PartOfSpeech { get; set; } = string.Empty; // noun, verb, adjective, etc.

    [JsonPropertyName("frequency")]
    public string Frequency { get; set; } = "common"; // common, uncommon, rare

    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = "beginner"; // beginner, intermediate, advanced

    [JsonPropertyName("morphology")]
    public string Morphology { get; set; } = string.Empty; // "noun + -하다", "verb stem + -는", etc.
}
```

### Updated `VocabularyWord` Model

**File:** `src/SentenceStudio.Shared/Models/VocabularyWord.cs`

```csharp
// Add these properties:

[Description("Relationship type: root, derived, inflected, phrase, compound, idiom")]
[ObservableProperty]
private string? relationshipType;

[Description("Part of speech: noun, verb, adjective, etc.")]
[ObservableProperty]
private string? partOfSpeech;

[Description("Word frequency: common, uncommon, rare")]
[ObservableProperty]
private string? frequency;

[Description("Difficulty level: beginner, intermediate, advanced")]
[ObservableProperty]
private string? difficulty;

[Description("Morphological structure description")]
[ObservableProperty]
private string? morphology;

// Navigation property for related words
[JsonIgnore]
public List<VocabularyWordRelation> RelatedWords { get; set; } = new();
```

### New Model: `VocabularyWordRelation`

**File:** `src/SentenceStudio.Shared/Models/VocabularyWordRelation.cs`

```csharp
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class VocabularyWordRelation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    public string SourceWordId { get; set; } = string.Empty; // FK
    
    public string RelatedWordId { get; set; } = string.Empty; // FK
    
    public string RelationType { get; set; } = "contains"; // contains, derived_from, inflected_from, compound_of
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    [JsonIgnore]
    public VocabularyWord SourceWord { get; set; } = null!;
    
    [JsonIgnore]
    public VocabularyWord RelatedWord { get; set; } = null!;
}
```

---

## 8. Implementation Checklist

### Phase 1: Prompt & Schema (River + Wash)
- [ ] Create `GenerateVocabularyWithHierarchy.scriban-txt` prompt template
- [ ] Create `VocabularyImportResponse.cs`, `VocabularyImportItem.cs`, `LinguisticMetadata.cs` models
- [ ] Create `VocabularyWordRelation.cs` model
- [ ] Add new properties to `VocabularyWord.cs`
- [ ] Create EF Core migration for new columns and `VocabularyWordRelations` table
- [ ] Update `ResourceEdit.razor` to call new prompt and process hierarchical response

### Phase 2: Repository Methods (Wash)
- [ ] Add `GetUserVocabularyForLanguageAsync(string language)` to repository
- [ ] Add `SaveVocabularyRelationAsync(VocabularyWordRelation relation)` to repository
- [ ] Add `GetRelatedWordsAsync(string wordId)` to repository
- [ ] Add `GetWordsByLemmaAsync(string lemma)` to repository

### Phase 3: Mastery Inheritance Logic (Wash + River)
- [ ] Implement mastery bootstrapping when importing derived/inflected forms
- [ ] Update `VocabularyProgressService` to check for related words during progress creation
- [ ] Add logic to avoid duplicate quizzing (e.g., don't test root and derived in same session)

### Phase 4: UI & Visualization (Kaylee)
- [ ] Add word hierarchy visualization in Vocabulary Detail page
- [ ] Show "Related Words" section in vocabulary cards
- [ ] Add filter/grouping by `relationshipType` in vocabulary lists
- [ ] Update import feedback to show "X new, Y linked, Z derived"

### Phase 5: Testing (Jayne)
- [ ] Test AI prompt with real Korean transcripts
- [ ] Verify hierarchy detection accuracy (root → derived → phrase)
- [ ] Test mastery inheritance with known root words
- [ ] Verify no duplicate quizzing of related words in same session
- [ ] Test with multiple languages (Korean, Spanish, Japanese)

---

## 9. Success Metrics

**Definition of Done:**

1. **AI accurately identifies relationships** — 90%+ precision on Korean transcript samples
2. **Mastery inheritance works** — Derived words start with 30-50% of root word's mastery
3. **No duplicate quizzing** — Users don't see "대학교" and "대학교 때" in the same quiz session
4. **User feedback positive** — "The app knows I already learned this root word!" sentiment in testing

---

## 10. Open Questions

1. **How aggressive should mastery inheritance be?**  
   - 30%? 40%? 50% of root mastery?  
   - Should it vary by relationship type? (derived = 40%, phrase = 25%)

2. **Should we auto-link old flat vocabulary to new hierarchical data?**  
   - Run a one-time migration to detect relationships in existing vocabulary?  
   - Or only apply to new imports?

3. **How do we handle multi-word compounds?**  
   - "자주 마시는" (often drinks) relates to both "자주" (often) AND "마시다" (to drink)  
   - Should it inherit mastery from BOTH? Average them?

4. **What if the AI makes a mistake?**  
   - Should users be able to manually edit `relatedTerms`?  
   - Add a "Report Wrong Relationship" button?

---

## 11. Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| AI prompt returns wrong relationships | Users get incorrect mastery credit | Add validation: lemma must match root word; manual review UI |
| Existing vocabulary breaks with schema change | Data loss or corruption | Create reversible EF Core migration; test on copy of production DB |
| Performance degrades with relation lookups | Slow vocabulary import/quiz generation | Add indexes on `SourceWordId`, `RelatedWordId`; cache related words |
| Users confused by partial mastery | "Why does this word start at 40%?" | Add explainer tooltip: "You already know '주문', so you're starting with credit for '주문하다'" |

---

## Next Steps

1. **Captain approval** — Review this proposal and approve/request changes
2. **Wash collaboration** — Design EF Core migration strategy
3. **Prototype prompt** — Test `GenerateVocabularyWithHierarchy.scriban-txt` on 5 real Korean transcripts
4. **Measure accuracy** — Manually verify relationship detection (aim for 90%+)
5. **Implement Phase 1** — Prompt + schema + basic import logic
6. **Test with real users** — Collect feedback on mastery inheritance

---

**River's Recommendation:**  
Start with **Phase 1 only** — prove the AI can detect relationships accurately before building mastery inheritance logic. If the prompt works, the rest is just wiring. If it doesn't, we iterate on the prompt design first.

Let's build intelligent vocabulary tracking that respects how humans actually learn languages. 🚀
