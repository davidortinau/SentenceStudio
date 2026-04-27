# Wash: Sentence Type Plumbing & Phrase-Save Bug Fix

**Date:** 2026-07-21  
**Author:** Wash (Backend Dev)  
**Branch:** `feature/import-content`

---

## Root Cause of v1.1 Phrase-Save Bug

The Phrases branch in `ParseContentAsync` (ContentImportService.cs ~line 192) called `ParseFreeTextContentAsync()` which uses the generic `FreeTextToVocab.scriban-txt` template. This prompt decomposes input into individual vocabulary words, discarding the original phrase/sentence entries entirely.

River's dedicated `ExtractVocabularyFromPhrases.scriban-txt` prompt had been written and deployed to the Raw resources folder — but was **never wired in**. The code had a TODO acknowledging this: "Use River's dedicated phrase extraction prompt when it lands."

**Fix:** Rewrote the Phrases branch to:
1. Parse delimited content first (pipe/CSV/TSV) to create primary phrase/sentence entries (user's original content preserved as-is)
2. Run River's AI phrase extraction prompt to harvest constituent words
3. Combine both sets with deduplication by target term
4. Apply harvest flag filtering

This ensures the 3 original pipe-delimited sentences are always present in the preview AND the commit, with constituent words added as a bonus.

---

## DTO Field Names for Kaylee (Round 2 UI)

### ContentType enum (unchanged values + new)
```csharp
public enum ContentType
{
    Vocabulary,   // existing
    Phrases,      // existing
    Sentences,    // NEW — complete grammatical sentences
    Transcript,   // existing
    Auto          // existing
}
```

### Harvest checkbox flags on ContentImportRequest
```csharp
bool HarvestTranscript    // Store full text on LearningResource
bool HarvestPhrases       // Extract Phrase-type entries
bool HarvestWords         // Extract Word-type entries (default: true)
bool HarvestSentences     // NEW — Extract Sentence-type entries
```

### Same flags on ContentImportCommit
```csharp
bool HarvestTranscript
bool HarvestPhrases
bool HarvestWords
bool HarvestSentences     // NEW
```

**UI guidance for Kaylee:**
- Add "Sentences" button to the content type selector (alongside Vocabulary, Phrases, Transcript)
- Add "Sentences" harvest checkbox (alongside Phrases, Words)
- `ContentType.Sentences` routes to the same pipeline as `ContentType.Phrases` — the ResolveLexicalUnitType heuristic handles classification automatically
- The `ContentTypeToString` helper needs a Sentences case

---

## Refined ResolveLexicalUnitType Heuristic

```
1. If AI classified as Phrase or Sentence → keep it
2. If no whitespace → Word
3. If whitespace + terminal punctuation (. ! ? 。 ！ ？) → Sentence
4. If whitespace + no terminal punctuation → Phrase
```

This replaces the v1.1 "contains space → Phrase" heuristic. Terminal punctuation is the reliable signal for complete sentences.

---

## No Schema Changes Required

- `LexicalUnitType` enum already has `Sentence = 3` in the DB
- No new columns, no new tables, no migration needed
- `ContentType` enum is a DTO-only enum (not persisted), so adding `Sentences` is safe
