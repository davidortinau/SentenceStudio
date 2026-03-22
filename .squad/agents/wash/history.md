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

## Work Sessions

### 2026-03-13 — Cross-Agent Update: Azure Deployment Issues

**Status:** In Progress  
**GitHub Issues:** #39-#65 created by Zoe (Lead)  
**Wash's Role:** Deployment orchestration support  

### 2026-03-15 — Cross-Agent Update: Mobile Auth Guard Bypass Fix (Kaylee)

**Status:** COMPLETED  
**Related Decision:** Mobile Auth Guard — Validate Tokens, Not Preferences  
**Impact on Wash:** No API changes required for this fix

**Summary:** Kaylee fixed critical mobile auth bypass in MainLayout.razor and Auth.razor. The auth gate now validates real token state (`IAuthService.IsSignedIn`) instead of checking a boolean preference flag. This enforces server authentication before any content access.

**What This Means for API Work:**
- Your JWT Bearer implementation (#43) is now critical — mobile clients will call API to validate tokens
- DevAuthHandler fallback keeps dev flow working
- No scope policy changes needed; endpoints using `RequireAuthorization()` work as-is
- Consider testing API token refresh flow with mobile clients (Jayne's E2E plan)

**Learnings Added:**
- Mobile apps cannot rely on persistent local flags for auth state — must validate against server on every session restart
- Preference flags are convenience hints, not security mechanisms
- SecureStorage persistence for MSAL tokens is essential for smooth UX (app restart with valid tokens should work seamlessly)

**Phase Execution Order:** Phase 2 (Secrets) → Phase 1 (Auth, localhost-testable) → Phase 3 (Infra) → Phase 4 (Pipeline) → Phase 5 (Hardening)

**Wash Coordination Points:**
- Phase 4 (Pipeline) — CI/deploy workflows — coordinate with Kaylee's automation
- Phase 3.5 (Container Apps) — deployment target provisioning
- Critical Path: CoreSync SQLite→PostgreSQL migration (#55, XL) — coordinate safe data migration in production

**Key Dependencies:** Zoe coordinates Phase 1-3 decisions; Kaylee implements CI/deploy automation; Captain provides Azure portal access.

### 2026-03-14 — API JWT Bearer Authentication (#43)

**Status:** Complete  
**Branch:** `feature/43-api-jwt-bearer`  
**PR:** #68  

Implemented JWT Bearer token authentication for the API:
- NuGet: Microsoft.Identity.Web (JWT validation + token acquisition)
- Conditional auth via `Auth:UseEntraId` flag (false = DevAuthHandler, true = Entra ID OIDC)
- JwtBearerScheme with token validation, issuer, and audience checks
- AuthorizeAttribute guards on API endpoints (/api/* routes)
- Integrates with Entra ID tenant and app registrations (#42)
- DevAuthHandler for local development (zero friction)
- Ready for WebApp + MAUI clients to call API with Bearer tokens

**Unblocks:** Kaylee's WebApp OIDC (#44), MAUI MSAL (#45), remaining auth work

### 2026-03-14 — CoreSync Auth: Bearer Token on Sync Client (#46)

**Status:** Complete  
**Branch:** `feature/46-coresync-auth`  
**Depends on:** #43 (API JWT), #45 (MAUI MSAL)

**What was done:**
- Merged #43 and #45 into branch as dependencies
- Added JWT Bearer auth to `SentenceStudio.Web` (CoreSync sync server)
- Created `DevAuthHandler` for dev mode (mirrors API pattern)
- `UseAuthentication()` + `UseAuthorization()` before `UseCoreSyncHttpServer()`
- Client side already handled by #45's `AuthenticatedHttpMessageHandler` on `"HttpClientToServer"`
- Graceful fallback: no token → request proceeds without auth header; server doesn't reject

**Key Insight:** CoreSync uses ASP.NET middleware (`UseCoreSyncHttpServer()`), not minimal API endpoints, so `RequireAuthorization()` can't be applied directly. Auth middleware populates identity; future enforcement needs a gating middleware or policy.

### 2026-03-14 — Phase 2 (Secrets) Completion

**Status:** COMPLETED  
**Issues:** #39 (user-secrets setup), #41 (security headers)

**Wash Completed #39:**
- Initialized user-secrets for Api, WebApp
- Created secrets.template.json with full inventory
- Updated README with three secrets management paths
- Documented AppHost → service flow via Aspire Parameters and env var normalization

**Kaylee Completed #41:**
- Added SecurityHeadersExtensions to shared lib (linked to all web projects)
- Implemented HSTS, CORS, AllowedHosts across API/WebApp/Marketing
- Environment-aware HTTPS redirect

**Phase 2 Closed:** Ready to begin Phase 1 (Entra ID) now that Captain has provisioned app registrations.

### 2026-03-16 — Issue #97 (API Error Investigation) and #95 (Password Reset URL Logging)

**Status:** COMPLETED

**Issue #97 - API Error Investigation:**
- Investigated Aspire dashboard API errors as reported by Captain
- Checked Aspire structured logs, console logs, and distributed traces for the API resource
- Found NO errors — API is running healthy with successful requests (OpenAI chat completions, auth flows)
- Logs show normal operation: token refresh cycles, email confirmations, database queries executing successfully
- Recent traces show OpenAI API calls returning 200 OK (1.5-2s response times)
- CORS and auth middleware properly configured
- Conclusion: API is operating as expected; no issues found

**Issue #95 - Password Reset URL Logging:**
- Added development-only logging for password reset URLs in both API and WebApp
- Modified `AuthEndpoints.ForgotPassword` (API) and `AccountEndpoints.ForgotPassword` (WebApp)
- Injected `IWebHostEnvironment` and `ILogger<PasswordResetLogger>` into password reset handlers
- Created nested `PasswordResetLogger` class in both static endpoint classes to provide logger category (workaround for static class limitation)
- Added `env.IsDevelopment()` guard before logging to ensure URLs never leak in production
- Reset URLs now logged at `LogInformation` level with clear "Copy and paste this URL" message
- Logs appear in both console and Aspire structured logs for easy dev access
- ConsoleEmailSender already logs email content; this adds explicit reset URL extraction for faster dev workflow

**Technical Notes:**
- Cannot use static classes as generic type parameters for `ILogger<T>`
- Workaround: Created nested private class `PasswordResetLogger` for logger category
- Development check: `env.IsDevelopment()` ensures production safety
- Format: `--- PASSWORD RESET LINK ---\nFor: {Email}\nReset URL: {ResetUrl}\n--- Copy and paste this URL into your browser ---`
- WebApp Login/Register pages use plain HTML `<form method="post">` (NOT Blazor interactive) -- JS-based interactivity required for things like password toggle
- AuthLayout is minimal (logo + @Body) -- no nav links
- AppRoutes.razor NotAuthorized uses RedirectToLogin component with `forceLoad: true` to redirect unauthenticated users to /Account/Login
- WebApp's ServerAuthService.SignInAsync NEVER checks IsEmailConfirmedAsync — web login always bypasses email confirmation
- API's AuthEndpoints.Login now auto-confirms email in development mode to match WebApp behavior; production still requires email confirmation
- IdentityAuthService (MAUI client) logs response body on login failure for better debugging


### 2026-03-16 — Vocabulary Hierarchy Schema Design

**Status:** PROPOSED  
**Decision Doc:** `.squad/decisions/inbox/wash-vocabulary-hierarchy-proposal.md`

**Problem:** Current flat vocabulary model causes duplication when AI extracts related terms (root word → phrase → idiom). Users must prove mastery redundantly for linguistically connected vocabulary.

**Analysis Completed:**
- Examined current schema: `VocabularyWord` (flat pairs), `VocabularyProgress` (per-word mastery), `ResourceVocabularyMapping` (junction), `VocabularyLearningContext` (practice attempts)
- Evaluated two architectural options:
  - **Option A (Recommended):** Self-referential FK — add `ParentVocabularyWordId` + `RelationType` enum to `VocabularyWord`
  - **Option B:** Separate `VocabularyRelationship` junction table for many-to-many relationships

**Key Technical Decisions:**
1. **Keep separate VocabularyProgress per word** — hierarchy is metadata, NOT for aggregating mastery scores. Phrases are distinct learning targets.
2. **Option A simpler for CoreSync** — single table sync, no new junction table, NULLable FK preserves existing data
3. **Migration risk: LOW** — additive changes only, no data loss, no destructive schema alterations
4. **AI extraction contract change needed** — must return parent-child relationships, not flat list (River's domain)

**VocabularyWordRelationType Enum Proposed:**
- `Inflection` — verb conjugations, noun declensions
- `Phrase` — word + particle/modifier
- `Idiom` — fixed multi-word expressions
- `Compound` — merged words
- `Synonym` / `Antonym` — semantic relationships

**CoreSync Implications:**
- Option A: Single table, existing FK handling works, validate against circular refs
- Option B: New sync table, relationship conflicts independent of word conflicts, cascade deletes

**Migration Phases:**
1. Schema extension (EF Core migration — 2 new columns)
2. Data backfill (optional — AI analysis of existing vocab)
3. CoreSync validation (conflict resolution, cascading deletes)
4. API + UI updates (hierarchy extraction, related words display)

**Team Coordination Required:**
- **Captain:** UX decision — show hierarchy to users immediately or backend-only?
- **River:** AI prompt engineering for parent-child extraction
- **Kaylee:** UI design for hierarchy display (tree view, chips, expandable sections)
- **Jayne:** E2E test scenarios for cross-device sync validation
- **Zoe:** Architecture alignment with multi-tenancy plans

**Recommendation:** Adopt Option A (self-referential) for Phase 1. Revisit Option B if multi-parent expressions become common.


---

## VOCABULARY HIERARCHY TEAM ANALYSIS — SCHEMA FINALIZED (2026-03-17)

**Session:** Vocabulary Hierarchy Analysis & Team Consensus  
**Role:** Backend Developer  
**Status:** PROPOSED — Awaiting Captain Approval

**Schema Decision: Option A (Self-Referential FK) — FINAL**

### Recommended Implementation

**Add to VocabularyWord entity:**
```csharp
// Linguistic hierarchy
public string? ParentVocabularyWordId { get; set; }
public VocabularyWordRelationType? RelationType { get; set; }

// Navigation properties
[JsonIgnore]
public VocabularyWord? ParentWord { get; set; }

[JsonIgnore]
public List<VocabularyWord> ChildWords { get; set; } = new();

public enum VocabularyWordRelationType
{
    Inflection,  // 주문 → 주문하다
    Phrase,      // 대학교 → 대학교 때
    Idiom,       // 주문하다 → 피자를 주문하는 게 어때요
    Compound,    // Two words merged
    Synonym,     // Alternative
    Antonym      // Opposite
}
```

**EF Core Migration (next step):**
```sql
ALTER TABLE VocabularyWord ADD COLUMN ParentVocabularyWordId TEXT NULL;
ALTER TABLE VocabularyWord ADD COLUMN RelationType INTEGER NULL;
CREATE INDEX IX_VocabularyWord_Parent ON VocabularyWord(ParentVocabularyWordId);
```

### Why Option A?
- **CoreSync compatibility:** Single table sync, no junction table, NULLable FK preserves existing data
- **Query simplicity:** Direct FK traversal, fast parent/child queries
- **Migration safety:** Additive changes only, zero data loss
- **Future-proof:** Can evolve to Option B (junction table) if multi-parent needed

### Team Validation
- ✅ Zoe (Architecture): Aligns with design pillars, conservative MVP approach
- ✅ River (AI): Prompt design ready to populate relatedTerms array
- ✅ SLA Expert: Independent mastery tracking preserves SRS spacing effect
- ✅ Learning Design: Supports hierarchy visualization without cognitive overload

### Migration Risk: LOW
- No destructive changes
- Existing vocabulary unaffected (NULLable columns)
- FK reference to same table (standard pattern)
- Cascade deletes optional (recommend ON DELETE SET NULL for safety)

### Immediate Next Steps
1. Captain approval
2. Generate EF Core migration file
3. Write integration tests (FK integrity, cascade rules)
4. CoreSync validation (bi-directional sync with new FK)
5. Repository methods: `GetChildWordsAsync()`, `GetRootWordAsync()`, `GetWordHierarchyAsync()`

### Future Expansion (Phase 2)
- If multi-parent relationships become common (rare), migrate to Option B (junction table)
- Option A→B migration path documented
- Low risk to defer this decision

### 2026-03-19 — PostgreSQL Migration Research (#55)

**Status:** PROPOSED  
**Decision Doc:** `.squad/decisions/inbox/wash-postgres-migration-map.md`

**Key Findings:**
- `CoreSync.PostgreSQL` v0.1.126 exists and is a drop-in API replacement for `CoreSync.Sqlite`
- `PostgreSqlSyncConfigurationBuilder` mirrors `SqliteSyncConfigurationBuilder` exactly (same `.Table<T>()` API, same `ProviderMode` enum)
- Mixed-provider sync (SQLite mobile ↔ PostgreSQL server) is explicitly supported by CoreSync
- Aspire AppHost already declares `AddPostgres("db")` — just needs `WithReference(postgres)` on API and WebApp
- Aspire's `AddNpgsqlDbContext<ApplicationDbContext>("sentencestudio")` replaces our manual `AddDataServices(databasePath)` for server projects
- EF Core migrations must be regenerated for PostgreSQL (existing SQLite migrations won't run on PG)
- Mobile already excludes `Migrations/**` via `.csproj` condition — safe to regenerate
- Current database: 5.4MB, ~12K rows across 11 synced tables + Identity tables — small enough for pgloader migration
- Recommended: fresh CoreSync provision on PG + full re-sync from mobile clients (safest approach)
- `sqlite-net-pcl` and `SQLiteNetExtensions` packages in Shared.csproj need investigation — may be unused

**Learnings:**
- CoreSync.PostgreSQL NuGet: v0.1.126, author adospace, 1,389 downloads, netstandard2.0
- CoreSync tracking tables: `__CORE_SYNC_CT`, `__CORE_SYNC_LOCAL_ID`, `__CORE_SYNC_REMOTE_ANCHOR`
- Aspire `WithReference(postgres)` injects `ConnectionStrings__sentencestudio` env var automatically
- Aspire `AddNpgsqlDbContext<T>("name")` reads from `ConnectionStrings` section by convention
- `WithLifetime(ContainerLifetime.Persistent)` on Aspire PostgreSQL keeps data between restarts
- Effort: 2-3 days estimated (packages + code + migrations + data migration + testing)

### 2026-03-20 — PostgreSQL Migration Execution: Phases 1-3 (#55)

**Status:** COMPLETED (Phases 1-3)  
**Branch:** `feature/55-postgres-migration`

**Changes Made:**
- Replaced `CoreSync.Sqlite` with `CoreSync.PostgreSQL` v0.1.126 in API csproj
- Added `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` v13.3.0-preview.1.26163.4 to API and WebApp
- Added `CoreSync.PostgreSQL` and `Npgsql.EntityFrameworkCore.PostgreSQL` to Shared (server-only, MSBuild condition)
- Added PostgreSQL overload to `SharedSyncRegistration.cs` with `#if !IOS && !ANDROID && !MACCATALYST` guard
- Changed `ApplicationDbContext.OnConfiguring` fallback: `UseSqlite` (mobile) / `UseNpgsql` (server) via `#if` directives
- Guarded `EnableWalMode()` with `#if IOS || ANDROID || MACCATALYST` (SQLite-only)
- Changed `DesignTimeDbContextFactory` to `UseNpgsql` with design-time connection string
- Added `.WithLifetime(ContainerLifetime.Persistent)` and `.WithReference(postgres)` to AppHost for API and WebApp
- Replaced `AddDataServices(databasePath)` with `builder.AddNpgsqlDbContext<ApplicationDbContext>("sentencestudio")` in both API and WebApp
- Replaced `SqliteSyncProvider`/`SqliteSyncConfigurationBuilder` with `PostgreSQLSyncProvider`/`PostgreSQLSyncConfigurationBuilder` in API
- Deleted all old SQLite migrations, generated fresh `InitialPostgreSQL` migration

**Key Learnings:**
- CoreSync.PostgreSQL types use uppercase "SQL": `PostgreSQLSyncProvider`, `PostgreSQLSyncConfigurationBuilder` (not `PostgreSql`)
- EF Core tools `dotnet ef` fail with `ResolvePackageAssets` target error on multi-TFM projects in .NET 10 SDK 10.0.101
- Workaround: temporarily change `<TargetFrameworks>` (plural) to `<TargetFramework>` (singular) for migration generation
- The `UseNpgsql` extension method lives in the `Microsoft.EntityFrameworkCore` namespace — no extra using needed
- Aspire preview packages (13.3.0-preview.1.26163.4) are resolvable from nuget.org for client integrations too
- Mobile MAUI code paths untouched — CoreSync mixed-provider sync (SQLite client ↔ PostgreSQL server) is the target architecture

### 2026-03-21 — YouTube API & Library Research

**Status:** RESEARCH COMPLETE  
**Decision Doc:** `.squad/decisions/inbox/wash-youtube-api-research.md`

**Key Findings:**
- YouTube official Captions API (`captions.download`) requires video ownership — CANNOT use it for third-party video transcripts (returns 403 Forbidden)
- Two-library strategy: `Google.Apis.YouTube.v3` v1.73.0.4053 for OAuth subscription management + `YoutubeExplode` v6.5.7 for transcript extraction
- YoutubeExplode reverse-engineers internal endpoints; can download auto-generated ASR captions from any public video without auth
- `subscriptions.list(mine=true)` costs only 1 quota unit; default quota is 10K units/day — sufficient for small-scale use
- PubSubHubbub push notifications eliminate polling entirely — YouTube pushes Atom XML to our webhook when channels upload new videos
- Google OAuth for YouTube requires only `youtube.readonly` scope — separate from our JWT auth flow
- Google refresh tokens are long-lived and must be encrypted at rest in server DB
- PubSubHubbub subscriptions expire (5-10 days) — need auto-renewal background service
- YoutubeExplode is LGPL-3.0 licensed — fine as unmodified NuGet dependency
- YoutubeExplode explicitly supports net10.0 TFM
- NuGet search for "youtube transcript" returns 0 packages — YoutubeExplode is the only .NET option
- Existing `SentenceStudio.Workers` project has placeholder BackgroundService — ideal home for webhook renewal + transcript processing
- Proposed 3 new entities: YouTubeConnection (OAuth tokens), YouTubeSubscription (channel monitoring), YouTubeVideoImport (processing state)
- YouTubeConnection should NOT sync to mobile (contains refresh tokens) — server-only entity
- LearningResource already has MediaType, MediaUrl, Transcript fields — YouTube imports fit naturally as MediaType="YouTube Video"

**Learnings Added:**
- YouTube Captions API requires video ownership for download — use YoutubeExplode for third-party transcripts
- Google.Apis.YouTube.v3 latest: v1.73.0.4053, depends on Google.Apis.Auth ≥ 1.73.0
- YoutubeExplode v6.5.7 supports net10.0, closed captions including auto-generated ASR tracks
- YouTube Data API default quota: 10K units/day; subscriptions.list = 1 unit, captions.list = 50, captions.download = 200, search = 100
- PubSubHubbub hub URL: https://pubsubhubbub.appspot.com/subscribe; topic format: https://www.youtube.com/feeds/videos.xml?channel_id=CHANNEL_ID
- Google.Apis.Auth.AspNetCore3 v1.73.0 provides IGoogleAuthProvider for incremental OAuth consent in ASP.NET Core
- Google OAuth scopes for YouTube read-only: `https://www.googleapis.com/auth/youtube.readonly`

### 2026-07-24 — Channel Monitoring Feasibility (No OAuth)

**Status:** RESEARCH COMPLETE — READY TO BUILD  
**Decision Doc:** `.squad/decisions/inbox/wash-channel-monitoring.md`

**Captain's Simplified Approach:** Drop Google OAuth entirely. User pastes a YouTube channel URL, app monitors it for new videos, auto-ingests transcripts via AI pipeline.

**Key Findings:**
- YoutubeExplode v6.5.6 (already in project) handles everything: `Channels.GetByHandleAsync()` resolves @handles, `Channels.GetUploadsAsync()` lists uploads newest-first
- `PlaylistVideo` (from GetUploadsAsync) lacks `UploadDate` — need `Videos.GetAsync()` per video for date filtering (negligible cost for small channels)
- Korean auto-generated captions confirmed accessible via `ClosedCaptions.GetManifestAsync()` — existing `YouTubeImportService` already does this
- Proposed 2 new entities: `MonitoredChannel` (tracking) + `VideoImport` (processing state) — both server-only, non-synced
- Workers project (`SentenceStudio.Workers`) is the home: `ChannelPollingWorker` (6h interval) + `TranscriptIngestionWorker` (processes pending imports)
- Dedup strategy: check `VideoImport.VideoId` before processing; `MonitoredChannel.LastSeenVideoId` for fast short-circuit
- Zero new NuGet packages needed — just add YoutubeExplode reference to Workers project
- Estimated effort: 2-3 days for core pipeline

**Learnings Added:**
- `youtube.Channels.GetByHandleAsync()` resolves @handle URLs to channel ID — no manual parsing needed
- `GetUploadsAsync()` returns `PlaylistVideo` (lightweight) — `UploadDate` requires full `Videos.GetAsync()` call
- Uploads are returned in reverse chronological order — iterate until hitting LastSeenVideoId or date cutoff
- YoutubeExplode has no API quota — but rapid calls can trigger YouTube throttling; add 500ms delay between fetches
- MonitoredChannel + VideoImport should be non-synced (int PK) server-only entities
- Two-worker separation: polling is fast (metadata only), ingestion is slow (transcript + AI) — don't let slow work block checks

### 2026-03-20 — YouTube Channel Monitoring Feature Implementation

**Status:** COMPLETED  
**Components:** API, Workers, Database Migration

**What was done:**

1. **DI Registration (Task 1):**
   - Added `ChannelMonitorService` and `VideoImportPipelineService` to `CoreServiceExtensions.cs` as Singleton services
   - Services use IServiceProvider internally for scoped DB access — registered as Singleton per existing pattern

2. **EF Core Migration (Task 2):**
   - Created migration `AddYouTubeChannelMonitoring` for `MonitoredChannel` and `VideoImport` entities
   - Used local dotnet-ef tool (v10.0.5) from Shared project with `--framework net10.0`
   - Migration includes proper indexes: `IX_MonitoredChannel_IsActive_LastCheckedAt`, `IX_VideoImport_VideoId`, foreign keys with SetNull on delete
   - Tables follow singular naming convention (MonitoredChannel, VideoImport)

3. **API Endpoints (Task 3):**
   - Created `/api/channels` endpoints: GET (list), POST (add with metadata resolution), PUT (update), DELETE (remove), POST /{id}/check (manual trigger)
   - Created `/api/imports` endpoints: GET (history), GET /{id} (status), POST (start import), POST /{id}/retry (retry failed)
   - All endpoints enforce user ownership via `user_profile_id` claim filtering
   - Pipeline runs in background (non-blocking) — returns import ID immediately for polling
   - Added platform services: `ApiConnectivityService`, `ApiFileSystemService` in `Platform/` folder
   - Registered all dependencies: language segmenters, AiService, YouTubeImportService, TranscriptFormattingService

4. **Worker Implementation (Task 4):**
   - Implemented `Worker.cs` as BackgroundService with 30-minute polling loop
   - `CheckChannelsAsync()`: Gets channels due for check, fetches recent videos, creates VideoImport records, runs pipeline in background
   - `CleanupStaleImportsAsync()`: Marks imports stuck in processing > 30 min as Failed
   - Rate limiting: 500ms delay between video metadata fetches
   - Created platform services: `WorkerConnectivityService`, `WorkerFileSystemService` in `Platform/` folder
   - Registered all dependencies in `Program.cs`: DbContext, language segmenters, YouTube services, AiService, OpenAI client

**Technical Decisions:**
- Services use IServiceProvider for scoped ApplicationDbContext access (Singleton services with scoped DB operations)
- Pipeline runs in Task.Run background fire-and-forget for non-blocking behavior
- API endpoints return immediately with import ID; client polls GET /api/imports/{id} for progress
- Worker uses separate scopes per channel check to avoid context lifetime issues
- OpenAI client registered conditionally when API key present
- Platform abstractions (IConnectivityService, IFileSystemService) implemented for API and Workers contexts

**Build Results:**
- ✅ API: Build succeeded (15 warnings, 0 errors)
- ✅ Workers: Build succeeded (14 warnings, 0 errors)
- ⚠️ WebApp: Build failed (UI errors in existing pages using YouTube services — not backend responsibility)

**Migration Path:**
- Migration created but not applied
- API Program.cs already applies migrations at startup via `db.Database.MigrateAsync()`
- Workers doesn't need to apply migrations (read-only for cleanup)

**Dependencies Added:**
- Workers project: Aspire.Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.EntityFrameworkCore, Microsoft.Extensions.AI.OpenAI, Npgsql.EntityFrameworkCore.PostgreSQL
- Workers project: SentenceStudio.Shared project reference

**Rate Limiting:**
- 500ms delay between video metadata fetches (YouTube API throttling protection)
- 200ms delay during channel video listing (already in ChannelMonitorService)

**Learnings:**
- Multi-targeted projects (net10.0 + iOS/Android) require `--framework net10.0` flag for EF migrations
- Local dotnet-ef tool (`dotnet tool install --local dotnet-ef`) works better than global tool for multi-targeted projects
- IServiceProvider pattern for Singleton services needing scoped DB contexts
- Fire-and-forget Task.Run for background pipelines in API endpoints (non-blocking response)
- Platform abstractions (IConnectivityService, IFileSystemService) need stub implementations for server contexts (API, Workers)
- Workers need same Aspire PostgreSQL package version as API for compatibility (13.3.0-preview.1.26156.1)

### 2026-03-22 — Release Notes Infrastructure

- Created `docs/release-notes/v1.0.md` and `v1.1.md` with YAML frontmatter (version, date, title)
- Built `ReleaseNotesService` in `SentenceStudio.Shared/Services/` with multi-platform support
  - Server/Desktop: reads from `docs/release-notes/` filesystem path
  - Mobile: reads from MauiAsset via `FileSystem.OpenAppPackageFileAsync()` lambda
  - Parses frontmatter using Regex to extract metadata
  - Returns sorted list (latest first) with caching
- Added `/api/version` endpoints (public, no auth):
  - `GET /api/version/info` — current version + release notes
  - `GET /api/version/latest` — latest available version
  - `GET /api/version/notes/{version}` — specific version notes
  - `GET /api/version/notes` — all release notes
- Registered `ReleaseNotesService` in DI:
  - API: `new ReleaseNotesService("docs/release-notes")` (filesystem)
  - WebApp: `new ReleaseNotesService("docs/release-notes")` (filesystem)
  - AppLib: `new ReleaseNotesService(fileName => FileSystem.OpenAppPackageFileAsync(fileName))` (MauiAsset)
- Added release notes `.md` files as `<MauiAsset>` in AppLib.csproj with `LogicalName="%(Filename)%(Extension)"`
- Fixed namespace reference in `Settings.razor` and `MainLayout.razor` from `SentenceStudio.Shared.Services` → `SentenceStudio.Services`
- Version info read from `Assembly.GetExecutingAssembly().GetName().Version` (server) or `AppInfo.Current.Version` (MAUI)

**Key Decision:** Multi-platform service with constructor-based dependency injection for asset loading. Server and MAUI use different constructors to provide filesystem or MauiAsset access without conditional compilation headaches in the service itself.

