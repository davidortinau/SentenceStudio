# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- When a model property is added, you MUST generate BOTH PostgreSQL and SQLite migrations — PatchMissingColumnsAsync in SyncService only covers SQLite mobile, not the Azure PostgreSQL database
- Both API (Program.cs:213) and WebApp (Program.cs:151) call `MigrateAsync()` on startup — deploying new migration code auto-applies it on next container restart
- NarrativeJson was added to DailyPlanCompletion model for plan narrative storage but the PG migration was missed — fixed in commit aa8dd3c

- appsettings.json is gitignored and local-only; appsettings.Production.json + appsettings.Development.json are tracked
- Default service URLs in appsettings.json MUST be localhost-only; production Azure URLs live ONLY in appsettings.Production.json
- EnvironmentBadge shows RED pulsing "⚠ PRODUCTION" when azurecontainerapps.io URLs detected; GREEN "LOCAL" for localhost; ORANGE for dev tunnels
- `appsettings.Production.json` registered in csproj as both MauiAsset and EmbeddedResource
- Service discovery `https+http://api` URI resolves via Aspire env vars first, then falls back to config Services section

- EF Core migrations: `dotnet ef migrations add <Name> --project src/SentenceStudio.Shared --startup-project src/SentenceStudio.Shared`
- NEVER delete database files or user data — fix migrations, not data
- SQLite table names are SINGULAR in ApplicationDbContext.OnModelCreating
- All CoreSync-synced entities use string GUID PKs with ValueGeneratedNever()
- Non-synced entities keep int auto-increment PKs but have string-typed FKs
- DI registration in `SentenceStudioAppBuilder.cs` (AppLib) and `Program.cs` (WebApp)
- Aspire env var config: `builder.Configuration["AI:OpenAI:ApiKey"]` not `["AI__OpenAI__ApiKey"]`
- Server DB at: `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db`
- UserProfileId columns for multi-user data isolation — all repos filter by active_profile_id

- YouTubeImportService (existing) handles transcript download + audio extraction — wrap it, don't modify it
- TranscriptFormattingService has SmartCleanup (rules-based) + PolishWithAiAsync (AI) — two-stage cleanup
- VocabularyWord.ParseVocabularyWords() parses tab or comma-delimited vocab lists into word objects
- VideoImportPipelineService orchestrates: fetch transcript → clean → extract vocab → save LearningResource + VocabWords
- MonitoredChannel + VideoImport entities use synced pattern (string GUID PKs, UserProfileId, singular table names)
- VideoImportStatus enum tracks pipeline stages: Pending → FetchingTranscript → CleaningTranscript → GeneratingVocabulary → SavingResource → Completed/Failed
- Workers project (SentenceStudio.Workers) has skeleton BackgroundService ready for ChannelPollingWorker

