# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Core Context (Summarized from Sessions)

**Backend Architecture:**
- Aspire orchestrates: api, cache (Redis), db (PostgreSQL), marketing, workers, webapp (CoreSync server)
- Production deploy: `azd deploy -e sstudio-prod --no-prompt` publishes to Azure Container Apps (Central US)
- Post-deploy validation critical: active revision must = latest revision (traffic can auto-route to old healthy revision while new crashes)
- Service discovery: `https+http://api` URI resolves via Aspire env vars, falls back to config Services section
- DB migrations: both API (Program.cs:213) and WebApp (Program.cs:151) call MigrateAsync() on startup → auto-apply

**Database & Models:**
- Server DB: PostgreSQL in Aspire (Production: Azure Container Apps managed); mobile: SQLite with CoreSync sync
- All synced entities: string GUID PKs (ValueGeneratedNever), UserProfileId for multi-user isolation, singular table names
- Shared project multi-target: hand-write migrations, update Designer + Snapshot manually (dotnet ef fails on multi-target)
- VocabularyProgress mastery = computed [NotMapped] property (uses MasteryScore, IsUserDeclared, VerificationState)
- ALL VocabularyProgressRepository methods must resolve ActiveUserId when userId empty — inconsistency causes silent bugs
- LearningResource cascade insertion bug: clear Vocabulary before Add, re-associate after SaveChanges (PG 23505 fix)
- Starter resource creation needs duplicate guard (checks "starter" tag + language + user)

**Activity & Progress Tracking:**
- ActivityTimerService → UpdatePlanItemProgressAsync is the ONLY completion persistence path (fires every minute + Pause)
- Activity pages GoBack(): Pause() → StopSession() → NavigateTo("/") — completion detected from time vs estimate
- DailyPlanCompletion records: written when plan first generated (InitializePlanCompletionRecordsAsync) — persistence mechanism for stability
- LoadPlanAsync: GetCachedPlanAsync first, falls back to GenerateTodaysPlanAsync; if both fail, new plan generated
- ProgressCacheService: Singleton with 5-minute TTL on all entries — plan cache can expire during completion
- DeterministicPlanBuilder: uses Guid.NewGuid() tiebreakers → non-deterministic (should be deterministic)
- BuildActivitySequenceAsync queries DailyPlanCompletions for last 3 days INCLUDING TODAY — today's records change activity selection

**Quiz & Vocabulary Scoring:**
- Phase 1 quiz (commit 75fdfe9): global streak-based mode (≥3 streak OR ≥0.50 mastery = text), PendingRecognitionCheck forces MC until correct, tiered rotation (Tier1=1, Tier2=2w1text, Tier3=3MC+3text), cumulative session counters
- DifficultyWeights: VocabMatching=0.8, WordAssociation=1.0, VocabQuiz MC=1.0, Cloze=1.2, VocabQuiz Text=1.5, Writing=1.0, VocabQuiz Sentence=2.5
- Cross-activity mastery via ExtractAndScoreVocabularyAsync (shared pipeline), deduplicate by DictionaryForm, collect probes AFTER loop
- Activity taxonomy: Writing/Translation/Scene=1.5 DW, Conversation=1.2, passive=0
- PenaltyOverride on VocabularyAttempt: Conversation=0.8x, others omit
- RecordPassiveExposureAsync for Reading lookups: updates LastExposedAt/ExposureCount, never touches mastery/streak
- VocabQuiz stale-progress: RecordPendingAttemptAsync discards returned VocabularyProgress — not written back to currentItem.Progress (bug)
- Translation bug: ProgressService injected but GradeMe() uses ad-hoc prompt not requesting vocabulary_analysis (fixed via GradeTranslation() switch)

**Auth & Identity:**
- JWT + refresh tokens: 120 min default (config Jwt:ExpiryMinutes), 90 days refresh (config RefreshToken:LifetimeDays)
- Mobile IdentityAuthService: restores JWT from SecureStorage before network refresh, 10s timeout for silent refresh
- CoreSync HTTP client: named `"HttpClientToServer"`, auth handler chains via AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()
- DevAuthHandler duplicated in both API/Web projects (future refactor to shared)
- Web auth: same Auth:UseEntraId pattern as API, CoreSync middleware must run AFTER auth middleware
- Microsoft.Identity.Web v3.8.2 for Entra ID Bearer auth, scope policies via authorization helpers
- TenantContextMiddleware: maps Entra ID claims (tid, oid, name) and DevAuthHandler claims (tenant_id, NameIdentifier, Name)
- Captain's requirement: never show login unless explicitly logged out, mobile auth keeps people signed in weeks (not multiple times/day)

**Entra ID Configuration:**
- Conditional auth via Auth:UseEntraId config flag (API + Web)
- AzureAd public IDs safe to commit (TenantId, ClientId, Audience NOT secrets)
- Scope policies (e.g., "user.read") enforced server-side
- MauiAuthenticationStateProvider wraps IAuthService for Blazor auth framework (Scoped lifetime)

**Data Sync & YouTube Integration:**
- CoreSync: synced entities use string GUID PKs, UserProfileId required, TriggerSyncAsync() MUST be called explicitly after SaveChangesAsync()
- SharedSyncRegistration source of truth: SQLite + PostgreSQL configs must match
- YouTube: YoutubeExplode (no API key, scraper), handles transcript fetch + audio extract + caption discovery
- TranscriptFormattingService: SmartCleanup (rules) + PolishWithAiAsync (AI) two-stage pipeline
- VideoImportPipelineService: orchestrates fetch → clean → extract vocab → save LearningResource + VocabWords
- MonitoredChannel + VideoImport: synced entities (string GUID PKs, UserProfileId)
- VideoImportStatus: Pending → FetchingTranscript → CleaningTranscript → GeneratingVocabulary → SavingResource → Completed/Failed
- Workers project (SentenceStudio.Workers): skeleton BackgroundService ready for ChannelPollingWorker

