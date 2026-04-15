# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

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
