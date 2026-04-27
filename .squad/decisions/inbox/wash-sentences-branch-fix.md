# Decision: Fix Sentences content type import — zero sentence rows reaching DB

**Date:** 2025-07-24
**Author:** Wash (Backend Dev)
**Branch:** `feature/import-content`

## Problem

Jayne's Round 3 E2E Test 2: importing 3 Korean sentences with Content Type = Sentences produced +9 entries, ALL `LexicalUnitType=1` (Word), ZERO type=3 (Sentence). The same input with Content Type = Phrases worked correctly (4 phrase entries landed).

## Root Cause (S2 primary, S1 secondary)

**S2 — Primary row classification:** Line 204 always passed `LexicalUnitType.Phrase` as the hint to `ResolveLexicalUnitType`, regardless of whether the user chose Sentences or Phrases. The heuristic's early-return at line 1060 (`if classification == Phrase → return Phrase`) meant primary rows were always classified as Phrase. With Sentences harvest defaults (`harvestSentences=true, harvestWords=true, harvestPhrases=false`), the harvest filter at line 1100 dropped all Phrase-typed rows. Only Word-typed AI rows survived.

**S1 — AI prompt mismatch:** The AI extraction always used the Phrases prompt (`ExtractVocabularyFromPhrases.scriban-txt`), which only emits Word and Phrase entries — never Sentence. River's `ExtractVocabularyFromSentences.scriban-txt` was never wired.

**S3 — DTO round-trip:** Not the cause. `ImportRow.LexicalUnitType` survives serialization; the rows simply never had Sentence classification to begin with.

## Fix

1. **Content-type-aware hint (S2):** When `effectiveContentType == ContentType.Sentences`, pass `LexicalUnitType.Sentence` as the hint. When Phrases, pass `Phrase`. Captain's directive: user's explicit content type is the strongest signal.

2. **ResolveLexicalUnitType guard:** Moved the Phrase/Sentence early-return AFTER the single-token check. Single-token terms are always Word regardless of hint. Multi-token terms trust the caller's Phrase/Sentence classification.

3. **Wire Sentences AI prompt (S1):** New `ExtractVocabularyFromSentencesAsync` method mirrors `ExtractVocabularyFromPhrasesAsync` but loads `ExtractVocabularyFromSentences.scriban-txt` and passes harvest flags (`harvest_sentences`, `harvest_phrases`, `harvest_words`) to the template.

## Tests Added

- `ParseContentAsync_Sentences_PrimaryRowsClassifiedAsSentence` — Captain's exact 3 lines with terminal periods → 3 Sentence rows.
- `ParseContentAsync_Sentences_NoPunctuation_StillClassifiedAsSentence` — Multi-token Korean without terminal period + ContentType=Sentences → still Sentence.
- `ParseContentAsync_Sentences_SingleTokenStaysWord` — Single-token "맥주" with ContentType=Sentences → stays Word.
- `ParseContentAsync_Phrases_StillWorkCorrectly` — Regression guard: Phrases content type still produces Phrase rows.

All 24 ContentImportService tests pass.

## Files Changed

- `src/SentenceStudio.Shared/Services/ContentImportService.cs` — primary row hint, AI prompt branching, new extraction method, heuristic fix
- `tests/SentenceStudio.UnitTests/Services/ContentImportServiceTests.cs` — 4 new tests
