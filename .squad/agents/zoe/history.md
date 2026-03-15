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
- Issues propagated to respective agent history files

See `.squad/decisions.md` for full decision record.

### 2026-03-14 — CRUD Feedback Audit & Standard

**Status:** Complete (PROPOSED decision)  
**Location:** `.squad/decisions/inbox/zoe-crud-feedback-standard.md`  

Audited all CRUD pages (Resources, Skills, Vocabulary, Minimal Pairs, Profile, Settings) for user feedback consistency. Found mostly good patterns with toasts, but inconsistencies exist:

**Gaps Found:**
1. **JS confirm dialogs** in 5 pages (ResourceEdit, SkillEdit, VocabularyWordEdit, MinimalPairs, Profile) — should be Bootstrap modals for accessibility
2. **Profile.razor** has silent load errors, uses modal for save errors (should be toast), missing delete success feedback

**Decision Written:**
- Success operations → Toast (auto-dismiss, 3s)
- Errors → Toast (longer, 5s)
- Warnings → Toast (medium, 4s)
- Destructive ops → Bootstrap modal confirmation BEFORE + Toast AFTER
- Information → Toast (short, 3s)

**Code patterns documented** for Kaylee: save operation, Bootstrap delete modal (markup + C#), load/list with errors.

**Next:** Captain approval, then Kaylee implements fixes.

### 2026-03-14 — Auth E2E Test Plan Created for iOS

**Status:** Complete  
**Location:** `.squad/skills/auth-e2e-testing/SKILL.md`  
**Executed by:** Jayne (Tester) — uses this plan to verify auth flow

Designed a comprehensive E2E test plan covering complete authentication flow on iOS with dev tunnel, local Aspire, and simulator. Plan includes 11 test suites with 45+ individual test cases:

**Coverage:**
- Registration (happy path, duplicate email, weak password)
- Login (happy path, wrong password, non-existent email)
- Onboarding (first-time, returning user skips)
- Token persistence (SecureStorage, kill/relaunch, logout clears)
- Token refresh & expiry (auto-refresh, 7-day boundary)
- Logout (UI flow, token cleanup)
- Profile operations (view, edit, delete account)
- Data isolation (User A not seeing User B's data, JWT claims)
- Error handling (API down, network timeout, malformed responses)
- Webapp regression (login, logout, registration still work)
- Load testing (optional, concurrent logins)

**Key Details:**
- Test infrastructure setup verified (Aspire dashboard, simulator, dev tunnel health)
- 11 test suites, 45+ individual test cases
- Each case includes: preconditions, steps, verification, expected outcome, screenshots
- Database queries for validation (SQLite)
- Aspire structured logs for error checking
- Tools: Playwright (webapp), maui-devflow (iOS), xcrun simctl (simulator management)
- Checklist for tracking execution
- Known issues & workarounds documented

**Dependencies:**
- Aspire running locally with all services (Api, WebApp, Workers, Redis, SqliteDb)
- iOS simulator booted (iPhone 17 Pro, iOS 26.2)
- Dev tunnel active: `https://c60qm31n-7012.use.devtunnels.ms`

**Next:** Jayne executes plan to verify mobile auth flow before feature freeze.

