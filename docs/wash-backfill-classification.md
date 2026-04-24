# Vocabulary Classification Backfill - Decision Record

**Date:** 2025-05-02  
**Agent:** Wash (Backend)  
**Todo:** backfill-classification

## Overview

Implemented a dedicated startup service (`VocabularyClassificationBackfillService`) that classifies existing `VocabularyWord` rows where `LexicalUnitType == Unknown` using heuristics. The service runs once after EF Core migrations complete during application startup, ensuring all vocabulary words are properly classified before the app begins normal operation.

## Key Design Decisions

### 1. Length Threshold: 12 Characters

**Choice:** Set `PhraseLengthThreshold = 12` with an XML comment noting it's tunable.

**Rationale:**
- Korean compound verbs like "공부하다" (4 chars) should remain classified as `Word`
- Multi-word constructions and longer phrases typically exceed 12 characters
- CJK characters are denser in information than Latin alphabet, so a moderate threshold balances accuracy
- The threshold complements the whitespace heuristic (most multi-word phrases have whitespace anyway)
- If a term is 13+ chars with no whitespace or punctuation, it's likely a long compound phrase worth marking as `Phrase` for learning purposes

**Alternative considered:** 15 characters would be more conservative but risks missing longer single-concept phrases that benefit from phrase-level treatment.

### 2. Startup Wiring Location

**Web hosts (API + WebApp):**
- Registered `VocabularyClassificationBackfillService` in `CoreServiceExtensions.AddSentenceStudioCoreServices()` as a singleton
- Called immediately after `MigrateAsync()` in `Program.cs` startup block:
  ```csharp
  await db.Database.MigrateAsync();
  var backfillService = scope.ServiceProvider.GetRequiredService<VocabularyClassificationBackfillService>();
  await backfillService.BackfillLexicalUnitTypesAsync();
  ```
- This ensures the backfill runs once per app start before any requests hit the database

**MAUI app:**
- Same DI registration via `CoreServiceExtensions`
- Invoked in `SyncService.InitializeDatabaseAsync()` right after the mobile-specific `MigrateAsync()` call (line ~207)
- Runs before CoreSync provisioning, ensuring vocabulary is classified before any sync operations

**Rationale:**
- Follows the existing `BackfillUserProfileIdsAsync` pattern, which also runs after migrations
- Keeps the hot path (`UserProfileRepository.GetAsync()`) clean — no backfill logic in per-request code
- Idempotent design (`WHERE LexicalUnitType == Unknown`) allows safe repeated execution without performance penalty
- Logging provides observability into counts per classification bucket

### 3. Classification Priority (Exact Order)

1. **Tags check** (case-insensitive): if `Tags` contains `"phrase"` → Phrase; if contains `"sentence"` → Sentence. Honors user-declared hints.
2. **Terminal punctuation**: if `TargetLanguageTerm.TrimEnd()` ends with any of `. ? ! 。 ？ ！` → Sentence.
3. **Whitespace OR length threshold**: if trimmed term contains any whitespace char (including CJK whitespace `\u3000`) OR length > 12 → Phrase.
4. **Default**: Word.
5. **Conservative guard**: if `TargetLanguageTerm` is single non-ASCII char (length 1, CJK range), leave `Unknown` — ambiguous single chars shouldn't be auto-classified.

**Why this order:**
- User intent (tags) takes precedence — if they marked something, respect it
- Punctuation is a strong signal for sentence-level completeness
- Whitespace/length is a good structural heuristic for phrases
- Default to `Word` for everything else (safest assumption)
- Guard against misclassifying single CJK characters that could be abbreviations, particles, or incomplete entries

### 4. Korean-Specific Nuance (River Input)

