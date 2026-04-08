# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

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

## Work Sessions

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

