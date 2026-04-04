# Decision: Dual Migration Required for Schema Changes

**Date:** 2026-04-04
**Author:** Wash (Backend Dev)
**Status:** PROPOSED

## Context

The `NarrativeJson` property was added to `DailyPlanCompletion` and the SQLite mobile app was patched via `PatchMissingColumnsAsync` in SyncService, but no PostgreSQL migration was generated. This caused a production error on Azure (PG error 42703: column does not exist).

## Decision

**Every model property addition MUST generate both:**
1. A PostgreSQL migration (via `dotnet ef migrations add`) — for the server-side API/WebApp on Azure
2. An entry in `SyncService.PatchMissingColumnsAsync` — for the SQLite mobile app

Neither alone is sufficient. Missing the PG migration breaks the webapp. Missing the SQLite patch breaks mobile.

## Rule

When adding a column to any EF Core entity:
- [ ] Generate PostgreSQL migration (follow sqlite-migration-generation skill for the csproj workaround)
- [ ] Add column to `PatchMissingColumnsAsync` expected columns list if the entity is synced to mobile
- [ ] Verify both API and WebApp build cleanly

## Impact

- Prevents future production outages from missing migrations
- Applies to all squad members who modify EF Core models
