# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

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

