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
- `.list-page` CSS class is the standard wrapper for scrollable list pages (overflow-x: hidden, safe-area-inset-bottom)
- Blazor `<Virtualize>` is the pattern for rendering 500+ items efficiently — load full dataset, filter it, pass filtered list to Virtualize

## Core Context (Current)

**Role:** Full-stack Developer  
**Focus Areas:** WebApp (Blazor Server), MAUI (mobile/desktop), CI/CD, Azure deployment  

**Current Phase:**
- Phase 1 (Auth) & Phase 2 (Secrets): Complete
- Phase 3-5 active: Infrastructure, CI pipeline, hardening

**Recent Completions (2026-03-20 to 2026-03-28):**
- Dashboard refresh button fix (runtime platform detection instead of preprocessor directives)
- Mobile UX research deliverable (docs/mobile-ux-research.md)
- Blazor Virtualize implementation (Vocabulary, Resources, Import, ChannelDetail)
- YouTube Import page redesign (3-tab hub: channels, single video, history)
- UI polish wave (import history layout, resources grid/list toggle, channel video discovery, pagination)
- What's New modal feature (version checking, release notes display)

**Key Tech Learnings:**
- Auth:UseEntraId config flag controls mode across all projects (API, WebApp, MAUI)
- MSAL.NET: no WithBroker() in v4.x; uses PKCE with system browser
- Blazor InputFile component works cross-platform (web + MAUI Hybrid) — standard Blazor, not platform-specific
- Microsoft.Identity.Web requires AddControllersWithViews() + MapControllers() for OIDC endpoints
- Redis distributed cache via Aspire.StackExchange.Redis.DistributedCaching
- **CRITICAL:** Shared Blazor Razor components (.razor) in class libraries don't get platform-specific preprocessor symbols — always use runtime detection
- Blazor Virtualize works in Blazor Hybrid; load full dataset, filter, pass to Virtualize for efficient 500+ item rendering
- Native RefreshView CANNOT wrap BlazorWebView (architectural limitation of Blazor Hybrid)

**Blockers:** Pre-existing MacCatalyst build error (DuplicateGroup in Vocabulary.razor) — blocks full platform builds

**Next:** Continue Phase 3 (Infrastructure) and Phase 4 (CI/Deploy pipeline) work; monitor virtualization performance

---

## Archived Core Context (Pre-2026-03-20)

**Summary of Prior Work (2026-03-07 to 2026-03-19):**

