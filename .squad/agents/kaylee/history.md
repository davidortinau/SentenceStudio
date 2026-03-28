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

### 2026-07-23 — Dashboard Refresh Feature (Captain Request)

**Status:** Complete  
**Feature:** Manual refresh button on dashboard for both mobile and web

**Key Changes:**
1. **PageHeader ToolbarActions** — Added refresh icon button (`bi-arrow-clockwise`) visible on all screen sizes
   - Button placed in `<ToolbarActions>` slot to render in header toolbar (mobile + desktop)
   - Spinning animation while refreshing via CSS `.spin` class
   - Disabled state during refresh to prevent multiple concurrent refreshes

2. **RefreshDashboardAsync() method:**
   - On mobile (IOS/ANDROID/MACCATALYST): Triggers `SyncService.TriggerSyncAsync()` first, then reloads data
   - On web: Just reloads dashboard data from PostgreSQL
   - Reloads vocabulary stats via `LoadVocabStatsAsync()`
   - Conditionally reloads plan via `LoadPlanAsync()` if in Today's Plan mode
   - Uses `isRefreshing` boolean flag + `StateHasChanged()` for UI feedback

3. **CSS animation:**
   - Added `@keyframes spin` + `.spin` class to `app.css`
   - Simple 360deg rotation, 1s linear infinite
   - Applied conditionally to icon: `@(isRefreshing ? "spin" : "")`

4. **Service injection:**
   - `ISyncService` injected as nullable (`ISyncService?`) since it's only registered on mobile clients
   - WebApp doesn't have SyncService (uses PostgreSQL directly), so null-check before calling

**UI Pattern Notes:**
- `ToolbarActions` slot renders icon buttons in PageHeader at all screen sizes — perfect for mobile refresh button
- Refresh in "Today's Plan" mode keeps existing "Regenerate Plan" dropdown option (different UX: refresh = reload current, regenerate = AI generates new plan)
- Refresh in "Choose My Own" mode just reloads stats (no plan to refresh)

**Learnings:**
- `PageHeader` ToolbarActions slot is the correct place for icon-only actions that should appear on mobile + desktop
- Nullable service injection pattern: `[Inject] private ISyncService? SyncService { get; set; }` — services that don't exist in all contexts (WebApp vs MAUI)
- Preprocessor directives in Blazor: `#if IOS || ANDROID || MACCATALYST` isolates mobile-specific code paths
- CSS `@keyframes` + conditional class binding for loading spinners: `@(isRefreshing ? "spin" : "")`
- Dashboard data reload pattern: trigger sync (mobile only) → reload stats → reload plan if applicable

**Build Status:** Clean build (0 errors, pre-existing warnings in Shared project only)

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

### 2026-07-23 — UI Polish Wave: Import History, Resources Toggle, Channel Discovery, Pagination

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


### 2026-03-20 — Mobile UX Research (Research Task)

**Deliverable:** `docs/mobile-ux-research.md` — comprehensive research on mobile-native UX patterns for Blazor Hybrid

**Key Findings:**

1. **Blazor Virtualize Component:**
   - Works in Blazor Hybrid, built-in, handles variable-height items
   - Recommended approach: Load full dataset into memory, filter it, pass to `<Virtualize Items="filteredList">`
   - For 2000-5000 vocabulary words, this is efficient and enables full-dataset search/filter
   - Pattern: Replace `@foreach` with `<Virtualize>`, set `ItemSize` estimate, use scrollable container with fixed height
   - Alternative: `ItemsProvider` delegate for true infinite scroll with database pagination (for 10,000+ items)

2. **Pull-to-Refresh:**
   - **Native RefreshView CANNOT wrap BlazorWebView** (confirmed in research + prior experience)
   - BlazorWebView is the scroll container; RefreshView needs to be ancestor of scrollable content
   - JavaScript-based pull-to-refresh is possible but complex (touch events + CSS overscroll-behavior)
   - **Recommendation:** Use refresh button in toolbar instead of pull-to-refresh for MVP
   - Already established pattern: toolbar actions in PageHeader component

3. **Mobile Touch Patterns:**
   - **Swipe actions:** Require JS interop (detect touch events, animate reveal). Alternative: long-press + action sheet (simpler)
   - **Bottom sheets:** Already using Bootstrap `offcanvas-bottom` with `d-md-none` (established pattern)
   - **Haptic feedback:** Available via `HapticFeedback.Default.Perform()` — wrap in service, inject into components
   - **Skeleton loading:** Pure CSS solution, better than spinners for perceived performance

4. **Platform Detection:**
   - Use `DeviceInfo.Platform` for runtime checks (not preprocessor directives in .razor files)
   - Pattern: Create `IPlatformService` wrapper with `IsMobile`, `IsWeb` properties
   - Prefer CSS media queries (`d-md-none`, `d-none d-md-block`) for layout differences
   - Use C# platform checks only for functional differences (e.g., Virtualize vs pagination, enable haptics)

**Priority Roadmap:**
1. **Phase 1 (Immediate):** Replace `@foreach` with `<Virtualize>` in Vocabulary and Resources pages
2. **Phase 2 (High):** Add skeleton loading placeholders during data load
3. **Phase 3 (Medium):** Implement haptic feedback service for quiz grading and key actions
4. **Phase 4 (Low):** Add long-press action sheets for list item actions
5. **Phase 5 (Low):** Defer pull-to-refresh unless user feedback demands it

**Technical Learnings:**
- Blazor `<Virtualize>` handles variable-height items automatically (uses `ItemSize` as estimate, measures actuals)
- Search/filter with virtualization: Maintain full dataset in memory, filter it, pass filtered list to Virtualize
- Platform-conditional rendering: Hybrid approach (CSS for layout, C# for behavior)
- MAUI HapticFeedback API: Two types (Click, LongPress), check `IsSupported` first

**Next Steps:**
- Review research with Captain
- Create GitHub issues for Phase 1 and 2
- Prototype Virtualize in Vocabulary.razor to validate approach

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

