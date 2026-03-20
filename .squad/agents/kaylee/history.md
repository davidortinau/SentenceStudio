# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- PageHeader `ToolbarActions` slot renders icon buttons in the header toolbar on ALL screen sizes — use `d-md-none` to restrict to mobile
- Bootstrap `offcanvas-bottom` with `d-md-none` is the pattern for mobile-only slide-up filter panels
- Vocabulary page filter dropdowns reuse the same `@onchange` handlers in both inline row and offcanvas — single source of truth via search query parsing
- `ActiveFilterCount` computed property: `parsedQuery?.Filters.Count ?? 0` — use for badge counts
- `SentenceStudio.AppLib` has `ImplicitUsings>disable` — must add explicit `using` for System.Net.Http, etc.
- `Auth:UseEntraId` config flag pattern works across Api, WebApp, and now MAUI clients
- MSAL.NET `WithBroker(BrokerOptions)` overload removed in v4.x — just omit it when not using a broker
- `AuthenticatedHttpMessageHandler` is wired into all HttpClient registrations (API + CoreSync) via `AddHttpMessageHandler<T>()`
- Pre-existing build error: `DuplicateGroup` missing in `SentenceStudio.UI/Pages/Vocabulary.razor` — blocks MacCatalyst full build
- `Auth:UseEntraId` config flag controls auth mode in both API and WebApp — false = DevAuthHandler, true = Entra ID OIDC
- Microsoft.Identity.Web OIDC uses `AddMicrosoftIdentityWebApp()` + `EnableTokenAcquisitionToCallDownstreamApi()` chain
- Redis-backed distributed token cache via `Aspire.StackExchange.Redis.DistributedCaching` (match AppHost Aspire version for preview packages)
- `ConfigureHttpClientDefaults` adds DelegatingHandler to ALL HttpClient instances from the factory
- Microsoft.Identity.Web.UI requires `AddControllersWithViews()` + `MapControllers()` for sign-in/sign-out endpoints
- `appsettings.json` is gitignored — config changes there are local-only, use `appsettings.Development.json` for tracked dev config
- Client secrets go in user-secrets, never in tracked config files

- Blazor pages in `src/SentenceStudio.UI/Pages/` — follow `activity-page-wrapper` layout pattern
- MauiReactor conventions: `VStart()` not `Top()`, `VEnd()` not `Bottom()`, `HStart()`/`HEnd()` not `Start()`/`End()`
- NEVER use emojis in UI — use Bootstrap icons (bi-*) or text. Non-negotiable.
- Use `@bind:event="oninput"` for real-time Blazor input binding (default `onchange` fires on blur)
- Activity pages: PageHeader, activity-content area, footer with activity-input-bar
- Word Association activity at `/word-association` — latest activity, has Grade-first UX flow
- Dashboard activities listed in `src/SentenceStudio.UI/Pages/Index.razor`

## Core Context (Current)

**Role:** Full-stack Developer  
**Focus Areas:** WebApp (Blazor Server), MAUI (mobile/desktop), CI/CD, Azure deployment  

**Current Phase:**
- Phase 1 (Auth) & Phase 2 (Secrets): Complete
- Phase 3-5 active: Infrastructure, CI pipeline, hardening

