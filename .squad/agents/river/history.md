# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- AI prompts are Scriban templates in `src/SentenceStudio.AppLib/Resources/Raw/*.scriban-txt`
- AI grading uses `AiService.SendPrompt<T>()` with structured JSON responses
- Grading philosophy: VERY permissive — accept associations, contrasts, feelings, moods, cultural links
- Only mark related=false if truly random with no possible link
- Never penalize spelling — provide corrected_text field instead
- When in doubt, ALWAYS give credit (err on side of related=true)
- Word Association prompt at `GradeWordAssociation.scriban-txt` — latest activity
- Response models in `src/SentenceStudio.Shared/Models/` — use JsonPropertyName attributes
- Support both target language and native language clues as valid input
- Vocabulary import uses inline prompt in `ResourceEdit.razor` (lines 365-391) — no Scriban template yet
- Current import is flat (no hierarchy) — returns `List<VocabularyWord>` with only TargetLanguageTerm + NativeLanguageTerm
- `VocabularyWord` model has `Lemma` field (nullable) but not populated during AI import
- `ExampleSentence` model links sentences to vocabulary words (useful for contextual review)
- `VocabularyProgress` tracks mastery with streak-based scoring (CurrentStreak, ProductionInStreak)
- Proposed vocabulary hierarchy tracking: root → derived → inflected → phrase → compound → idiom
- AI prompt needs structured JSON response with relationshipType, relatedTerms, linguisticMetadata
- Mastery inheritance: derived words should bootstrap with partial credit from root words (30-50%)
- New schema needed: `VocabularyWordRelations` table + new columns (RelationshipType, PartOfSpeech, Frequency, Difficulty, Morphology)

---

## VOCABULARY HIERARCHY TEAM ANALYSIS — FINAL DESIGN (2026-03-17)

**Session:** Vocabulary Hierarchy Analysis & Team Consensus  
**Role:** AI/Prompt Engineer  
**Status:** PROPOSED — Awaiting Captain Approval

**AI Import Design Finalized:**

### Hierarchical JSON Schema (Final)
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
      "linguisticMetadata": { ... }
    }
  ]
}
```

### Multi-Pass Prompt Strategy
1. **Pass 1:** Extract all vocabulary items (existing logic)
2. **Pass 2:** Identify relationships between extracted items
3. **Pass 3:** Enrich with linguistic metadata
4. **Pass 4:** Validate for accuracy (90%+ precision target)

### Team Consensus
- Wash approved schema (self-referential FK ready)
- Zoe aligned architecture (four design pillars locked)
- SLA Expert validated (morphological awareness, spacing effect)
- Learning Design approved (progressive disclosure)

### Relationship Types Supported
- `Inflection` — verb conjugations, noun declensions (주문 → 주문하다)
- `Phrase` — word + particle/modifier (대학교 → 대학교 때)
- `Idiom` — fixed expressions (주문하다 → 피자를 주문하는 게 어때요)
- `Compound` — merged words
- `Synonym` / `Antonym` — semantic relationships

### MVP Scope
- Phase 1: Hierarchical prompts with relationshipType + relatedTerms
- Phase 2 (Future): Mastery inheritance based on transfer of learning data
- Not in MVP: Lemma group assignment (keep existing Lemma field as-is)

### Next
1. Captain approval
2. Prototype on 5 real Korean transcripts
3. Manual accuracy verification (90%+ target)
4. Implement Phase 1 (prompt + schema + basic import)
