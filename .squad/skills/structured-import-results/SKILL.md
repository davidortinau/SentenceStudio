# Skill: Structured Per-Item Import Results

## Pattern

When an import or sync operation processes multiple rows, return both:
1. **Aggregate counts** (CreatedCount, SkippedCount, etc.) for summary display
2. **Per-item detail list** (`IReadOnlyList<ItemResult>`) with:
   - Entity ID (nullable — null only when the row failed before DB insertion)
   - Key identifying fields (lemma, name, etc.)
   - Status enum (Created / Updated / Skipped / Failed)
   - Curated user-facing Reason (null for success statuses; short sentence for skip/fail)

## Logging Contract

Failed items MUST log the raw exception via `_logger.LogError(ex, ...)` with structured fields (lemma, type, curated reason). The DTO `Reason` stays user-friendly. The raw stack trace lives in logs only (retrievable from Aspire dashboard).

## Invariant

`Items.Count == CreatedCount + UpdatedCount + SkippedCount + FailedCount`

Tests should always assert this invariant on mixed-batch scenarios.

## When to Apply

- Content import (ContentImportService.CommitImportAsync) — implemented
- CoreSync bulk push/pull — candidate
- Vocabulary merge/dedup tools — candidate
- Any batch DB operation where the user needs per-row feedback

## Anti-patterns

- Don't put raw exception messages in the DTO Reason (security + UX)
- Don't omit the item from the list on failure (the UI needs to show what failed)
- Don't rely solely on aggregate counts (Captain's directive: "2 created — 2 what?")
