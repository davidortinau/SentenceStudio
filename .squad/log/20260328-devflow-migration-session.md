# Session Log: DevFlow Package Migration & Team Decisions Merge

**Date:** 2026-03-28  
**Session ID:** 20260328-devflow-migration  
**Team Members:** Wash, Kaylee, Copilot (Scribe role)

---

## Overview

Multi-agent session focused on DevFlow package migration from Redth to Microsoft.Maui.DevFlow v0.24.0-dev, plus four additional architectural decisions captured from parallel work streams.

---

## Primary Work: Wash — DevFlow Migration

**Outcome:** ✅ COMPLETE  
**Span:** 2026-03-28  
**Changes:** 20 across 10 files (5 csproj + 5 MauiProgram.cs)

### Summary

Wash migrated all 5 platform projects (iOS, Android, MacCatalyst, MacOS, Windows) from deprecated `Redth.MauiDevFlow.*` packages to custom `Microsoft.Maui.DevFlow.*` v0.24.0-dev packages built from dotnet/maui-labs.

**Motivation:** Critical broker registration fix required for MauiDevFlow tool integration. Custom packages give local control and explicit versioning.

**Verification:**
- dotnet restore succeeded on iOS
- All packages resolved from localnugets NuGet source
- No build errors
- Zero Redth references remain

**Impact:** All 5 platforms now have the broker fix and proper Debug configuration (MacOS Blazor was missing the Debug condition).

See orchestration log: `.squad/orchestration-log/20260328-222746-wash-devflow-migration.md`

---

## Secondary Decisions Captured

### 1. Wash — Safe Service URL Defaults
**Date:** 2026-03-28  
**Status:** Implemented  
**Impact:** All debug builds now safely target localhost by default

**Changes:**
- `appsettings.json` (gitignored) → localhost URLs
- `appsettings.Production.json` (NEW, tracked) → Azure URLs
- `EnvironmentBadge` → red pulsing "⚠ PRODUCTION" warning
- CSS styling for production badge

**Rationale:** "Dev debug builds must NEVER talk to production servers." — Captain directive.

---

### 2. Wash — Auth Route Consolidation + Secure Storage
**Date:** 2026-03-28  
**Status:** Implemented  
**Impact:** Web auth flows simplified; sensitive data now encrypted in storage

**Changes:**
- Removed duplicate `/Account/*` forms; consolidated to `/auth/*` Blazor pages
- `ServerAuthService` → `AutoSignIn` redirection simplified
- `WebSecureStorageService` → ASP.NET Core Data Protection API for encryption
- Added `NativeLanguage` to `is_onboarded` check (bug fix, matching iOS behavior)

---

### 3. Wash — Legacy SQLite Schema Patching
**Date:** 2026-03-28  
**Status:** Implemented  
**Impact:** Legacy databases no longer fail on new-column queries

**Changes:**
- `SyncService` now calls `PatchMissingColumnsAsync()` for legacy DBs
- `pragma_table_info` inspection + `ALTER TABLE ADD COLUMN` for missing fields
- **Team reminder:** New model columns must be added to `expectedColumns` array

**Fixes:** iOS simulator `VocabularyWord.Language` query error

---

### 4. Kaylee — Onboarding Gate (Documented)
**Date:** 2025-07-15 (captured this session)  
**Status:** Implemented  
**Impact:** Onboarding gate now enforces all three profile fields

**Rule:** `is_onboarded = true` requires ALL of: TargetLanguage, NativeLanguage, Name.

---

### 5. Kaylee — WebFilePickerService (Documented)
**Date:** 2025-07-18 (captured this session)  
**Status:** Implemented  
**Impact:** File picker now works on web via JS interop

**Changes:**
- JS interop (`filePicker.js`) with hidden `<input type="file">`
- Changed DI from Singleton → Scoped (IJSRuntime compatibility)

---

### 6. Copilot Directive — Dev/Prod Separation
**Date:** 2026-03-28T19:04:00Z  
**Captured from:** David Ortinau (Captain)  
**Focus:** Safety-critical principle for environment configuration

> "Dev debug builds must NEVER talk to production servers. Always, always, always keep dev and production separate. Default configuration (appsettings.json) must point to local/dev endpoints. Production URLs should only be injected via environment variables or production-specific config overlays."

This directive informed the `wash-safe-service-url-defaults` decision.

---

## Decisions Merged

All 7 inbox files merged into canonical `.squad/decisions.md`:

1. ✅ `wash-devflow-package-migration.md`
2. ✅ `wash-safe-service-url-defaults.md`
3. ✅ `wash-auth-consolidation.md`
4. ✅ `wash-legacy-schema-patching.md`
5. ✅ `kaylee-onboarding-gate-check.md`
6. ✅ `kaylee-web-filepicker.md`
7. ✅ `copilot-directive-20260328T190400Z.md`

**Duplicates:** None detected.

---

## Outcomes

✅ All 5 platform projects now use Microsoft.Maui.DevFlow v0.24.0-dev  
✅ Debug builds default to localhost (no accidental production URLs)  
✅ Auth routes consolidated; web auth secure storage encrypted  
✅ Legacy SQLite databases auto-patched for missing columns  
✅ All team decisions documented in canonical decisions.md  
✅ Orchestration log created for DevFlow migration  

---

## Next Steps

- Monitor dotnet/maui-labs for new DevFlow releases
- Rebuild custom packages if breaking changes land
- Validate DevFlow tooling works end-to-end on all platforms (iOS, Android, MacCatalyst, MacOS, Windows)

---

**Authored by:** Scribe  
**Session Completed:** 2026-03-28