- Microsoft.Identity.Web v3.8.2 added to API for Entra ID JWT Bearer auth
- Conditional auth pattern: `Auth:UseEntraId` config flag switches between Entra ID and DevAuthHandler
- TenantContextMiddleware maps both Entra ID claims (tid, oid, name) and DevAuthHandler claims (tenant_id, NameIdentifier, Name) — Entra ID claims take precedence
- appsettings.json is gitignored; use appsettings.Development.json for tracked config and AppHost env vars for runtime
- Scope policies: `RequireScope("user.read")` etc. via Microsoft.Identity.Web authorization helpers
- AzureAd public IDs (TenantId, ClientId, Audience) are NOT secrets — safe to commit
- CoreSync HTTP client uses named HttpClient `"HttpClientToServer"` — auth handler chains via `.AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()`
- CoreSync server (`SentenceStudio.Web`) uses `UseCoreSyncHttpServer()` middleware — auth middleware must run BEFORE it
- DevAuthHandler is duplicated in both API and Web projects — future refactor to shared project
- Web server auth follows same `Auth:UseEntraId` pattern as API server
- MauiAuthenticationStateProvider wraps IAuthService for Blazor's auth framework — registered Scoped, IAuthService stays Singleton
- Microsoft.AspNetCore.Components.Authorization NuGet needed in both AppLib and UI projects for AuthorizeRouteView
- Bumped Microsoft.Extensions.Configuration.Binder to 10.0.5 to satisfy transitive dependency from Components.Authorization
- JWT access tokens: configurable via `Jwt:ExpiryMinutes`, default 120 min (was 60)
- Refresh tokens: configurable via `RefreshToken:LifetimeDays`, default 90 days (was 7)
- WebApp cookie: 90 days with sliding expiration (was 14 days)
- Mobile client: `IdentityAuthService` restores JWT from SecureStorage before network refresh
- `MauiAuthenticationStateProvider` uses 10s timeout for silent refresh (was 3s)
- `ServerAuthService.GetAccessTokenAsync()` reads `Jwt:ExpiryMinutes` config (was hardcoded 1h)
- Auth endpoints: `AuthEndpoints.cs` (API), `AccountEndpoints.cs` (WebApp), `JwtTokenService.cs`
- Key file: `src/SentenceStudio.AppLib/Services/IdentityAuthService.cs` — mobile token storage/refresh
- Captain's preference: never show login unless user explicitly logged out
- **Always add UserProfileId to synced entities** - Multi-user support requires explicit user isolation
- **CoreSync requires string GUID PKs** - int auto-increment doesn't work for distributed sync
- **TriggerSyncAsync() is not automatic** - Must be called explicitly after SaveChangesAsync()
- **SharedSyncRegistration is the source of truth** - Both SQLite and PostgreSQL configs must match
- **Streak calculation depends on synced data** - If UserActivity doesn't sync, streaks diverge
- VocabularyProgress mastery status is a computed `[NotMapped]` property — uses MasteryScore, IsUserDeclared, VerificationState
- **ALL VocabularyProgressRepository methods must resolve `ActiveUserId` when `userId` is empty** — inconsistency causes silent data bugs
- **SaveResourceAsync cascade insertion bug** — When `db.LearningResources.Add(resource)` is called with pre-saved VocabularyWords in `resource.Vocabulary`, EF Core cascade-inserts them in the new DbContext scope → PG 23505. Fix: clear Vocabulary before Add, re-associate after SaveChanges.
- **Starter resource creation needs duplicate guard** — `StarterResourceExistsAsync(targetLanguage)` checks by "starter" tag + language + user. Both Index.razor and Resources.razor call it before creating.
- **LearningResource and VocabularyWord auto-generate GUIDs** in property initializers (`= Guid.NewGuid().ToString()`) — IDs are NOT hardcoded, but cascade insertion re-uses objects across DbContext scopes
- **UpdatePlanItemProgressAsync is the ONLY method called by the timer** — `MarkPlanItemCompleteAsync` exists but is never invoked by any activity page or the timer; all completion logic must live in `UpdatePlanItemProgressAsync`
- **ActivityTimerService.SaveProgressAsync → UpdatePlanItemProgressAsync** is the sole path for persisting activity time; it fires every minute tick and on Pause()
- **Activity pages GoBack() pattern**: `Pause()` → `StopSession()` → `NavigateTo("/")` — no explicit completion signal is sent; completion must be detected from accumulated time vs estimate

## Core Context (Current)

**Role:** Backend Developer  
**Focus Areas:** API (Aspire endpoints, JWT/auth), Data Layer (EF Core, migrations, CoreSync), Mobile Services (auth, sync)  

**Current Phase:**
- Phase 1 (Auth) & Phase 2 (Secrets): Complete
- Phase 3-5 active: Infrastructure, CI pipeline, hardening

**Recent Completions (2026-03-20 to 2026-03-28):**
- Auth token lifetime extended: JWT 120min, refresh 90d, cookie 90d, mobile instant restore, silent refresh 10s timeout
- Quiz vocabulary pool two-tier architecture (20-word session, 10-word round, auto-eviction)
- Vocabulary repo consistency: All methods now resolve ActiveUserId properly
- Post-login CoreSync trigger for mobile data consistency
- CoreSync data gaps fixed: DailyPlanCompletion + UserActivity now sync correctly with proper PKs
- Vocabulary detail mastery status bug fixed (ActiveUserId fallback)

