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

### 2026-03-15 — Blazor Hybrid Auth Architecture Research

**Status:** Complete  
**Location:** `docs/blazor-hybrid-auth-research.md`, `.squad/decisions.md` (Decision #12)  
**Decision:** PROPOSED (awaiting Captain approval)

Conducted deep architectural research into Microsoft's official Blazor Hybrid authentication patterns after persistent issues with auth gate in MainLayout.razor. Identified fundamental gap: we're not using Blazor's authentication system at all.

**Key Findings:**

1. **Microsoft prescribes AuthenticationStateProvider** — the official abstraction for auth in Blazor, NOT custom boolean preferences or IAuthService alone
2. **Router must use AuthorizeRouteView** — NOT plain RouteView. Declarative auth via `[Authorize]` attributes, inline rendering in `<NotAuthorized>` slot
3. **No NavigateTo() for auth redirects** — Router handles gating declaratively during component lifecycle, eliminating timing issues
4. **Reactive state changes** — NotifyAuthenticationStateChanged() updates all components automatically
5. **ClaimsPrincipal is the security boundary** — not boolean preferences

**Gaps in Our Implementation:**

| Component | Microsoft Pattern | Our Implementation | Severity |
|-----------|-------------------|--------------------| ---------|
| Auth Abstraction | AuthenticationStateProvider | IAuthService only | HIGH |
| Router | AuthorizeRouteView | RouteView | HIGH |
| Auth Gate | Router `<NotAuthorized>` slot | MainLayout boolean logic | HIGH |
| Page Protection | `[Authorize]` attributes | None | MEDIUM |
| State Change | NotifyAuthenticationStateChanged() | Manual preference writes | HIGH |
| DI Registration | AddAuthorizationCore() | Missing | HIGH |

**Root Cause of NavigateTo() Issues:**

NavigateTo() during OnInitializedAsync in Blazor Hybrid WebView is unreliable due to WebView lifecycle timing and race conditions with router initialization. Microsoft's pattern avoids this entirely via declarative routing.

**Proposed Architecture:**

1. Create `SentenceStudioAuthStateProvider : AuthenticationStateProvider` (wraps existing IAuthService)
2. Replace `<RouteView>` with `<AuthorizeRouteView>` in Routes.razor
3. Remove manual auth gate logic from MainLayout.razor (~60 lines deleted)
4. Add `[Authorize]` attributes to protected pages
5. Keep IAuthService unchanged (well-designed for token management)

**4-Phase Migration Path:**
- Phase 1: Add auth infrastructure (no breaking changes)
- Phase 2: Router replacement
- Phase 3: MainLayout simplification
- Phase 4: Page attributes

**Estimated effort:** 1-2 days (Phases 1-4)

**Documentation Sources:**
- [ASP.NET Core Blazor Hybrid authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/?view=aspnetcore-10.0&pivots=maui)
- [MAUI Blazor Hybrid and Web App with ASP.NET Core Identity sample](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/maui-blazor-web-identity?view=aspnetcore-10.0)
- [Blazor Hybrid security considerations](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/security-considerations?view=aspnetcore-10.0)

**Key Learnings:**
- AuthenticationStateProvider is the official auth abstraction in Blazor — custom gate logic bypasses the framework
- AuthorizeRouteView handles auth declaratively via `[Authorize]` attributes and `<NotAuthorized>` inline rendering
- NavigateTo() during component init in Blazor Hybrid is unreliable — use router-based declarative auth instead
- ClaimsPrincipal with ClaimsIdentity is the security boundary, not boolean preferences
- NotifyAuthenticationStateChanged() enables reactive auth state updates across components
- Microsoft sample uses inline component rendering in `<NotAuthorized>` slot, NOT NavigateTo() redirects
- IAuthService pattern (token management, API calls) is complementary to AuthenticationStateProvider, not a replacement
- AddAuthorizationCore() registration is required for `[Authorize]` attributes and auth components to work

**Next:** Captain approval, then 4-phase implementation.

### 2026-03-15 — Fixed Theme Flickering (Issue #96)

**Status:** Complete  
**Root Cause:** SSR-to-interactive hydration race condition  
**Files Changed:** `src/SentenceStudio.WebApp/Components/App.razor`

**Problem:**
WebApp was flickering between light and dark mode during page load. Investigation revealed:
1. App.razor had hardcoded `data-bs-theme="dark" data-ss-theme="seoul-pop"` in the HTML element
2. SSR rendered the page with these hardcoded values first
3. After interactive hydration, MainLayout.razor's `OnAfterRenderAsync` called `applyTheme()` with the user's saved preference from ThemeService
4. If user preference was different (e.g., light mode), JS changed the attributes → visible flicker

**Solution:**
Injected `ThemeService` into App.razor and replaced hardcoded theme attributes with dynamic Razor expressions:
```razor
@inject ThemeService ThemeService
<html lang="en" data-bs-theme="@ThemeService.CurrentMode" data-ss-theme="@ThemeService.CurrentTheme">
```

Now SSR renders with the correct theme from the start (ThemeService reads from persisted preferences), eliminating the flicker. The MainLayout's `applyTheme()` call becomes a no-op if the theme is already correct.

**Key Learnings:**
- Blazor SSR hydration can cause theme flicker if theme attributes are hardcoded in App.razor
- ThemeService correctly reads from IPreferencesService (persisted JSON) during SSR, making it safe to inject into App.razor
- Theme attributes on `<html>` must match user preference from first render to avoid SSR→interactive flash
- No CSS `prefers-color-scheme` media queries were found — all theme control is via JS and CSS variables


### 2026-03-15 — Vocabulary Relationship Architecture Framing

**Status:** PROPOSED  
**Location:** `.squad/decisions/inbox/zoe-vocab-relationships-architecture.md`

Framed the architecture question for vocabulary relationship tracking after Captain reported overlap/duplication issue: users master root words (e.g., Korean `자주` - "often") but must re-prove mastery for derived forms (`자주 마시는` - "often drinks").

**Key Architecture Questions Identified:**

1. **Relationship Model**: Three schema options analyzed
   - Option A (ParentWordId FK): Simple, fast, can't handle multi-word relationships
   - Option B (Junction Table): Flexible graph model, handles complex relationships, more queries
   - Option C (Lemma Group): Linguistically accurate, requires AI lemma assignment

2. **Mastery Propagation**: Four approaches evaluated
   - Full Credit (optimistic) — root mastery grants 70% to derived
   - Partial Credit (weighted) — 40-60% transfer
   - Separate Mastery with Hints (conservative) — current behavior + UI hints
   - Context-Based Mastery (advanced) — track per-context (standalone vs. phrase)

3. **AI Import Enhancement**: Three strategies
   - Relationship-Aware Prompt — AI flags relationships during generation
   - Lemma Grouping Prompt — AI assigns lemma groups
   - Hybrid (AI suggests + user confirms) — accuracy vs. friction tradeoff

4. **SRS Impact**: Three scheduling options
   - Clustered Review — batch root + derived forms
   - Weighted Intervals — derived forms reviewed less often
   - Independent Scheduling — status quo (SM-2 per word)

**Recommendation (Phase 1 MVP):**
- Schema: ParentWordId FK (Option A) — minimal risk, additive
- Mastery: Separate tracking with UI hints (Option C) — transparent, no shortcuts
- AI Import: Relationship-aware prompt (Option A) — capture relationships during import
- SRS: Independent scheduling (Option C) — don't refactor proven SM-2 yet

**Cross-Team Questions Identified:**
- **River (AI)**: Accuracy of relationship detection, lemma assignment reliability
- **Wash (Backend)**: Schema preference, migration complexity, query performance
- **Kaylee (UI)**: Relationship visualization, import preview UX, mastery feedback patterns
- **Language Tutor Agents**: Cognitive load, review frequency, SLA research validation

**Key Files Referenced:**
- `src/SentenceStudio.Shared/Models/VocabularyWord.cs` — current data model
- `src/SentenceStudio.Shared/Models/VocabularyProgress.cs` — mastery tracking
- `src/SentenceStudio.UI/Pages/ResourceEdit.razor` — AI import flow
- `docs/vocabulary-deduplication-enhancement.md` — current AI deduplication strategy

**Next:** Captain approval on MVP vs. ambitious path, then team input from River/Wash/Kaylee/language tutors.

---

## VOCABULARY HIERARCHY TEAM ANALYSIS — TEAM CONSENSUS (2026-03-17)

**Session:** Vocabulary Hierarchy Analysis & Consensus Building  
**Team:** Zoe (Lead), River (AI), Wash (Backend), SLA Expert, Learning Design Expert  
**Status:** PROPOSED — Awaiting Captain Approval

**Consensus Reached on MVP Approach:**

### Recommendation Summary
- **Data Model:** Self-referential FK (ParentWordId) — Option A approved by all stakeholders
- **Mastery Propagation:** Independent mastery tracking with UI hints (Option C) — transparent, no shortcuts
- **AI Import:** Hierarchical prompts with relationship detection — River implemented full schema
- **SRS Scheduling:** Independent scheduling (preserve SM-2) — no refactoring in MVP
- **Learning Design:** Progressive disclosure with engagement tracking — supports transfer of learning data

### Why This Consensus?
- **Wash validated:** Option A is simplest for CoreSync (single table, NULLable FK, no cascade complexity)
- **River designed:** Hierarchical JSON schema with relationshipType, relatedTerms, linguisticMetadata
- **SLA Expert confirmed:** Independent mastery aligns with morphological awareness research + spacing effect
- **Learning Design:** Progressive hints prevent cognitive overload, preserve challenge
- **Captain's Request Met:** "More nuanced" without committing to complex mastery transfer or graph models

### Team Outputs
- `docs/zoe-vocab-relationships-architecture.md` — Architecture framework & design pillars
- `docs/wash-vocabulary-hierarchy-proposal.md` — Data model analysis + EF Core implementation
- `docs/vocabulary-hierarchy-prompt-design.md` — Hierarchical JSON schema + multi-pass prompt strategy
- `docs/vocabulary-hierarchy-learning-design.md` — UX flow, engagement metrics, confidence building
- `docs/vocabulary-hierarchy-consensus-plan.md` — Integrated consensus plan (synthesis of all)

### Critical Path (After Captain Approval)
1. Wash: Generate EF Core migration (ParentWordId, RelationType)
2. River: Implement hierarchical prompt endpoint
3. Kaylee: Vocabulary detail page redesign (show parent + siblings)
4. All: E2E testing on 5 real Korean transcripts

### Awaiting
- Captain approval on MVP approach
- Decision: Migrate old vocabulary or only new imports?
- Decision: Mastery inheritance enablement timeline (Phase 1 MVP vs. future)
