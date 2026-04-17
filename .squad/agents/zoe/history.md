# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-04-17: **Help Flyout Wiring Pattern** — Dynamic IHelpKit reflection to keep UI project portable (net10.0-browser, no MAUI refs). Runtime type detection via `Type.GetType()` + method invocation; graceful degrade if HelpKit absent (WebApp). Used in NavMenu.razor for both MAUI (Help visible) and WebApp (Help hidden).
- 2026-04-17: **HelpKit Alpha shipped** — library, RAG pipeline, storage, 3 samples, eval harness, CI, docs all delivered.

## Core Context (Summarized from Sessions)

**Architecture:**
- Multi-target csproj on Shared project for migrations: hand-write migrations + update Designer/Snapshot manually
- Post-deploy validation requires: revision health check (active = latest), indirect DB check (login test), 4-phase validation (Wash: infra + smoke + change-specific + regression)
- Phase 0-3 quiz behavior finalized: global streak-based mode selection, PendingRecognitionCheck flag, tiered rotation, cumulative session counters
- Cross-activity mastery pipeline via ExtractAndScoreVocabularyAsync (shared on VocabularyProgressService), dedup by DictionaryForm, scoring loop → probe collection AFTER
- Activity taxonomy: recognition vs production (Writing/Translation/Scene/Conversation production), DifficultyWeights established (VocabQuiz=1.0–2.5, Writing/Translation=1.5, Conversation=1.2)

**Database & Migrations:**
- All CoreSync-synced entities: string GUID PKs (ValueGeneratedNever), UserProfileId for multi-user isolation, singular table names
- Migrations: `dotnet ef migrations add <Name> --project src/SentenceStudio.Shared --startup-project src/SentenceStudio.Shared`
- NEVER delete data; fix migrations instead. Both API and WebApp call MigrateAsync() on startup.
- NarrativeJson added to DailyPlanCompletion for plan narrative storage

**Auth & Config:**
- JWT + refresh token flow: API endpoints in AuthEndpoints.cs (API), AccountEndpoints.cs (WebApp), JwtTokenService.cs
- Captain's preference: never show login unless explicitly logged out; mobile auth should keep people signed in weeks
- Entra ID support: Microsoft.Identity.Web v3.8.2, conditional via Auth:UseEntraId config flag, TenantContextMiddleware maps both claim sets
- Config: appsettings.json gitignored (local-only), appsettings.Production.json + appsettings.Development.json tracked
- Service URLs: localhost-only in appsettings.json, production URLs in appsettings.Production.json, EnvironmentBadge shows RED/ORANGE/GREEN

**Coding Standards:**
- MauiReactor: VStart() not Top(), VEnd() not Bottom(), HStart()/HEnd() not Start()/End()
- NEVER use emojis in UI — use Bootstrap icons (bi-*) or text
- All entities synced via CoreSync use string GUID PKs
- Database migrations MUST use `dotnet ef`, never raw SQL ALTER TABLE
- NEVER delete user data or database files
- Build with TFM: `dotnet build -f net10.0-maccatalyst`
- E2E testing mandatory for every feature/fix

**Activity Pages Pattern:**
- Structure: `activity-page-wrapper` → `PageHeader` → `activity-content` → `activity-input-bar`
- CRUD feedback: success/errors use toasts (auto-dismiss), destructive ops need Bootstrap modal confirmation BEFORE + toast AFTER
- Timer integration: ActivityTimerService.SaveProgressAsync → UpdatePlanItemProgressAsync (sole completion path, fires every minute + on Pause)
- GoBack() pattern: Pause() → StopSession() → NavigateTo("/") — no explicit signal, completion detected from accumulated time vs estimate

**Known Bugs & Open Items:**
- ProgressCacheService 5-minute TTL can expire during normal completion
- DeterministicPlanBuilder uses Guid.NewGuid() tiebreakers → non-deterministic plan generation
- VocabQuiz stale-progress: RecordPendingAttemptAsync doesn't write back returned VocabularyProgress to currentItem.Progress
- Translation.razor: ProgressService injected but GradeMe() uses ad-hoc prompt (fixed via GradeTranslation() switch)
- API lacks proper /health endpoint (currently use login as indirect check)

## Learnings

## Learnings

- 2026-04-17: HelpKit Alpha — public API frozen at 0.1.0-alpha, CI workflow + docs landed, supervised the fleet end-to-end.
- 2026-04-17: HelpKit status check — library is net10-multi-targeted and ready, SentenceStudio integration wired (UseHelpKit + background ingest + 11 help articles), but NO UI trigger exists yet. Three unpushed commits include the full Alpha build. All guard removal complete, builds should succeed. Alfred menu item and chat overlay are scaffolding-ready but need UI wiring. Captain will need to add a help button somewhere in the Blazor UI that injects IHelpKit and calls ShowAsync().

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- `azd deploy` exit code 0 means the UPLOAD worked (images pushed, revisions created). It says NOTHING about whether the system actually works. Azure Container Apps can auto-route traffic to old healthy revisions while the new one crash-loops silently.
- The API has NO explicit `/health` endpoint — the deploy runbook previously referenced one that doesn't exist. Use POST `/api/auth/login` with bad creds as an indirect health check: 400/401 = alive + DB reachable, 500/502/503 = broken.
- When Azure Container Apps detects a failing new revision, it keeps traffic on the previous revision. This means "the app works" does NOT mean "the new code is running." Always verify the ACTIVE revision is the LATEST revision.
- Deployed services are: api, webapp, marketing, workers. All 4 must be checked for revision health after deploy.
- Post-deploy validation script lives at `scripts/post-deploy-validate.sh` — mirrors the pattern of `scripts/pre-deploy-check.sh`.

- HelpKit Alpha (0.1.0) flyout wiring (2026-04-17): Added Help menu item to SentenceStudio.UI/Layout/NavMenu.razor (MAUI Hybrid sidebar). Used dynamic type resolution (Type.GetType + reflection) to keep the UI project portable as a browser-only Razor class library — it can't reference Plugin.Maui.HelpKit directly without breaking the WebApp build. The Help item appears ONLY in MAUI apps (iOS/MacCatalyst) where HelpKit is registered via UseHelpKit(); WebApp doesn't have HelpKit in DI and won't see the menu item. Localization keys ("Help" / "도움말") added to AppResources en + ko-KR. IHelpKit.ShowAsync() invoked via reflection when the button is tapped, then closes the mobile offcanvas menu. No additional icon was needed — used Bootstrap's bi-question-circle directly in the markup.