**Scoring & Rules:**
- Wrong-answer temporal weighting: penalty scales by track record (0.6–0.92), partial streak preservation (0–50%), correct-answer floor (Math.Max)
- Tier 1 rotation requires BOTH text production AND cleared PendingRecognitionCheck
- Recovery-aware mastery: +0.02 per correct when streak < mastery (eliminates plateau)
- LostKnownThisSession detection AFTER RecordAttemptAsync (deferred), never eagerly
- IsKnown re-qualification: 14-day review interval (not 60)
- Words never repeat within round — rounds shrink as mastered words exit
- OverriddenScoring ExpiresAt: validates on read, expired overrides silently disregarded

**Coding Standards:**
- EF tooling: multi-target csproj fails with "ResolvePackageAssets" → hand-write migration files + update Designer/Snapshot
- Migrations: `dotnet ef migrations add <Name> --project src/SentenceStudio.Shared --startup-project src/SentenceStudio.Shared`
- NEVER delete data; fix migrations instead
- Build with TFM: `dotnet build -f net10.0-maccatalyst` or `dotnet build -t:Run -f net10.0-maccatalyst`
- All CoreSync-synced entities: string GUID PKs with ValueGeneratedNever()
- Aspire config: `builder.Configuration["AI:OpenAI:ApiKey"]` NOT `["AI__OpenAI__ApiKey"]`
- IChatClient nullable pattern: inject as `IChatClient?`, fall back gracefully if null
- GitHub HttpClient: named client "GitHub" with PAT set per-request from config
- Feedback endpoints: HMAC-signed preview tokens (Base64Url + HMACSHA256), 10-min expiry

## Learnings

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- ProgressCacheService is a Singleton with 5-minute TTL on all cache entries — plan cache can expire during normal activity completion
- DeterministicPlanBuilder uses `Guid.NewGuid()` tiebreakers in SelectInputActivity and SelectOutputActivity — this makes plan generation non-deterministic even with identical inputs
- DailyPlanCompletion records are written to DB when plan is first generated (InitializePlanCompletionRecordsAsync) — these are the persistence mechanism for plan stability across restarts
- LoadPlanAsync calls GetCachedPlanAsync first, then falls back to GenerateTodaysPlanAsync — if both cache and DB reconstruction fail, a completely new plan is generated
- BuildActivitySequenceAsync queries DailyPlanCompletions for the last 3 days including TODAY — today's newly-written completion records change the input to activity selection

- Shadowing page (Shadowing.razor) uses Plugin.Maui.Audio IAudioPlayer for native playback and JS interop for server-side Blazor — playbackPosition is a 0-1 normalized ratio passed to WaveformDisplay
- Transcript-mode shadowing sentences have null NativeLanguageText (no translation); AI-generated mode populates translations via Scriban template. Translation toggle button must be conditionally shown.
- Audio playback position requires a System.Threading.Timer polling at ~100ms to update the waveform/timer display in real-time; IAudioPlayer has no built-in position-changed event

- JWT claims contain email/name data that must be explicitly backfilled into local SQLite UserProfile on mobile — the webapp reads these from Identity server-side, but the mobile app only gets them via the JWT

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
- **Feedback endpoints** use HMAC-signed preview tokens (Base64Url payload + HMACSHA256 signature) with 10-min expiry — token carries full issue content so submit is a single token POST
- **GitHub HttpClient** registered as named client `"GitHub"` with base address `https://api.github.com`, default headers (Accept, User-Agent, X-GitHub-Api-Version) — PAT set per-request from config `GitHub:Pat`
- **IChatClient nullable pattern** — always inject as `IChatClient?` in endpoints; if null (no API key), fall back gracefully instead of 500
- **AI label allowlist** — server-side filter to `["bug", "enhancement"]` only; never trust AI-generated labels directly for GitHub issue creation
- **AppHost secret params** follow pattern: `builder.AddParameter("name", secret: true)` then `.WithEnvironment("Section__Key", param)` — added `githubpat` for feedback
- `azd deploy -e sstudio-prod --no-prompt` successfully published `api`, `cache`, `db`, `marketing`, `webapp`, and `workers` in Central US; validate against the ACA default hostname first and treat custom-domain DNS as separate follow-up
- Production smoke-check pattern: `webapp.livelyforest-*.azurecontainerapps.io` should return `200` and redirect to `/auth/login`, `api.../api/channels` should return `401` when anonymous, and `marketing` must be validated via `https://www.sentencestudio.com` because `SentenceStudio.Marketing/appsettings.Production.json` restricts `AllowedHosts`
- **VocabQuiz stale-progress bug**: `RecordPendingAttemptAsync` calls `ProgressService.RecordAttemptAsync()` but discards the returned `VocabularyProgress` — never assigns it back to `currentItem.Progress`. The sentence shortcut path (line 933) does write back correctly. Fix: add `currentItem.Progress = updatedProgress` in `RecordPendingAttemptAsync`.
- **EF tooling vs multi-target csproj**: `dotnet ef migrations add` fails with "ResolvePackageAssets does not exist" on the multi-target Shared project (net10.0;net10.0-ios;net10.0-android;net10.0-maccatalyst). Hand-write migration files + update both Designer and Snapshot files manually. Pattern established in `CurrentStreakToFloat` and `AddPassiveExposureFields` migrations.
- **ExtractAndScoreVocabularyAsync** is the shared extraction pipeline for all sentence-based activities. Deduplicate VocabularyAnalysis by DictionaryForm, score via RecordAttemptAsync, collect verification probes AFTER loop. Lives on VocabularyProgressService. VocabScoringResult is the return type.
- **PenaltyOverride on VocabularyAttempt** — if set, RecordAttemptAsync uses it instead of the computed scaled penalty factor. Only Conversation should pass 0.8; all others omit it.
- **Translation.razor was a dead injection** — VocabularyProgressService was injected but GradeMe() used a raw ad-hoc AI prompt that didn't request vocabulary_analysis. Fixed by switching to TeacherSvc.GradeTranslation() which uses the GradeTranslation.scriban-txt template.
- **RecordPassiveExposureAsync** — new method for Reading word lookups. Creates VocabularyLearningContext with InputMode="Passive", ContextType="Exposure". Updates LastExposedAt/ExposureCount on VocabularyProgress. Never touches mastery/streak/SRS.
- **Phase 1 quiz behavior** (commit 75fdfe9): Mode selection now uses global `Progress.CurrentStreak >= 3 OR MasteryScore >= 0.50`, NOT session-local streaks. `PendingRecognitionCheck` flag on `VocabQuizItem` forces MC until correct MC clears it. Tiered rotation: Tier 1 (high mastery) = 1 text correct, Tier 2 (mid) = 2 correct w/ 1 text, Tier 3 (low) = 3 MC + 3 text. Session counters are CUMULATIVE (never reset on wrong). Words rotate out mid-round immediately. `UpdateProgressAsync` added to `IVocabularyProgressService` for SRS field persistence.

