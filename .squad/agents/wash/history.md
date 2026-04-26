# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-04-25: **v1.1 Content Import Backend** — Implemented three new import branches (Phrase, Transcript, Auto-detect) in ContentImportService plus checkbox harvest model. Migration `SetDefaultLexicalUnitType` backfills Unknown→Word/Phrase via space heuristic (dual-provider: Postgres POSITION, SQLite INSTR). Auto-detect uses three-tier confidence gate (>=0.85 auto, 0.70-0.84 suggest, <0.70 manual) with classification running BEFORE any DB persistence. Transcript branch reuses `ExtractVocabularyFromTranscript.scriban-txt` with word-biased extraction. Phrase branch reuses `FreeTextToVocab.scriban-txt` (awaiting River's dedicated prompt). Zero-vocab: persist resource + warning. Chunking: reject >30KB, v1.2 follow-up. DTOs updated with harvest booleans and LexicalUnitType per row. UI adapted (DetectContentType→ClassifyContentAsync). Build green: Shared, MacCatalyst, API. Doc: `.squad/decisions/inbox/wash-v11-backend.md`
- 2026-04-23: **Word/Phrase Feature Completed** — Delivered 9 todos: model-enum (LexicalUnitType), model-constituent (PhraseConstituent), migration-schema (dual-provider), backfill-classification (heuristic), backfill-constituents (lemma tokenization), progress-cascade (passive exposure), shadowing-consumer (LexicalUnitType branching), smart-resource-phrases (new type), smart-resource-phrases-fix (scope bug). Total: 147 tests passing, feature complete, e2e blocked on SQLite migration history mismatch (Captain decision needed). Documented in `.squad/log/2026-04-23T2219Z-wordphrase-squad-wrap.md`.
- 2026-05-20: **Smart Resource: Phrases** — Added `Phrases` smart resource type for practicing all phrase/sentence vocabulary. Uses `LexicalUnitType.Phrase | Sentence` filter with user scoping via `VocabularyProgress.UserId` join (VocabularyWord has no UserProfileId). Intent-driven like Struggling (excluded from planner via `.Where(r => !r.IsSmartResource)` in DeterministicPlanBuilder). Initialization creates 4th smart resource (DailyReview, NewWords, Struggling, Phrases). ResourceVocabularyMapping population via same refresh/bulk-associate pattern. Empty on new users (populates after backfill classification). Build green (Shared, MacCatalyst, Api). Doc: `.squad/decisions/inbox/wash-smart-resource-phrases.md`
- 2025-01-24: **Shadowing LexicalUnitType Consumer** — Modified `ShadowingService.GenerateSentencesAsync()` to branch on `VocabularyWord.LexicalUnitType`: only `Word` entries trigger AI carrier-sentence generation via Scriban template; `Phrase | Sentence | Unknown` use `TargetLanguageTerm` as-is (no AI round-trip). Unknown entries emit structured log `ShadowingUnknownTerm` (Information level, WordId+Term fields) for downstream UI reclassification. As-is sentences populate same `ShadowingSentence` DTO shape (TargetLanguageText=term, NativeLanguageText=translation, PronunciationNotes=null). No public API changes, no Scriban template changes. All target projects (Shared, MacCatalyst, Api) build green. No external call sites — all routing internal to ShadowingService. Doc: `.squad/decisions/inbox/wash-shadowing-consumer.md`
- 2025-01-21: **Phrase Constituent Backfill Service** — Extended `VocabularyClassificationBackfillService` with `BackfillPhraseConstituentsAsync()` to populate `PhraseConstituent` join rows for existing phrases/sentences. Key discovery: VocabularyWord is NOT user-scoped directly — must query through `VocabularyProgress.UserId` with `.Include(vp => vp.VocabularyWord)` to get user-specific vocabulary. Tokenization with Korean particle stripping (`이, 가, 을, 를, 은, 는, 에, 의, 로, 으로, 와, 과, 에서, 에게, 도, 만, 부터, 까지`). Lemma dictionary pre-built once per user (no N+1). Idempotent via existing-constituent guard. Substring fallback for unmatched tokens 2+ chars. Wired into startup after classification backfill in API/WebApp/MAUI (SyncService). Public static `TokenizePhrase(string, string)` for unit testing. Doc: `.squad/decisions/inbox/wash-backfill-constituents.md`
- 2026-04-17: **Help Flyout Integration Pattern (MAUI Hybrid)** — HelpKit library (Plugin.Maui.HelpKit 0.1.0-alpha) now wired into SentenceStudio UI as Help menu item in NavMenu.razor. Used dynamic reflection pattern (Type.GetType() + method invocation) to keep UI project browser-only. MAUI apps see Help button (invokes HelpKit overlay), WebApp doesn't (graceful degrade). Reflects HelpKit portability: library complete, UI trigger now operational.

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

- 2026-04-17: HelpKit Alpha — storage/ingestion/cache/rate-limit complete; SS integration blocked on TFM.

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


## 2026-04-17 — HelpKit Alpha Wave 2: Storage + Ingestion + Rate Limit + Scanner

Landed the backend layer end-to-end and wired HelpKitService.

**Schema (sqlite-net-pcl, singular table names):**
- `conversation` (Id PK GUID, UserId?, Title, CreatedAt, UpdatedAt)
- `message` (Id PK GUID, ConversationId FK, Role, Content, CitationsJson, CreatedAt)
- `schema_version` (single row)
- `ingestion_fingerprint` (single row; full re-ingest when mismatched)
- `answer_cache` (Key = SHA-256(lower(trim(question)) + "|" + fingerprint), ExpiresAt)
- Migrations are forward-only: unknown higher version logs a warning and continues. **Never destroys user data.**
- Citations stored as JSON blobs so field-order changes in the public record don't break the schema.

**Fingerprint semantics (`PipelineFingerprint.Compute`):**
- Inputs: embedder model id + chunker version + chunk size (512) + overlap (128) + heading format.
- Any change = full re-ingest + `AnswerCache.InvalidateAllAsync()`. Conversations/messages untouched.

**Retention:**
- 30-day default purge on every `HelpKitService` construction (cheap no-op).
- `ClearHistoryAsync` is `CurrentUserProvider`-scoped (never cross-user).

**Rate limit:**
- Per-user sliding 60s window, `ConcurrentDictionary<userKey, Queue<DateTime>>`.
- Anonymous bucket keyed `"_anon"`. Disabled when `MaxQuestionsPerMinute <= 0`.
- Rejection increments `helpkit.rate_limit.rejected` and yields a polite Alpha refusal line.

**Vector store rationale:**
- Hand-rolled in-memory store (`VectorStore` + `HelpKitChunkRecord`) over a concrete `M.E.VectorData` in-memory connector. Keeps dependency set at abstractions + sqlite-net-pcl + JSON. Matches Captain's Wave-2 minimal-deps mandate.
- Persists to `{storagePath}/helpkit/vectors.json` (gzip, atomic .tmp+replace). Hydrates lazily.
- Uses `RetrievalService.CosineSimilarity` for ranking; threshold via `SimilarityThresholds.DefaultFor(modelId)` unless `SimilarityThresholdOverride` is set.

**Service wiring (`HelpKitService`):**
- Ingest → `IngestionCoordinator.IngestAsync`.
- StreamAsk → rate-limit → conversation touch/create → persist user turn → answer-cache lookup → embed query → `VectorStore.SearchAsync` → threshold check (refusal line if all below) → `SystemPrompt.Build` → `IChatClient.GetStreamingResponseAsync` → accumulate + yield progressively → `CitationValidator.Validate` + `PromptInjectionFilter.TryDetectLeak` on final → persist assistant turn → `AnswerCache.PutAsync` → yield final with validated citations.
- Namespace collisions handled by keeping `Rag.HelpKitMessage`/`Rag.HelpKitCitation` fully qualified; public `HelpKitMessage`/`HelpKitCitation` live in root namespace unqualified.

**Scanner:**
- `Plugin.Maui.HelpKit.Scanner.StubScanner.RunAsync(projectRoot, outputDir, ct)` walks `*.xaml`, emits `{outputDir}/helpkit-scan/pages/{slug}.md` with frontmatter. Never overwrites existing files; idempotent re-runs. Skips bin/obj. AI enrichment marked `TODO(Beta)`.

**Unshipped:**
- `Plugin.Maui.HelpKit.Scanner.targets` is a stub (Message-only target). Real `UsingTask` + runtime invocation tagged `TODO(Beta)`.
- `Rag.IngestionOrchestrator` and `Rag.RetrievalService.RetrieveAsync` still throw NotImplemented — Wash's new `IngestionCoordinator` and inline retrieval in `HelpKitService` supersede them for Alpha.

Build NOT attempted — net11 preview SDK + MAUI workload aren't installed locally (Zoe flagged the same). Code intentionally compile-verified against public surface only; CI on a properly provisioned box is the next gate.

---

## Learnings — 2026-04-17 (SentenceStudio dogfood integration)

**TFM tension is the dominant blocker, not DI.** SentenceStudio's entire project graph is `net10.0-*`; HelpKit ships `net11.0-*` only. A net10 head cannot ProjectReference a net11-only library — restore fails before compile. The integration approach must be additive and reversible until one side moves. I gated everything behind `$(TargetFramework.StartsWith('net11.0'))` (csproj `<ItemGroup>` condition) and `#if NET11_0_OR_GREATER` (C# preprocessor). Net10 dev build verified green after the changes (0 errors). When SentenceStudio bumps OR Zoe multi-targets HelpKit to also include `net10.0-*`, the integration activates with no further code work.

**Single-source linked file beats per-head duplication.** Put the integration helper at `src/Shared/HelpKitIntegration.cs` and `<Compile Include="..\Shared\HelpKitIntegration.cs" Link="Setup\HelpKitIntegration.cs" />` from each MAUI head csproj that wants it. One copy, multiple TFMs, clear file location in IDE solution explorer thanks to `Link` metadata.

**Keyed DI alias is opportunistic, not required.** Zoe's `HelpKitAiResolver` already falls back from keyed("helpkit") to unkeyed `IChatClient`. SentenceStudio has exactly one unkeyed `IChatClient` registration — the resolver picks it up automatically. I added the keyed alias too as belt-and-suspenders for a multi-provider future, but it's not load-bearing today.

**Embedding generator must be host-app concern.** SentenceStudio had no `IEmbeddingGenerator` registered. I added one using the same `OpenAIClient` and same API key resolution path Captain already uses (env var on desktop, `Settings.OpenAIKey` elsewhere) so there's no second credential to manage. Defaulted model to `text-embedding-3-small` with a `TODO(Captain)` to confirm — cheap, ubiquitous, 1536 dims.