- Phase 0+1 scoring engine review: LostKnownThisSession detection must happen AFTER RecordAttemptAsync (deferred), never eagerly in the immediate answer handler. Eager detection based on MasteredAt!=null fires even when temporal weighting keeps IsKnown intact, causing incorrect 14-day re-qualification.
- When reviewing IsKnown loss detection, always trace the full deferred-persistence flow: immediate state changes → RecordPendingAttemptAsync → snapshot wasKnownBefore → RecordAttemptAsync → compare. The timing of flag-setting relative to DB recording is critical.
- Spec pseudocode may contain casts (e.g., `(int)`) from before a type change (int→float). When the type has changed, the cast should be dropped. Review spec code as guidance, not copy-paste.

- Writing.razor is the ONLY activity that already does multi-word vocabulary scoring via `VocabularyAnalysis` from `GradeResponse`. It matches each word's `DictionaryForm` against the user's tracked vocabulary and calls `RecordAttemptAsync` per word. This pattern must be extracted into a shared service method for reuse by Translation, Scene, and Conversation.
- Translation.razor has a BUG: `VocabularyProgressService` is injected but `RecordAttemptAsync` is never called. The grading prompt also doesn't request `VocabularyAnalysis`. Both need fixing.
- Conversation and Scene have AI grading but return NO `VocabularyAnalysis` — their grading services need extension to return per-word correctness data before vocabulary progress can be recorded.
- Reading, Shadowing, VideoWatching, HowDoYouSay are passive activities — no production output to score for mastery.
- MinimalPairs uses a completely separate progress system (`MinimalPairSessionRepository`) — not connected to `VocabularyProgressService`.
- Current DifficultyWeight values across activities: VocabMatching=0.8, WordAssociation=1.0, VocabQuiz MC=1.0, Cloze=1.2, VocabQuiz Text=1.5, Writing=1.0, VocabQuiz Sentence=2.5.
- Cross-activity mastery spec recommended as Option B: new `cross-activity-mastery.md` referencing quiz spec for shared formulas. Quiz spec (963 lines) is too dense to absorb all activities.
- `TeacherSvc.GradeTranslation()` (line 138) already exists and its Scriban template already requests `vocabulary_analysis`. Translation.razor should use this instead of its ad-hoc prompt — NOT `GradeSentence()`.
- Passive exposure (Reading word lookups) should update `LastExposedAt`, NOT `LastPracticedAt`. The latter is used by SRS scheduling and must not be contaminated by passive encounters.
- `HandleVerificationProbeResultAsync` directly overwrites mastery/streak fields (not additive). Must be called AFTER the scoring loop in `ExtractAndScoreVocabularyAsync`, not interleaved with `RecordAttemptAsync` calls, to avoid stale-state reads.

- DifficultyWeight is FUNCTIONAL, not decorative — it directly multiplies the streak increment (MC=1.0, Text=1.5, Sentence=2.5). CurrentStreak must be float to support this.
- Tier 1 rotation (high mastery) requires BOTH text production AND cleared PendingRecognitionCheck — a mastered word returning via SRS cannot rotate out after a single MC answer if it was wrong first.
- Recovery-aware mastery formula adds +0.02 per correct answer when streak hasn't caught up to mastery. Eliminates the flat plateau from simple Math.Max. The boost stops automatically once streak >= mastery.
- DueOnly filter applies ONCE at session start, never re-applied between rounds. Rotation controlled exclusively by mastery/tiered logic.
- IsKnown re-qualification after losing it gets 14-day review interval (not 60) — tracked via LostKnownThisSession session flag.
- Words never repeat within a round — rounds naturally shrink as words rotate out, with no minimum round size.