- **IsKnown re-qualification**: `LostKnownThisSession` flag tracks words that lose IsKnown mid-session; on re-qualification, `ReviewInterval = 14` days (not 60). Detection in `RecordPendingAttemptAsync` via pre/post IsKnown snapshot.
- **Repos use per-call scoped DbContext** — every repository method creates its own `IServiceProvider.CreateScope()`, so returned entities are detached POCOs. No tracking conflicts between quiz's in-memory reference and future DB operations.
- **Only VocabQuiz displays live progress** — Cloze, Writing, VocabMatching all call RecordAttemptAsync fire-and-forget. Only VocabQuiz has a Learning Details panel reading `currentItem.Progress`. Other activities don't have this stale-data bug.
- **MasteryScore correct-answer double-whammy**: On correct answer, `MasteryScore = min(EffectiveStreak/7, 1.0)` REPLACES old score entirely. After a wrong resets streak to 0, the next correct sets mastery to 1/7=0.14 — worse than the `*=0.6` penalty result of 0.6. The penalty multiplier is almost meaningless; the real damage is streak reset + replacement formula.
- **Temporal weighting design (2025-07-23)**: Recommended 3-part hybrid: (1) scaled penalty via `max(0.6, 1 - 0.4/(1+log(1+CorrectAttempts)))`, (2) partial streak preservation `min(0.5, log(1+CorrectAttempts)/8)`, (3) `Math.Max(streakScore, oldMastery)` on correct answers so correct never lowers score. No new DB fields needed. Decision doc: `.squad/decisions/inbox/wash-temporal-weighting.md`

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

### 2026-04-08 — Cross-Agent Update: GitHub Backlog Triage Complete

**Team Status:** Backlog triage completed. Infrastructure audit recommended keeping most deployment/hardening issues open.

**Key Decision:** Feedback auth fix (user_profile_id JWT claim) merged to team decisions log. Multiple endpoints (Feedback, Channel, Import) required this claim addition across three token paths: JwtTokenService, ServerAuthService, DevAuthHandler.

