# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- `GradeSentence.scriban-txt` is shared by Writing, Cloze, and VocabQuiz sentence shortcut — the `target_word` conditional section (lines 17-28) only activates for vocab quiz grading
- `TeacherService.GradeTargetWordSentence()` should pass empty `userMeaning` — target word context goes through dedicated `targetWord`/`targetWordMeaning` params, not the `userMeaning` slot which is for Writing activity's "what I meant to say"
- Sentence shortcut DifficultyWeight is 2.5f (increased from 1.5f) — writing sentences requires more production knowledge than matching answers
- Grading philosophy for sentence shortcut: grade for CONTEXTUAL USAGE (using word naturally in a sentence), never for definition-recitation ("X means Y")
- The `userMeaning` template variable in GradeSentence.scriban-txt maps to "which I mean to express..." — passing meta-instructions here biases AI grading toward definition patterns

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

### YouTube AI Pipeline Prompts (2025-07-17)
- Created `CleanTranscript.scriban-txt` — cleans raw YouTube auto-captions into readable Korean text. Returns plain text via `SendPrompt<string>`. Handles: timing artifacts, fragmented words, spacing, punctuation, mixed-language code-switching.
- Created `ExtractVocabularyFromTranscript.scriban-txt` — extracts structured vocab from cleaned transcript. Returns JSON via `SendPrompt<VocabularyExtractionResponse>`. Includes: romanization, TOPIK level, part of speech, frequency count, real example sentences from transcript.
- Two-stage architecture (clean → extract) chosen over single-pass because: cleanup is plain text, extraction needs clean input, each stage retriable independently, keeps prompts under token limits.
- Chunking strategy: 4,000 Korean chars per cleanup call with 200-char overlap. Vocab extraction on full cleaned text. Default 30 words per video.
- `existing_terms` parameter in extraction prompt enables dedup at AI level (skip words user already knows).
- Response models: `TranscriptCleanupResult` (metadata wrapper, not JSON-deserialized), `VocabularyExtractionResponse` + `ExtractedVocabularyItem` (JSON DTO with `ToVocabularyWord()` converter).
- Open question: VocabularyWord model lacks `Romanization` field — currently returned by AI but not persisted. Needs Captain decision.

### YouTube Pipeline Template Integration (2025-03-22)
- **WIRED**: CleanTranscript.scriban-txt → `TranscriptFormattingService.PolishWithAiAsync()` replaces inline prompt. Added `IFileSystemService` dependency to load template.
- **WIRED**: ExtractVocabularyFromTranscript.scriban-txt → `VideoImportPipelineService.ExtractVocabularyAsync()` replaces inline prompt. Upgraded from tab-separated string parsing to structured JSON with `VocabularyExtractionResponse`.
- **JSON VERIFIED**: All `[JsonPropertyName]` attributes in `VocabularyExtractionResponse` match the template output spec exactly (targetLanguageTerm, nativeLanguageTerm, romanization, lemma, partOfSpeech, topikLevel, frequencyInTranscript, exampleSentence, exampleSentenceTranslation, tags).
- **DI PATTERN**: Shared project services use `IFileSystemService.OpenAppPackageFileAsync()` to load Scriban templates from AppLib/Resources/Raw.
- **CONVERTER**: `ExtractedVocabularyItem.ToVocabularyWord()` method converts AI response DTO to persistable VocabularyWord model — enriches with language param, defaults Lemma if not provided.
- **BUILD**: Both SentenceStudio.Shared and SentenceStudio.AppLib build successfully (warnings only, no errors).

### Real YouTube Caption Analysis (2025-07-17)
- Tested 3 channels: @My_easykorean (beginner, pure Korean), @koreancheatcode (bilingual English+Korean), @KoreanwithSol (conversational podcast Korean)
- YouTube ".이" artifact is the #1 cleanup issue — captioner merges sentence period with next word's first syllable. Must be explicitly called out in prompt.
- koreancheatcode is ~44% English lines mixed with Korean — prompt must handle bilingual content, not strip English
- Typical 10-20 min Korean learning video = 6-13KB raw caption text. Single API call is sufficient; chunking only needed for 30+ min videos.
- Line fragmentation is severe: myeasykorean has 77/218 lines that are mid-sentence continuations
- Auto-captioner struggles with English loanwords in Korean context: "be터스" (bittersweet), "호라이 펜" (frying pan)
- Test fixtures saved: `tests/SentenceStudio.UnitTests/TestData/YouTubeTranscripts/` (3 raw transcripts from target channels)