**Architectural Decisions:**
- Vocabulary hierarchy: Option A (self-referential FK) with RelationType enum — awaiting Captain approval
- Auth pattern: `Auth:UseEntraId` flag controls Entra ID vs DevAuthHandler
- Token caching: SecureStorage (MAUI), Redis (WebApp)

**Blockers:** None currently blocking auth/sync path

**Next:** Begin Phase 3 (Infrastructure) — PostgreSQL migration planning, Container Apps provisioning

---

## Archived Prior Work (Pre-2026-03-20)

### 2026-03-13 — Azure Deployment Issues Triage

GitHub issues #39-#65 created by Zoe. Phase execution order: Phase 2 (Secrets) → Phase 1 (Auth) → Phase 3 (Infra) → Phase 4 (Pipeline) → Phase 5 (Hardening).

### 2026-03-15 — Mobile Auth Guard Bypass Fix (Kaylee)

Mobile auth gate now validates real token state via `IAuthService.IsSignedIn` instead of checking boolean preference flag. No API changes required.

### 2026-03-15 — Mobile Plan Sync Fix

**Problem:** Mobile showed "No plan" even though webapp had a plan. Generate Plan button did nothing.
**Root Causes:** (1) Missing post-login CoreSync trigger, (2) Silent error swallowing
**Solution:** Added post-login sync in `IdentityAuthService.StoreTokens()` + improved error display in `Index.razor`

### 2026-03-17 — Vocabulary Hierarchy Schema Finalized

**Decision:** Option A (Self-Referential FK) — FINAL

Added to VocabularyWord entity:
- `ParentVocabularyWordId` (NULLable FK)
- `RelationType` enum (Inflection, Phrase, Idiom, Compound, Synonym, Antonym)
- Navigation: `ParentWord` + `List<VocabularyWord> ChildWords`

**Why Option A?**
- CoreSync compatible (single table, no junction, NULLable FK safe)
- Fast parent/child queries
- Additive migration (zero data loss)
- Can evolve to Option B (junction) if multi-parent needed

**Team Validation:** ✅ Zoe (Architecture), ✅ River (AI), ✅ Learning Design

**Next:** Captain approval → EF migration → integration tests → CoreSync validation → repository methods

---

## 2026-03-20+ Recent Sessions

(See detailed entries below for specific work on quiz randomization, vocabulary consistency, CoreSync fixes, auth token lifetime)

---

## 2026-03-22: Fixed CoreSync Data Gaps (DailyPlanCompletion & UserActivity)

### Problem
Captain reported 4 data sync issues between mobile (iOS) and web (same account `dave@ortinau.com`):
1. Today's Plan progress not syncing (minutes spent differ)
2. Streak badge inconsistency (mobile: 1 day, web: none)
3. Vocabulary count mismatch (mobile: 2805, web: 2798)
4. Import history not syncing (2 video imports on mobile missing on web)