**Recent Completions (2026-03-19 to 2026-03-20):**
- WebApp OIDC (#44, PR #70 merged)
- MAUI MSAL (#45, PR #71 merged)
- CI Workflow (#56, merged)
- File-based vocabulary import (InputFile component) — feature/file-vocab-import, commit fe312d6, 183 lines
- Phase 2 Security & Secrets — with Wash (#39-41 complete)
- Mobile auth gate fix (MainLayout async verification)
- Blazor Hybrid auth research (7-phase roadmap, Decision #13)

**Key Tech Learnings:**
- Auth:UseEntraId config flag controls mode across all projects (API, WebApp, MAUI)
- MSAL.NET: no WithBroker() in v4.x; uses PKCE with system browser
- Blazor InputFile component works cross-platform (web + MAUI Hybrid) — standard Blazor, not platform-specific
- Microsoft.Identity.Web requires AddControllersWithViews() + MapControllers() for OIDC endpoints
- Redis distributed cache via Aspire.StackExchange.Redis.DistributedCaching

**Blockers:** Pre-existing MacCatalyst build error (DuplicateGroup in Vocabulary.razor) — blocks full platform builds

**Next:** Continue Phase 3 (Infrastructure) and Phase 4 (CI/Deploy pipeline) work


### 2026-03-19 — Mobile UX Fixes Wave 2 (Issues #104, #109, #114, #116, #119, #120)

**Issue #104 — Register page keyboard clipping:**
- Replaced `vh-100` with `min-height: 100dvh` on wrapper div
- Added `overflow-y: auto` to allow scrolling when iOS keyboard opens
- Uses `dvh` (dynamic viewport height) which accounts for mobile browser chrome

**Issue #109 — Cloze font too large for Korean:**
- Added mobile media query override in app.css: `.ss-display { font-size: 1.5rem; }` inside `@media (max-width: 767.98px)`
- Reduces from 42px (desktop) to 1.5rem (~24px) on mobile
- Prevents Korean sentences from wrapping excessively on small screens

**Issue #114 — Profile button groups break on mobile:**
- Replaced `btn-group flex-wrap` with `d-flex flex-column flex-md-row gap-2`
- Buttons now stack vertically on mobile, horizontal row on desktop
- Applied to Session Duration and Target CEFR Level sections

**Issue #116 — Settings exposes Database Migrations to end users:**
- Wrapped Database Migrations card in `#if DEBUG` preprocessor directive
- Card now only renders in Debug builds, hidden from Release/production
- No code-behind changes needed — Blazor Razor supports preprocessor directives

**Issue #119 — Vocabulary bulk edit toolbar overflows:**
- Changed toolbar from `d-flex gap-2` to `d-flex flex-wrap gap-2`
- Separated "Select All"/"Select None" buttons into `w-100 d-flex gap-2 d-md-inline-flex w-md-auto` wrapper
- Buttons drop to new line on mobile, inline on desktop

**Issue #120 — Writing input bar cramped:**
- Made Grade button icon-only on mobile: `<i class="bi bi-send d-md-none"></i>`
- Text label "Grade" uses `d-none d-md-inline` to hide on mobile, show on desktop
- Saves ~60px horizontal space on mobile screens

**Build Verification:** 0 errors, 279 warnings (pre-existing)

**Learnings:**
- `min-height: 100dvh` (dynamic viewport height) is better than `vh-100` for mobile forms — accounts for keyboard and browser chrome
- Blazor Razor files support `#if DEBUG` preprocessor directives — no need for code-behind boolean flags
- `d-flex flex-column flex-md-row gap-2` is the correct Bootstrap 5.3 pattern for responsive button groups — `btn-group flex-wrap` has poor mobile behavior
- Icon-only buttons on mobile with `d-md-none` / `d-none d-md-inline` pattern saves horizontal space without losing desktop clarity
- `flex-wrap` + strategic line-break wrappers (w-100 on mobile, w-md-auto on desktop) gives precise control over toolbar wrapping behavior

### 2026-07-22 — Onboarding Gate Fix for Returning Users

**Status:** Complete
**Bug:** Returning users (dave@ortinau.com) with extensive data (2472 vocab words) were forced through onboarding after every app rebuild/redeploy because `is_onboarded` is a device-local preference that gets wiped.

**Root Cause:** `MainLayout.razor` line 77 checked only `Preferences.Get("is_onboarded", false)` with no fallback for existing users. `AccountEndpoints.cs` AutoSignIn/Login never set `is_onboarded = true`.

**Fix (3 touch points):**
1. **MainLayout.razor** — Injected `UserProfileRepository`, added profile data check before onboarding redirect. If user has a profile with Name + TargetLanguage set, auto-sets `is_onboarded = true` and skips onboarding.
2. **AccountEndpoints.cs (AutoSignIn)** — After setting `active_profile_id`, checks profile data and sets `is_onboarded = true` for returning users.
3. **AccountEndpoints.cs (Login)** — Same returning-user check added to the password login flow.

**Key Design Decisions:**
- Profile check is lightweight (single DB lookup) and only runs when `is_onboarded` is false
- Once set to true, subsequent page loads skip the check entirely
- No schema changes needed — purely logic/preferences fix
- Criteria for "onboarded": profile exists with non-empty Name AND non-empty TargetLanguage
- Bootstrap responsive display utilities: use `d-none d-md-flex` to hide elements on mobile (<768px) and show on md+ screens
- Vocabulary page stats bar and filter chips now hidden on mobile to reduce clutter — offcanvas already shows active filters
- Learning progress status: converted from icon button group to dropdown select for cleaner mobile UX and reduced vertical space
- Resource associations: removed horizontal scroll, removed descriptions, added CSS truncation at 2 lines max for titles
- CSS pattern: `-webkit-line-clamp: 2` with `-webkit-box-orient: vertical` for multi-line text truncation

### 2026-07-22 — File-Based Vocabulary Import on Resource Pages

**Status:** Complete  
**Branch:** `feature/file-vocab-import`  

Added Blazor `InputFile` component to both ResourceAdd.razor and ResourceEdit.razor for importing vocabulary from .txt/.csv files. Works cross-platform (web + MAUI Blazor Hybrid) without needing platform-specific file pickers.

**Key Decisions:**
- Used standard Blazor `InputFile` (not WebFilePickerService/MauiFilePickerService) — works everywhere
- Hidden `<input>` with styled `<label>` button for consistent Bootstrap look
- Reuses `ParseVocabularyWords()` and `GetOrCreateWordAsync()` — no new parsing logic
- ResourceAdd uses in-memory dedup only; ResourceEdit persists via repository (matching existing patterns)
- 1 MB file size cap via `OpenReadStream(maxAllowedSize:)`

**Learnings:**
- Blazor `InputFile` with hidden `class="d-none"` + label-as-button is the cross-platform file picker pattern
- `InputFileChangeEventArgs.File.OpenReadStream()` needs explicit `maxAllowedSize` param (default is 500 KB)
- ResourceAdd and ResourceEdit have different persistence patterns: Add defers to save, Edit persists immediately

---

## 2026-03-20 — Team Sync: Zoe's Getting-Started Dashboard

**Impact on Kaylee's Work:**
- Zoe implemented getting-started onboarding flow (Dashboard, feature/getting-started-dashboard)
- Creates "Korean Basics" skill profile + 20 vocab words + "Korean Starter Pack" resource for new users
- Uses existing Kaylee UI patterns (Bootstrap icons, in-place state transitions)
- No changes required to Kaylee's current work — the file-import feature remains independent

**Cross-Agent Notes:**
- Kaylee's file-vocab-import feature (InputFile on ResourceAdd/ResourceEdit) works seamlessly with Zoe's getting-started flow
- Both features are non-blocking and can be merged in any order