**Mobile UX Fixes Wave (2026-03-19, Issues #104, #109, #114, #116, #119, #120):**
- Register page: Replaced `vh-100` with `min-height: 100dvh` + `overflow-y: auto` for iOS keyboard
- Cloze font sizing: Mobile media query override for Korean text (24px vs 42px desktop)
- Button groups: Changed to flex-column/row responsive layout
- Settings: Wrapped Database Migrations card in `#if DEBUG` preprocessor
- Bulk edit toolbar: Added `flex-wrap` for responsive button wrapping
- Writing input: Icon-only Grade button on mobile (`d-md-none`)

**File-Based Vocabulary Import (2026-03-14, feature/file-vocab-import):**
- InputFile component for importing CSV/Excel on ResourceAdd and ResourceEdit pages
- Pattern: Shared Blazor component, works cross-platform (web + MAUI Hybrid)

**Phase 1 & 2 Auth Completion (2026-03-14 to 2026-03-19):**
- WebApp OIDC (#44, PR #70) — Microsoft.Identity.Web integration, DelegatingHandler, Redis token cache
- MAUI MSAL (#45, PR #71) — MSAL.NET, WebAuthenticator, SecureStorage, Bearer token injection
- CI Workflow (#56, PR #69) — GitHub Actions multi-platform matrix, artifact publishing
- Mobile auth gate fix: MainLayout async verification logic

---

---

## Archived Context (Pre-2026-03-20)

**2026-03-07 to 2026-03-19: Phase 1 (Auth) & Phase 2 (Secrets) — COMPLETE**

**Auth Implementation (WebApp + MAUI):**
- WebApp OIDC: Microsoft.Identity.Web + DelegatingHandler + Redis distributed cache
- MAUI MSAL: WebAuthenticator + SecureStorage + Bearer token injection  
- Mobile auth gate fix: MainLayout async verification logic  
- CI Workflow: GitHub Actions multi-platform matrix + artifact publishing  

**Mobile UX Fixes & Features:**
- Register page: 100dvh + overflow-y fix for iOS keyboard
- Cloze font sizing: Mobile media query (24px vs 42px desktop)
- Button groups: Responsive flex-column/row layout  
- Settings page: Wrapped debug DB migrations in #if DEBUG  
- Bulk toolbar: Added flex-wrap for responsive wrapping  
- Writing input: Icon-only Grade button on mobile  
- File-import feature: InputFile component for CSV/Excel (cross-platform Blazor)  

**Mobile UX Research Deliverable:** docs/mobile-ux-research.md  

**Key Tech Learnings:**
- Shared Blazor Razor components (.razor) in class libraries DON'T inherit platform-specific preprocessor symbols — use runtime detection only
- MSAL.NET: no `WithBroker()` in v4.x; uses PKCE with system browser  
- Blazor InputFile works cross-platform (web + MAUI Hybrid) — standard Blazor  
- Microsoft.Identity.Web requires `AddControllersWithViews()` + `MapControllers()` for OIDC endpoints  
- PageHeader `ToolbarActions` slot renders on ALL screen sizes — use `d-md-none` for mobile-only  
- Bootstrap `offcanvas-bottom` + `d-md-none` pattern for mobile slide-up filter panels  

---

**Impact on Kaylee's Work:**
- Zoe implemented getting-started onboarding flow (Dashboard, feature/getting-started-dashboard)
- Creates "Korean Basics" skill profile + 20 vocab words + "Korean Starter Pack" resource for new users
- Uses existing Kaylee UI patterns (Bootstrap icons, in-place state transitions)
- No changes required to Kaylee's current work — the file-import feature remains independent

**Cross-Agent Notes:**
- Kaylee's file-vocab-import feature (InputFile on ResourceAdd/ResourceEdit) works seamlessly with Zoe's getting-started flow
- Both features are non-blocking and can be merged in any order

### 2026-03-20 — YouTube Channel Monitoring Import Page Redesign

**Status:** Complete  
**Feature:** Redesigned Import page for channel monitoring + single video import + history  

**Key Changes:**
1. **Import.razor** — Transformed from single-purpose to three-tab hub:
   - **My Channels tab:** Lists all monitored channels with status, recent imports, edit/pause controls
   - **Single Video tab:** Original single-video import flow (preserved)
   - **Import History tab:** All imports (manual + channel) with real-time status badges
   
2. **ChannelDetail.razor** — NEW page for add/edit channel:
   - Route: `/import/channel/{Id?}`
   - Form: Channel URL, Language dropdown, Check interval, Active toggle
   - Auto-resolves channel metadata on save (name, handle)
   - Pre-populates language from user profile on add

3. **Real-time status polling:** 
   - Timer polls every 5 seconds for in-progress imports
   - Updates status badges live as pipeline progresses
   - Solves Captain's concern: "What happens when user leaves during import?" → Status persists, UI reflects current state on return

4. **Service integration:**
   - `ChannelMonitorService` (DI) — CRUD for MonitoredChannel entities
   - `VideoImportPipelineService` (DI) — Import history + status tracking
   - Services registered by Wash in parallel — injected as nullable with null-checks

**UI Patterns:**
- Bootstrap icon usage: `bi-youtube`, `bi-plus-circle`, `bi-arrow-repeat`, `bi-check-circle`, `bi-x-circle`, `bi-hourglass-split`, `bi-toggle-on/off`, `bi-pencil`, `bi-clock-history`
- Status badges: `badge bg-warning/info/success/danger` with spinners for in-progress states
- Tab switcher: `btn-group` with conditional `btn-ss-primary` vs `btn-outline-secondary`
- PageHeader with PrimaryActions (desktop "Add Channel" button) + SecondaryActions (overflow menu)

**Status Badge Mapping (VideoImportStatus → UI):**
- Pending (0) → Yellow badge, hourglass icon
- FetchingTranscript (1) → Blue badge, download icon + spinner
- CleaningTranscript (2) → Blue badge, stars icon + spinner
- GeneratingVocabulary (3) → Blue badge, card-text icon + spinner
- SavingResource (4) → Blue badge, save icon + spinner
- Completed (10) → Green badge, check-circle icon
- Failed (99) → Red badge, x-circle icon

**Learnings:**
- `System.Threading.Timer` for polling — must dispose in `IDisposable.Dispose()` to prevent memory leaks
- `InvokeAsync(StateHasChanged)` required when updating UI from Timer callback (non-UI thread)
- Nullable service injection pattern: `[Inject] private ChannelMonitorService? ChannelMonitorSvc { get; set; }` with null-checks before use
- RenderFragment helper method for status badges: `private RenderFragment RenderStatusBadge(VideoImportStatus status) => __builder => { }`
- Tab state management: enum + conditional rendering (`@if (currentTab == Tab.Channels)`)
- `ShowBack="true"` on PageHeader triggers back chevron instead of hamburger menu
- Channel handle extraction from YouTube URL pattern: `https://www.youtube.com/@handle` → split on `/@`

**Data Flow:**
1. User adds channel via ChannelDetail page → saves to MonitoredChannel table
2. Background worker (not part of this PR) polls channels, creates VideoImport records
3. Import page loads channels + imports on init, starts polling timer
4. Timer refreshes import statuses every 5s, updates UI in real-time
5. When import completes, user clicks "View Resource" → navigates to ResourceEdit page

**No Breaking Changes:** Single video import flow unchanged, still works as before

### 2026-03-20 — Dashboard Refresh Button Fix

**Issue:** Dashboard refresh button didn't work on mobile — the sync wasn't triggering and data wasn't reloading.

**Root Cause:** Preprocessor directive bug in shared Blazor Razor component
- `Index.razor` used `#if IOS || ANDROID || MACCATALYST` to conditionally trigger sync
- **Blazor Razor files (.razor) in shared libraries don't inherit platform-specific preprocessor symbols from head projects**
- The `#if` block was NEVER compiled into the component, so `SyncService.TriggerSyncAsync()` never ran
- This is a common gotcha: preprocessor directives work in .cs files but not reliably in shared .razor components

**Fix:** Runtime platform detection instead of compile-time
- Changed from `#if IOS || ANDROID || MACCATALYST` to `if (SyncService != null && DeviceInfo.Platform != DevicePlatform.Unknown)`
- This ensures sync triggers on all mobile platforms (iOS, Android, Mac Catalyst, Windows) while web uses NoOpSyncService
- Added better logging: "Triggering CoreSync from dashboard refresh (Platform: {Platform})"
- Changed LogWarning to LogError for failed refreshes

**Pull-to-Refresh Research:**
- **Native RefreshView does NOT work with BlazorWebView** 
- RefreshView requires child to be a scrollable MAUI control (ScrollView, CollectionView, ListView)
- BlazorWebView is a WebView wrapper — scrolling happens inside web content, not at MAUI level
- Native gesture doesn't propagate into the WebView
- **Alternative:** Implement at Blazor level with JavaScript touch events (complex) or use refresh button (simpler)
- **Decision:** Keep refresh button as primary solution since native pull-to-refresh isn't feasible with Blazor Hybrid

**Key Learning:** 
- **CRITICAL:** Shared Blazor Razor components (.razor) in class libraries don't get platform-specific preprocessor symbols
- Always use runtime detection (`DeviceInfo.Platform`, `DeviceInfo.Idiom`) in .razor files instead of `#if` directives
- Only use preprocessor directives in platform-specific head projects or in .cs code-behind files

**Files Modified:**
- `src/SentenceStudio.UI/Pages/Index.razor` — RefreshDashboardAsync() method

**Build Status:** Both WebApp and iOS build clean (0 errors, pre-existing warnings only)


### 2026-03-20 — Blazor Virtualize Implementation

**Task:** Replace `@foreach` loops with Blazor's `<Virtualize>` component on vocabulary and resource list pages to handle large datasets (2000-3000+ items) efficiently.

**Pages Modified:**
1. **Vocabulary.razor** — Main vocabulary list
   - Replaced grid view `@foreach` with `<Virtualize Items="filteredWords">`
   - Replaced list view `@foreach` with `<Virtualize Items="filteredWords">`
   - Scrollable container: `calc(100vh - 380px)` height for responsive layout
   - Grid items wrapped in single-column row per item (Virtualize doesn't support CSS Grid/Flexbox directly)

2. **Resources.razor** — Learning resources list
   - Replaced both grid and list view `@foreach` with `<Virtualize Items="resources">`
   - Removed "Show More" pagination button and logic
   - Removed pagination state: `resourcesPageSize`, `resourcesDisplayCount`, `displayedResources`, `ShowMoreResources()`
   - Scrollable container: `calc(100vh - 280px)` height
   - Updated count display from "Showing X of Y" to just "X resource(s)"

3. **ChannelDetail.razor** — YouTube video discovery list
   - Replaced `@foreach` with `<Virtualize Items="discoveredVideos">`
   - Scrollable container: `50vh` height (half viewport for this context)
   - Video list shows import status (Imported, Failed, In Progress) with action buttons

**Pattern Established:**
```razor
<div style="height: calc(100vh - Xpx); overflow-y: auto;">
    <Virtualize Items="filteredList" Context="item">
        <!-- item template here -->
    </Virtualize>
</div>
```

**Key Points:**
- Search/filter operates on the **full dataset** in memory, then passes filtered results to Virtualize
- Grid view workaround: Each Virtualize item contains `<div class="row g-3 mb-3">` with single column — Virtualize renders items vertically
- No changes to search, filter, or sort logic — all existing functionality preserved
- Removed pagination UI and state management code from Resources page

**Build & Deploy:**
- WebApp build: Clean (0 errors)
- iOS build: Clean (0 errors)
- Committed to main: `75c19ca` — "Implement Blazor Virtualize on vocabulary and resource lists"
- Pushed to GitHub
- Deployed to Azure: 2m25s (SUCCESS)
- Installed on iPhone (DX24 device): Success

**Testing Notes:**
- Vocabulary list with 2000+ words now renders only visible items
- Smooth scrolling on mobile — no janky rendering of 2000+ DOM nodes
- Search/filter still works on full dataset before passing to Virtualize
- Resources page no longer requires clicking "Show More" repeatedly

**Technical Learnings:**
- Blazor's `<Virtualize>` works in Blazor Hybrid (WebApp and MAUI)
- `Context="item"` parameter names the iteration variable
- Fixed-height scrollable container is required for Virtualize to work (uses viewport to calculate visible range)
- Grid layout workaround: wrap each item's row inside the Virtualize template
- For lists with dynamic filtering, load all data and filter before Virtualize (cleaner than ItemsProvider delegate for our use case)

**Performance Impact:**
- Initial render: Fast (only ~20-30 items rendered, not 2000+)
- Memory: Entire dataset still in memory (acceptable for 2000-5000 items)
- Scrolling: Smooth — Virtualize dynamically adds/removes DOM nodes as user scrolls
- Search: Still operates on full dataset — no performance regression

**Next Steps:**
- Monitor user feedback on scrolling performance
- Consider skeleton loading placeholders during data load (Phase 2)
- Evaluate if any other pages need virtualization (e.g., quiz results history)

### 2026-03-21 — What's New Modal Feature (Issue #XX)

**WhatsNewModal Component (Shared/WhatsNewModal.razor):**
- Bootstrap 5 modal with centered, scrollable dialog
- Parameters: IsVisible, OnDismissed, Title, Version, Content (HTML)
- Uses bi-megaphone icon (no emojis, per project rules)
- Modal overlay via `d-block` + backdrop RGBA
- Modal content renders as MarkupString (accepts pre-rendered HTML)
- "Got it!" button with bi-check-circle icon

**MainLayout Version Check:**
- Injected ReleaseNotesService
- OnAfterRenderAsync calls CheckVersionAndShowWhatsNew() after auth confirmed
- Reads "last_seen_version" from Preferences
- Gets current version via Assembly.GetName().Version.ToString(2) (major.minor format)
- Shows modal on first run or after version bump
- DismissWhatsNew() saves current version to Preferences
- Includes ConvertMarkdownToHtml() helper: headers, bold, italic, links, lists, paragraphs

**Settings Page Updates:**
- Dynamic version in About section: "SentenceStudio v@currentVersion"
- "What's New" button with bi-megaphone icon
- ShowReleaseNotes() fetches notes for current version via ReleaseNotesService
- Reuses WhatsNewModal component (same instance as MainLayout)
- ConvertMarkdownToHtml() duplicated (could be extracted to utility class)

**Key Patterns:**
- Assembly version format: `.ToString(2)` → "1.0" (major.minor)
- Simple markdown-to-HTML: Regex replacements for common patterns (headers, bold, italic, lists)
- No Markdig dependency — lightweight inline conversion
- Bootstrap 5 modal classes: `modal-dialog-centered`, `modal-dialog-scrollable`
- State management: boolean + EventCallback pattern for modal visibility

**Decision Trade-offs:**
- Duplicated ConvertMarkdownToHtml() in MainLayout + Settings → could extract to shared utility
- Simple markdown parser sufficient for release notes (no complex nested lists or code blocks)
- WhatsNewModal is stateless — parent controls visibility and content

**Build Status:** Clean build (0 errors, pre-existing warnings only)

### 2026-03-21 — UI Polish Wave: Import History, Resources Toggle, Channel Discovery, Pagination

**Status:** Complete  
**Tasks:** 4 UI improvements from Captain's feedback

**Task 1 — Import History Tighter Layout:**
- Replaced card layout with `list-group` / `list-group-item-action` pattern (matching Vocabulary list view)
- Removed "View Resource" button — whole row is now clickable
- Added channel name lookup via `GetChannelName()` using existing `channels` list
- Status badge | title | channel name | date on each row
- Failed imports show inline error below the row

**Task 2 — Resources Page Grid/List Toggle:**
- Added `btn-group` toggle (bi-grid-3x3-gap / bi-list-ul) in filter bar at `ms-auto`
- Grid view = existing card layout, List view = `list-group` with media icon, title, type+lang, date
- Persists preference via `IPreferencesService` (key: `resources-view-mode`)
- Injected `IPreferencesService` (was not previously injected on Resources page)

**Task 3 — Channel Detail Video Discovery:**
- Added "Discover Videos" section below form for existing channels (edit mode only)
- "Check for New Videos" calls `ChannelMonitorSvc.GetRecentVideosAsync(channel)`
- Each video checked via `IsVideoAlreadyImportedAsync` — already-imported shown as disabled/greyed
- Checkboxes for selection, "Select All New" toggle, "Import Selected" batch action
- Creates `VideoImport` objects matching Worker.cs pattern, runs pipeline via `Task.Run()`
- Injected `VideoImportPipelineService` into ChannelDetail

**Task 4 — Pagination for Long Lists:**
- Import History: `displayedImports` computed property, 50-item pages, "Show More" button
- Resources: `displayedResources` computed property, 50-item pages, "Show More" button
- Both show "Showing X of Y" count

**Build Verification:** 0 errors, 298 warnings (all pre-existing)

**Learnings:**
- `list-group-item-action` with `@onclick` makes the whole row act as a button — cleaner than card+button
- `pe-none` Bootstrap utility disables pointer events — useful for non-actionable rows in a clickable list
- `record` types work in Blazor `@code` blocks for inline DTOs (e.g., `DiscoveredVideo`)
- `IPreferencesService.Set<T>()` persists view mode — simpler than JS localStorage for Blazor Hybrid

## 2026-03-28T01:15: Cross-Agent Update: Auth Token Lifetime

**Source:** Wash (Backend Dev) — auth token lifetime work  
**Impact on Kaylee:** WebApp authentication cookie now 90 days

**What Changed:**
- WebApp (Blazor Server) authentication cookie lifetime extended to 90 days
- JWT Bearer token (API) extended to 120 minutes
- Mobile instant JWT restore from SecureStorage (no unnecessary OAuth flow)
- Silent refresh timeout: 10 seconds

**For Kaylee's Awareness:**
- Users will stay signed in for 90 days on web (per device)
- WebApp doesn't need to refresh token unless explicitly testing refresh logic
- No UI changes required — authentication flow remains same
- If WebApp tests token expiry/refresh, test with 120-min JWT window + silent refresh fallback

**Related:** Squad decision #6 (auth token lifetime) — see `.squad/decisions.md`

### Onboarding Routing Fixes (2025-07-15)

Fixed four bugs preventing new users from reaching the Onboarding.razor flow:

1. **MainLayout.razor** — onboarding bypass check now requires `TargetLanguage` AND `NativeLanguage` AND `Name` (previously missing `NativeLanguage`)
2. **LoginPage.razor** — post-login redirect now checks `is_onboarded` pref; sends un-onboarded users to `/onboarding` instead of `/`
3. **Auth.razor** — local profile selection no longer unconditionally sets `is_onboarded = true`; only does so when profile has all three fields
4. **Index.razor** — "Quick Start" starter content no longer hardcodes "Korean"; reads user's `TargetLanguage` from profile; redirects to `/onboarding` if language is missing

## Learnings (continued)

- The `is_onboarded` preference is the routing gate for onboarding — MainLayout checks it on every page load
- `ProfileRepo.GetAsync()` returns the active user's profile (or null) — use it for language checks
- `AppState.CurrentUserProfile` may be null at dashboard load; always fall back to `ProfileRepo.GetAsync()`
- Onboarding.razor sets `is_onboarded = true` only at the end of its own `FinishOnboarding()` — don't set it elsewhere unless profile is provably complete