**Considered but NOT implemented:**
- **Sentence-enders (다/요/까) detection:** Korean sentence endings alone are NOT sufficient to mark as `Sentence` without punctuation. Many verb forms end in 다 (dictionary form) but aren't complete sentences. The terminal punctuation heuristic handles actual sentences correctly.
- **Particle stripping:** Too brittle for a heuristic — Korean particles (은/는/이/가/을/를) require linguistic analysis beyond a simple rule. Left for future AI-based classification if needed.
- **Compound verbs:** "공부하다" (4 chars) correctly remains `Word` under the 12-char threshold. No special handling needed — the threshold naturally handles this.

**Whitespace heuristic caveat:**
- Korean written text may omit spaces in informal contexts, but well-formed multi-word phrases typically include them. The length threshold catches most space-omitted phrases anyway.

## Edge Cases Found

1. **Empty/null `TargetLanguageTerm`:** Returns `Unknown` (safeguard at top of heuristic)
2. **Single non-ASCII char:** Returns `Unknown` (CJK ambiguity guard — could be abbreviation, particle, or incomplete entry)
3. **Tags with "sentence" AND terminal punctuation:** Tags take priority, but both would classify as `Sentence` anyway (consistent result)
4. **CJK ideographic space (U+3000):** Explicitly checked in whitespace detection to handle full-width space in CJK text

## Test Hooks for Jayne

Exposed a **pure static method** for unit testing without DB dependency:

```csharp
public static LexicalUnitType ClassifyHeuristic(string term, string? tags)
```

This allows Jayne to write comprehensive unit tests covering:
- All priority branches (tags, punctuation, whitespace, length, guard, default)
- Korean-specific cases (공부하다, sentence-enders, ideographic space)
- Edge cases (empty, null, single-char CJK, mixed punctuation)
- All classification outcomes (Word, Phrase, Sentence, Unknown)

## Performance Characteristics

- **One-time cost at startup:** Loads all rows with `LexicalUnitType == Unknown`, classifies in-memory, saves once
- **Idempotent:** Subsequent runs find zero rows and exit immediately ("No vocabulary words with Unknown classification found")
- **Batch size:** No explicit batching — all rows in one transaction. For this repo's scale (< 10K vocab words expected), one-shot is fine. If vocabulary grows to 100K+, consider batching in chunks of 1000.
- **Logging:** Emits total count, per-classification counts, and elapsed milliseconds for observability

## Migration Path

**No migration file needed:**
- `LexicalUnitType` column already exists in `VocabularyWord` (default value: `Unknown`)
- Backfill operates on existing data, no schema change required
- Future rows created by the app will have `LexicalUnitType` set by AI or user input — this backfill only fixes legacy Unknown entries

## Future Enhancements (Out of Scope)

1. **AI-based classification:** For ambiguous cases (single-char CJK, complex compounds), invoke LLM with linguistic context
2. **User override UI:** Allow manual reclassification from vocabulary list (already possible via Tags editing as a workaround)
3. **Telemetry:** Track classification accuracy by sampling user corrections (if we add a "wrong classification" feedback button)

## Verification

✅ Shared project builds without errors  
✅ MacCatalyst project builds without errors  
✅ Static method exposed for Jayne's unit tests  
✅ Wired in all startup paths (API, WebApp, MAUI SyncService)  
✅ Logging includes counts and elapsed time  
✅ Follows existing `BackfillUserProfileIdsAsync` pattern  
✅ Does NOT modify `UserProfileRepository.GetAsync()` hot path  

## Files Modified

- **Created:** `src/SentenceStudio.Shared/Services/VocabularyClassificationBackfillService.cs`
- **Modified:** `src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs` (DI registration)
- **Modified:** `src/SentenceStudio.WebApp/Program.cs` (startup wiring)
- **Modified:** `src/SentenceStudio.Api/Program.cs` (startup wiring)
- **Modified:** `src/SentenceStudio.Shared/Services/SyncService.cs` (MAUI startup wiring)

---

**Status:** ✅ Complete. Code-only deliverable. Backfill will execute at next app start (API, WebApp, or MAUI).
