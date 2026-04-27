# Decision: Per-item result detail on ContentImportResult

**Date:** 2025-07-25
**Author:** Wash (Backend Dev)
**Branch:** current working tree

## Summary

Added per-row detail to `ContentImportResult` so the Import Complete screen can show exactly what happened to each row (created/updated/skipped/failed) with linkable vocabulary IDs and curated reasons.

## New Types

### `ImportItemStatus` enum
- `Created` — new VocabularyWord inserted
- `Updated` — existing VocabularyWord modified (DedupMode.Update)
- `Skipped` — duplicate found (DB or intra-batch)
- `Failed` — row could not be imported (empty term, etc.)

### `ContentImportItemResult` class
| Field | Type | Notes |
|---|---|---|
| `VocabularyWordId` | `string?` | Null only when Status=Failed and no DB row created |
| `Lemma` | `string` | The target-language term |
| `NativeLanguageTerm` | `string` | Translation (empty string if unavailable) |
| `Type` | `LexicalUnitType` | Word / Phrase / Sentence |
| `Status` | `ImportItemStatus` | Created / Updated / Skipped / Failed |
| `Reason` | `string?` | Null for Created/Updated; curated user-facing message for Skipped/Failed |

### `ContentImportResult.Items`
- Type: `IReadOnlyList<ContentImportItemResult>`
- Default: `Array.Empty<ContentImportItemResult>()`
- Aggregate counts (`CreatedCount`, `SkippedCount`, `UpdatedCount`, `FailedCount`) remain for summary cards.
- Invariant: `Items.Count == CreatedCount + SkippedCount + UpdatedCount + FailedCount`

## Curated Reason Strings (stable for Kaylee's UI)
- **Skipped (DB duplicate):** `"Already exists in resource"`
- **Skipped (intra-batch):** `"Duplicate within batch"`
- **Failed (empty target):** `"Target language term is empty"`
- **Failed (empty native):** `"Native language term is empty (AI translation not yet implemented)"`

## Logging Contract
Every Failed branch calls:
```csharp
_logger.LogError("Import row failed for lemma {Lemma} (type {Type}): {Reason}", lemma, type, curatedReason);
```
Raw exceptions (when present) are passed as the first arg to `LogError(ex, ...)` so they appear in Aspire structured logs. The curated `Reason` on the DTO stays user-friendly.

## Kaylee Integration Notes
- `Items` is populated in the same order as `selectedRows` iteration
- `VocabularyWordId` on Created/Updated/Skipped rows is always non-null and can be used for navigation to `/vocabulary/{id}`
- Failed rows with `VocabularyWordId == null` should not render a link
- `Reason` can be displayed inline in the table row for Skipped/Failed statuses

## Tests
8 new tests added covering all statuses, sentence type, intra-batch dedup, mixed-batch aggregate invariant, and logger verification. Total: 32 ContentImportService tests passing.