**Impact for Wash:** Operational priorities identified (#57, #59, #58). No stale duplicates in backlog. Ready for DevOps roadmap integration.

## Plan Generation Test Suite — Bug Documentation

**Date:** 2025-07-23
**Task:** Write comprehensive test suite against DeterministicPlanBuilder, PlanConverter, and new GeneratedPlanValidator.

### Files Created
- `src/SentenceStudio.Shared/Services/PlanGeneration/GeneratedPlanValidator.cs` — runtime invariant checker (NEW production code)
- `tests/SentenceStudio.UnitTests/PlanGeneration/PlanGenerationTestFixture.cs` — in-memory SQLite + full DI integration fixture
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderVocabularyReviewTests.cs` (7 tests)
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderResourceSelectionTests.cs` (7 tests)
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderActivitySequenceTests.cs` (7 tests)
- `tests/SentenceStudio.UnitTests/PlanGeneration/PlanValidationTests.cs` (8 tests)
- `tests/SentenceStudio.UnitTests/PlanGeneration/StudyPlanIntegrationTests.cs` (6 tests)
- `tests/SentenceStudio.UnitTests/PlanGeneration/PlanConverterTests.cs` (8 tests)

### Test Results: 43 total, 40 passed, 3 failed

**FAILED (confirmed bugs):**

1. **`SelectionIsDeterministic_WithSameInputs`** — `.ThenBy(c => Guid.NewGuid())` tiebreaker makes resource selection non-deterministic when scores are equal. Fix: use deterministic tiebreaker (e.g., resource ID hash).

2. **`VocabCount_MatchesReality`** — `WordCount = Math.Min(20, dueWords.Count)` caps the count at 20, but `DueWords = dueWords` passes ALL due words (e.g., 25). WordCount says 20 but DueWords.Count is 25. Fix: either cap the DueWords list or set WordCount = DueWords.Count.

3. **`ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed`** — 14-day recency window (`today.AddDays(-14)`) means a resource used 15 days ago has no usage record visible, giving it DaysSinceLastUse=999 (same as never-used). Fix: extend window or use all-time history.

**PASSED (validator-detected design gaps):**
- `HighMasteryLowProduction_IncludedButShouldBeProductionMode` — validator correctly flags that VocabularyReviewBlock has no per-word mode tracking for promoted words (MasteryScore >= 0.50)
- `ValidatorPassesOnGeneratedPlan` — validator finds violations on real generated plans, confirming the production mode gap

### Learnings
- ApplicationDbContext inherits IdentityDbContext on net10.0 — test project needs `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
- In-memory SQLite with shared connection works well for integration testing DeterministicPlanBuilder's scoped DI pattern
- Repositories use `GetService<>` (not `GetRequiredService<>`) for optional deps like ISyncService and AiService — null-safe in test context
- Test filter `--filter "PlanGeneration|PlanValidation|PlanConverter"` targets all plan test classes
- DeterministicPlanBuilder: tiebreakers must use deterministic hashing (e.g., `Id.GetHashCode() ^ today.GetHashCode()`), NEVER `Guid.NewGuid()` — same inputs must yield same outputs
- DeterministicPlanBuilder: `VocabularyReviewBlock.DueWords` must be truncated to match `WordCount` — invariant: `WordCount == DueWords.Count`
- DeterministicPlanBuilder: resource usage lookback window is 30 days (not 14) — resources used 15-30 days ago must not appear as "never used"

- VocabQuiz.razor was filtering words with obsolete IsCompleted (persisted bool, never updated) instead of IsKnown (computed: MasteryScore >= 0.85 AND ProductionInStreak >= 2) — caused known words to leak into quizzes
- VocabQuiz.razor ignored DueOnly query parameter entirely — when SRS plan routes to quiz with DueOnly=true, the quiz must filter to words where NextReviewDate <= now (or unseen words with TotalAttempts == 0)
- VocabQuiz.razor mode selection logic (MasteryScore >= 0.50 to Text) was correct all along; Bug C was a non-issue since Progress is always constructed for each word (never null at mode-check time)
- Quiz page query params: resourceIds, skillId, planItemId, and now DueOnly — all via [SupplyParameterFromQuery]
- When creating default VocabularyProgress for words not in progressDict, do NOT set obsolete fields (IsCompleted, CurrentPhase) — rely on computed properties

### Structural Activity Page Fixes — Scene, Conversation, Listening (2026-07-17)

**Context:** Captain ordered a full audit of activity pages launched from the study plan. Three pages had structural problems — they ignored the plan's ResourceId/SkillId parameters or routed to nonexistent pages.

**What was wrong:**
1. **Scene.razor** (`/scene`) — Accepted no ResourceId/SkillId query params. Plan passed them but they were silently dropped.
2. **Conversation.razor** (`/conversation`) — Same problem. Only accepted scenarioId, ignoring ResourceId/SkillId.
3. **Listening route** — PlanConverter mapped Listening to "/listening" but no Listening.razor exists. Would 404.
4. **Bonus find:** PlanConverter also mapped SceneDescription to "/describe-scene" but the actual page is at `/scene`. Index.razor had its own MapActivityRoute that papered over this, but PlanConverter's stored routes were wrong.

**What I fixed:**
- **Scene.razor:** Added `[SupplyParameterFromQuery]` for `resourceId` and `skillId`. Enhanced logging. SceneImageService has no resource-aware filtering (simple image gallery), so scenes remain random — params accepted for future use.
- **Conversation.razor:** Added `[SupplyParameterFromQuery]` for `resourceId` and `skillId`. Enhanced logging. ScenarioService has no resource-awareness, so behavior falls back to default scenario — params accepted for future matching.
- **PlanConverter.cs:** Changed Listening to "/shadowing" (no dedicated listening page; shadowing handles audio comprehension). Changed SceneDescription to "/scene" (matching actual page route).
- **Index.razor:** Synced MapActivityRoute — changed Listening to "shadowing" (was incorrectly mapped to "reading").
- **PlanConverterTests.cs:** Updated expected routes for Listening (/shadowing) and SceneDescription (/scene).

**Verification:** 0 build errors across UI and WebApp projects. All 275 unit tests pass.

**Limitation noted:** Scene and Conversation accept ResourceId/SkillId but cannot yet filter content by resource. Their services need resource-aware methods (future work). The parameters are wired and logged so the plan connection is established.

---

## 2025-07-18 — SECURITY FIX: Cross-user data leak via shared preferences singleton

**Bug:** After every Azure deploy, all users got logged in as "Jose" (or whichever user last signed in). The `WebPreferencesService` was a server-side singleton backed by a single JSON file that stored `active_profile_id` — shared across ALL HTTP requests. This is a cross-user data leak.

**Root Cause:** The preference system was designed for MAUI (single-user per device). On the server, a singleton JSON file is shared by all users. When User A logs in, their profile ID overwrites User B's in the shared file.

**Fix — `IActiveUserProvider` abstraction:**
- Created `IActiveUserProvider` interface with `GetActiveProfileId()` + `ShouldFallbackToFirstProfile`
- **MAUI implementation** (`PreferencesActiveUserProvider`): reads from device preferences (existing behavior, safe for single-user)
- **WebApp implementation** (`ClaimsActiveUserProvider`): reads from authenticated Identity user's `UserProfileId` via `IHttpContextAccessor` and `UserManager<ApplicationUser>` lookup. Falls back to `user_profile_id` JWT claim if present.
- Server implementation returns `ShouldFallbackToFirstProfile = false` so repositories return null instead of leaking another user's profile via `FirstOrDefaultAsync()`

**Files Created:**
- `src/SentenceStudio.Shared/Abstractions/IActiveUserProvider.cs`
- `src/SentenceStudio.Shared/Services/PreferencesActiveUserProvider.cs`
- `src/SentenceStudio.WebApp/Auth/ClaimsActiveUserProvider.cs`

**Files Modified (removed direct `_preferences.Get("active_profile_id")` reads):**
- `UserProfileRepository.cs` — uses `IActiveUserProvider`, respects `ShouldFallbackToFirstProfile`
- `SkillProfileRepository.cs` — uses `IActiveUserProvider`
- `UserActivityRepository.cs` — uses `IActiveUserProvider`
- `VocabularyProgressRepository.cs` — uses `IActiveUserProvider`
- `LearningResourceRepository.cs` — uses `IActiveUserProvider`
- `ProgressCacheService.cs` — uses `IActiveUserProvider`
- `WebApp/Program.cs` — registers `ClaimsActiveUserProvider`
- `CoreServiceExtensions.cs` — registers `PreferencesActiveUserProvider` via `TryAddSingleton`

**DI Registration Strategy:**
- WebApp registers `ClaimsActiveUserProvider` as `IActiveUserProvider` BEFORE calling `AddSentenceStudioCoreServices()`
- `CoreServiceExtensions` uses `TryAddSingleton` so the WebApp's registration wins; MAUI hosts get the preferences-based default

**Verification:** WebApp (net10.0), UI project, and all unit tests build and pass with 0 errors.

**Remaining work (lower priority):** Many Blazor `.razor` pages also read `active_profile_id` directly from `IPreferencesService`. These should eventually migrate to `IActiveUserProvider` too, but the critical data-layer leak is now plugged.

### Test Fixture Fix (same session)

After the security fix, test fixtures needed `IActiveUserProvider` registration since repos no longer read directly from `IPreferencesService`.

**`PlanGenerationTestFixture.cs`** — added `IActiveUserProvider` registration backed by the existing mock preferences:
```csharp
services.AddSingleton<IActiveUserProvider>(new PreferencesActiveUserProvider(mockPreferences.Object));
```

**`ProgressCacheServiceTests.cs`** — `ProgressCacheService` constructor changed from `(ILogger, IPreferencesService)` to `(ILogger, IServiceProvider)`. Replaced `Mock<IPreferencesService>` user-switching with `Mock<IActiveUserProvider>` backed by a minimal `ServiceProvider`.

**Result:** 363 passed, 1 pre-existing failure (`ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed` — fails on clean main too).

### Post-mortem code improvements (2026-07-17)

- Added automation IDs to VocabQuiz.razor key elements: `quiz-info-button`, `quiz-info-panel`, `quiz-option-a` through `quiz-option-d`, `quiz-text-input`, `quiz-progress`, `quiz-correct-count` — these make DevFlow automation reliable without fragile CSS selectors
- Created `DebugHealth.razor` at `/debug/health` route — a `#if DEBUG`-gated page showing DB status, migrations, user profile, vocab counts, resource counts, API connectivity, and CoreSync status
- The UI project (`SentenceStudio.UI`) is a Razor class library targeting `net10.0` — `#if DEBUG` works in `@code` blocks but not in Razor markup, so use a `const bool IsDebugBuild` pattern set via `#if DEBUG` in code and `@if (IsDebugBuild)` in markup
- `ApplicationDbContext` DbSet names: `VocabularyWords`, `VocabularyProgresses`, `LearningResources`, `UserProfiles` (plural)
- Named HttpClient `"HttpClientToServer"` is registered in `ServiceCollectionExtentions.cs` and is the standard way to reach the API from UI code
- `/health` endpoint exists in Aspire service defaults (`WebServiceDefaults/Extensions.cs`) — safe to ping for connectivity checks

### Phase 0 — Scoring Engine Foundation (2026-04-15)

- Changed VocabularyProgress.CurrentStreak from int to float — needed for weighted difficulty increments (MC=1.0, Text=1.5, Sentence=2.5)
- EF migration CurrentStreakToFloat created manually for both SQLite and PostgreSQL (EF tooling has ResolvePackageAssets target issue with multi-target MAUI projects)
- StreakInfo in IProgressService tracks daily practice streak (consecutive days) — NOT the same as VocabularyProgress.CurrentStreak (weighted correct answers). Do NOT change StreakInfo to float.
- Scoring engine now uses 5 named constants: WRONG_ANSWER_FLOOR, MAX_WRONG_PENALTY, MAX_STREAK_PRESERVE, STREAK_PRESERVE_DIVISOR, RECOVERY_BOOST
- VocabQuiz.razor RecordPendingAttemptAsync was missing write-back: currentItem.Progress = updatedProgress — this caused the quiz UI to show stale progress data
- When updating scoring behavior, check test assertions in MasteryAlgorithmIntegrationTests, MultiDayLearningJourneyTests, and ScoringEngineTests — they assert specific numeric outcomes


### 2025-07-25 — Production Data Safety Response & Azure Hardening (P0)

**Incident:** `aspire deploy` recreated Postgres container without Azure File share volume mount, destroying all production user data. No backup existed — data loss permanent.

**Response Phases:**

1. **Phone Data Recovery Investigation**
   - Analyzed CoreSync bidirectional sync to determine if user data synced to phone could be recovered
   - Identified UserId orphaning risk: recovery logic MUST validate that words being re-tagged belong to correct user profile
   - Recommended: Before any recovery attempt, add explicit user ID validation guardrails
   - Finding: Phone-based recovery IS viable if synced data was pushed to device before server loss and UserId is validated correctly

2. **Azure Infrastructure Hardening (Layers 2–5 of 5-layer defense)**
   - **Layer 2 — Resource Locks:** Applied `CanNotDelete` locks to:
     - `db` container app
     - `vol3ovvqiybthkb6` storage account
   - **Layer 3 — Pre-deploy Hook:** Created `scripts/pre-deploy-check.sh` with 5 mandatory checks:
     - Resource locks exist (at least 2)
     - db container app exists
     - Current revision has AzureFile volume mount
     - Storage account exists
     - File share exists and is non-empty
   - **Layer 4 — Runbook Verification:** Updated `docs/deploy-runbook.md` Step 5 with lock verification checks
   - **Layer 5 — Managed DB Migration:** Documented Azure PostgreSQL Flexible Server migration path (eliminates container-recreation risk entirely)

**Files Modified:**
- `AppHost.cs` — PublishAsAzureContainerApp callback wiring (pre-incident, ensures volume mount on future revisions)
- `azure.yaml` — preprovision hook calling pre-deploy-check.sh
- `DataRecoveryService.cs` — Phone recovery flow analysis (NEW)
- `IdentityAuthService.cs` — Cross-referenced in recovery scenario
- `docs/deploy-runbook.md` — Hardened with mandatory checklist + migration plan

**Commits:**
- `b466985` — Orphan recovery support (phone data recovery analysis)
- `9d71fbf` — Azure resource locks + pre-deploy hook verification

**Key Learnings:**
- Deploy tool mixing (azd vs aspire deploy) silently overwrites infrastructure configuration — Bicep + AppHost DSL manage resources differently
- Container-based databases are architecturally fragile — any tool that recreates containers can accidentally lose volume mounts
- Pre-deploy hooks in azure.yaml are effective enforcement mechanism (stronger than advisory checklists)
- Phone-based recovery is viable path if synced data existed pre-loss and UserId validation is explicit

**Cross-team coordination:**
- Zoe: Wrote P0 governance decision + migration plan
- Wash: Implemented infrastructure enforcement + phone recovery analysis
- Both: Coordinated on 5-layer defense model

**Next:** Monitor next deploy cycle; begin managed database migration planning (1 day effort, ~$17/month, eliminates vulnerability class)

## Issue #162 — UserProfile Name/Email not populated on mobile after login

**Date:** 2025-07-22
**Scope:** `IdentityAuthService.StoreTokens()` — backfill local UserProfile from JWT claims

**Problem:** Mobile app local SQLite UserProfile had empty Name/Email after login. Webapp showed them because it read from Identity claims server-side, but mobile never wrote JWT claim data into the local profile.

**Fix:**
- Injected `UserProfileRepository?` into `IdentityAuthService` (optional param, same pattern as `DataRecoveryService?`)
- Added `BackfillProfileFromJwtAsync(token)` called after orphan recovery in `StoreTokens`
- Extracts email and name from JWT claims (ClaimTypes.Email, "email", ClaimTypes.Name, "name", "unique_name")
- Only sets fields that are currently empty (never overwrites user edits)
- Non-fatal: wrapped in try/catch so login flow continues even if backfill fails
- **Daily plan stability 3-part fix** (deterministic tiebreakers + date-keyed cache + exclude-today completions):
  - `HashCode.Combine(DateTime.Today, activityName)` replaces `Guid.NewGuid()` tiebreakers in SelectInputActivity / SelectOutputActivity
  - ProgressCacheService plan cache now keyed by `{userId}:plan_{yyyy-MM-dd}` with TTL until midnight, replacing the 5-minute TTL
  - BuildActivitySequenceAsync recent-completions query now uses `c.Date < today` to exclude same-day records from influencing activity selection

## Learnings

### 2025-01-14: Activity Log Data Layer Implementation

**Task**: Implement backend data layer for the Activity Log page - DTOs, category mapper, and service methods.

**Key Implementation Details**:

1. **DTOs Added to IProgressService.cs**:
   - `ActivityCategory` enum (Input/Output)
   - `ActivityLogEntry` - individual plan item with category, time, completion status
   - `ActivityLogPlan` - cluster of items generated at the same time
   - `ActivityLogDay` - all plans for a given date
   - `ActivityLogWeek` - Monday-anchored week with 7 days (some may be empty)
   - `ActivityCategoryMapper` static helper to categorize activities

2. **GetActivityLogAsync Implementation**:
   - Query pattern: Use `_serviceProvider.CreateScope()` to get fresh DbContext (matches existing patterns)
   - User ID: Retrieved via `_userProfileRepo.GetAsync()` and use `userProfile.Id`
   - Plan clustering: Items with CreatedAt within 60 seconds belong to same plan generation
   - Week building: Monday-anchored, always 7 elements in Days array, fill empty days with zero values
   - Filtering: Optional ActivityCategory filter applied at entry level
   - Resource/Skill lookup: Used existing repositories to enrich entries with titles
   - Order: Most recent week first (weeks.Reverse())

3. **Patterns Followed**:
   - Scoped DbContext pattern from `ClearCachedPlanAsync` and `ReconstructPlanFromDatabase`
   - User profile retrieval from `_userProfileRepo.GetAsync()`
   - Logging with emoji prefixes for visual parsing
   - Graceful handling of empty data (return empty list, not null)
   - Dictionary lookups for O(1) resource/skill title resolution

4. **Gotchas Avoided**:
   - SkillProfile.Title (not Name) - caught during build
   - DailyPlanCompletion.ActivityType is string, requires Enum.TryParse
   - Empty days must be filled in weeks to ensure 7-element array
   - Filter applied after parsing, not at query level (cleaner logic)

**Build Status**: ✅ Clean build, 0 errors (658 warnings are pre-existing)


### Activity Log Data Service (2026-04-16)

Implemented backend service for Activity Log feature (Strava-inspired Practice Calendar):

**DTOs Added:**
- `ActivityLogEntry`: Individual activity session (type, duration, resource/skill, timestamp)
- `ActivityLogPlan`: Plan view with activities and completion metadata
- `ActivityLogDay`: Day aggregation with completeness and total minutes
- `ActivityLogWeek`: Week aggregation with 7 days (Monday-anchored)
- `ActivityCategory` enum: Input, Output, Mixed, Rest
- `ActivityCategoryMapper`: Deterministic categorization based on activity type

**Service Implementation (ProgressService):**
- `GetActivityLogAsync(userId, startDate, weeks)`: Main API
- Temporal clustering: Groups completions by day, then week
- Resource/skill enrichment: Joins with LearningResource and SkillProfile tables
- Deterministic categorization: Uses ActivityCategoryMapper (not dynamic/user-driven)
- Pagination: Returns N weeks from startDate
- Null-safe: Handles deleted resources gracefully

**Design Decisions:**
- Monday-anchored weeks (ISO 8601) — first week of request may be partial
- Input/Output determined by activity type, not user annotation
- Service owns all filtering logic; Kaylee's UI layer calls GetActivityLogAsync with filters
- ActivityCategory enum is definitive source for UI color/styling logic

**Integration:**
- Depends on existing DailyPlanCompletion and ActivityPlanItem models
- No breaking changes to ProgressService public API
- Data compatible with Kaylee's ActivityLog.razor + ActivityDot.razor components

**Cross-Agent:** Kaylee integrated DTOs into UI layer; Coordinator fixed build errors

---

## 2025-01-24: Quiz Resource Mismatch — Root Cause Diagnosis

**Task:** Captain reported mismatch between Dashboard Insights panel (8 new, 12 review, 497 due words) and VocabQuiz page ("no vocabulary loaded" toast).

**Investigation:**
Traced two divergent code paths:

1. **Insights Panel (Global Scope):**
   - `DeterministicPlanBuilder.BuildNarrative()` → creates `VocabInsight` from `GetDueVocabularyAsync()`
   - Query: `VocabularyProgressRepository.GetDueVocabularyAsync()` returns ALL due words for user (497 total)
   - Filters: `NextReviewDate <= today` AND NOT Known (mastery < 0.85 OR production < 2)
   - **NO resource filter** — global user scope

2. **Quiz Vocabulary Loading (Resource-Scoped):**
   - `VocabQuiz.razor.LoadVocabulary()` parses `resourceIds` query param
   - Loads vocabulary from `LearningResourceRepository.GetResourceAsync(id)` for each resource
   - Filters by `IsKnown`, `DueOnly`, `IsInGracePeriod`
   - **ONLY words linked to specified LearningResourceId**

**Root Cause:**
Filter divergence at navigation boundary:
- Plan builder picks a LearningResourceId (e.g., "daily review") based on most-common resource among due words (via `LearningContexts`)
- Insights panel counts ALL 497 due words (global)
- Plan converter passes `ResourceId` in route params (`PlanConverter.BuildRouteParameters`)
- Dashboard navigation constructs `resourceIds={item.ResourceId}` query string
- Quiz filters to ONLY words from that resource → may find 0 words if they're all mastered

**Data Model Finding:**
- `VocabularyProgress` → `LearningContext[]` → `LearningResourceId` (many-to-many)
- A word can belong to multiple resources
- "daily review" is likely a seed/default bucket for ungrouped vocabulary
- Plan builder selects this resource because it has most due words, but NOT all 497 belong to it

**Hypothesis Type:** FILTER BUG (scope mismatch)
- Insights = global (user-level)
- Quiz = resource-scoped
- NOT a data bug or model bug — both are correct within their scope

**Recommended Fix (for Zoe to decide):**
Option 2: Make Quiz global when VocabularyReview activity doesn't pass ResourceId
- Remove `parameters["ResourceId"]` from `PlanConverter` for VocabularyReview
- Quiz falls back to `GetAllVocabularyWordsWithResourcesAsync()` (already exists)
- Insights and Quiz now use same scope (global user due words)

**Alternatives:**
- Option 1: Scope Insights to ResourceId (hides full due count)
- Option 3: Dual-mode quiz with spillover (contextual + backfill)
- Option 4: Show both counts in UI ("497 total, 20 in today's focus")

**Report:** `.squad/decisions/inbox/wash-quiz-resource-mismatch-trace.md`

**Cross-Agent:** Zoe to review and assign fix strategy; may involve frontend changes (Kaylee) if Option 4 chosen


---

## 2025-01-24 — Quiz Resource Decoupling Implementation

**Request:** Implement Option A/Option 2 (Clean Decoupling) from Zoe's architecture decision

**Context:**
- Captain confirmed: "For a quiz, it's all about the vocabulary. I don't see the point of a learning resource driving the Quiz."
- Zoe's decision: VocabularyReview is vocabulary-driven, NOT resource-driven
- Remove ResourceId from Quiz plan-item pathway so Quiz loader falls through to global user vocab pool

**Implementation:**
1. **PlanConverter.cs (Lines 125-131):** Removed `parameters["ResourceId"] = resourceId` for VocabularyReview. Added comment explaining vocabulary-driven nature.

2. **DeterministicPlanBuilder.cs (Lines 455-467):** Set `ResourceId = null` for main plan VocabularyReview PlannedActivity. Simplified Rationale to always show global count (removed conditional "contextual learning" text).

3. **DeterministicPlanBuilder.cs (Lines 73-83):** Set `ResourceId = null` for fallback plan VocabularyReview. Added consistent Rationale.

**Verification:**
- ✅ Build clean: `dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` (725 warnings, 0 errors)
- ✅ VocabQuiz.razor already handles empty resourceIds correctly (falls through to `GetAllVocabularyWordsWithResourcesAsync()`)
- ✅ Index.razor LaunchPlanItem correctly skips resourceIds parameter when `item.ResourceId` is null

**Key Findings:**
- VocabularyGame already decoupled (only passes SkillId, not ResourceId)
- Contextual resource selection logic (lines 154-185) still runs but ResourceId is no longer used by PlannedActivity or Quiz loader — kept for future pedagogical features or diagnostics
- No database changes needed — `DailyPlanCompletion.ResourceId` can be NULL

**Changes:**
- 3 surgical edits in 2 files
- No schema changes, no migrations, no test changes
- Historical plan items will have ResourceId populated until next regeneration

**Outcome:** VocabularyReview now loads from full user vocabulary pool (497 words) instead of resource-scoped subset (20 words). Insights panel and Quiz are now consistent.

**Report:** `.squad/decisions/inbox/wash-quiz-decouple-fix.md`

**Next:** Jayne to verify via e2e-testing skill, then Squad coordinates commit


- Quiz/LearningResource decoupling shipped (commit 88a0272) — VocabularyReview is vocabulary-driven, not resource-scoped. Root cause was Insights (global count) vs. Quiz (resource-filtered) mismatch. Fix: remove ResourceId from VocabularyReview plan items, always load from full user vocab pool.
- **CRITICAL**: Persisted plan items from before the decoupling fix still contain ResourceId in the database — even after deploy, old plan items bypass PlanConverter logic. Fix requires guarding the LAUNCH path (Index.razor:LaunchPlanItem) not just the generation path.
- Index.razor LaunchPlanItem (lines 922-954) reads item.ResourceId DIRECTLY and constructs query strings, bypassing PlanConverter.BuildRouteParameters — this was the second leak point after DeterministicPlanBuilder was fixed
- VocabQuiz.razor LoadVocabulary must also implement defense-in-depth: when DueOnly=true (SRS mode from daily plan), ignore ResourceIds parameter even if passed
- Two-layer fix: (1) Index.razor skips ResourceId for VocabularyReview activity type, (2) VocabQuiz.razor ignores ResourceIds when DueOnly=true — this ensures robustness at both the launch point and the loading point

---

## 2026-04-17 — VocabularyReview Persisted Plan Item Leak Fix (Verified on Device)

**Status:** ✅ VERIFIED ON DEVICE

**What:** After shipping the Quiz/LearningResource decoupling fix (commit 88a0272), Captain reported the quiz was still showing "All vocabulary in this resource are mastered." The fix addressed GENERATION paths but missed old PERSISTED plan items.

**Root Cause:** DailyPlans generated BEFORE the deploy were persisted to the database with ResourceId stamped on VocabularyReview plan items. When Captain reopened the app after deploy, the Dashboard loaded these old plan items. **Index.razor's LaunchPlanItem method read item.ResourceId DIRECTLY**, bypassing the corrected PlanConverter logic entirely.

**Two-Layer Fix:**

1. **Index.razor (Line 928):** Added `item.ActivityType != PlanActivityType.VocabularyReview` guard before reading ResourceId
   - Ensures VocabularyReview never passes ResourceId to quiz, even from old persisted plan items

2. **VocabQuiz.razor (Line 634):** When `DueOnly=true` (SRS mode), ignore ResourceIds parameter
   - Defense in depth: quiz loader itself knows to ignore ResourceIds when in SRS mode

**Verification:**
- ✅ Build clean: 0 errors
- ✅ Code-review agent: PASS verdict
- ✅ Commit: c081a63 merged to main
- ✅ Azure deploy: ✅ SUCCESS (2m44s)
- ✅ Post-deploy validation: 17/17 pass
- ✅ iOS Release: Built and installed to DX24
- ✅ **Captain verified on device:** "i see vocab now."

**Key Learning:** Persisted data from before the fix can bypass corrected code paths if the LAUNCH point still reads the stale field directly. Always guard the launch/entry points, not just the generation/data creation points.

**Report:** `.squad/decisions/inbox/wash-quiz-still-broken-fix.md` (now merged to decisions.md)


---

## 2025-01-29: Vocabulary Matching - Resource Decoupling

**Task:** Fix "no vocabulary available" bug when launching Vocabulary Matching from Today Plan on iOS DX24  
**Pattern:** Applied same four-layer fix used for VocabQuiz (commits 88a0272 + c081a63)

### Investigation

- **Activity type:** `PlanActivityType.VocabularyGame` → route `/vocabulary-matching`
- **Root cause:** VocabMatching.razor line 131 exited early when `resourceIds.Length == 0`
- **Philosophy:** VocabularyGame is vocabulary-driven, NOT resource-driven (like VocabularyReview)

### Four-Layer Fix

1. **DeterministicPlanBuilder** (already correct): Line 519 sets `ResourceId = null` for VocabularyGame
2. **PlanConverter** (fixed): Lines 132-138 now add `DueOnly=true` and skip ResourceId for VocabularyGame
3. **Index.razor** (fixed): Lines 929-936 guard extended to skip ResourceId for both VocabularyReview AND VocabularyGame
4. **VocabMatching.razor** (fixed): Added DueOnly param, rewrote LoadVocabulary (lines 126-152) with defense-in-depth:
   - `DueOnly=true` → load ALL user vocabulary across all resources
   - `DueOnly=false` → use existing resource-filtered logic (preserves user-initiated launches)

### Build Result

✅ 0 errors, 363 warnings (all pre-existing)

### Key Learning

When vocabulary-testing activities share the same architectural pattern (global vocab pool for SRS, resource-filtered for manual), apply the same four-layer decoupling pattern consistently:
1. Plan builder sets ResourceId = null
2. PlanConverter adds DueOnly, skips ResourceId
3. Index.razor guards ResourceId passing
4. Page implements DueOnly defense-in-depth

This ensures plan-initiated vocabulary activities work correctly while preserving user-initiated resource-filtered behavior.

### Files Changed

- `src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs`
- `src/SentenceStudio.UI/Pages/Index.razor`
- `src/SentenceStudio.UI/Pages/VocabMatching.razor`

### Decision Doc

`.squad/decisions/inbox/wash-vocab-matching-decouple.md`

## 2026-04-17 — Plugin.Maui.HelpKit Alpha Scope Locked

Captain locked 8 decisions. Alpha scope frozen. Implications for Wash (SQLite + sqlite-vec):
- **Deferred:** sqlite-vec fully deferred to v1 (weeks of native-build work; not Alpha-worthy)
- **Alpha storage:** Microsoft.Extensions.VectorData in-memory + JSON disk persistence instead
- **Simplification:** No native binary bundling, no vec0 virtual table complexity in Alpha
- **Post-Alpha path:** v1 will revisit sqlite-vec if performance/scale demands justify the build complexity

SPIKE-1 unblocked focusing on in-memory VectorData validation and JSON serialization performance.