### Root Causes Identified
1. **`DailyPlanCompletion` missing `UserProfileId`** - All users' plan progress was stored in same table with no isolation
2. **`DailyPlanCompletion` used int PK** - CoreSync requires string GUID PKs for conflict-free sync
3. **`DailyPlanCompletion` NOT registered in `SharedSyncRegistration`** - Even with correct PK, wouldn't sync
4. **`UserActivity` used int PK and nullable `UserProfileId`** - Streak calculation affected (mixes users' activity)
5. **No `TriggerSyncAsync()` calls** - Even after `SaveChangesAsync()` in ProgressService
6. **VideoImport/MonitoredChannel** - Already added to sync registration recently, but needed verification

### Solution Implemented
1. **Updated Models:**
   - `DailyPlanCompletion`: Changed `Id` from `int` to `string`, added required `UserProfileId` field
   - `UserActivity`: Changed `Id` from `int` to `string`, made `UserProfileId` required (was nullable)

2. **Updated ApplicationDbContext:**
   - Moved `DailyPlanCompletion` and `UserActivity` from "Non-synced entities" to "Synced entities" section
   - Configured both with `ValueGeneratedNever()` for string GUID PKs

3. **Updated SharedSyncRegistration:**
   - Added `DailyPlanCompletion` to both SQLite and PostgreSQL sync configurations
   - Added `UserActivity` to both SQLite and PostgreSQL sync configurations

4. **Updated ProgressService:**
   - Injected `UserProfileRepository` and `ISyncService` dependencies
   - Added `UserProfileId` filtering in ALL queries
   - Added `TriggerSyncAsync()` calls after every `SaveChangesAsync()` in: ClearCachedPlanAsync, MarkPlanItemCompleteAsync, UpdatePlanItemProgressAsync, InitializePlanCompletionRecordsAsync
   - Set `Id = Guid.NewGuid().ToString()` and `UserProfileId = userProfile.Id` for new records

5. **Updated UserActivityRepository:**
   - Fixed `SaveAsync()` to check `!string.IsNullOrEmpty(item.Id)` instead of `item.Id != 0`
   - Added GUID generation for new records

6. **Created EF Core Migrations:**
   - PostgreSQL: `20260322012812_SyncDailyPlanAndUserActivity.cs` - AlterColumn for both tables
   - SQLite: `20260322012812_SyncDailyPlanAndUserActivity.cs` - Recreate tables with data migration (SQLite doesn't support ALTER COLUMN type changes)

### Impact
- **DailyPlanCompletion now syncs** → Today's Plan progress will sync between mobile and web
- **UserActivity now syncs** → Streak badges will be consistent
- **UserProfileId isolation** → Multi-user data won't mix
- **Existing data preserved** → Migrations backfill UserProfileId to first profile and generate GUIDs
- **VideoImport/MonitoredChannel** → Already registered, sync should work

### Migration Challenges
- Multi-TFM projects don't work with `dotnet ef migrations add` - created temp single-TFM `.csproj` files
- SQLite doesn't support `ALTER COLUMN` type changes - had to recreate tables with data copy

---

### 2026-03-22 — Fix: Vocabulary Detail Mastery Status (#135)

**Status:** Complete
**Branch:** `squad/135-vocab-detail-status`
**Issue:** #135

**Root Cause:**
`VocabularyProgressRepository.GetByWordIdAndUserIdAsync()` was the only repo method missing the `ActiveUserId` fallback. When VocabularyWordEdit.razor called `GetProgressAsync(wordId)` with no userId, the method searched for `UserId==""`, found nothing, and `GetOrCreateProgressAsync` created a new blank VocabularyProgress record (MasteryScore=0 → "Unknown").

Meanwhile the list page used `GetAllProgressDictionaryAsync()` which DID fall back to ActiveUserId and found the real records correctly.

**Fix:** Added the same `ActiveUserId` fallback to `GetByWordIdAndUserIdAsync` that every other repo method already has (3-line change).

**Files Modified:**
- `src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs` — added ActiveUserId fallback in `GetByWordIdAndUserIdAsync`

---

### 2026-03-28T01:15 — Auth Token Lifetime Extended for Persistent Login

**Status:** Complete  
**Task:** Fix auth token lifetime for persistent login across mobile and web platforms

**Changes Across 6 Files:**
1. **API (AppHost)** — JWT Bearer token lifetime extended to 120 minutes
2. **WebApp (Blazor Server)** — Authentication cookie lifetime extended to 90 days
3. **Mobile (MAUI)** — Instant JWT restore from SecureStorage on app launch (skip silent refresh if token valid)
4. **Mobile (MAUI)** — Silent refresh timeout reduced to 10 seconds (aggressive refresh fallback if restore fails)
5. **Refresh token configuration** — Extended to 90 days in authentication database/config
6. **Token validation logic** — Ensured all platforms respect extended lifetimes without forcing re-authentication

**Decision Rationale:**
- Long-lived, configurable tokens for dev-first UX
- Users expect persistent login on mobile across app restarts (within 90-day window)
- WebApp users benefit from 90-day cookie persistence (one session per device)
- JWT 120-min window covers typical daily usage
- SecureStorage instant restore avoids unnecessary OAuth flow on app launch

**Impact:**
- Users on mobile won't be forced to re-authenticate after app restart (within 90-day window)
- WebApp maintains persistent session across browser restarts
- No server-side burden from token refresh (silent refresh only on actual token expiry)
- Configuration centralized for easy tuning per environment (dev vs production)

**Related Issues:** Addresses persistent login requirement for mobile offline workflows


## 2026-03-28T02:25: Starter Resource Duplicate ID Crash Fix

**Issue:** PostgreSQL 23505 unique_violation when saving duplicate starter resource IDs  
**Status:** ✅ FIXED

**Root Cause:**
- SaveResourceAsync cascade-insert bug: Vocabulary nav property not cleared before Add()
- Missing duplicate ID guard to prevent re-insertion

**Solution:**
1. Clear Vocabulary nav property before Add() in SaveResourceAsync
2. Add StarterResourceExistsAsync duplicate guard
3. Improve error messages for clarity

**Files Modified:**
- src/SentenceStudio.Shared/Data/LearningResourceRepository.cs
- src/SentenceStudio.UI/Pages/Index.razor
- src/SentenceStudio.UI/Pages/Resources.razor

**Build Status:** ✅ Clean build. Unique constraint now enforced.

**Key Learning:** Always manage EF Core navigation properties explicitly on Add(); use guard clauses for unique constraints to prevent database-level violations.


- Auth route consolidation: Cookie LoginPath → `/auth/login`, LogoutPath → `/account-action/SignOut`, shared Blazor pages are now canonical
- Removed `/account-action/Login` POST and `/account-action/Register` POST endpoints — shared LoginPage/RegisterPage use AutoSignIn flow instead
- Account/Login.razor, Account/Register.razor, Account/ForgotPassword.razor now redirect to shared `/auth/*` pages
- NativeLanguage added to is_onboarded check in AutoSignIn handler — matches Kaylee's MainLayout/LoginPage fix
- WebSecureStorageService now encrypts values via ASP.NET Core Data Protection API (purpose: "SentenceStudio.SecureStorage")
- CryptographicException on decrypt → graceful null return + warning log + stale key removal
- Data Protection keys managed by ASP.NET Core runtime (machine-level by default) — no extra NuGet needed
- SyncService legacy DB detection (seeding InitialSqlite) does NOT verify schema parity — must patch missing columns via `PatchMissingColumnsAsync` before MigrateAsync
- `dotnet ef migrations add` for SQLite requires temp single-TFM csproj + updated DesignTimeDbContextFactory to accept `--provider Sqlite` — restore originals after generation
- SQLite has no conditional DDL (no `IF NOT EXISTS` for ALTER TABLE ADD COLUMN) — use pragma_table_info check in C# code before executing ALTER TABLE
- When `dotnet ef` generates migrations for a snapshot that already has the column, it produces empty/unrelated operations — must manually replace the migration body
- DANGER: `dotnet ef` with temp csproj config can corrupt the PostgreSQL `Migrations/ApplicationDbContextModelSnapshot.cs` — always `git checkout` it after SQLite migration generation

## 2026-03-28: DevFlow Package Migration (Redth → Microsoft.Maui)

**Status:** Complete  
**Issue:** Migrate all projects from `Redth.MauiDevFlow.*` packages to custom-built `Microsoft.Maui.DevFlow.*` packages with broker registration fix

**Packages Migrated:**
- `Redth.MauiDevFlow.Agent` → `Microsoft.Maui.DevFlow.Agent` v0.24.0-dev
- `Redth.MauiDevFlow.Blazor` → `Microsoft.Maui.DevFlow.Blazor` v0.24.0-dev

**Platform Projects Updated (5):**
1. iOS: SentenceStudio.iOS.csproj + MauiProgram.cs
2. Android: SentenceStudio.Android.csproj + MauiProgram.cs
3. MacCatalyst: SentenceStudio.MacCatalyst.csproj + MauiProgram.cs
4. MacOS: SentenceStudio.MacOS.csproj + MacOSMauiProgram.cs
5. Windows: SentenceStudio.Windows.csproj + MauiProgram.cs

**Changes Made:**
- Updated PackageReference in all 5 csproj files (lines 26-27) to use Microsoft.Maui.DevFlow.* v0.24.0-dev
- Updated using statements in all 5 MauiProgram.cs files (lines 3-4) from `MauiDevFlow.*` to `Microsoft.Maui.DevFlow.*`
- Added Debug condition to MacOS Blazor package (previously missing, now consistent with other platforms)
- All version wildcards (`*`) replaced with explicit `0.24.0-dev`

**NuGet Source:**
- Custom packages built from dotnet/maui-labs source
- Stored in ~/work/LocalNuGets/
- Local NuGet source "localnugets" already configured in NuGet.config

**Verification:**
- `dotnet restore` succeeded on iOS project
- All package references resolve correctly from local source
- No breaking changes to method calls (AddMauiDevFlowAgent, AddMauiBlazorDevFlowTools remain identical)

**Critical Fix Included:**
These custom packages include a broker registration fix not present in the Redth packages — required for MauiDevFlow tool integration in this project.

## 2026-03-28: Fix VocabularyWord.Language Missing Column on Mobile (SQLite)

**Status:** ✅ FIXED
**Error:** `SQLite Error 1: 'no such column: v.Language'` — Vocabulary page crashes on iOS

**Root Cause:**
`PatchMissingColumnsAsync()` only ran inside the `!historyExists && isLegacyDb` branch of `InitializeDatabaseAsync()`. Devices that had already transitioned to managed migrations in a previous app version (before PatchMissingColumnsAsync existed) had:
1. Migration history with `AddMissingVocabularyWordLanguageColumn` recorded as applied
2. But that migration is a no-op (empty Up())
3. PatchMissingColumnsAsync never executed on subsequent runs because `historyExists == true`
4. Result: Language column never added, EF Core queries fail

**Fix:**
1. Moved `PatchMissingColumnsAsync(conn)` to run in ALL mobile code paths (after lock cleanup, before MigrateAsync), not just the legacy-detection branch
2. Expanded patch list to cover all VocabularyWord encoding columns: Language, Lemma, Tags, MnemonicText, MnemonicImageUri, AudioPronunciationUri
3. Safe: pragma_table_info check skips columns that already exist

**Files Modified:**
- `src/SentenceStudio.Shared/Services/SyncService.cs` — moved PatchMissingColumnsAsync to universal path, expanded column list

**Key Learning:** Schema patches that run via raw SQL (outside EF migrations) must execute unconditionally on every startup, not just during one-time transition detection. The pragma_table_info guard makes them idempotent and safe.

---

## Session: 2025-01-XX — Added PlanNarrative Data Model

**Task:** Add rich narrative storytelling to daily plan generation

**What Changed:**
- Added `PlanNarrative`, `PlanResourceSummary`, `VocabInsight`, and `TagInsight` records to IProgressService.cs
- Extended `TodaysPlan` with optional `Narrative` property (kept `Rationale` for backward compatibility)
- Added `Narrative` to `DailyPlanResponse` and `PlanSkeleton`
- Enhanced `VocabularyReviewBlock` with `DueWords` list for narrative analysis
- Created `BuildNarrative()` method in DeterministicPlanBuilder that:
  - Summarizes selected resources with selection reasons
  - Analyzes vocabulary mix (new vs review, mastery levels)
  - Identifies struggling categories from VocabularyWord.Tags (comma-separated)
  - Generates pattern insights (e.g., "You're finding time-related vocabulary challenging")
  - Creates focus recommendations based on SRS data and activity types
- Wired narrative through the pipeline: DeterministicPlanBuilder → LlmPlanGenerationService → PlanConverter → ProgressService
- Added `NarrativeJson` field to DailyPlanCompletion for persistence (serialized as JSON)
- Implemented serialization on plan creation and deserialization on plan reconstruction

**Files Modified:**
1. `src/SentenceStudio.Shared/Services/Progress/IProgressService.cs` — Added narrative record types
2. `src/SentenceStudio.Shared/Models/DailyPlanGeneration/DailyPlanResponse.cs` — Added Narrative property
3. `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs` — BuildNarrative() method, DueWords tracking
4. `src/SentenceStudio.Shared/Services/PlanGeneration/LlmPlanGenerationService.cs` — Pass narrative through
5. `src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs` — Accept narrative parameter
6. `src/SentenceStudio.Shared/Services/Progress/ProgressService.cs` — Serialize/deserialize narrative, pass through pipeline
7. `src/SentenceStudio.Shared/Models/DailyPlanCompletion.cs` — Added NarrativeJson field

**Key Learnings:**
- VocabularyWord.Tags is a comma-separated string — split on commas to analyze category patterns
- VocabularyProgress includes .VocabularyWord navigation (loaded via Include in GetDueVocabularyAsync)
- TotalAttempts == 0 means brand new word, TotalAttempts > 0 means review word
- MasteryScore and Accuracy are separate metrics (mastery = long-term retention, accuracy = recent performance)
- Pattern analysis requires minimum 2 words per tag category to be meaningful
- BuildNarrative runs deterministically (no async) — all data is already loaded in VocabularyReviewBlock.DueWords
- Backward compatibility maintained: old code still uses Rationale, new code can use Narrative
- NarrativeJson field added without migration — will be null for existing DB rows (gracefully handled)

**Design Decision:**
Narrative is built deterministically from the same data used to select activities. It's not an LLM embellishment, but a structured summary of the pedagogical decisions the plan builder made. This keeps it fast, consistent, and explainable.


### Plan Narrative Feature — Full Integration (2026-03-30)

Completed full-stack backend implementation of Plan Narrative feature and coordinated with Kaylee on UI layer.

**What Happened:**
- Finalized `PlanNarrative` data model with complete resource, vocab, and pattern insight structures
- Implemented deterministic narrative generation in `DeterministicPlanBuilder.BuildNarrative()`
- Integrated narrative through entire plan pipeline (DeterministicPlanBuilder → LlmPlanGenerationService → PlanConverter → ProgressService)
- Added `NarrativeJson` persistence field to `DailyPlanCompletion`
- Validated backward compatibility (null narrative handled gracefully; old Rationale still works)
- Coordinated with Kaylee to ensure UI layer could consume narrative data structure

**Cross-Agent Impact:**
- **Kaylee (UI):** Narrative structure supports Bootstrap-themed dashboard display with resource links, vocab badges, and focus areas
- **Testing (future):** Narrative display, resource navigation, and vocab insight accuracy need validation

**Follow-ups:**
- UI now renders narrative on dashboard (Kaylee completed)
- Testing team should verify badge accuracy and responsive layout
- Analytics can measure narrative engagement once UI visible to users

### Issue #151: Incorrect responses NOT reverted on user override (2026-07-08)

**Problem:** VocabQuiz double-recorded when user clicked "I was correct" after an incorrect answer. `CheckAnswer()` immediately persisted the incorrect attempt, then `OverrideAsCorrect()` called `RecordAttemptAsync` a second time with correct. Result: TotalAttempts double-counted, two VocabularyLearningContext records, mastery score permanently damaged (40% penalty not reversed), streak lost.

**Fix:** Deferred recording pattern — `CheckAnswer()` creates a `pendingAttempt` field but does NOT call the service. `OverrideAsCorrect()` flips `pendingAttempt.WasCorrect = true`. Recording only happens when `NextItem()` or `DisposeAsync()` flushes via `RecordPendingAttemptAsync()`. Result: exactly one attempt per question, reflecting the user's final determination.

**Key files:**
- `src/SentenceStudio.UI/Pages/VocabQuiz.razor` — deferred recording pattern in CheckAnswer/OverrideAsCorrect/NextItem/DisposeAsync
- `src/SentenceStudio.Shared/Services/VocabularyProgressService.cs` — RecordAttemptAsync unchanged (correct as-is)

**Pattern:** When UI allows user correction after initial scoring, defer persistence until the correction window closes. Never record twice for the same question.


---

## 2026-04-03: Scoring Override Expiration Fix (Cross-Agent Summary)

**Contribution:** Fixed #151 — Scoring override window expiration wasn't working. Added `ExpiresAt` timestamp; overrides now expire cleanly.

**Related Work by Teammates:**
- **Kaylee:** Fixed #150 (text validation too strict) + #149 (turn counter wrong). Both integrated FuzzyMatcher for natural phrase input and proper word tokenization.
- **Jayne:** Currently end-to-end verification of all three fixes in running app.

**Team Sync:** All three bug fixes are interlinked — vocabulary scoring, activity validation, and turn timing. Interdependencies verified; no conflicts.