**`CurrentUserProvider` reads preferences directly, not via service.** `UserProfileRepository.ActiveProfileIdKey == "active_profile_id"` is a static const, and `Preferences.Get` is sync — perfect match for HelpKit's sync `Func<IServiceProvider, string?>` delegate. Avoids dragging EF context into a getter that fires on every history query.

**Help corpus authored against actual Razor pages.** Walked `src/SentenceStudio.UI/Pages/*.razor` to confirm activity names (Cloze, Writing, Translation, Vocabulary, Word Association, Conversation) and to keep article content accurate. Eleven articles, ~1KB each. Lives at `src/SentenceStudio.AppLib/Resources/Raw/sentencestudio-help/` and bundles automatically via existing `<MauiAsset Include="Resources\Raw\**" />` glob — no csproj edit needed for content.

**No Shell flyout for SentenceStudio.** App is Blazor Hybrid hosted in a single `BlazorApp` content page. No MAUI Shell exists. Did NOT call `AddHelpKitShellFlyout`. UI trigger will be a Razor button (Kaylee's territory) once TFM unblocks.

**Two-stage init: stage content from app package to AppData, then ingest.** `InitializeHelpContentAsync` copies bundled markdown to `FileSystem.AppDataDirectory/sentencestudio-help/` only if files are missing (preserves any per-install edits). Then `TriggerBackgroundIngest` fires-and-forgets `IHelpKit.IngestAsync()` — errors logged, never thrown, so help can never block app boot.

**Build verification:** `dotnet build src/SentenceStudio.MacCatalyst -f net10.0-maccatalyst` → 0 errors, 563 pre-existing warnings. Net11 build not attempted (same SDK/workload absence Zoe flagged). Decision memo at `.squad/decisions/inbox/wash-helpkit-ss-integration.md` lists the three unblock options and recommends Zoe multi-target HelpKit to net10 for the incubation window.

---

## 2026-04-18 — Display Language DB Sanity Check (Kaylee Unblock)

**Requested by:** David (Captain, AFK) for Kaylee to restore Display Language feature  
**Task:** Confirm UserProfile.DisplayLanguage is a mapped, migrated column and SaveDisplayCultureAsync persists end-to-end

### Findings

✅ **Column exists + mapped:**
- `UserProfile.cs:25`: `public string? DisplayLanguage { get; set; }` — no `[NotMapped]` attribute
- `ApplicationDbContext.cs:79`: Entity mapped to `UserProfile` table, DisplayLanguage included by default

✅ **Already migrated (both schemas):**
- PostgreSQL: `20260320161534_InitialPostgreSQL.cs:286` — `DisplayLanguage` column added as nullable text
- SQLite: `20260321133148_InitialSqlite.cs:285` — `DisplayLanguage` column added as nullable TEXT
- `ApplicationDbContextModelSnapshot.cs`: Latest compiled model includes DisplayLanguage — **no pending delta**

✅ **Persistence logic verified:**
- `UserProfileRepository.SaveDisplayCultureAsync()` (line 307–329):
  1. Maps culture code to "English" or "Korean"
  2. Creates profile or retrieves existing one
  3. Assigns `profile.DisplayLanguage = displayLanguage`
  4. Calls `SaveAsync(profile)` → `DbSet.Update()` → `SaveChangesAsync()` → database commit
  5. Updates `LocalizationManager` for immediate effect

### Verdict

✅ **PASS** — DisplayLanguage is fully mapped, already in both DB schemas since day 1 (Initial migrations 2026-03-20/03-21). Kaylee's save flow works end-to-end with zero friction:
- Call `SaveDisplayCultureAsync("en")` → persists as "English" → app locale changes
- Zero DB migration needed, zero schema risk, zero data loss
- Ready to ship

### Decision Memo

`.squad/decisions/inbox/wash-displaylanguage-db-verify.md`


## Learnings — 2026-04-18: RESX manifest name mismatch (Phase 1 locale P0)

- **Symptom:** `MissingManifestResourceException` at first authenticated render; 500 on `/` after onboarding.
- **Root cause:** `AppResources.Designer.cs` asks for `SentenceStudio.Resources.Strings.AppResources` (namespace-based, no assembly prefix), but MSBuild's default embedding prepended the assembly name → `SentenceStudio.Shared.Resources.Strings.AppResources.resources`. Latent bug; Phase 1 made NavMenu fire 14 lookups/render so it became catastrophic.
- **Fix (Option A, surgical):** add `<LogicalName>` to EmbeddedResource entries in `src/SentenceStudio.Shared/SentenceStudio.Shared.csproj`:
  - `AppResources.resx` → `SentenceStudio.Resources.Strings.AppResources.resources`
  - `AppResources.ko-KR.resx` → `SentenceStudio.Resources.Strings.AppResources.ko-KR.resources` (satellite stream; culture lives in the satellite DLL path, but stream naming follows `{BaseName}.{CultureName}.resources`).
- **Rule of thumb:** whenever a resx's logical namespace differs from `{AssemblyName}.{FolderPath}`, pin `<LogicalName>` explicitly. Don't regenerate Designer.cs to match the default — blast radius across MAUI/UI `using` statements is far larger than one csproj tweak.
- **Verification pattern:** build → `Assembly.LoadFrom(...).GetManifestResourceNames()` + an actual `ResourceManager.GetString(key, culture)` call for both invariant and satellite culture. `strings` on the DLL confirms embedding; reflection confirms ResourceManager actually resolves it.
- **Don't touch Designer.cs:** auto-generated, will be clobbered on next resx save. Fix belongs on the embed side.

## Learnings — 2026-04-18 (round 2): resx culture identifier alignment

- **Symptom:** After round-1 LogicalName fix, `MissingManifestResourceException` gone — but switching to Korean produced zero Korean. Cookie/whitelist/DB all used `ko`; resx filename used `ko-KR`.
- **Root cause:** `ResourceManager` fallback walks **specific → parent → invariant**. A `CurrentUICulture=ko` request looks for `.ko.resources`, falls back to invariant (English). It never tries `ko-KR` because `ko-KR` is more specific than `ko`, not a parent of it. And the WebApp `SupportedCultures` whitelist rejected `ko-KR` so you can't just set the cookie that way either.
- **Fix (Option A):** `git mv AppResources.ko-KR.resx AppResources.ko.resx`; update csproj `<LogicalName>` to `...AppResources.ko.resources`. Zero other code changes — all call sites already use `ko`.
- **Why this is also safe for MAUI:** MAUI code paths that happen to use `CultureInfo("ko-KR")` still resolve correctly via the *other* direction of parent-chain fallback: `ko-KR` → parent `ko` → satellite. Neutral-culture resx is the more flexible choice; use specific `ko-KR` only if you actually need regional variants (you don't, there's no `ko-KP`).
- **Clean build when changing satellite naming:** delete stale `obj/**/<old-culture>/` and `bin/**/<old-culture>/` directories before rebuilding, otherwise MSBuild incremental build leaves the wrong satellite sitting next to the new one.
- **Verification pattern (reusable):** reflection smoke test that enumerates manifest resources AND calls `ResourceManager.GetString(key, CultureInfo)` across invariant / `en` / `ko` / `ko-KR` is the fastest way to catch both manifest-name bugs AND culture-matching bugs in a single run. Took ~5s, caught everything.
- **Rule of thumb:** keep culture identifiers aligned across **all** five touchpoints: DB value, cookie value, `SupportedCultures` whitelist, resx filename, and `<LogicalName>`. Any mismatch and fallback silently returns invariant. No exception, just English.

- 2026-04-18: **Resx Manifest & Culture Identifier Alignment** — <LogicalName> csproj override forces correct embed stream name (Designer hardcodes SentenceStudio.Resources.Strings.AppResources but MSBuild defaults to assembly-qualified path). Culture filename MUST match all five touchpoints: DB (ko), cookie (ko), whitelist (ko), endpoint validator (ko), resx file (ko). Rename ko-KR → ko: ResourceManager fallback walks specific → parent → invariant; ko is neutral (no regional variant needed), satellite resolution via parent fallback handles ko-KR requests. Two hotfixes applied as lockout-honors when Kaylee's code was rejected for revision: Round 1 manifest fix, Round 2 culture rename.


---

## 2026-04-19 — Observability Audit (Captain: "Can I see errors in Aspire on Azure?")

**Short answer:** No Aspire dashboard on Azure. OTLP exporter in ServiceDefaults is gated on `OTEL_EXPORTER_OTLP_ENDPOINT`, which is unset in production ACA. No App Insights wired. No `UseExceptionHandler`. No `/health` endpoint mapped.

**What production observability actually is today:**
- stdout/stderr from each container → Container Apps system logs → Log Analytics workspace `law-3ovvqiybthkb6` in `rg-sstudio-prod` (table: `ContainerAppConsoleLogs_CL`).
- Default ASP.NET Core console logger picks up `ILogger<T>` writes. `FeedbackEndpoints` does log warnings on AI failures and errors on GitHub API failures via `loggerFactory.CreateLogger("FeedbackEndpoints")`.
- `/api/v1/ai/chat` returns `Results.Problem(...)` but does NOT log the underlying exception — failures there are invisible unless the ASP.NET Core pipeline logs the unhandled exception.

**Quiz sentence scoring path:** clients POST to `/api/v1/ai/chat` or `/api/v1/ai/chat-messages` with a scoring prompt (River's prompts). No dedicated "score" endpoint. Any 5xx from these lands in container console logs as default Kestrel exception log.

**Feedback path:** `/api/v1/feedback/preview` + `/submit`. Logs "FeedbackEndpoints" category. AI enrichment failures log warning + fall back; GitHub failures log error.

**What's missing (and recommended):**
1. Application Insights wired to API + WebApp containers (cheapest observability gain — request traces, dependencies, exceptions, end-to-end correlation).
2. `app.UseExceptionHandler()` + `ProblemDetails` so unhandled exceptions are logged with context instead of silently swallowed.
3. `/api/v1/health` endpoint (live + ready) so ACA probe failures are explicit.
4. Wrap `/api/v1/ai/chat` handlers in try/catch → `logger.LogError(ex, ...)` so OpenAI failures appear with stack traces, not just 503s.

**Azure resources from `.azure/sstudio-prod/.env`:**
- Subscription: `a25bc5f2-e641-47b9-89a8-5e5fd428d9d6`
- RG: `rg-sstudio-prod`
- ACA env: `cae-3ovvqiybthkb6` (domain `livelyforest-b32e7d63.centralus.azurecontainerapps.io`)
- LAW: `law-3ovvqiybthkb6`
- Container app names follow Aspire resource names: `api`, `webapp`, `marketing`, `workers`.

**Immediate command for Captain** — tail the API container now:
`az containerapp logs tail -g rg-sstudio-prod -n api --follow --tail 200`

And for retrospective KQL over this morning:
```kusto
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(12h)
| where ContainerAppName_s == "api"
| where Log_s has_any ("error", "Exception", "fail", "Unhandled", "FeedbackEndpoints")
| project TimeGenerated, Log_s
| order by TimeGenerated desc
```

**Decision memo:** `.squad/decisions/inbox/wash-observability.md` — recommend wiring App Insights + exception handler + `/health` in next sprint.

---

**2026-04-19: Observability Audit Note**
Captain reported intermittent prod errors (quiz scoring, feedback). Decision memo filed: wire App Insights, add exception handler + ProblemDetails, wrap AI endpoint failures with try/catch+LogError, add /health endpoint. Awaiting approval; ~1 day implement + e2e verify.


---

## 2026-04-19 — Mobile Observability Plan (Captain: "what are you gonna do to add App Insights to the mobile app?")

**Key finding:** Mobile side is 80% already done. Didn't expect that going in.

**Inventory:**
- `SentenceStudio.MauiServiceDefaults/Extensions.cs` already calls `ConfigureOpenTelemetry()` with Logging + Metrics (HttpClient, Runtime) + Tracing (HttpClient). OTLP exporter is gated on `OTEL_EXPORTER_OTLP_ENDPOINT` (unset for mobile — works in local Aspire dev only).
- `MauiExceptions.cs` already handles the platform gauntlet: AppDomain, TaskScheduler, iOS MarshalManagedException with `UnwindNativeCode`, Android `UnhandledExceptionRaiser`, WinUI 3 FirstChance+Application.UnhandledException. But **no subscriber is attached** anywhere → crashes die silently today.
- `AddEmbeddedAppSettings()` loads invariant + Production/Development JSON from `SentenceStudio.AppLib` assembly manifest resources. Natural home for Azure Monitor connection string.
- Typed HttpClients (`AiApiClient`, `FeedbackApiClient`, `SpeechApiClient`, `PlansApiClient`) already flow through `AddStandardResilienceHandler` + service discovery. OTel HttpClient instrumentation already captures them.
- Zero `Microsoft.ApplicationInsights.*` refs anywhere. Clean slate.

**Plan delivered (memo):** Add `Azure.Monitor.OpenTelemetry.Exporter` 1.3.0 (NOT classic AI SDK — MS .NET 10+ recommended path), plug into existing OTel pipeline via `AddOpenTelemetry().UseAzureMonitor(...)`, subscribe `ILogger` sink to `MauiExceptions.UnhandledException`, add a tiny `wwwroot/js/error-bridge.js` + `[JSInvokable] JsErrorBridge` for BlazorWebView JS errors, DEBUG-guard the connection string load so dev/simulator builds emit nothing.

**Correlation:** Automatic via W3C `traceparent` header injection by OTel HttpClient instrumentation — works end-to-end once API side also emits OTel → App Insights. **Therefore server memo must ship first or in parallel** for correlation to be real.

**iOS gotchas to remember for implementation:**
- Full-link Release builds will strip `Azure.Monitor.OpenTelemetry.Exporter` reflection targets → need `Properties/LinkerConfig.xml` preserve directive.
- `PrivacyInfo.xcprivacy` needs "Crash Data" + "Performance Data" entries for App Store — not needed for DX24 sideload.
- Exporter has built-in 24h local file cache; don't disable it (handles offline).

**PII discipline:** UserProfileId (GUID) yes. Email/display name/Korean user text NO. Scrub at log sites, not via a processor (easier). OTel doesn't capture HTTP bodies by default — don't opt in.

**Effort:** ~1 day total. Recommended first-increment slice is ~3 hours: exporter + MauiExceptions subscriber only, Mac Catalyst first, prove the pipe works before investing in JS bridge / custom events / iOS AOT work.

**Memo filed:** `.squad/decisions/inbox/wash-mobile-observability.md`.

**Rule of thumb learned:** Before proposing new infrastructure, always inventory what's already wired. `MauiServiceDefaults` had the whole OTel pipeline sitting there, gated on an env var. The real gap was an exporter + a subscriber, not a rebuild.


---

## Learnings — 2026-04-20 (Mobile App Insights follow-up: TinyInsights eval + security stance)

**Connection string is write-only; embed it.** InstrumentationKey authorizes ingestion push only — can't read telemetry or touch other Azure resources. Microsoft's own docs tell mobile/desktop/JS clients to ship it in the app bundle. Worst case is fake-telemetry spam, bounded by daily ingestion cap ($5/day) + sampling. All the "secure" alternatives (fetch from API at startup, per-user keys, Key Vault) are **strictly worse** for a mobile app — chicken-and-egg (no telemetry if API is down, which is exactly when you need it), massive complexity for zero security gain, or require an Azure identity the app doesn't have. Rule: write-only keys with bounded blast radius belong in the client. Read-capable secrets never do.

**TinyInsights.Maui evaluated — REJECTED for this project.** Active project (Daniel Hindrikes, MVP, net10 support Jan 2026, crash improvements Apr 2026), nice developer ergonomics. BUT it depends on the **legacy `Microsoft.ApplicationInsights` 2.23.0** SDK, not OpenTelemetry. Our `SentenceStudio.MauiServiceDefaults` already has an OTel pipeline, and the API side is planning Azure Monitor OTel exporter. Mixing SDK families breaks W3C `traceparent` correlation between MAUI and API — which is the whole reason we want ONE App Insights resource in the first place. Would also duplicate telemetry (double HttpClient tracking, double exporters, double cost). Stuck with `Azure.Monitor.OpenTelemetry.Exporter` 1.3.0.

**Rule of thumb: SDK family consistency > convenience.** When the server tier commits to OpenTelemetry, the client tier has to stay on OpenTelemetry too — or correlation is theater. Check the `<PackageReference>` before adopting any MAUI observability library: if it pulls `Microsoft.ApplicationInsights.*` (classic SDK) and your server uses `Azure.Monitor.OpenTelemetry.Exporter`, walk away no matter how good the DX looks.

**First-increment plan UNCHANGED.** TinyInsights rejection doesn't alter the 3-hour Mac Catalyst slice: add exporter package, wire `UseAzureMonitor` in `ConfigureOpenTelemetry` guarded on Release+connection-string-present, subscribe `ILogger` to `MauiExceptions.UnhandledException`, embed connection string in `appsettings.Production.json`. Ship in parallel with server memo's PR so day-one traces already span both tiers.

---

## 2026-04-19 — Mobile App Insights Slice Shipped (Path 1, Mac Catalyst, PR #165)

**Captain approved "path 1 go".** Shipped the ~3-hour small slice on branch `squad/mobile-appinsights-slice` as draft PR #165.

**What landed**
- Azure resource `sstudio-mobile-ai` in `rg-sstudio-prod` (centralus), workspace-linked to `law-3ovvqiybthkb6`, daily cap 0.5 GB, notification-on-cap on.
- `Azure.Monitor.OpenTelemetry.Exporter` 1.7.0 in `SentenceStudio.MauiServiceDefaults`. AddAzureMonitor{Log,Metric,Trace}Exporter gated on `#if !DEBUG` + non-empty `AzureMonitor:ConnectionString`.
- `ResourceBuilder.AddService("SentenceStudio.Mobile.{DeviceInfo.Platform}")` so `cloud_RoleName` identifies the client.
- Single subscriber on `MauiExceptions.UnhandledException` in `SentenceStudioAppBuilder.InitializeApp` → `ILogger.LogCritical` → best-effort 3s `ForceFlush` on Logger/Tracer/Meter providers.
- Connection string committed to `appsettings.Production.json` (write-only key, bounded by cap — consistent with `wash-mobile-appinsights-answers` memo).

**Validated.** Forced `InvalidOperationException` via temp `Thread` in `InitializeApp` (env-var-gated) → KQL confirmed the record landed with `cloud_RoleName = SentenceStudio.Mobile.MacCatalyst` in ~5 minutes. Bonus finding: pipeline also captures *caught* exceptions that are logged via `ILogger.LogError(ex, ...)` — during startup we saw EF Core NativeAOT model-build warnings and HelpKit presenter init errors show up in App Insights automatically. That's free coverage.

## Learnings

- **Resource/ARN pattern.** `rg-sstudio-prod` / `sstudio-mobile-ai` / AppId `74e94530-d17f-404a-8726-b7266724b70f`. Connection string lives in `src/SentenceStudio.AppLib/appsettings.Production.json` under key `AzureMonitor:ConnectionString`. The file is tracked in git (not gitignored despite appsettings.json being ignored — the `.Production.json` suffix variant is explicitly tracked).
- **Package floor matters.** `Azure.Monitor.OpenTelemetry.Exporter` 1.7.0 requires `OpenTelemetry.Extensions.Hosting >= 1.15.1`. `MauiServiceDefaults` had the whole OTel stack pinned at 1.9.0 and the restore failed with NU1605 until I bumped Extensions.Hosting + Exporter.OTLP to 1.15.1 and Instrumentation.Http to 1.15.0 (1.15.1 doesn't exist for that one — watch for `NU1102 Unable to find package`). Runtime instrumentation bumped to 1.12.0 for consistency.
- **`cloud_RoleName` strategy.** Use `AddService(serviceName: ...)` on the `ResourceBuilder` inside `ConfigureResource`, BEFORE the `WithLogging/WithMetrics/WithTracing` chain. Using `DeviceInfo.Platform.ToString()` at runtime works cleanly because `MauiServiceDefaults` has `<UseMaui>true</UseMaui>` — `DeviceInfo` is available. I initially tried `#if MACCATALYST`/`#if IOS` compile-time detection but `MauiServiceDefaults` targets plain `net10.0` with no platform TFMs, so those symbols were never defined. Runtime detection was required.
- **Can't reference `MauiExceptions` from `MauiServiceDefaults`.** `MauiExceptions` lives in `SentenceStudio.AppLib`, and `MauiServiceDefaults` can't reference AppLib (that'd be a dependency inversion — AppLib references MauiServiceDefaults transitively via platform heads). Solution: put the subscriber in `SentenceStudioAppBuilder.InitializeApp` (AppLib) where `MauiExceptions` is directly accessible and resolve `LoggerProvider/TracerProvider/MeterProvider` from `app.Services` for the ForceFlush. AppLib already had `OpenTelemetry.Extensions.Hosting 1.11.2` — no new package needed. Minor version mismatch between AppLib (1.11.2) and MauiServiceDefaults (1.15.x) was source-compatible for the provider types.
- **Forcing an unhandled exception for validation — gotcha.** `Task.Run(() => throw …)` → `UnobservedTaskException` fires only on GC (unreliable timing). `new Timer(_ => throw …)` → timer can be GC'd before firing even when locally captured. `new Thread(() => Sleep; throw).Start()` with `IsBackground=true` → reliably crashes on the thread pool, fires `AppDomain.UnhandledException`, and `MauiExceptions` catches it. Use that for any future mobile telemetry validation.
- **Mac Catalyst pre-existing Release break.** `src/SentenceStudio.MacCatalyst/MauiProgram.cs` had unguarded `using Microsoft.Maui.DevFlow.{Agent,Blazor};` but those `<PackageReference>` are `Condition='$(Configuration)'=='Debug'`. Release-config compile fails with `CS0234`. Fixed with `#if DEBUG` around the usings. This has probably been broken for anyone building Release locally for a while — iOS device publish uses the Release config but pulls `src/SentenceStudio.iOS` which has its own `MauiProgram.cs` (so that path was unaffected).
- **`appsettings.Production.json` is tracked.** `.gitignore` line 411 ignores `appsettings.json` (bare), but `.Production.json` and `.Development.json` are tracked. Adding secrets there commits them. For write-only keys like the App Insights connection string, that's consistent with the rationale in my earlier `wash-mobile-appinsights-answers` memo — read-capable secrets still go to env vars / Key Vault.
- **Free bonus: caught `ILogger` exceptions also reach App Insights.** Because `Logging.AddOpenTelemetry()` registers OTel as a logging provider and `AddAzureMonitorLogExporter` exports it, every `logger.LogError(ex, ...)` / `LogCritical(ex, ...)` call in the codebase now sends exception telemetry — not just unhandled crashes. Validation showed existing code paths already flowing through.

## Learnings — 2026-04-21 (PR #165 review-fix follow-up)

- **Platform-string threading beats `DeviceInfo` at host-builder time.** The prior slice used `DeviceInfo.Platform` inside `ConfigureOpenTelemetry` to build `cloud_RoleName`. Code review flagged that correctly: `MauiServiceDefaults.ConfigureOpenTelemetry` runs while the host builder is still configuring, before `MauiApp.Build()`. MAUI Essentials may not be fully initialized then — risk of `Unknown` or throw. Fix that shipped: added `string platformName = "Unknown"` parameter to `AddMauiServiceDefaults` / `ConfigureOpenTelemetry`, and each platform head passes the literal (`"MacCatalyst"`, `"iOS"`, `"Android"`) from its `MauiProgram.cs` where the per-TFM context is unambiguous. No `IMauiInitializeService` needed. Deterministic, zero-runtime-surface, works even pre-Essentials. Windows head doesn't call `AddMauiServiceDefaults` at all today — left alone.
- **`open --env` propagates env vars to Mac Catalyst apps correctly.** Validated `SENTENCESTUDIO_CRASH_TEST=1` flowing via `open -n --env SENTENCESTUDIO_CRASH_TEST=1 SentenceStudio.app`. Do NOT run the binary directly (`Contents/MacOS/SentenceStudio.MacCatalyst`) — MAUI Mac Catalyst aborts in `load_aot_module` when launched outside LaunchServices. Use `open`.
- **Stale `obj/Release` causes ghost `Failed to load AOT module 'Azure.Core'`.** Incremental Release builds after package bumps can produce an .app that aborts at AOT load with no useful managed stack. Clean `src/*/obj/Release` + rebuild fixes it. First debugging checkpoint if a fresh Release bundle crashes before reaching your managed `Main`.
- **Parallel bounded flush on last-chance handlers is the right shape for any mobile OTel pipeline.** Serial `ForceFlush(3000)` across logger + tracer + meter can approach 9s worst case — iOS watchdog kills at ~5–10s. Three `Task.Run` calls (each with their own 2500ms internal timeout) wrapped in a single `Task.WaitAll(..., 3000ms)` holds a hard 3s ceiling regardless of per-provider stalls. Captured in `.squad/skills/maui-azure-monitor/SKILL.md`.
- **Idempotent event subscription via `Interlocked.Exchange` + static int flag.** Don't use a plain `bool` — hot-reload + dual-init paths can race. `Interlocked.Exchange(ref _flag, 1) == 0` is the cheap race-safe gate. Applied to `MauiExceptions.UnhandledException` so repeated `InitializeApp` calls (hot reload, test harnesses) can't double-fire the crash handler.


## Learnings — 2026-04-22 (Server-side App Insights companion, PR TBD on `squad/server-appinsights`)

- **API secrets live in three places, not one.** `AppHost.cs` passes real secrets (OpenAI key, ElevenLabs key, JWT signing key, DB user/pass, GitHub PAT) via `builder.AddParameter("…", secret: true)` + `.WithEnvironment("AI__OpenAI__ApiKey", openaikey)`. Non-secret config and write-only keys (CORS origins, service discovery, and now `AzureMonitor:ConnectionString`) live in `src/SentenceStudio.Api/appsettings.Production.json`. Runtime env vars (`ASPNETCORE_ENVIRONMENT=Production`) come from the azd/ACA manifest. When adding new config: secret → `AppHost.AddParameter`; write-only or structural → `appsettings.Production.json`; environment-toggle → ACA manifest. Don't invent a fourth channel.
- **The `AspNetCore` flavor of Azure Monitor is MAUI-incompatible.** `Azure.Monitor.OpenTelemetry.AspNetCore` transitively pulls `OpenTelemetry.Instrumentation.AspNetCore`, which declares `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. `Microsoft.AspNetCore.App` has no runtime pack for `maccatalyst-*` / `ios-*` / `android-*` RIDs. Since `SentenceStudio.ServiceDefaults` is (transitively) referenced by every MAUI head via `AppLib`, adding `.AspNetCore` to that shared project breaks MAUI builds with `NETSDK1082`. Solution: use lower-level `Azure.Monitor.OpenTelemetry.Exporter 1.7.0` + `AddAzureMonitor{Log,Metric,Trace}Exporter` in the shared defaults, and add `OpenTelemetry.Instrumentation.AspNetCore` **only to web-host csprojs** (API, etc.), wiring `.AddAspNetCoreInstrumentation()` from their individual `Program.cs`. This is the same pattern the `maui-azure-monitor` skill documents for the client side — just applied to the shared project too because MAUI is in its consumer graph.
- **`ConfigureResource(r => r.AddService(roleName))` is the only portable way to set `cloud_RoleName` for OTel → Azure Monitor.** Don't try to reach into Azure Monitor options — the exporter respects whatever `ResourceAttributes` the OTel provider has at export time. `AddService(name)` populates `service.name` which Azure Monitor maps to `cloud_RoleName`. Must be called **before** `.WithMetrics(...)` / `.WithTracing(...)` / `.AddOpenTelemetry(...)` configuration of the providers so every exporter sees the same resource.
- **Package-version alignment across shared projects is load-bearing.** `MauiServiceDefaults` was at OTel 1.15.x (from PR #165), `ServiceDefaults` was at 1.9.0, `AppLib` at 1.11.x. Adding Azure Monitor to `ServiceDefaults` forced it to 1.15.1 via transitive deps, which exposed `NU1605` in every downstream web project because `AppLib`'s 1.11.x pins now looked like downgrades. Fix: bump all three to the same 1.15.x stack at once. Lesson for future OTel / Azure Monitor bumps — grep all four OTel csproj lines (`ServiceDefaults`, `MauiServiceDefaults`, `AppLib`, `WebServiceDefaults`) before shipping.
- **Aspire CLI's `aspire run` rebuilds all MAUI heads on a clean `obj` tree.** That's 10+ minutes worst case for first run when the cache is cold — which is why it appeared hung during local smoke. For a "did my ServiceDefaults change break anything?" smoke, direct Release/Debug builds on API + one MAUI head are a faster signal than waiting for Aspire. Use the full Aspire run only when you need the dashboard (correlation proof) or for real multi-resource integration tests.
- **`AddServiceDefaults` overload with a `cloudRoleName` param beats auto-detection.** Each Program.cs passes its literal (`"SentenceStudio.Api"`, `"SentenceStudio.WebApp"`, etc.). `builder.Environment.ApplicationName` fallback is there for test hosts and one-offs. Same principle as the mobile slice fix for `DeviceInfo.Platform`: don't detect runtime state at host-builder time — thread it in from the entry point where it's unambiguous.

## Learnings — 2026-04-22 (PR #166 pre-deploy review fixes)

- **App Insights daily cap raise via Azure CLI.** `az monitor app-insights component billing update --app <name> --resource-group <rg> --cap <GB> --stop <bool>` — the `--stop` / `-s` flag controls `stopSendNotificationWhenHitCap`, not the threshold (there's no separate `--stop-sending-notification-when-hitting-threshold` flag despite what some docs suggest — the CLI rejects it). Read-back with `az monitor app-insights component billing show …` returns the full `dataVolumeCap` object: `cap`, `warningThreshold` (% of cap, default 90), `stopSendNotificationWhenHitCap`, `stopSendNotificationWhenHitThreshold`, `maxHistoryCap`, `resetTime`. Captured `sstudio-mobile-ai` going 0.5 → 2.0 GB for PR #166 to give ~4× headroom for mobile + 4 server emitters (API, WebApp, Workers, Marketing).
- **`UseExceptionHandler` + `ILogger.LogError` is required on top of `AddAspNetCoreInstrumentation` for App Insights `exceptions` rows.** Previously assumed ASP.NET Core OTel instrumentation alone covered this (see PR #165 follow-up note). It doesn't. The instrumentation **tags the request span with exception events and sets the span status to Error**, which populates the `requests` row's `success=false` + the `customDimensions.exception.*` attributes. But it does **NOT** emit a separate record to the `exceptions` table — Azure Monitor's OTel exporter only produces an `exceptions` row when it receives an `ILogger` log record carrying an `Exception`. AspNetCore's own `ExceptionHandlerMiddleware` logs one at `Error` level (`Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware[1]`), but you only get that if `UseExceptionHandler` is wired. Without it, Captain's `exceptions | where cloud_RoleName == 'SentenceStudio.Api'` KQL returns empty for any unhandled controller/minimal-API throw. Explicit `UseExceptionHandler` → `feature.Error` → `logger.LogError(ex, ...)` closes the gap and produces a `UnhandledException` category log record that maps to an `exceptions` row. Pattern now captured in `.squad/skills/aspnetcore-azure-monitor/SKILL.md` (the previous version of that skill incorrectly claimed the middleware was unnecessary — corrected in the same commit).
- **`UseExceptionHandler` must land BEFORE `UseAuthentication` / `UseAuthorization` / `UseCors` / custom middleware** to wrap them all. First in the pipeline. If it's placed after auth, an exception in the auth handler dies with a raw 500 and no log record, skipping the handler entirely.
- **Smoke validation without full `aspire run`.** `aspire run` takes 90+ seconds even on warm caches because it has to build the AppHost graph + start Postgres container + wait on all downstream resources. For a one-off middleware smoke test, running the API directly with `ConnectionStrings__sentencestudio=<bogus> Jwt__SigningKey=<32-char-dummy> Database__SkipMigrateOnStartup=true dotnet run --no-build --project src/SentenceStudio.Api` is 20 seconds to first request. The DB skip flag prevents `MigrateAsync` from hanging on the bogus connection; the SigningKey length has to be ≥32 chars for `SymmetricSecurityKey` to accept it. Anonymous endpoints like `/__debug/boom` served fine without real auth/DB. Use this pattern for any future API-only pipeline smoke test.

---

## 2026-04-22 — PR #166 dress rehearsal (Release-build, local, no `azd deploy`)

**Requested by:** Captain — validate the full PR #166 pipeline against real App Insights before risking a production deploy. The `#if !DEBUG` guard means `aspire run` can't test Azure Monitor, so the API had to be built Release and run Production standalone.

### Setup that worked

Went straight to **Option B (Docker Postgres)** — `aspire run` + dashboard-kill-resource felt higher-friction for a single-API validation. Worked first try:

```bash
docker run -d --name sstudio-pg-rehearsal \
  -e POSTGRES_PASSWORD=devpass -e POSTGRES_USER=postgres -e POSTGRES_DB=sentencestudio \
  -p 5433:5432 postgres:16        # 5433 avoids clash with Aspire's 5432 if it's running

dotnet build src/SentenceStudio.Api/SentenceStudio.Api.csproj -c Release

ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_URLS="https://localhost:7801;http://localhost:7802" \
  ConnectionStrings__sentencestudio="Host=localhost;Port=5433;Database=sentencestudio;Username=postgres;Password=devpass" \
  Jwt__SigningKey="dress-rehearsal-key-at-least-32-characters-long-xxxx" \
  dotnet run --project src/SentenceStudio.Api -c Release --no-build --no-launch-profile
```

**Critical: `--no-launch-profile`.** Without it, `src/SentenceStudio.Api/Properties/launchSettings.json` wins and force-sets `ASPNETCORE_ENVIRONMENT=Development` + overrides `ASPNETCORE_URLS` to its own ports. In Development, `appsettings.Production.json` isn't loaded, so `AzureMonitor:ConnectionString` comes back null and the exporters don't register — telemetry goes nowhere. First attempt showed `Hosting environment: Development` on port 5081 despite env vars; `--no-launch-profile` fixed it cleanly.

**Which env vars are minimum to bind in Production config:**
- `ASPNETCORE_ENVIRONMENT=Production` (loads appsettings.Production.json where AzureMonitor:ConnectionString lives)
- `ASPNETCORE_URLS` (override launchSettings)
- `ConnectionStrings__sentencestudio` (EF migration + CoreSync)
- `Jwt__SigningKey` (Program.cs:130 throws in non-Development without it)

Not required: `AI:OpenAI:ApiKey`, `ElevenLabsKey`, `GitHub:Pat`, email config — all gated by null checks.

### Boom-endpoint recipe (same as the review-fix smoke)

Added at end of `Program.cs`, uncommitted:
```csharp
app.MapGet("/__debug/boom", () => { throw new InvalidOperationException("Dress rehearsal: server-side AppInsights exception capture"); });
```
Rebuilt, hit it twice, confirmed 500 + `application/problem+json` body AND `fail: UnhandledException[0]` ILogger record in the console (full stack trace, exception carried). Removed before stopping.

### KQL proof — all three green at +4 min ingestion

- **Query A (exceptions):** 4 rows, `cloud_RoleName=SentenceStudio.Api`, outerMessage matches "Dress rehearsal". Each boom hit emitted 2 log records (one from my `UnhandledException` logger, one from ASP.NET Core's `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware` ID 1) — both reach the `exceptions` table. Fine.
- **Query B (requests):** 4 rows — 2× auth-login 401, 2× boom 500 — all role=SentenceStudio.Api. `AddAspNetCoreInstrumentation` + Azure Monitor trace exporter working.
- **Query C (traceparent):** simulated a mobile-originated call by injecting `traceparent: 00-5c4324bba96c15b5da00f712ac863982-d96513170a11dd97-01`. Server request AND child Postgres dependency both carry `operation_Id == 5c4324bba96c15b5da00f712ac863982` and the request row's `operation_ParentId == d96513170a11dd97`. Proves W3C propagation: when deployed mobile traffic arrives, the `requests` × `dependencies` join in `SKILL.md`'s correlation KQL will light up automatically.

### Latency surprise: none

`az monitor app-insights query` returned results ~4 min after emit. Within the 2–5 min window the skill documents. No retries, no surprise.

### Learnings

- **`--no-launch-profile` is non-negotiable** when running a launchSettings-owning web host Release/Production standalone for Azure Monitor validation. Silent telemetry drop if you miss it.
- **Docker Postgres on a non-5432 port** sidesteps any collision with a parallel Aspire session. `5433` is the polite choice.
- **Port choice tip:** picking `7801`/`7802` avoided the Kestrel dev-cert complaint chain that kicks in on `7012`/`5081` (the launchSettings defaults). Even so, `curl -k` is fine for localhost smoke.
- **Two exception log rows per boom is expected.** Both my `UnhandledException` `ILogger.LogError` AND ASP.NET Core's built-in `ExceptionHandlerMiddleware` EventId 1 emit an exception log record. Both land in the `exceptions` table. Not a bug — it's how the framework is wired. Do NOT try to deduplicate.

### Zero commits on squad/server-appinsights

Validation-only, as briefed. Boom endpoint removed; working tree clean at end of task. PR body updated with a `## Dress rehearsal (Release-build, local)` section containing the three KQL tables. PR stays draft for Captain to flip ready-for-review.

## 2026-04-22 — PR #166 server-side App Insights shipped

### Big learning (write-in-stone)

**ALWAYS read `AppHost.cs` before recommending a deploy-tool flip.** This session, I endorsed switching from `azd deploy` → `aspire deploy` based on Captain's verbal briefing that the AppHost registered `AddAzureContainerAppEnvironment("aca-env").WithAzdResourceNaming()`. It doesn't — lines 5–9 contain an explicit comment saying that registration was removed because it broke azd compatibility. Session summaries drift from code reality. The AppHost source is authoritative for deploy-tool feasibility. Verify, don't trust recap.

### Stale safety checks are worse than no safety checks

The pre-existing `scripts/pre-deploy-check.sh` was built for the pre-migration container-Postgres + AzureFile-volume architecture. Post-Flexible-Server migration, 2 of 5 checks were false negatives. The cultural failure mode: people bypass with `SKIP_PREDEPLOY_CHECK=1` because "the script is always wrong now," and a real failure gets missed. **Rewrite stale safety scripts in the same PR that exposes them; don't leave them to rot.**

### Production-specific KQL gotcha

Azure Container Apps prepends the ACA env name to `cloud_RoleName`: `[cae-3ovvqiybthkb6]/SentenceStudio.Api` not `SentenceStudio.Api`. Local dress rehearsals (the service running on `localhost`) emit the plain name. Any KQL committed during rehearsal needs `endswith "SentenceStudio.Api"` or a bracket-strip to work in prod. Document this in dress-rehearsal runbooks going forward.

### `azd deploy` on Flexible-Server architecture — works cleanly

- 2m 18s for all 5 services (api, cache, marketing, webapp, workers), incremental container image push
- Zero database interaction (Flexible Server is independent of the deploy tool)
- `azd env list` confirmed `sstudio-prod` as the default local env
- `azd deploy --no-prompt` is the non-interactive variant and worked first try

### `post-deploy-validate.sh` is MANDATORY, not decorative

Phase 1 (infra health) + Phase 4 (regression) are the gates — 17 PASS, 0 FAIL. Phase 2 auth tests skipped (no `DEPLOY_TEST_PASSWORD` in env — configure this for future deploys; it would have tested the login proxy). Phase 3 (change-specific) is manual and was done via the KQL queries in the PR body.

### Follow-up issues filed from this deploy

#167 aspire-deploy migration · #168 Managed Identity for AI auth · #169 Blazor WebView JS exception bridge · #170 OTel linker preserve configs

## Learnings — 2026-04-22 iOS publish to DX24

### Build
- **net11 preview 3 SDK (`11.0.100-preview.3.26209.122`) builds iOS Release on Xcode 26.3 cleanly.** net10 GA (10.0.101) expects Xcode 26.2 and fails validation. Swap via `global.json` at repo root — MUST restore after or Mac Catalyst daily loop breaks.
- Build was 40 seconds (incremental from prior work). 0 errors, 571 pre-existing warnings. Full Release + ios-arm64 AOT did NOT require `obj/Release` clean.
- `global.json` is gitignored — swapping and restoring leaves no git diff.

### Deploy to DX24
- **`xcrun devicectl device install` first attempt may fail with "Socket is not connected" / NWError 57** even when device shows `available (paired)` in `devicectl list devices`. Retry after 5s almost always succeeds — the control channel just needs a moment to reestablish.
- **`xcrun devicectl device process launch` fails with FBSOpenApplicationErrorDomain error 7 (Locked)** if the iPhone is locked. Captain must physically unlock (Face ID / passcode) before launch. Install works while locked; launch does not.
- **NEVER `devicectl device uninstall` on DX24** — it's Captain's daily iPhone. `install` upgrades in place and preserves app sandbox (SQLite, Preferences, Keychain). Uninstalling would wipe production user data.

### Production App Insights correlation gotcha
- **Mobile emits traces, NOT dependencies.** The original runbook KQL joined `requests` (mobile) to `requests` (api). Wrong on both sides. Correct shape: `dependencies` (mobile outbound HTTP) to `requests` (api inbound HTTP), joined on `operation_Id`.
- **BUT**: with PR #165's current mobile OTel setup, mobile emits neither `dependencies` nor `requests` — only `traces`. HttpClient calls show up as log messages like `"Sending HTTP request GET https://..."` because the logging provider is wired, but `AddHttpClientInstrumentation()` on the TracerProvider is missing. That means no client spans are created, no W3C `traceparent` header is injected, and the API starts a fresh root trace for every incoming call (`operation_Id == operation_ParentId`).
- **Diagnostic tell:** if API's `operation_Id == operation_ParentId` for mobile-called endpoints, the mobile side is not propagating trace context. Check the mobile TracerProvider registration.
- **Production prefixes ACA role name:** `[cae-3ovvqiybthkb6]/SentenceStudio.Api`. Always use `endswith "SentenceStudio.Api"` in KQL, never `==`.

- 2026-07-26: **Word/Phrase Schema Review** — Reviewed plan for `LexicalUnitType` enum + `PhraseConstituent` join table + mastery cascade policy. Found 5 blockers: (1) enum needs explicit `.HasConversion<int>().HasDefaultValue(0)` for backfill safety, (2) `ConstituentWordId` must be nullable for `SetNull` cascade + needs index + unique constraint on pair, (3) backfill must move to dedicated service/startup hook NOT hot path in GetAsync, (4) cascade needs explicit userId scoping + logging for visibility, (5) SQLite migration must be hand-written (multi-target dotnet ef broken). All changes preserve existing data — no user-data risk, but missing these will cause prod incidents (silent enum breakage, perf crater on backfill, cascade to wrong user). Approved with changes. Verdict in `.squad/decisions/inbox/wash-word-phrase-schema-review.md`.

- 2026-07-26: **LexicalUnitType Model Added (Todo `model-enum`)** — Created `LexicalUnitType` enum (Unknown=0, Word=1, Phrase=2, Sentence=3) in new file `Models/LexicalUnitType.cs`. Added non-nullable `LexicalUnitType` property to `VocabularyWord` using `[ObservableProperty]` pattern with default `LexicalUnitType.Unknown`. Configured EF Core in `ApplicationDbContext.OnModelCreating` with explicit `.HasConversion<int>().HasDefaultValue(LexicalUnitType.Unknown)` for reliable 0-storage on backfill across PostgreSQL (server) and SQLite (MAUI). Codebase conventions discovered: (1) Enums defined in separate files in Models/ folder, (2) Enum properties on entities use `[ObservableProperty]` with private field + default value (matches VideoImport.Status pattern), (3) ApplicationDbContext entity configuration is in one massive OnModelCreating method with inline builder calls (no separate config classes), (4) This is the FIRST enum with explicit HasConversion<int> — other enums (LearningPhase, VideoImportStatus) rely on EF Core's default integer storage. Build green on both Shared project (net10.0) and MacCatalyst head (net10.0-maccatalyst). Migration NOT created yet (that's todo `migration-schema`). EF Core WILL warn about PendingModelChanges until migration is applied — this is EXPECTED intermediate state.

- 2026-07-26: **PhraseConstituent Join Entity Created (Todo `model-constituent`)** — Created `PhraseConstituent` join entity (new file `Models/PhraseConstituent.cs`) linking phrase words to their constituent words via nullable FK (`ConstituentWordId` allows `SetNull` cascade). Added DbSet + OnModelCreating fluent config to `ApplicationDbContext`: PhraseWordId FK with Cascade delete, ConstituentWordId FK with SetNull delete, single-column indexes on both FKs, and unique composite index on (PhraseWordId, ConstituentWordId) to prevent duplicate constituent links. Both FKs target VocabularyWord — used explicit `.HasForeignKey()` + `.WithMany()` to disambiguate (no inverse nav collection on VocabularyWord). Registered in `SharedSyncRegistration.cs` for CoreSync (both SQLite and PostgreSQL providers). Model follows house conventions: string GUID ID with `Guid.NewGuid().ToString()` + `ValueGeneratedNever()`, plain class (no ObservableObject for join entities), `[JsonIgnore]` on nav props. Build green on both Shared (net10.0) and MacCatalyst (net10.0-maccatalyst). Migration NOT created yet (that's next todo). EF Core WILL continue warning about PendingModelChanges — this is EXPECTED.

- 2026-04-23: **EF Migration for LexicalUnitType + PhraseConstituent (Todo `migration-schema`)** — Generated dual-provider EF Core migration `AddLexicalUnitTypeAndConstituents` (timestamp 20260423213242) adding `LexicalUnitType` integer column (default 0) to `VocabularyWord` and creating `PhraseConstituent` table with two VocabularyWord FKs (Cascade + SetNull), 3 indexes (FK1, FK2, unique composite). **EF tooling workaround discovered:** `dotnet ef migrations add` fails on multi-targeted csproj (`<TargetFrameworks>net10.0;net10.0-ios;...`) with `MSB4057: target "ResolvePackageAssets" does not exist`. Solution: temporarily switch to `<TargetFramework>net10.0</TargetFramework>` (singular), generate migration, restore multi-targeting. PostgreSQL migration auto-generated via `dotnet ef`, SQLite migration hand-written by converting PG migration (type substitution: `integer`→`INTEGER`, `text`→`TEXT`, `timestamp with time zone`→`TEXT`) and removing Npgsql-specific extension calls (`NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn`). Both snapshots updated. **Table naming validated:** EF respected singular names (`VocabularyWord`, `PhraseConstituent`) from explicit `.ToTable()` calls in DbContext — no plural gotcha. **SQLite SetNull confirmed:** SQLite migrations use `ReferentialAction.SetNull` identically to PostgreSQL (no special handling). Both builds green (Shared + MacCatalyst). No `PendingModelChangesWarning` after snapshot update. Migration will auto-apply at runtime via `MigrateAsync()` in `UserProfileRepository.GetAsync()`. Full writeup in `.squad/decisions/inbox/wash-migration-schema.md`.

---

## 2025-05-02: Vocabulary Classification Backfill Service

**Task:** Implement dedicated startup service to backfill `LexicalUnitType` for existing `VocabularyWord` rows where `LexicalUnitType == Unknown`.

**Implementation:**
- Created `VocabularyClassificationBackfillService.cs` in `src/SentenceStudio.Shared/Services/`
- Registered as singleton in `CoreServiceExtensions.AddSentenceStudioCoreServices()`
- Wired to run after `MigrateAsync()` in:
  - API: `Program.cs` (line ~268, inside `!skipDatabaseInitialization` block)
  - WebApp: `Program.cs` (line ~169, after migrations)
  - MAUI: `SyncService.InitializeDatabaseAsync()` (line ~208, before CoreSync provisioning)

**Classification Heuristic (Priority Order):**
1. **Tags check** (case-insensitive): "phrase" → Phrase; "sentence" → Sentence
2. **Terminal punctuation**: ends with `. ? ! 。 ？ ！` → Sentence
3. **Whitespace OR length > 12**: contains whitespace (including CJK U+3000) OR length exceeds threshold → Phrase
4. **Default**: Word
5. **Guard**: single non-ASCII char (CJK ambiguous) → Unknown

**Key Decisions:**
- **Length threshold = 12 chars:** Balances Korean compounds (공부하다 = 4 chars stays Word) vs. longer phrases. CJK density means 12 is moderate but effective.
- **Static test method:** `ClassifyHeuristic(string, string?)` exposed for Jayne's unit tests without DB dependency
- **Idempotent:** Only touches rows where `LexicalUnitType == Unknown`, safe to run repeatedly
- **NOT in hot path:** Runs once at startup, NOT inside `UserProfileRepository.GetAsync()` request loop
- **Logging:** Counts per classification bucket + total + elapsed ms for observability

**Pattern Match:**
- Mirrors `BackfillUserProfileIdsAsync` structure (idempotent, batch, logging, one transaction)
- Runs AFTER `MigrateAsync()` but BEFORE CoreSync provisioning (MAUI) or request handling (web)
- No migration file needed — operates on existing column with default `Unknown` value

**Korean-Specific Notes:**
- **NOT using sentence-ender detection:** 다/요/까 alone insufficient (many verb forms end in 다 without being sentences)
- **NOT stripping particles:** Too brittle for heuristic — whitespace/length catches most cases
- **CJK ideographic space (U+3000):** Explicitly checked in whitespace detection

**Edge Cases:**
- Empty/null term → Unknown
- Single non-ASCII char → Unknown (could be abbreviation, particle, incomplete entry)
- CJK ideographic space → treated as whitespace

**Files Modified:**
- Created: `src/SentenceStudio.Shared/Services/VocabularyClassificationBackfillService.cs`
- Modified: `CoreServiceExtensions.cs`, `Program.cs` (API + WebApp), `SyncService.cs` (MAUI)
- Documented: `docs/wash-backfill-classification.md`

**Verification:**
- ✅ Shared project builds
- ✅ MacCatalyst project builds
- ✅ Static method exposed for unit testing
- ✅ Wired in all startup paths
- ✅ Logging includes counts per bucket + elapsed time

**Next Steps (Future):**
- AI-based classification for ambiguous cases (single-char CJK, complex compounds)
- User override UI (already possible via Tags as workaround)
- Telemetry to track accuracy via user corrections

---

## 2025-06-01: Progress Cascade for Phrase Constituents

**Task**: `progress-cascade`  
**Status**: ✅ Complete  
**PR**: (pending)

### Summary
Implemented passive exposure cascade in `VocabularyProgressService.RecordAttemptAsync` so that when a user practices a phrase/sentence, each constituent word receives a passive exposure record (no streak/mastery change). This allows the mastery algorithm to reflect that the user saw those words.

### Key changes
- Added `IServiceProvider` to `VocabularyProgressService` constructor to enable scoped `ApplicationDbContext` queries
- Inserted cascade block after phrase mastery commits, before recording detailed context
- Query `VocabularyWords` to check `LexicalUnitType` (Phrase/Sentence)
- Query `PhraseConstituents` to get constituent word IDs
- For each constituent: `GetOrCreateProgressAsync` → `RecordPassiveExposureAsync`
- Best-effort per constituent with structured logging (failures swallowed, don't roll back phrase mastery)
- Cascade regardless of attempt correctness (activity tag includes `:Incorrect` suffix for analytics)

### Design decisions honored
1. **Transaction policy**: Best-effort with logging — phrase mastery commits independently from constituents
2. **Defense in depth**: Explicit `GetOrCreateProgressAsync` before `RecordPassiveExposureAsync`
3. **One level only**: No recursive cascade (guarded by `LexicalUnitType` check)
4. **Explicit userId**: Pass `attempt.UserId` to all methods, no ambient context
5. **Cascade on incorrect attempts**: User still saw the words (per Captain's call)

### Technical notes
- Used existing `GetOrCreateProgressAsync(wordId, userId)` at line 433
- Used existing `RecordPassiveExposureAsync(wordId, userId, activity)` at line 737
- Activity naming: `"PhraseCascade:{activity}"` or `"PhraseCascade:{activity}:Incorrect"`
- Structured logs: `PhraseCascade start` (Info), `PhraseCascade constituent exposure failed` (Error)
- No new migrations, no DB resets, no schema changes
- Builds green: Shared, MacCatalyst, API (warnings are pre-existing)

### Deviations
None. Exact match to Captain's specification.

### Deliverables
- ✅ Edited `VocabularyProgressService.RecordAttemptAsync`
- ✅ Build green (Shared + MacCatalyst + Api)
- ✅ Writeup: `docs/decisions/inbox/wash-progress-cascade.md`
- ✅ History: `.squad/agents/wash/history.md` (this entry)
- ⏳ Unit tests: Jayne owns `tests-mastery-cascade` (parallel todo)

### Files modified
- `src/SentenceStudio.Shared/Services/VocabularyProgressService.cs` (added IServiceProvider, using statements, cascade block)

**Ready for Jayne's testing and Captain's review.**

- 2025-01-25: **SmartResourceService Scope Bug Fix** — Fixed `GetPhrasesVocabularyIdsAsync()` ActiveUserId scope bug that returned 0 mappings (6 of 12 tests failed). Root cause: called `GetAllVocabularyWordsAsync()` which depends on ActiveUserId, but SmartResourceService never sets it. Solution: replaced with direct EF query via scoped ApplicationDbContext — fetch user's word IDs from VocabularyProgress (already filtered by userId param), then query VocabularyWords with `.Where(w => userWordIds.Contains(w.Id))`. Added IServiceProvider ctor dependency. Fixed one test EF tracking conflict (used Find() guard before Remove()). Build: 0 errors, 149 warnings (pre-existing). Tests: 12/12 pass. Follow-up: DailyReview/Struggling methods may have same pattern (not in scope). Doc: `.squad/decisions/inbox/wash-smart-resource-phrases-fix.md`

- 2026-04-23: **SQLite migration history reconciliation (local MacCatalyst dev DB drift)** — procedure: find DB in `~/Library/Containers/<GUID>/Data/Library/`, back up first (`cp -a` + size check), inspect `.tables`/`.schema` vs each migration's `Up()`, insert `__EFMigrationsHistory` rows only for migrations whose effects are already present, leave the target migration (and any truly-unapplied migrations) unlisted so EF applies them on next launch. Legacy orphan history rows are ignored by EF — preserve them. ProductVersion comes from each migration's Designer.cs `HasAnnotation("ProductVersion", ...)`. Codified in `.squad/skills/sqlite-migration-history-reconcile/SKILL.md`.

- 2026-04-24: **SmartResource per-type idempotency fix (Phrases missing for upgraded users)** — `InitializeSmartResourcesAsync` previously short-circuited whenever any smart resource existed (`if (existingSmartResources.Any()) return;`), so users who pre-dated the `Phrases` addition never got that entry seeded. Refactored to per-type idempotency: build a HashSet of existing `SmartResourceType` values, iterate an ordered seed-definition array (Daily Review → New Words → Struggling → Phrases), and create only the types that are missing. Refresh only newly-created resources this pass (existing ones refresh via `RefreshAllSmartResourcesAsync` on launch). No schema change, no migration, no duplicate/delete/reorder. Seed titles/descriptions/tags/enum values untouched. Build: 0 errors (MacCatalyst, 541 pre-existing warnings). Decision: `.squad/decisions/inbox/wash-smartresource-idempotency.md`. Uncommitted — awaiting Captain's `/review`.

- 2026-04-24: **Wired SmartResourceService into UserProfileRepository.GetAsync** — prior fix had zero production callers (only `SmartResourcePhrasesTests`). Added `EnsureSmartResourcesAsync(UserProfile)` to `UserProfileRepository`, invoked from `GetAsync` immediately after profile load / `DisplayLanguage` normalization. Pattern mirrors the existing `EnsureMultiUserBackfillAsync` "once per session" flag, but keyed per `profile.Id` via static `HashSet<string>` + lock (multi-profile safe). Resolves `SmartResourceService` via `GetService<T>` (no-op if not registered), passes `profile.TargetLanguage ?? "Korean"` and `profile.Id`. Exceptions caught + logged Warning — never blocks profile load. The in-session guard sets in `finally` so transient failures can retry on next launch. DB-layer per-type idempotency from prior turn is unchanged. Build: 0 errors (MacCatalyst, 541 pre-existing warnings). Decision: `.squad/decisions/inbox/wash-smartresource-wireup.md`. Uncommitted — awaiting Captain's `/review`.

---

## Session: 2026-04-24 — Word vs Phrase (WoC) Final Batch Consolidation

**Focus:** DB reconciliation (Option A), smart-resource seeding fix (per-type idempotency), smart-resource wire-up (GetAsync hook), migration file renames (git-mv 4 files, PG+SQLite pairs).

**Decisions made:**
- Per-type idempotent smart-resource seed: `HashSet<SmartResourceType>` check, append-only seed array for future types
- Wire `InitializeSmartResourcesAsync` into `UserProfileRepository.GetAsync` with per-profile in-session guard + non-fatal exception handling
- Migration file renames (20260725230000 → 20260415024019) after Coordinator confirmed prod PG already had [Migration] attribute; DB backfill `__EFMigrationsHistory`

**E2E outcome:** Step 5 re-run PASS — Phrases smart resource present, 4 total resources in DB, UI Phrases card rendered, 403-item payload (dynamic, by design).

**Artifacts:** `.squad/decisions/inbox/wash-smartresource-idempotency.md`, `wash-smartresource-wireup.md`, `wash-db-reconcile-option-a.md` (consolidated to `.squad/decisions.md`). Build green. No code changes outside of smart-resource + migration wire-up committed.


- 2026-04-24: **DX24 LexicalUnitType Hotfix (Production Emergency)** — Captain installed Release iOS build (feat/vocab Word-vs-Phrase, commit ff0bb25) to DX24 (iPhone) but app errored on every activity page with "no such column: LexicalUnitType". Root cause: `SyncService.InitializeDatabaseAsync` has a catch-all at lines 227-230 that logs MigrateAsync exceptions and continues, so the new migration `20260423213242_AddLexicalUnitTypeAndConstituents` failed silently on device. Established pattern from `AddMissingVocabularyWordLanguageColumn.cs`: SQLite migrations for mobile must be idempotent via `PatchMissingColumnsAsync` because MigrateAsync failures are swallowed. Fix: (1) Made SQLite migration Up() empty with doc comment explaining snapshot-only advancement + PatchMissingColumnsAsync pre-migration patching pattern. (2) Extended `PatchMissingColumnsAsync` to add `LexicalUnitType INTEGER NOT NULL DEFAULT 0` column to VocabularyWord if missing AND create `PhraseConstituent` table with all 3 indexes (FK1, FK2, unique composite) using `CREATE TABLE IF NOT EXISTS` + `CREATE INDEX IF NOT EXISTS` for idempotency. Pattern confirmed: future SQLite migrations adding columns/tables must pair migration file with PatchMissingColumnsAsync entries at landing time or risk silent schema drift on mobile. Build green (Shared Release). Decision: `.squad/decisions/inbox/wash-dx24-lexical-patch.md`.

---

## CRITICAL RULE: SQLite Migration Defense-in-Depth (2026-04-24)

**Context:** DX24 vocab-page crash (NULL at ordinal 8, then LexicalUnitType missing column).

**Pattern:** Defensive ALTER TABLE patches MUST include DEFAULT clauses for non-nullable entity properties, PLUS an idempotent backfill UPDATE for databases patched before the fix shipped.

**Example:**
```csharp
// In migration: add NOT NULL DEFAULT when possible
migrationBuilder.AddColumn<int>(
    name: "ExposureCount",
    table: "VocabularyProgress",
    nullable: false,
    defaultValue: 0);

// In SyncService.PatchMissingColumnsAsync: idempotent backfill
var result = await connection.ExecuteAsync(
    "UPDATE VocabularyProgress SET ExposureCount = 0 WHERE ExposureCount IS NULL");
_logger.LogWarning($"Patched {result} rows: ExposureCount NULL → 0");
```

**When migration can't use DEFAULT** (e.g., computed column, complex logic):
1. Still add the column with a safe default (NULL if nullable, 0 if int, empty string if text, etc.)
2. Implement idempotent post-migration backfill in `PatchMissingColumnsAsync` using `WHERE ... IS NULL` or `WHERE ... = <old_value>`
3. Log at WARNING level with row count
4. Non-fatal on error (log + continue, user app must not crash)

**Why this matters:**
- SQLite on iOS/Android can have legacy migration history seeded without schema applied
- `MigrateAsync` failures are caught + logged in `SyncService.InitializeDatabaseAsync` (non-fatal, degraded mode)
- Silent schema drift means NULLs in non-nullable EF entity properties → `SqliteException` on every query
- User sees crash only when navigating to a page that queries the incomplete schema (very late in session)

**For all future SQLite migrations (mobile):**
1. Make the SQLite migration Up() idempotent (either empty with doc comment explaining PatchMissingColumnsAsync handles it, or use SQL that works on repeat)
2. Add corresponding entry to `PatchMissingColumnsAsync` at the same time the migration lands
3. Use IF NOT EXISTS / pragma checks for idempotency
4. Reference: `AddMissingVocabularyWordLanguageColumn.cs` (empty Up() + patch pattern) and commit c9b1d0a (ExposureCount example)

**Verification:** Always test on Mac Catalyst Debug build (via `scripts/validate-mobile-migrations.sh`) and device before merge.

### 2026-05-30: Bulk Import Data Layer Patterns (Scout for File Import Feature)

**Context:** Pre-architecture scouting for Zoe to design a file import feature for vocabulary lists (CSV/text). No implementation — read-only investigation of existing patterns.

**Key discoveries:**

1. **Dedup inconsistency:** `VideoImportPipelineService` uses case-sensitive exact match on `TargetLanguageTerm` (line 368), but `LearningResourceRepository` utilities use case-insensitive trimmed comparison (line 940). This creates duplicates when same word appears with different casing across imports. **Recommendation:** Standardize to case-insensitive trimmed dedup in service layer (both YouTube and file imports).

2. **Shared vocabulary model:** `VocabularyWord` has NO `UserProfileId` — vocabulary is shared across users. Per-user data lives in `VocabularyProgress` (created lazily on first practice, NOT at import time). `ResourceVocabularyMapping` provides many-to-many between user's `LearningResource` and shared `VocabularyWord` pool.

3. **Batch import status tracking pattern:** `VideoImport` entity tracks pipeline state with enum statuses (`Pending`, `FetchingTranscript`, etc.). Background execution via `Task.Run`, caller polls `/api/imports/{id}` for progress. Pattern is reusable for file imports if status UI is needed.

4. **Repository transaction pattern:** `SaveResourceAsync` (LearningResourceRepository:210-250+) handles resource + vocabulary in single transaction: (a) detach nav props, (b) check existing resource, (c) save resource, (d) dedup words via `GetWordByTargetTermAsync`, (e) create mappings, (f) SaveChanges, (g) trigger sync. Use this pattern for file import to maintain data integrity.

5. **File picker ready to use:** `IFilePickerService` / `MauiFilePickerService` abstraction exists and is production-tested. Returns `Stream` for parsing. Static parser `VocabularyWord.ParseVocabularyWords()` exists but is NOT wired to repository/persistence — new service layer needed.

**Delivered:** `.squad/decisions/inbox/wash-import-scout-findings.md` for Zoe's architecture proposal.

---

## 2026-04-24 — Import Data Layer Scout (Multi-Agent Session)

Conducted data layer survey for new import feature. Identified YouTube pipeline as template, found file import UI/service gap, discovered dedup inconsistency, confirmed no schema changes needed.

**Key findings:**
- YouTube pipeline pattern in `VideoImportPipelineService` (dedup by TargetLanguageTerm)
- File import UI missing, but parser utility `VocabularyWord.ParseVocabularyWords()` exists
- Dedup inconsistency: case-sensitive in pipeline vs. case-insensitive in repo utilities
- MVP reuses all existing tables (LearningResource, VocabularyWord, ResourceVocabularyMapping)
- Migration gotcha: multi-TFM requires temporary single-TFM switch for `dotnet ef`

**Recommendations to Zoe:**
- Standardize dedup to case-insensitive trimmed
- New `VocabularyImportService` following YouTube pattern
- Optional `FileImport` entity for status tracking

**Coordinated with:** Zoe (architecture), River (AI), Kaylee (UI), Copilot

**Next:** Implementation uses findings for service + DB layer.


## Learnings

### 2026-05-30 — ContentImportService Skeleton + DI Registration

**Files Created:**
- `src/SentenceStudio.Shared/Services/ContentImportService.cs` — Interface `IContentImportService` + implementation class with DTOs for Wave 1 Track A MVP

**Files Modified:**
- `src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs` — Added scoped registration for `IContentImportService`

**DI Registration:**
- Registered as **scoped** in `CoreServiceExtensions.AddSentenceStudioCoreServices()` (line ~96)
- Matches `LearningResourceRepository` lifetime (singleton) but scoped is safer for transient operations
- Service requires `IServiceProvider` injection for DbContext scoping pattern

**Dedup Rule Applied:**
- Case-sensitive, whitespace-trimmed match on `TargetLanguageTerm` only
- Matches YouTube pipeline (`VideoImportPipelineService:368`) and Captain's ruling (2026-04-24)
- Three modes: Skip (default, safest), Update (dangerous, warns), ImportAll (creates duplicates)

**Transaction Pattern:**
1. Get or create target resource
2. Load existing mappings into HashSet to prevent duplicates
3. For each selected row:
   - Check existing word via `FirstOrDefaultAsync(w => w.TargetLanguageTerm == trimmedTarget)`
   - Apply dedup mode (Skip / Update / ImportAll)
   - Detach nav props for Update mode (prevents cascade insert errors)
   - Add word if new, update if Update mode, reuse if Skip mode
   - Create mapping only if not already in resource's mapping set
4. Update resource timestamp
5. Single `SaveChangesAsync` for entire transaction
6. Trigger sync (fire-and-forget)

**Key Patterns Followed:**
- `SaveResourceAsync` transaction pattern from `LearningResourceRepository` (detach nav props → dedup → save → create mappings → single SaveChanges)
- Scoped DbContext via `_serviceProvider.CreateScope()` (not constructor injection — allows multiple scopes)
- Defensive null-handling on all DTOs
- XML doc comments on all public surfaces
- Microsoft.Extensions.AI `[Description]` attributes on all DTO properties (per repo convention)

**MVP Scope Boundaries:**
- ParseContentAsync: Vocabulary only (Phrases/Transcript throw `NotSupportedException` with v2 TODO)
- Format detection: Stub (returns delimiter type for MVP, AI heuristics in Wave 2)
- Single-column translation: Stub (returns error for MVP, AI translation in Wave 2 per Captain ruling #3)
- Content type detection: Stub (returns explicit type for MVP, AI classifier in Wave 2)

**Future Wash Sessions Should Know:**
- CommitImportAsync body is **production-quality MVP** — full transaction, real dedup, real mapping creation
- ParseContentAsync is **Wave 2 placeholder** — format detection and AI fallback hooks are stubbed but API surface is locked
- No database migrations required (zero new tables per MVP plan)
- Dedup on `NativeLanguageTerm` is explicitly excluded (allows multiple English definitions for same Korean word)
- Smart resources (`IsSmartResource == true`) are never import targets (user-created resources only)


## Learnings

### 2026-05-30 — Wave 2 Content Import: Format Detection + AI Wiring

**Context:** Filled in the parsing pipeline for Wave 2 Track A — format detection, AI translation, AI free-text extraction

**Parser approach:**
- **Format detection order:** Explicit delimiter → JSON parse → delimiter sniffing (60% consistency threshold across first 10 lines) → free-text fallback
- **CSV quote handling:** Simple state machine (toggle inQuotes on `"`, split on `,` only when !inQuotes). Not full RFC 4180 but production-quality for MVP.
- **JSON property heuristics:** Try common names (target/targetLanguageTerm/korean/term, native/nativeLanguageTerm/english/translation/definition) — works for both object and array formats
- **Delimiter preference:** Tab → Pipe → Comma (comma is most ambiguous, prefer less common delimiters)

**AI integration:**
- **Template loading pattern:** `IFileSystemService.OpenAppPackageFileAsync("FreeTextToVocab.scriban-txt")` → StreamReader → Template.Parse → Render with anonymous object → AiService.SendPrompt<T>()
- **Single-column translation:** Batch all missing native terms, single AI call, map back via dictionary (TargetLanguageTerm key), mark with `IsAiTranslated = true`
- **Free-text extraction:** 50KB size cap (~12,500 tokens), confidence mapping (high→Ok, medium→Warning, low→Error), empty result gracefully handled
- **Error handling:** Catch AI exceptions at parse time, show user-friendly error row + retry suggestion (never crash the preview)

**Dedup audit findings:**
- **VideoImportPipelineService (line 368):** Case-sensitive, NO trim — vulnerable to whitespace duplicates
- **ContentImportService (Wave 1+2):** Case-sensitive, trimmed — CORRECT per Captain's ruling
- **GetWordByTargetTermAsync (line 50):** Case-sensitive, NO trim — same vulnerability, but zero call sites found
- **LearningResourceRepository (line 940):** Case-insensitive, trimmed — used for dual-key lookup (target+native), not dedup
- **Recommendation:** Audit only for Wave 2, no behavior changes. Separate PR needed to fix YouTube pipeline + merge existing whitespace duplicates.

**Library choices:**
- **No external CSV parser added** — hand-rolled quote-aware state machine is sufficient for MVP, avoids new dependency
- **System.Text.Json for JSON parsing** — already in use, JsonDocument.Parse() with try/catch for format detection
- **Scriban for templates** — existing pattern, reused from VideoImportPipelineService

**Build:** ✅ 0 errors, 729 pre-existing warnings. No new warnings from this code.

**Files modified:**
- `src/SentenceStudio.Shared/Services/ContentImportService.cs` (+467 lines: replaced ParseContentAsync stub, added 6 helper methods, added IsAiTranslated property to ImportRow DTO)

**Decision docs:**
- `.squad/decisions/inbox/wash-format-detector-ai-wiring.md` — Wave 2 implementation details, format detection strategy, AI wiring, dedup audit findings

- 2026-05-01: **IAiService Interface Extraction** — Unblocked Jayne's unit tests for ContentImportService by extracting IAiService interface from concrete AiService class. Implemented dual DI registration strategy (concrete + interface alias) to preserve existing consumers while enabling mockability for new code. Zero blast radius — only ContentImportService migrated. Build clean (UI + tests). Full details in `.squad/decisions/inbox/wash-iaiservice-extraction.md`.

---

## 2026-04-25 — Import Scope Correction + v1.1 Architecture (Team Update)

**Event:** Captain's process-correction round + Zoe's architecture spec completion  
**Status:** �� BLOCKED on captain-confirm-scope  

**What happened:**
- Captain identified process issue: Phrases/Transcripts/Auto-detect were silently moved to v2 without asking him by name. Scope corrected; all three are back in v1.1.
- Zoe completed architecture spec and **corrected Squad's Decision #1**: `LexicalUnitType` enum already exists (not a new enum needed). Only a backfill migration required (Unknown→Word).
- New scope flag from Zoe: free-text phrase extraction deferred to v1.2 (CSV + paired-line phrases stay in v1.1).

**For Wash specifically:**
- **Decision #1 (corrected):** Use existing `LexicalUnitType` enum, not a new EntryType enum. Backfill: set all Unknown→Word for existing rows.
- **Decision #2 (affirmed):** Transcript handling — store in `LearningResource.Transcript` + run `ExtractVocabularyFromTranscript`.
- **Implementation blocked** until Captain confirms. See `.squad/decisions.md` for full spec (section "Import Content — Scope Correction & Expansion" + "Import Content v1.1 Architecture").

**No action needed from you yet.** Read the decisions ledger when Captain unblocks. Zoe's spec has implementation order: River → Wash → Kaylee → Jayne.



---

## 2026-04-25 — v1.1 Data Import Backend Implementation

**Status:** DELIVERED — Migration + 3 ContentImportService branches + DTO updates.

**Deliverables:**
1. `SetDefaultLexicalUnitType` migration — Heuristic backfill (TRIM+space check → Phrase, else Word). Down() no-op. Both Postgres and SQLite variants.
2. ContentImportService Phrase branch — FreeTextToVocab routing, Words+Phrases per checkbox flags.
3. ContentImportService Transcript branch — LearningResource.Transcript storage + word-biased vocabulary extraction.
4. ContentImportService Auto-detect branch — 3-tier confidence gate, classification before DB persistence.
5. DTO updates: HarvestTranscript/HarvestPhrases/HarvestWords on request/commit DTOs. ImportRow gains LexicalUnitType. ContentImportPreview gains Classification.

**Edge cases resolved:** Zero-vocab = persist + warn. >30KB = reject (chunking v1.2).

---

## 2026-04-26 to 2026-04-27 — v1.1 Data Import Lockout & Resolution

**Status:** LOCKED OUT (scoped) → RESOLUTION DELIVERED BY ESCALATION

**Event:** Jayne's e2e run revealed 3 P1/P0 bugs in ContentImportService.cs (Wash's authored work). Under Reviewer Rejection Protocol, Wash was locked out and Simon (escalation specialist) was routed to fix.

**Lockout scope:** 3 specific bugs only (UserProfileId, Transcript, LexicalUnitType). No impact on prior v1.0 or other features.

**Resolution:**
- Simon fixed all 3 bugs (backend DTO + mapping discipline)
- Kaylee discovered + fixed frontend DTO mapping gap (same cycle)
- Jayne's retest + full sweep confirmed all bugs fixed, zero regressions
- **SHIP verdict: CLEARED** (2026-04-27, 10/10 scenarios PASS)

**Lesson for Wash:** UserProfileId scoping (bypass-repo writes need explicit ActiveUserId resolution) and transcript DTO carry-through (SourceText must round-trip via preview). Both now baked into Simon's documented learnings for future cycles.

**Status resolved:** Lockout was temporary and scoped. Feature shipped clean with all fixes verified.

