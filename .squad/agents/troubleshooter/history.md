# Troubleshooter — History Summary

**Summarized by Scribe:** 2026-06-10T03:35:00Z
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** Runtime diagnosis, environment mismatch analysis, sync/data corruption triage, validation playbooks.

## Key diagnoses

- 2026-05-01 login failure was not email confirmation. Root cause was the wrong AppHost running from a separate worktree with a fresh empty Postgres volume. Captain's real account was in a different live container volume from the main checkout. Preferred fix: switch Aspire back to the main checkout; no DB mutation required.
- 2026-05-02 Dashboard first-load empty selectors were a Blazor Hybrid first-render race. `OnInitializedAsync` deferred while initial sync ran, so `OnAfterRenderAsync(firstRender:true)` skipped selector init; when sync completed, state changed but firstRender was false. Fix pattern: after deferred state resolution, wait for render and invoke idempotent JS init when mode requires selectors.
- CoreSync VocabularyProgress sync failure surfaced as Postgres 42804 and EF DateTime parse errors. Local rows had literal column names such as `UserDeclaredAt` stored as values. Evidence pointed to tainted data plus provider weaknesses around nullable/enum conversion and string passthrough to PostgreSQL.

## Diagnostic playbooks

- For "some data error" in this app, start with Aspire structured logs for `SentenceStudio.Api`; dashboard resource IDs differ from OTLP service names. Use trace structured logs when duplicate exception text hides the full stack.
- Sync failures often appear on `/api/sync-agent/changes-bulk-complete/{guid}`. The batch GUID stays stable across retries and confirms stuck-batch behavior.
- For EF materialization exceptions, reproduce the query in SQLite and inspect distinct values for suspect columns before assuming code-level mapping bugs.
- For sandboxed Mac Catalyst databases, copy for read-only inspection rather than mutating or deleting live data. Do not kill app processes or alter Captain's live DB without explicit permission.
- When comparing local NuGet variants, decompile installed package versions and diff them; local version stamps may be metadata-only.

## Durable cautions

- Wrong-worktree and wrong-container issues can masquerade as auth bugs. Always identify which AppHost and database volume are active before changing code.
- CoreSync failures may not kill the process but can cause retry pressure and UI symptoms downstream.
- Dogfooding directive applies: tooling failures discovered during validation should become issues, skills, or durable playbooks instead of hidden workarounds.
