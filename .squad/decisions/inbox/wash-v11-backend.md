# Wash v1.1 Backend Implementation

**Date:** 2026-04-25
**Author:** Wash (Backend Dev)
**Branch:** `feature/import-content-mvp`

## Migration: SetDefaultLexicalUnitType

- **File:** `20260425134549_SetDefaultLexicalUnitType.cs` (both Postgres and SQLite variants)
- **Purpose:** Heuristic backfill of existing Unknown (0) LexicalUnitType entries
- **Logic:** `TRIM(TargetLanguageTerm)` checked for space → Phrase (2), else Word (1)
- **Down():** No-op (Captain D1: resetting to Unknown = data loss)
- **Postgres SQL:** Uses `POSITION(' ' IN TRIM("TargetLanguageTerm"))` with quoted identifiers
- **SQLite SQL:** Uses `INSTR(TRIM(TargetLanguageTerm), ' ')` with bare identifiers
- **Build verification:** Shared (net10.0), MacCatalyst (net10.0-maccatalyst), API all pass
- **validate-mobile-migrations.sh:** Requires running app + maui devflow — deferred to Captain's manual gate (script requires interactive device connection)

## ContentImportService v1.1 Branch Summary

### Phrase Branch
- Routes through existing `FreeTextToVocab.scriban-txt` which already classifies LexicalUnitType per entry
- Harvest both Words AND Phrases per Captain's harvest matrix
- TODO: Replace with River's dedicated `ExtractPhrasesFromContent.scriban-txt` when available
- Filters results by harvest checkbox flags (harvestWords, harvestPhrases)

### Transcript Branch
- Stores full text on `LearningResource.Transcript`, sets `MediaType="Transcript"`
- Extracts vocabulary using existing `ExtractVocabularyFromTranscript.scriban-txt`
- Word-biased extraction per Captain's D2 refinement
- Respects harvest checkboxes independently

### Auto-detect Branch
- AI classification prompt built inline (River's `ClassifyImportContent.scriban-txt` not yet landed)
- Three-tier confidence gate (Captain D3):
  - >= 0.85: auto-route, no user confirmation
  - 0.70-0.84: return to UI for user confirmation, no DB writes
  - < 0.70: return to UI, user must pick manually
- Classification runs BEFORE any DB persistence (Captain's directive)
- `ContentClassificationResult` DTO carries type, confidence, reasoning, and signals

### Checkbox Harvest Model (DTO contract)
- `ContentImportRequest` gains: `HarvestTranscript`, `HarvestPhrases`, `HarvestWords` booleans
- `ContentImportCommit` gains: same three booleans + `TranscriptText` string
- Backend validates at least one must be true
- `ImportRow` gains `LexicalUnitType` field for per-row classification
- `ContentImportPreview` gains `Classification` and `RequiresUserConfirmation` fields

## Edge Case Decisions

### Zero-vocab extraction
**Decision:** Persist the LearningResource (if transcript was requested) with empty vocab set + clear warning message. Do NOT silently succeed and do NOT error/rollback.
**Rationale:** A transcript is valuable even without extracted vocab. The user explicitly asked for transcript storage. Surfacing a warning lets UI show "Transcript stored, no vocabulary extracted" which is truthful and actionable.

### Transcript chunking >30KB
**Decision:** Reject with clear error message. Limit is 30KB (not the original 50KB) for transcript extraction because LLM context windows work better with shorter inputs.
**Rationale:** Captain hasn't decided on chunking strategy. v1.1 processes the whole text in one prompt up to 30KB. Anything larger gets a clear rejection message pointing to v1.2 chunking support.
**Follow-up:** v1.2 should implement sliding-window or semantic chunking with merge-dedup.

## Blockers Waiting on River
1. `ClassifyImportContent.scriban-txt` — using inline prompt as bridge; replace when River lands it
2. `ExtractPhrasesFromContent.scriban-txt` — using FreeTextToVocab as bridge (it already classifies LexicalUnitType)
3. River's transcript prompt word-bias adjustment — current `ExtractVocabularyFromTranscript.scriban-txt` already handles LexicalUnitType; may need refinement to suppress Phrase extraction for transcript context

## Files Changed
- `src/SentenceStudio.Shared/Migrations/20260425134549_SetDefaultLexicalUnitType.cs` (new)
- `src/SentenceStudio.Shared/Migrations/20260425134549_SetDefaultLexicalUnitType.Designer.cs` (new)
- `src/SentenceStudio.Shared/Migrations/Sqlite/20260425134549_SetDefaultLexicalUnitType.cs` (new)
- `src/SentenceStudio.Shared/Migrations/Sqlite/20260425134549_SetDefaultLexicalUnitType.Designer.cs` (new)
- `src/SentenceStudio.Shared/Services/ContentImportService.cs` (updated)
- `src/SentenceStudio.UI/Pages/ImportContent.razor` (updated — adapted DetectContentType → ClassifyContentAsync)
