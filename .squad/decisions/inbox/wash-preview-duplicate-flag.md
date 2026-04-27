# Decision: Preview Duplicate Detection — DTO Contract for Kaylee

**Date:** 2025-07-25
**Author:** Wash (Backend Dev)
**Branch:** `feature/import-content`

## Summary

Added duplicate-detection enrichment to the import preview so users see which rows already exist in the database BEFORE committing. The same matching predicate used by `CommitImportAsync` is now extracted into a shared helper (`NormalizeTargetTerm`) and reused by `EnrichPreviewWithDuplicateInfoAsync`.

## New DTO Properties on `ImportRow`

| Property | Type | Description |
|---|---|---|
| `IsDuplicate` | `bool` | `true` if this row matches an existing vocabulary word in the DB (commit with `DedupMode.Skip` will skip it). Default: `false`. |
| `DuplicateReason` | `string?` | Stable enum-style key. `null` when `IsDuplicate` is `false`. |

### DuplicateReason Values

| Key | Meaning |
|---|---|
| `"AlreadyInVocabulary"` | Term already exists in the `VocabularyWord` table (exact match on trimmed `TargetLanguageTerm`, case-sensitive). |
| `"DuplicateWithinBatch"` | Same term appears earlier in this preview batch (second+ occurrence). |

## New Interface Method

```csharp
Task EnrichPreviewWithDuplicateInfoAsync(ContentImportPreview preview, CancellationToken ct = default);
```

**Call this after `ParseContentAsync` returns and before rendering the preview table.** It mutates the `ImportRow` objects in place (sets `IsDuplicate` and `DuplicateReason`).

## UI Integration (Kaylee)

1. After `ParseContentAsync` returns, call `await ImportService.EnrichPreviewWithDuplicateInfoAsync(previewResult);`
2. In the preview table, check `row.IsDuplicate`:
   - If `true` with reason `"AlreadyInVocabulary"`: show a badge like "Already in vocabulary"
   - If `true` with reason `"DuplicateWithinBatch"`: show "Duplicate in batch"
3. Localized display strings are your domain — the reason keys are stable and won't change.
4. Duplicate rows remain `IsSelected = true` by default — users can still include them if they switch to `DedupMode.Update` or `ImportAll`.

## Performance

Single batched DB query per `EnrichPreviewWithDuplicateInfoAsync` call (uses `WHERE IN` with a `HashSet` of normalized terms). No N+1.

## Tests Added

4 new tests (36 total):
- `EnrichPreview_FlagsExactDuplicate_WhenTermExistsInDb`
- `EnrichPreview_DoesNotFlag_NearMiss_DifferentLemma`
- `EnrichPreview_UsesBatchQuery_NotNPlusOne`
- `EnrichPreview_MatchesCommitBehavior_RoundTrip` (invariant: Preview's IsDuplicate matches Commit's Skip/Create)
