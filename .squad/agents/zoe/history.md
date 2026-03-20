# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

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

