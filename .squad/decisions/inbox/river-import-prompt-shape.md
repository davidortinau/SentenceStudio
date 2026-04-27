# River — Import Prompt JSON Response Shape Contract

**Date:** 2026-04-27  
**Author:** River (AI/Prompt Engineer)  
**Status:** LOCKED — Wash, wire against this shape  
**Branch:** `feature/import-content`

## Response DTO

All three extraction prompts (`ExtractVocabularyFromPhrases`, `ExtractVocabularyFromSentences`, `ExtractVocabularyFromTranscript`) return the **same** JSON shape. Deserialize with `VocabularyExtractionResponse` (or `FreeTextVocabularyExtractionResponse` which has the same `vocabulary` array with a `confidence` string field).

```json
{
  "vocabulary": [
    {
      "targetLanguageTerm": "string — full sentence/phrase OR dictionary-form word",
      "nativeLanguageTerm": "string — translation or definition",
      "confidence": "high | medium | low",
      "notes": "string? — optional context (line number, extraction notes)",
      "partOfSpeech": "noun | verb | adjective | adverb | expression | counter | particle",
      "topikLevel": 3,
      "lexicalUnitType": "Word | Phrase | Sentence",
      "relatedTerms": ["string — constituent words in dictionary form"]
    }
  ]
}
```

## Key Rules for Wash

### lexicalUnitType values
- `"Word"` — single dictionary entry (maps to `LexicalUnitType.Word = 1`)
- `"Phrase"` — multi-word unit, idiom, or full-line expression from Phrases import (maps to `LexicalUnitType.Phrase = 2`)
- `"Sentence"` — complete grammatical sentence from Sentences import (maps to `LexicalUnitType.Sentence = 3`)

### Wiring the prompts
1. **Phrases branch** (line ~190 in ContentImportService.cs): Replace `ParseFreeTextContentAsync` with a new method that loads `ExtractVocabularyFromPhrases.scriban-txt`. This is the ROOT CAUSE of the bug — the correct prompt exists but the service never calls it.
2. **Sentences branch**: Add a new `ContentType.Sentences` enum value and route to `ExtractVocabularyFromSentences.scriban-txt`.
3. Both prompts accept these Scriban variables:
   - `source_text` (string) — the raw user input
   - `target_language` (string) — e.g., "Korean"
   - `native_language` (string) — e.g., "English"
   - `existing_terms` (List<string>) — already-known terms to skip
   - `topik_level` (int?) — optional proficiency level
   - `harvest_words` (bool) — emit Word-type entries
   - `harvest_phrases` (bool) — emit Phrase-type entries
   - `harvest_sentences` (bool) — (Sentences prompt only) emit Sentence-type entries

### FilterRowsByHarvestFlags
The existing filter groups Phrase + Sentence together under `harvestPhrases`. Consider adding a `harvestSentences` flag, or keep the current behavior where `harvestPhrases` covers both Phrase and Sentence types. Captain's call.

### Classifier update
`ClassifyImportContent.scriban-txt` now returns four types: `"Vocabulary"`, `"Phrases"`, `"Sentences"`, `"Transcript"`. The classifier service code (line ~784) needs a new case: `"sentences" => ContentType.Sentences`.

## ContentType enum change needed
Add `Sentences` to the `ContentType` enum in `ContentImportService.cs`:
```csharp
[Description("Complete grammatical sentences")]
Sentences,
```
