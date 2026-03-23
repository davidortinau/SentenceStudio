# Decision: All VocabularyProgressRepository methods must resolve ActiveUserId

**Date:** 2026-03-22
**Author:** Wash (Backend Dev)
**Status:** Applied
**Related Issue:** #135

## Context

`GetByWordIdAndUserIdAsync` was the only repo method that did NOT fall back to `ActiveUserId` when `userId` was empty. Every other method (`ListAsync`, `GetByWordIdAsync`, `GetAllForUserAsync`, `SaveAsync`) had this fallback. The inconsistency caused the detail page to silently create duplicate blank-userId progress records instead of finding existing ones.

## Decision

**All VocabularyProgressRepository query methods must resolve `ActiveUserId` when `userId` is empty.** This is the same pattern already used by every other method in the class.

## Rationale

- Callers (Blazor pages, services) don't always have convenient access to the userId
- The repo already owns the `ActiveUserId` property via `IPreferencesService`
- Missing this fallback in even one method causes silent data corruption (ghost duplicate records)
- Consistent behavior prevents future bugs when new callers are added

## Impact

- Fixes #135 (detail page shows wrong mastery status)
- Prevents future duplicate VocabularyProgress records
- Existing ghost records with empty userId are harmless (MasteryScore=0, no real data)