- PendingRecognitionCheck OVERRIDES lifetime mode selection — it's Priority 1 in the unified mode algorithm. Without this, established words with preserved streaks skip the recognition check after a wrong text answer.
- Session counters (SessionMCCorrect, SessionTextCorrect, SessionCorrectCount) are CUMULATIVE, not consecutive — they never reset on wrong answers. This is a fundamental semantic change from the old QuizRecognitionStreak/QuizProductionStreak which were consecutive and reset on wrong.
- Cross-activity behavior: Global progress (CurrentStreak, MasteryScore) is shared across ALL activities, but session-local fields (PendingRecognitionCheck, SessionCorrectCount) are VocabQuiz-scoped. Activity-switching legitimately resets session-local state by design.
- Spec heading levels matter for section hierarchy — a `##` subsection vs `###` changes which sections appear as siblings vs children. Always verify heading levels match the intended numbering scheme.
- Quiz mode selection (MC vs Text) must be based on LIFETIME `VocabularyProgress.CurrentStreak`, not session-local `QuizRecognitionStreak`. Captain's correction: 3 consecutive correct across ANY sessions = Text mode.
- Wrong text answer demotion should be gentle: exactly 1 MC check turn, then back to Text. NOT a full reset requiring 3 more MC correct. Needs `PendingRecognitionCheck` flag mechanism.
- PendingRecognitionCheck flag clears ONLY on correct MC answer — never on wrong. Captain's principle: "Incorrect responses should never result in a promotion."
- Mastered words must be removed IMMEDIATELY mid-round, not deferred to round boundaries. Check mastery after each answer recording.
- Session-local streaks (`QuizRecognitionStreak`, `QuizProductionStreak`) are for ROTATION-OUT logic only, not for mode selection. Mode is driven by global persisted progress.
- QuizRecognitionStreak is potentially redundant — Captain asked whether it should be removed. Recommended tiered rotation based on global progress instead. Awaiting Captain approval.
- Captain APPROVED tiered rotation model (R3): replaces QuizRecognitionStreak/QuizProductionStreak with SessionCorrectCount + tiered requirements based on global mastery. High-mastery = 1 correct, mid = 2, low = 3+3.
- Deferred persistence pattern (answer → review → advance → DB write) is sound, but the returned VocabularyProgress must be written back to currentItem.Progress immediately for Learning Details to reflect current state.
- Sentence production DifficultyWeight is 2.5f (higher than text entry's 1.5f) — Captain confirmed River's implementation at 2.5f.
- Temporal weighting APPROVED (R3): wrong-answer penalty scales by track record (0.6–0.92), partial streak preservation (0–50%), and correct-answer mastery floor (Math.Max). Fixes the double-whammy bug where correct answer after wrong makes mastery worse.

- YouTubeImportService already exists using YoutubeExplode (no API key needed) — handles transcript fetch, audio extraction, and caption track discovery for any public video
- VideoWatchingService handles YouTube URL parsing and embed generation for LearningResources
- TranscriptFormattingService + TranscriptSentenceExtractor provide full transcript cleanup and sentence extraction pipeline
- Workers project (SentenceStudio.Workers) is a stub BackgroundService — ready for background job expansion (polling, processing queues)
- No external OAuth providers exist yet (Google, Microsoft, etc.) — only ASP.NET Identity with email/password + JWT
- YouTube Data API v3 daily quota is 10,000 units; subscriptions.list costs 100 units, playlistItems.list costs 1 unit — server-side shared polling is essential at scale
- YoutubeExplode is a scraping library (not official API) — version-pin and handle breakage gracefully
- Google OAuth for YouTube scopes should be handled server-side (API project) to avoid client secrets in mobile apps

- Project uses MauiReactor for native pages: `VStart()` not `Top()`, `VEnd()` not `Bottom()`, `HStart()`/`HEnd()` not `Start()`/`End()`
- NEVER use emojis in UI — use Bootstrap icons (bi-*) or text. Non-negotiable.
- The user goes by "Captain" and prefers pirate talk
- All entities synced via CoreSync use string GUID PKs
- Database migrations MUST use `dotnet ef`, never raw SQL ALTER TABLE
- NEVER delete user data or database files
- Build with TFM: `dotnet build -f net10.0-maccatalyst`
- E2E testing is mandatory for every feature/fix
- Activities follow pattern: `activity-page-wrapper` → `PageHeader` → `activity-content` → `activity-input-bar`
- CRUD feedback pattern: Success/errors use toasts (auto-dismiss), destructive ops require Bootstrap modal confirmation BEFORE + toast AFTER
- Auth flow complete: IdentityAuthService handles JWT + refresh tokens via API, SecureStorage persists on iOS, token auto-refresh on expiry via 60s buffer
- Token lifespan: JWT ~15 min, refresh tokens 7 days; /api/auth endpoints: register, login, refresh, confirm-email, forgot-password, reset-password, delete (protected)

- Phase 3 review (Scene+Conversation scoring): GradeMyDescription.scriban-txt already had vocabulary_analysis at line 22 — no template change was needed for Scene. Only the Conversation templates needed JSON schema additions.
- Both ContinueConversation templates (default + scenario) must include the full JSON output schema with vocabulary_analysis for the AI to return parseable structured data. Free-form prompts without explicit JSON format do not reliably return vocabulary analysis.
- `ExtractAndScoreVocabularyAsync` shared method signature: (List<VocabularyAnalysis>?, List<VocabularyWord>, string userId, string activity, float difficultyWeight, float? penaltyOverride = null). All dedup + verification probe ordering lives inside — callers only pass parameters.

## Work Sessions


### 2025-07-27 — Post-Deploy Validation Spec & Automation

**Status:** Complete
**Output:** `docs/specs/post-deploy-validation.md`, `scripts/post-deploy-validate.sh`, `.squad/decisions/inbox/zoe-post-deploy-validation.md`

**What:**
Designed comprehensive post-deploy validation after Captain identified critical process failure: deploys were declared successful based on `azd deploy` exit code 0 while the API was crash-looping for 15+ minutes undetected. Root cause: traffic auto-routed to old revision, making login appear to work.

**Deliverables:**
1. Full spec (4-phase validation: infrastructure, smoke test, change-specific, regression)
2. Executable bash script (`post-deploy-validate.sh`) covering Phases 1, 2, and 4
3. Updated deploy runbook with Step 3 (validation) and Step 4 (change-specific)
4. Updated Publish Workflow in squad.agent.md — validation is mandatory before iOS build
5. Decision document in inbox

**Key Design Decisions:**
- Indirect DB health check via login endpoint (no explicit /health endpoint exists)
- 30-second startup wait before checks (container pull + migration time)
- Active revision = latest revision check catches the specific bug from the incident
- Phase 3 is intentionally manual — deployer must think about what changed and verify it

**Open Items:**
- Need dedicated deploy-test account for Phase 2 auth tests
- API should add a proper /health endpoint (future work)
- Consider `azure.yaml` post-deploy hook for automatic validation

### 2025-07-25 — Cross-Activity Mastery Spec (Option B)

**Status:** Complete (spec written + R2 revisions applied, awaiting implementation)
**Output:** `docs/specs/cross-activity-mastery.md`, `.squad/decisions/inbox/zoe-cross-activity-spec.md`, `.squad/decisions/inbox/zoe-cross-activity-r2.md`

**What:**
Surveyed all 15 activity pages, classified each by recognition/production and single/multi-word, then wrote the full cross-activity mastery spec per Captain's directive. Applied R2 revisions based on architect/skeptic review feedback + Captain's answers. Key deliverables:

- Activity taxonomy with DifficultyWeight assignments (Captain approved: Writing/Translation/Scene=1.5, Conversation=1.2, passive=0)
- Shared `ExtractAndScoreVocabularyAsync` pipeline design (extracted from Writing's existing inline pattern)
- `PenaltyOverride` mechanism on VocabularyAttempt (Conversation gets 0.8x instead of standard 0.6x)
- `RecordPassiveExposureAsync` for Reading word lookups (analytics only, no mastery change)
- Translation.razor bug documented (ProgressService injected but never called)
- 5-phase implementation sequence ordered by risk

**Captain's Design Decisions:**
1. Translation IS production (native→target), DW=1.5
2. Conversation penalty softer: 0.8x (chat context)
3. Reading lookups = passive exposure (log only)
4. Writing DW upgraded 1.0→1.5

**R2 Revisions (Captain's answers + mechanical fixes):**
1. Processing order is non-issue (independent VocabularyProgress records)
2. SRS reset same everywhere (ReviewInterval=1 on wrong, no special softening)
3. Deduplicate via `.DistinctBy(v => v.DictionaryForm)` before scoring loop
4. Use `GradeTranslation()` not `GradeSentence()` for Translation (method already exists)
5. [NOT YET IMPLEMENTED] markers for R5 quiz spec formulas
6. Verification probe separation (handle AFTER loop, not inside)
7. Conversation template JSON format prerequisite note
8. `LastExposedAt` replaces `LastPracticedAt` for passive exposure

**Learnings:**
- Writing.razor is the only existing multi-word scoring implementation — its VocabularyAnalysis matching loop is the basis for the shared pipeline
- Translation has a recording bug — ProgressService wired but never called, must be fixed
- `TeacherSvc.GradeTranslation()` already exists with vocabulary_analysis in its template — simpler fix than initially expected
- Conversation and Scene need AI grading template extensions to return VocabularyAnalysis
- No formula changes needed in RecordAttemptAsync (only PenaltyOverride addition)

### 2026-03-13 — GitHub Issues Created for Azure + Entra ID Plan

**Status:** Complete  
**Issues Created:** 27 issues (#39–#65)  
**Dependencies:** All cross-referenced with dependency links  

**Cross-Team Impact:**
- **Kaylee:** 8 issues assigned (#44–45, #56–59, #60)
- **Captain:** 1 issue assigned (#42)

## Core Context (Current)

**Role:** Lead / Architecture & Infrastructure  
**Focus Areas:** Aspire, EF Core, databases, architecture decisions, team coordination

**Current Phase:**
- Phase 2 (Secrets) & Phase 1 (Auth): Complete
- Phase 3-5 active: Infrastructure (Postgres migration), CI/deploy, hardening

**Recent Completions (2026-03-13 to 2026-03-20):**
- Architecture plan for Azure deployment + Entra ID (5-phase roadmap)
- 27 GitHub issues created (decomposed plan into actionable work, Dependencies linked)
- Phase 1 & 2 auth architecture decisions (WebApp OIDC, MAUI MSAL, Bearer API)
- Phase 2 completion (user-secrets workflow, security headers, HTTPS)
- Getting-started dashboard for new users (feature/getting-started-dashboard, commit 0636f06, 190 lines)
  - Detection: lightweight queries for resources/vocabulary/skills
  - Quick Start: Creates skill profile, 20 vocab words, pre-built resource
  - Styling: Bootstrap icons only (no emojis)

**Key Tech Learnings:**
- CoreSync SQLite→PostgreSQL migration is critical path (Phase 3.7, XL complexity)
- Aspire.StackExchange.Redis provides distributed token cache (match preview package versions)
- LocalDev auth: DevAuthHandler as fallback when Entra ID is disabled
- New-user onboarding: detect empty state (resources/vocab/skills), show guided flow, transition in-place (no redirect)

**Blockers:** None current

**Next:** Phase 3 infrastructure work (Postgres setup, CoreSync migration) and Phase 4 CI/deploy pipeline

### 2025-07-22 — Feedback Feature Architecture (#139)

**Status:** Architecture complete, ready for implementation  
**Output:** `.squad/decisions/inbox/zoe-feedback-architecture.md`

**What:**
Designed the full architecture for user feedback submission as GitHub issues. Two-endpoint flow (preview + submit) with AI enrichment via IChatClient and server-side GitHub issue creation via REST API.

**Key Architecture Decisions:**
- **Server-side GitHub PAT** — kept secure in Aspire secrets, never exposed to clients
- **Two endpoints with preview token** — POST `/api/v1/feedback/preview` returns AI-enriched draft + HMAC token; POST `/api/v1/feedback/submit` accepts token only. Prevents tampering between preview and creation.
- **Structured AI output + deterministic markdown** — AI returns typed `FeedbackDraft` object, server renders GitHub markdown and whitelists labels. Prevents hallucinated structure.
- **AI fallback** — 15s timeout, raw submission allowed if AI fails. Never blocks the user.
- **Auth required** — matches existing endpoint patterns, prevents spam
- **Raw HttpClient over Octokit** — single POST call doesn't justify a library dependency
- **Client metadata** — version, platform, route, timestamp captured automatically in collapsible details section

**Work Breakdown:**
- Wash: Backend (AppHost secret, Contracts DTOs, FeedbackEndpoints.cs, GitHub API integration, HMAC token)
- Kaylee: UI (NavMenu + NavigationMemoryService update, Feedback.razor page)
- River: AI prompt design for feedback enrichment

**Learnings:**
- NavigationMemoryService.Sections array must be updated when adding any new nav item — otherwise active-state detection breaks
- Preview token pattern (HMAC-signed, short-lived) is a good fit for any two-step confirm flow — reusable for future features
- GitHub REST API v3 for issue creation is trivial (single POST with title + body + labels) — no need for Octokit for simple operations

### 2026-03-18 — Getting Started Dashboard Experience

**Status:** Complete (feature branch `feature/getting-started-dashboard`)  
**File Changed:** `src/SentenceStudio.UI/Pages/Index.razor`

**What:**
Added a getting-started flow to the Dashboard for new users who have no learning resources, no vocabulary, or no skill profile. When any of these are missing, the normal dashboard is replaced with a welcoming two-option card layout:

1. **Quick Start** — Creates a "Korean Starter Pack" resource with 20 common Korean vocabulary words and a "Korean Basics" skill profile, then transitions to the normal dashboard.
2. **Create Your Own** — Links to `/resources/add` for manual resource creation.

**Architecture Decisions:**
- Check runs in `OnInitializedAsync` via lightweight queries (no eager loading of vocab on resources)
- `isNewUser` flag gates the entire dashboard markup — no partial empty-state handling
- Starter resource uses `SaveResourceAsync` which handles vocab association through skip navigation
- Skill profile created only if none exist — won't duplicate on retry
- After creation, the page transitions in-place (no redirect) by flipping `isNewUser = false` and loading dashboard data

**Key Learnings:**
- `GetAllResourcesLightweightAsync()` is the fast path for existence checks (no Include)
- `SaveResourceAsync` handles both new-resource creation and vocabulary word association in a single call
- `SaveWordAsync` does upsert by checking DB existence — safe for retries
- SkillProfile model defaults `Language = "Korean"` which aligns with Captain's target language

---

## 2026-03-20 — Team Sync: Kaylee's File-Import Feature

**Impact on Zoe's Work:**
- Kaylee implemented file-based vocabulary import (ResourceAdd/ResourceEdit, feature/file-vocab-import)
- Uses Blazor `InputFile` component for cross-platform (web + MAUI Blazor Hybrid) file picking
- Commit: fe312d6 | 183 lines | Build: clean
- No changes required to Zoe's getting-started flow — the file-import feature is orthogonal

**Cross-Agent Notes:**
- When users click "Create Your Own" in Zoe's getting-started flow, they land on Kaylee's ResourceAdd page which now has file-import capability
- Both features are non-blocking and can be merged in any order

---

## 2026-07-22 — YouTube Integration Feasibility Research

**Status:** Complete (research only — no code changes)  
**Output:** `.squad/decisions/inbox/zoe-youtube-feasibility.md`

**What:**
Captain asked if YouTube subscription monitoring + auto-import of transcripts/vocabulary is feasible. Researched YouTube Data API v3, OAuth flows, and mapped it against the existing codebase.

**Key Findings:**
- **Feasible.** 2-3 sprint effort, phased approach recommended.
- YouTubeImportService + YoutubeExplode already solve transcript extraction — no new work needed there
- YouTube Data API v3 can list subscriptions (100 quota units) and channel videos (1 unit each)
- Google OAuth is the critical path — no external OAuth exists in the project today
- Recommended server-side OAuth (API project handles Google token, returns JWT to clients)
- Workers project is a ready stub for polling + transcript processing background jobs
- AI vocab extraction pipeline already exists — just needs wiring

**Recommended Phases:**
1. Manual YouTube URL import with auto-vocab (weekend project, no OAuth)
2. Google OAuth + subscription picker (1-2 sprints, the hard part)
3. Background polling worker + auto-import (1 sprint, straightforward)

### 2026-07-22 — YouTube Import Architecture + Issues Created

**Status:** Complete (architecture + 5 GitHub issues)
**Output:** `.squad/decisions/inbox/zoe-youtube-import-architecture.md`
**Issues:** #126–#130

**What:**
Designed full architecture for YouTube channel monitoring + video import feature. Created 5 decomposed GitHub issues with dependency chains.

**Issues Created:**
- **#126** Data model + migration (MonitoredChannel, VideoImport) — Wash
- **#127** AI pipeline — vocab extraction from transcripts — River
- **#128** Background workers (VideoImportWorker, ChannelPollingWorker) — Wash
- **#129** UI redesign — tabbed Import page + channel management — Kaylee
- **#130** Long-running task UX — polling, status display, retry — Kaylee + Wash

**Key Architecture Decisions:**
- **Polling over SignalR** for long-running task UX — Blazor Hybrid on mobile kills WebSocket connections on backgrounding. Polling (5s on Import page) is simpler and equally effective for 15-60s operations.
- **Job queue pattern** — User submits → VideoImport record created (Queued) → Worker processes in background → UI polls for status. Works on both MAUI and web.
- **Simplified single-video import** — Reduced from 4-step inline editor to 1-click "Import" button. Transcript editing moves to ResourceEdit page after completion.
- **No Google OAuth needed** — YoutubeExplode handles everything via scraping. Channel URLs pasted directly by user.
- **Two separate BackgroundServices** — VideoImportWorker (processes queue) and ChannelPollingWorker (discovers new videos). Separation of concerns, independent scaling.

**Learnings:**
- Import pipeline takes 15-60 seconds (transcript fetch + AI polish + vocab extraction) — too long for synchronous UI, too short to justify SignalR complexity
- YoutubeExplode can list channel uploads via `Channels.GetUploadsAsync()` — no API key needed
- Mobile app backgrounding is the key constraint for UX design — anything that relies on persistent connections fails

- Phase 2 review (c806a96): APPROVED. ExtractAndScoreVocabularyAsync dedup/probe-separation is clean. PenaltyOverride wired correctly into RecordAttemptAsync. Migration filenames don't match [Migration] attribute IDs (cosmetic). ExposureCount increment is a useful additive beyond the spec sample.

## Learnings

- **PRODUCTION DATA LOSS (2025-07-25):** `aspire deploy` recreated the Postgres container WITHOUT its Azure File share volume mount. `.WithDataVolume()` does NOT automatically translate to ACA volume mounts — the `PublishAsAzureContainerApp` callback was added AFTER the incident but there was no backup to recover from. All user data was permanently lost.
- **Deploy tool mixing is dangerous:** `azd deploy` and `aspire deploy` manage Azure resources through different Bicep pipelines. Using both on the same infrastructure can cause one tool to silently overwrite stateful configuration set by the other.
- **Advisory checklists are worthless without enforcement:** The runbook had pre-deploy checks but they were presented as suggestions. They needed to be mandatory with explicit pass/fail criteria and hard-stop language.
- **Custom instructions must cover production, not just local dev:** The "Data Preservation Rules" had five rules, all about local development (don't delete SQLite files, don't uninstall apps). Zero rules about production deploys, backups, or volume mounts. This gap has been closed.
- **Containerized DB + file share is architecturally fragile:** Any tool that recreates the container risks losing the volume mount. Azure PostgreSQL Flexible Server eliminates this entire vulnerability class. Migration is ~1 day of work, ~$17/month. Should be done this week.
- **The `db` container is the ONLY stateful container at risk:** Redis has no volume (cache, acceptable to lose). Azure Storage is a managed service. All other containers are stateless .NET projects.

### 2025-07-25 — Production Data Safety Governance (P0)

**Status:** Decision enacted, runbook updated, migration plan written  
**Output:** `.squad/decisions/inbox/zoe-production-data-safety.md`, updated `docs/deploy-runbook.md`

**What:**
Following production data loss caused by `aspire deploy` dropping the Postgres container's Azure File share volume mount, conducted a full audit of existing data safety governance and created comprehensive, enforceable production data safety rules. Six rules enacted: mandatory backup, no mixing deploy tools, mandatory pre-deploy checks, mandatory post-deploy verification, managed DB migration plan, stateful resource audit.

**Key Findings:**
- Custom instructions "Data Preservation Rules" covered ONLY local dev — zero production coverage
- No decision in decisions.md addressed production data safety
- The deploy runbook had advisory checks with no enforcement language
- The `PublishAsAzureContainerApp` fix in AppHost.cs was applied AFTER data loss, not before
- Only the `db` container is at risk — Redis is cache-only, Azure Storage is managed

**Deliverables:**
1. Decision document: `.squad/decisions/inbox/zoe-production-data-safety.md` — P0 priority, six enforceable rules
2. Runbook overhaul: `docs/deploy-runbook.md` — mandatory checklist with backup, verification, and hard-stop language
3. Custom instructions recommendation: expanded "Data Preservation Rules" covering production deploys
4. Migration plan: Azure PostgreSQL Flexible Server — eliminates the vulnerability class entirely (~$17/month, ~1 day of work)
5. Stateful resource audit: only `db` is at risk, Redis and Azure Storage are safe


### 2025-07-25 — Cross-Activity Mastery Spec R5 & R2 Revisions Merged (Complete)

**Status:** Spec frozen, awaiting implementation  
**Specs:** `docs/specs/quiz-learning-journey.md`, `docs/specs/cross-activity-mastery.md`

**What Happened:**

Merged Captain's 6 design decisions (from Jayne's skeptic review answers) into R5 quiz spec, plus merged R2 revisions for cross-activity spec. Both specs now stable and ready for implementation.

**R5 Quiz Spec — 6 Captain Decisions Integrated:**

1. **DifficultyWeight accelerates mastery** — Streak increment now multiplied by DifficultyWeight (MC=1.0, Text=1.5, Sentence=2.5). CurrentStreak changed from int to float to support fractional increments.

2. **Tier 1 rotation requires text + cleared recognition** — High mastery (>=0.80) now requires `SessionTextCorrect >= 1 AND PendingRecognitionCheck == false`. Mastered word returning after months must re-demonstrate both recognition AND production before rotating out.

3. **No repeat within a round** — Added explicit rule: "A word is NEVER presented twice in a round." Rounds naturally shrink as words rotate out with no minimum round size.

4. **Recovery-aware mastery formula (no plateau)** — Replaced simple `Math.Max(streakScore, MasteryScore)` with recovery formula that adds `+0.02` per correct during recovery. Eliminates flat period where correct answers showed no visible mastery progress.

5. **DueOnly filter applies at session start ONLY** — Removed "re-apply between rounds" rule. DueOnly applies once at initial word selection; words that become not-due mid-session remain in batch pool. Rotation controlled exclusively by mastery/tiered logic.

6. **IsKnown re-qualification gets 14-day review interval** — When word loses IsKnown status (wrong answer) and re-qualifies, ReviewInterval = 14 days (not 60). New `LostKnownThisSession` flag on session counter model for detection.

**R2 Cross-Activity Mastery Revisions — Captain Decisions + Mechanical Fixes:**

**Captain's R2 Decisions:**
1. Processing order is non-issue — each word has independent `VocabularyProgress` record
2. SRS reset same everywhere — ReviewInterval=1 on wrong (no special softening for Conversation)
3. Dedup before scoring — use `.DistinctBy(v => v.DictionaryForm)`, first occurrence wins

**Mechanical Fixes Applied:**
4. GradeTranslation, not GradeSentence — Translation.razor should use `TeacherSvc.GradeTranslation()` which already has vocabulary_analysis in template
5. [NOT YET IMPLEMENTED] markers added — R5 quiz spec formulas (DifficultyWeight streak acceleration, temporal weighting, recovery boost, CurrentStreak as float) approved but not yet in code
6. Section 3.6 dedup row updated — Points to `.DistinctBy()` in step 2 of section 3.4
7. Conversation JSON format prerequisite — ContinueConversation templates need proper JSON output format before vocabulary_analysis can be added reliably
8. Verification probe separation — `HandleVerificationProbeResultAsync` must NOT be called inside scoring loop; collect probes during loop, fire after
9. LastExposedAt replaces LastPracticedAt for passive exposure — New `DateTime? LastExposedAt` field on `VocabularyProgress`

**Impact on Implementation:**
- R5 quiz spec is stable (Captain approved all 6 decisions)
- R2 cross-activity spec is stable (Captain approved fixes, no formula changes)
- Implementation work items tracked via section 6 discrepancy tables
- Key new work: CurrentStreak int→float, recovery boost formula, tier 1 rotation logic, LostKnownThisSession tracking, 14-day re-qualification path

**Files Updated:**
- `docs/specs/quiz-learning-journey.md` — R5 complete
- `docs/specs/cross-activity-mastery.md` — R2 complete
- Sections 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 across both specs

**Next:** Implementation phase — prioritize by risk and dependency order specified in section 6 of both specs.


---

## Session: 2026-04-11 - Quiz Vocabulary Decoupling Architecture Review

**Task:** Investigate why Daily Plan Quiz shows "no vocabulary loaded" despite Insights panel showing 497 words due, and propose architectural fix.

**Context:** Captain observed that the Quiz activity (VocabularyReview) on the Daily Plan was showing "All vocabulary in this resource are mastered" even though the Insights panel on the same plan item reported 497 words due, 12 in review, 8 new. Captain's product stance is clear: "Words to be studied are chosen first. For a quiz, it's all about the vocabulary. I don't see the point of a learning resource driving the Quiz."

**Analysis Performed:**
1. Categorized all activity types into vocabulary-driven vs resource-driven taxonomies
2. Traced code paths: DeterministicPlanBuilder (plan generation), VocabQuiz.razor (loader), ProgressService (plan creation), and Insights panel rendering
3. Identified root cause: VocabularyReview is assigned a ResourceId in plan generation, and VocabQuiz loader filters to only that resource's vocabulary, creating a mismatch between global insights (all due words) and scoped quiz pool (one resource's words)

**Key Findings:**
- VocabularyReview and VocabularyGame are vocabulary-driven activities (SRS-based word selection)
- Reading, Listening, VideoWatching, Shadowing are resource-driven activities (media artifact required)
- Translation, Cloze, Writing are hybrid but lean resource-driven (need source content)
- The bug occurs when DeterministicPlanBuilder assigns vocabReview.ResourceId (line 458), which VocabQuiz treats as a hard filter (lines 637-651)
- Insights panel computes stats from full due pool (VocabInsight from DueWords), creating the disconnect

**Recommended Fix: Option A (Clean Decoupling)**
- Remove ResourceId from VocabularyReview plan items entirely
- VocabQuiz always loads from full user vocabulary pool (GetAllVocabularyWordsWithResourcesAsync)
- SRS filtering (DueOnly, mastery, grace period) already handled by progress service
- No migration needed (plan generation is ephemeral, completion records allow NULL ResourceId)
- Aligns with Captain's product vision: vocabulary-driven activities should not be gated by resource state

**Alternative Options Evaluated:**
- Option B (soft hint with fallback): More complex, misleading UX
- Option C (separate model types): Too invasive for this fix
- Option D (fix fallback logic): Fragile, doesn't address conceptual coupling

**Deliverables:**
- Architecture recommendation document: .squad/decisions/inbox/zoe-quiz-vocab-decoupling.md
- Activity taxonomy (vocabulary-driven vs resource-driven)
- Root-cause explanation (1-2 sentences referencing Wash's parallel trace)
- Implementation approach with code locations
- Backwards compatibility analysis
- Open questions for Captain (narrative display, contextual learning value, completion tracking)

**Impact:**
- Implementation effort: 3 edits (DeterministicPlanBuilder.cs, VocabQuiz.razor, narrative builder)
- No schema changes or migrations required
- Low risk, high clarity gain
- Verification via e2e-testing skill to confirm Quiz loads words when insights show due count > 0

**Learning:**
This investigation reinforced the importance of separating vocabulary-driven activities (where words are the unit of work) from resource-driven activities (where media artifacts are the unit of work). The coupling between VocabularyReview and LearningResource was a conceptual mismatch that manifested as a UX bug. Clean architectural boundaries prevent such issues.

- Activity taxonomy decision (2026-04-17): Categorized as vocabulary-driven (VocabularyReview, VocabularyGame, Writing, Translation, Cloze) vs resource-driven (Reading, Listening, VideoWatching, Shadowing, SceneDescription). VocabularyReview decoupled from LearningResource entirely — Option A (Clean Decoupling) adopted and shipped.

- Plugin.Maui.HelpKit architecture decision (2026-07-25): Designed AI-assisted in-app help NuGet library using BlazorWebView overlay (modal ContentPage, not popup), Microsoft.Extensions.VectorData + sqlite-vec for local RAG, and convention-over-config API (`AddHelpKit()` extension). Key insight: BlazorWebView works in ANY MAUI host (MauiReactor, XAML, C# Markup) — library adds it, host doesn't need to be Blazor Hybrid. Highest risk: sqlite-vec native binary bundling across 4 platforms. Alpha scope: conversation only, no tours/tooltips.

## 2026-04-17 — Plugin.Maui.HelpKit Alpha Scope Locked

Captain locked all 8 open questions from HelpKit plan v2:
1. **UI:** Native MAUI chat (CollectionView + streaming) PRIMARY; BlazorWebView deferred
2. **Incubation:** `lib/Plugin.Maui.HelpKit/` in SentenceStudio; extract via `git subtree split` at Alpha close
3. **Storage:** Microsoft.Extensions.VectorData in-memory + JSON; sqlite-vec deferred to v1
4. **License:** MIT
5. **AI provider:** Host app brings IChatClient + IEmbeddingGenerator; HelpKit brings nothing
6. **Scanner:** Stub scanner shipped in Alpha (non-AI); AI-enriched stays Beta
7. **TFMs:** net11.0-* MAUI targets primary; net10 multi-target possible at Alpha close if demand
8. **Rate limit:** 10 q/min default, configurable

SPIKE-1 (native-first) and SPIKE-2 (presenter abstraction) now unblocked awaiting Captain go-ahead. Plan v2 needs net11 TFM + "app owns the model" framing incorporated.


---

## Session: 2026-04-17 — Plugin.Maui.HelpKit Wave 1 Scaffold + Public API

**Task:** Scaffold `lib/Plugin.Maui.HelpKit/` and freeze the public API surface for Wave 2 crew (Wash, River, Kaylee, Jayne).

### Delivered
- Full project tree under `lib/Plugin.Maui.HelpKit/` (solution, library, 3 sample placeholders, 2 test projects, license, readme, changelog, gitignore, Directory.Build.props, global.json).
- MAUI class library `src/Plugin.Maui.HelpKit/Plugin.Maui.HelpKit.csproj` pinned to `net11.0-{android,ios,maccatalyst,windows10.0.19041.0}` with MIT license, 0.1.0-alpha version, and required preview package refs (`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.VectorData.Abstractions`, `sqlite-net-pcl`).
- Public API: `HelpKitOptions`, `IHelpKit` (+ `HelpKitMessage`, `HelpKitCitation`), `IHelpKitPresenter` + three concrete presenters (Shell / Window / MauiReactor), `IHelpKitContentFilter` + `DefaultSecretRedactor`, `HelpKitServiceCollectionExtensions` (`AddHelpKit`, `AddHelpKitShellFlyout`), internal `HelpKitAiResolver` (keyed → unkeyed fallback).
- `HelpKitService` wires `ShowAsync` / `HideAsync` via the presenter today; `ClearHistoryAsync` / `IngestAsync` / `StreamAskAsync` throw `NotImplementedException` with explicit `TODO(Wash)` / `TODO(River)` markers so Wave 2 owners know exactly where to plug in.
- `HelpKitPage` placeholder ContentPage so presenters have something to push.
- xUnit test project seeded with `DefaultSecretRedactorTests`.
- Decision drop at `.squad/decisions/inbox/zoe-helpkit-public-api.md` documenting the contract for Wash/River/Kaylee/Jayne.

### Key paths
- Library: `lib/Plugin.Maui.HelpKit/src/Plugin.Maui.HelpKit/`
- Solution: `lib/Plugin.Maui.HelpKit/Plugin.Maui.HelpKit.sln`
- Decision drop: `.squad/decisions/inbox/zoe-helpkit-public-api.md`
- Plan: `~/.copilot/session-state/03d1f22f-56b2-4651-9999-bd73f5a0aaf9/plan.md`

### API design decisions
1. **Presenter default = `MauiReactorPresenter`**, which internally prefers Shell when `Shell.Current` is non-null and falls back to `WindowPresenter`. This gives SentenceStudio (MauiReactor) the right path out of the box while keeping Shell and Plain hosts first-class. Hosts can override by registering their own `IHelpKitPresenter` after `AddHelpKit(...)` — all DI registrations use `TryAddSingleton`.
2. **Keyed DI fallback in the resolver, not the registration.** `HelpKitAiResolver` does runtime keyed → unkeyed resolution. Rationale: attribute-based `[FromKeyedServices]` didn't fit the factory-delegate path for `HelpKitService`, and runtime resolution gives a much clearer error message when neither registration exists ("HelpKit deliberately ships no model — you bring the client").
3. **Content filter default is implicit, not explicit.** If `HelpKitOptions.ContentFilter` is null, the DI registration substitutes `DefaultSecretRedactor`. Keeps `AddHelpKit()` zero-config while preserving the escape hatch.
4. **`AddHelpKitShellFlyout` writes to a marker `HelpKitShellFlyoutOptions` type.** Kaylee reads it at app-ready time. No silent Shell mutation — matches the v2 "no silent Shell mutation" decision.
5. **`IHelpKit.IngestAsync` as first-class.** Not just internal startup magic. Hosts will want to re-ingest after updating help content, and the eval harness will want a deterministic ingestion entry point.
6. **Record types for `HelpKitMessage` / `HelpKitCitation`.** Value semantics, immutability, trivial to diff in tests — and they flow through an `IAsyncEnumerable`, so non-ref-equality is the right call.
7. **Samples are TFM-pinned but otherwise empty placeholders with `TODO.md`.** Per task spec (Wave 3 scope — Kaylee fills in).

### Issues hit
- **net11 preview SDK not installed locally.** `dotnet restore` fails with `NETSDK1139: target platform android not recognized` because the workstation is on .NET SDK 10.0.101 (net10). The scaffold is correct per the locked net11-only decision; this is environmental. Documented in README (Prerequisites section) and in the decision drop. `global.json` pins `rollForward: latestFeature` + `allowPrerelease: true` so once the net11 preview SDK + MAUI workload land, restore + build are expected to succeed without changes.
- **`Microsoft.Extensions.AI.Abstractions` / `Microsoft.Extensions.VectorData.Abstractions` are in preview** — I pinned `9.4.0-preview.1.25207.5`. Version may need bumping when Wash/River actually restore; the API shape is stable but preview serial numbers churn.

### Learnings
- Plugin scaffold convention nailed down: `src/` for library, `samples/` for hosts (one per presenter shape), `tests/` split into unit (`.Tests`) + eval (`.Eval`) so CI gates can target them separately. Keep incubating inside SentenceStudio until Alpha close, then `git subtree split` (locked in decisions 2026-04-17).
- Always use `TryAddSingleton` in `AddHelpKit` so host apps can override ANY component by registering their own instance. Last-wins is easier to reason about than "did my registration fire before yours?".
- For libraries that consume `IChatClient` / `IEmbeddingGenerator` from the host, prefer runtime keyed→unkeyed resolution over DI attribute magic. The error message quality matters more than the elegance when a dev forgets to register the client.
- Plan v2 "Honest Messaging" copy (no "offline" / "zero hallucination") is carried verbatim into README and CHANGELOG. Every library touching LLMs should have this calibration from day 1.

## Learnings

### 2026-04-17 — HelpKit CI gating, extract runbook, honest-messaging discipline

**CI gating strategy.** The eval harness is the only gate that actually
protects users. Build matrix catches TFM regressions; unit tests catch
behavior regressions; but the eval gate (>=85% correct AND 0 fabricated
cites) is what keeps the honest-messaging pitch honest. Ran fake-client
mode by default for speed and secret hygiene; live-mode is an opt-in env
var flip for release gates. Everything else (build, tests, pack-preview)
is table stakes. The pack-preview job intentionally does NOT publish to
NuGet during Alpha — manual publish keeps the Captain in the loop for
each alpha drop.

**Extract-repo runbook.** `git subtree split --prefix=` is the right
primitive; it preserves history and is non-destructive (the split branch
can be regenerated freely). Key ordering: (1) CI green for 3+ consecutive
runs, (2) scan for SentenceStudio leaks before splitting — a single
`using SentenceStudio.X` line would forever live in the extracted repo's
history, (3) switch SentenceStudio to NuGet BEFORE deleting the
incubation folder, (4) keep squad `.squad/agents/*/history.md` entries in
SentenceStudio for historical context, just mark them archived. Do not
migrate them to the new repo.

**Honest-messaging discipline.** Banned phrases: "offline-first", "no
hallucination", "zero hallucination". These are not true and every
consumer will discover that within a day of using the library. Replaced
with a FAQ that says "no" directly. What IS true and we DO claim:
"citations are validated before yielding; fabricated citations are
stripped; the eval gate fails CI on fabricated cites." This is
falsifiable and enforced — so it belongs in the README. The
"What does NOT ship in Alpha" section is equally important: AI-enriched
scanner, Blazor companion, sqlite-vec. Calling out what is absent
prevents support-load spikes from consumers who assumed those features
were there.

## 2026-04-17 (later) — HelpKit multi-target to net10 (incubation grace target)

**Decision.** Multi-targeted Plugin.Maui.HelpKit to add net10.0-* TFMs alongside the net11.0-* primary so SentenceStudio (locked to net10 by Captain's global.json) can ProjectReference HelpKit during incubation. net11 TFMs gated behind `-p:IncludeNet11Targets=true` because no net11 SDK/workload is installed locally and unconditional cross-targeting fails restore. Reversibility documented in SUPPORT.md and EXTRACT-RUNBOOK.md handoff: at Alpha extract, drop net10 and ship net11-only.

**Net10 compatibility patches required.** HelpKit had never actually compiled — Wash developed against an uninstalled net11 preview SDK. Surfacing it on net10 / MAUI 10.0.1 GA exposed six real bugs, none of them public-API-surface:
- `yield in catch` (CS1631) x3 in `HelpKitService.cs` — refactored to set sentinel/error string in catch, yield outside.
- `Task.FromResult(...)` nullability mismatch in `ConversationRepository.GetAsync` — explicit `<ConversationRow?>` type arg.
- `MenuShellItem` was made internal in MAUI 10 — replaced direct construction with reflection-based instantiation, preserving the existing try/catch fallback that already documented the XAML workaround.
- `IView.SetBinding` no longer carries a string-path overload — cast to `BindableObject` before calling.

**Package version floors bumped.** Microsoft.Extensions.AI.Abstractions 9.4.0-preview → 9.5.0; VectorData.Abstractions 9.4.0-preview → 9.5.0; DependencyInjection.Abstractions, Logging.Abstractions, Options 9.0.0 → 10.0.0. Required because MAUI 10.0.1 transitively pulls Microsoft.Extensions.* 10.0.0 and the previous floors triggered NU1605 downgrade-as-error. Floors only — net11 will resolve higher.

**SentenceStudio activation.** Removed the `#if NET11_0_OR_GREATER` guards from `src/Shared/HelpKitIntegration.cs` and the `Condition="$(TargetFramework.StartsWith('net11.0'))"` from MacCatalyst + iOS heads. Removed the wrapping `#if` blocks in both MauiProgram.cs files. Added missing `using Microsoft.Extensions.Configuration;` for the `.Get<Settings>()` extension. SentenceStudio.MacCatalyst net10-maccatalyst build: **0 errors, 120 warnings**. None of the warnings are from HelpKit; the count is the per-head baseline (Wash's 563 was a different scope).

**Learning.** "Estimated 5 minutes plus a restore" was wildly off. A library that has never actually compiled against the SDK its consumers will use is not green — it is structurally untested. Every multi-target should include a CI gate that builds at least one TFM the team can actually run, otherwise "the contract is frozen" hides real bugs. EXTRACT-RUNBOOK should require: HelpKit must build clean against the SentenceStudio SDK before any Alpha drop, even if net11 is the primary ship target.

**Honest delta.** This took six source patches and a config bump beyond the planned csproj edit. None of them touched the public API, so the contract is intact. But the precedent is recorded: incubating a library against an uninstalled preview SDK is the same as not testing it.
