# Kaylee's History

## Learnings

- 2026-04-17: **Dynamic Platform Detection in Shared UI** — When wiring platform-specific features (HelpKit) into portable UI projects, use runtime type resolution via `Type.GetType()` + reflection to invoke methods. Keeps UI project browser-only (no MAUI refs), works in both MAUI and WebApp contexts, graceful degrade on missing types. Applied in NavMenu.razor for Help button.
- 2026-04-17: HelpKit Alpha — native chat UI + 3 samples (Shell/Plain/MauiReactor) shipped.

### Activity Log UI Implementation (2025-05-XX)

Built the complete Activity Log feature (Strava-inspired Practice Calendar) for SentenceStudio.UI:

**Navigation Setup:**
- Added "Activity" as the second nav item in NavMenu.razor (after Dashboard, before Resources)
- Registered `/activity-log` route in NavigationMemoryService.cs at index 1

**Components Created:**
- **ActivityLog.razor**: Main page with filtering (All/Input/Output), weekly cards, expandable day details, and "Load More" pagination (starts with 8 weeks, loads 4 more at a time)
- **ActivityDot.razor**: Visual indicator with size based on minutes (sm <10, md 10-25, lg 25+) and color based on activity type (blue=Input, orange=Output, gradient=Both, green border=Complete)
- **PlanSummaryCard.razor**: Expandable detail showing original plan items with completion status, minutes spent, and activity breakdown

**CSS Additions:**
- Added activity-dot styles to app.css: size variants (sm/md/lg), color classes (input/output/split), hover effects, and completion indicator

**Layout Patterns:**
- Used responsive Bootstrap utilities for mobile/desktop differences (single-char day names on mobile, full names on desktop)
- PageHeader component with ToolbarActions (refresh) and SecondaryActions (filter dropdown)
- Card-based layout following existing patterns (card-ss, ss-body1, ss-caption1 typography classes)
- Week header shows date range and total minutes; 7-day grid shows dots or "Rest"; expandable detail shows full plan breakdown

**Key Decisions:**
- No year sidebar (keeps mobile-first, week cards are self-explanatory with date ranges)
- Filter re-queries rather than client-side filtering (keeps service layer responsible for data)
- Toggle day detail on tap (simpler than modal, keeps context visible)
- Used hardcoded English strings with TODO comments for future localization (prioritized getting UI working)

### Activity Log UI Implementation (2026-04-16)

Built complete Activity Log feature UI for SentenceStudio.UI (Strava-inspired Practice Calendar):

**Components Created:**
- **ActivityLog.razor**: Main page with All/Input/Output filtering, weekly card layout, expandable day details, "Load More" pagination (8 weeks initial, 4 at a time)
- **ActivityDot.razor**: Visual indicator—size by minutes (sm <10, md 10-25, lg 25+), color by type (blue=Input, orange=Output, gradient=Both), green border for completion
- **PlanSummaryCard.razor**: Expandable detail showing all day activities with resource title, minutes, completion status

**Navigation & Routing:**
- Added "Activity" as second nav item in NavMenu.razor (after Dashboard)
- Registered `/activity-log` route in NavigationMemoryService at index 1

**Styling (app.css):**
- `.activity-dot` with size/color variants, hover effects, responsive spacing
- Completion indicator styling (green border)

**Layout Patterns:**
- Responsive typography: single-char day names (mobile), full names (desktop)
- PageHeader with ToolbarActions (refresh) + SecondaryActions (filter dropdown)
- Card-based layout consistent with existing SentenceStudio.UI patterns (card-ss, ss-body1, ss-caption1)
- Week header shows date range and total minutes; 7-day grid shows dots or "Rest"

**Key Decisions:**
- No year sidebar (mobile-first, week cards self-explanatory)
- Service-owned filtering (no client-side filtering)
- Toggle day detail on tap (simpler than modal)
- Size-based dots show time investment at a glance

**Integration with Wash's DTOs:**
- Consumes ActivityLogWeek, ActivityLogDay, ActivityLogEntry from ProgressService
- Uses ActivityCategory enum for color/sizing logic
- 4-week pagination batches per UI spec

**Build Fixes (Coordinator):**
- Fixed Razor switch expression HTML parsing bug (`< 10` → `&lt; 10`)
- Fixed duplicate key in ToDictionary for resource/skill grouping

## 2026-04-17 — Plugin.Maui.HelpKit Alpha Scope Locked

Captain locked 8 decisions. Alpha scope frozen. Implications for Kaylee (BlazorWebView overlay):
- **Deferred for now:** BlazorWebView chat UI deferred to post-Alpha optional companion. Primary UI is native MAUI CollectionView + streaming.
- **SPIKE-2 unblocked:** Presenter abstraction spike ready to execute (native-first UI prototype)
- **Storage decided:** Microsoft.Extensions.VectorData (in-memory) + JSON — no sqlite-vec native build complexity in Alpha
- **TFM committed:** net11.0-* targets

SPIKE-2 focuses on native MAUI chat components, streaming text handling, and retry/error states.


### Plugin.Maui.HelpKit — Native chat UI + presenters (2026-04-17 Wave 3)

Shipped the native MAUI chat experience for HelpKit. Key implementation notes:

**Streaming rendering approach:**
- Built `HelpKitMessageViewModel` as an `INotifyPropertyChanged` wrapper around `HelpKitMessage`. User messages stay immutable; assistant messages mutate their `Content` as `IHelpKit.StreamAskAsync` yields snapshots. Each yielded `HelpKitMessage` is treated as the current full content (River's contract: later chunks supersede earlier ones; final chunk carries validated citations).
- `HelpKitPageViewModel.SendAsync` appends a placeholder assistant bubble showing "Thinking..." (localized), then replaces its `Content` on every stream iteration. On completion with no tokens, falls back to localized "no documentation" copy. On error, swaps the placeholder for a muted-red error bubble; never crashes the page.
- Rate-limit exceptions from Wash/River are detected by type-name (`*RateLimit*`) so we don't couple the UI project to the storage project's exception type. Falls back to generic error copy.
- Auto-scroll uses `CollectionView.ScrollTo(item, End, animate:true)` in both `ObservableCollection.CollectionChanged` and a dedicated `MessageAdded` event. `ItemsUpdatingScrollMode = KeepLastItemInView` keeps streaming tokens pinned.

**Presenter fallback strategy:**
- `DefaultPresenterSelector` is registered as **Transient** (not Singleton) so presenter selection re-evaluates on every `IHelpKit.ShowAsync` call. Rationale: Shell may be null at DI-build time but present later. A Singleton would cache the wrong choice forever.
- `HelpKitService` no longer injects `IHelpKitPresenter`; it resolves per-call via `IServiceProvider.GetRequiredService<IHelpKitPresenter>()`. This preserves the freshness guarantee for the Transient selector.
- `MauiReactorPresenter` now logs which path it takes (Shell vs Window) and wraps the Shell attempt in try/catch so a MauiReactor host with broken Shell still falls back to window-level nav. MauiReactor ultimately materialises plain MAUI pages, so the underlying navigation API is identical — no special Reactor-only code path was needed at this layer.
- `ShellPresenter` / `WindowPresenter` Zoe shipped were already correct; left untouched aside from the selector wiring.

**Localization pattern:**
- `HelpKitLocalizer` reads **embedded** `Strings.{lang}.json` via `Assembly.GetManifestResourceStream`. Files live in `Resources/` with an `<EmbeddedResource>` glob in the csproj. Avoids any runtime file lookup, works on all MAUI targets without extra asset-pipeline config.
- Static cache keyed by language — loaded once per process. Falls back to English then to the key itself so a missing key never renders empty UI.
- Injected as Singleton; page, view-model, and Shell flyout initializer all resolve it through DI.
- `HelpKitOptions.Language` drives selection. Alpha ships `en` + `ko`; adding a language = drop a JSON file + add to csproj glob.

**Theme approach:**
- `HelpKitThemeResources.ApplyDefaults(ResourceDictionary)` is **opt-in** via `HelpKitPage` calling it on its own `Resources` dictionary — never mutates `Application.Current.Resources` to avoid polluting host app state. Hosts that want custom colors override the same keys in their own dictionary.
- Light/dark resolved at `ApplyDefaults` time from `Application.Current.RequestedTheme`. Runtime theme-switching left to hosts (they register their own AppThemeBinding entries).

**Shell flyout helper:**
- `AddHelpKitShellFlyout()` registers a `HelpKitShellFlyoutInitializer` as `IMauiInitializeService`. Runs at app-ready, then defers to main-thread dispatch because `Shell.Current` isn't populated until after `Application.MainPage` is set.
- Appends a `MenuShellItem(new MenuItem { ... })` to `Shell.Items` — MenuShellItem is the public code-behind equivalent of XAML's `<MenuItem>` direct child of `<Shell>`. If the host isn't Shell-based, logs a warning and skips silently (never throws).

**Things I did NOT touch:**
- `Storage/`, `Ingestion/`, `RateLimit/`, `Scanner/` (Wash's) — per parallel work agreement.
- `Rag/` (River's).
- `HelpKitService.StreamAskAsync` body — it still throws NotImplementedException; River wires it in Wave 2/3 overlap.


---

## Learnings — HelpKit sample apps (Wave 3 follow-up)

**Sample matrix shipped:**
- `samples/HelpKitSample.Shell/` — full Shell host with Dashboard/Profile/About flyout tabs, demonstrates `AddHelpKitShellFlyout("Help")`. ShellPresenter is what the default selector picks at runtime.
- `samples/HelpKitSample.Plain/` — `NavigationPage(new MainPage())` host, no Shell. ToolbarItem + body button both invoke `_helpKit.ShowAsync()` via constructor-injected `IHelpKit`. WindowPresenter is the default-selector fallback here.
- `samples/HelpKitSample.MauiReactor/` — `Component<MainPageState>` rendering a label + Ask Help button. `UseMauiReactorApp<MainPage>(...)` plus `AddHelpKit(...)`. Resolves IHelpKit from `Application.Current.Handler.MauiContext.Services` (no constructor injection because MauiReactor Components are not DI-instantiated).

**DRY pattern that worked:**
- `samples/SharedStubs/{StubChatClient,StubEmbeddingGenerator,SampleHelpContentInstaller}.cs` — single source of truth for stub providers and the asset-copy installer.
- Each sample csproj links them in via `<Compile Include="..\SharedStubs\*.cs" LinkBase="SharedStubs" />`. No duplication, no project-reference indirection.
- Sample markdown lives in `samples/SampleHelpContent/*.md` and is bundled per-sample as `<MauiAsset Include="..\SampleHelpContent\*.md" LogicalName="help-content\%(Filename)%(Extension)" />`. Same content, three copies in built APKs/IPAs/MSIXs — acceptable for samples.

**Stub chat client design:**
- Implements full `IChatClient` (sync `GetResponseAsync` + streaming `GetStreamingResponseAsync`).
- Picks one of four canned answers via `unchecked hash % length` of last user message — deterministic so demos are stable across launches, but varied across questions.
- Streams in 24-char chunks with a 30ms delay so the UI animates like a real model.
- Includes `[cite:filename.md]` tokens in answers to exercise River's citation validator once it's wired.

**Stub embedding generator design:**
- 32-dim float vector via FNV-1a-ish expansion + L2 normalization. Same input always returns the same vector — keeps the pipeline fingerprint stable so re-ingest doesn't loop.
- Reports its dimensionality via `EmbeddingGeneratorMetadata` so any downstream code that asks gets the right answer.

**MauiReactor integration notes:**
- MauiReactor Components are NOT DI-resolved, so the canonical pattern for grabbing services inside a Component is `Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<T>()`. Documented in `MainPage.cs`.
- Followed Captain's MauiReactor conventions: `VStart()`, `HStart()`, `Center()`, `OnClicked`, fluent `Padding` — zero `HorizontalOptions`/`VerticalOptions` calls.
- Pinned `Reactor.Maui` 3.1.0 with a comment that the version may need bumping per net11 preview compatibility — it's a sample, not a shipping dependency.

**Help-content delivery:**
- MAUI assets are not direct-readable as filesystem paths — `FileSystem.OpenAppPackageFileAsync` is the only way in. HelpKit's `ContentDirectories` requires real paths, so `SampleHelpContentInstaller.EnsureInstalledAsync()` copies bundled assets into `{AppDataDirectory}/help-content` on first run, then HelpKit ingests from there.
- Each sample triggers the install + ingest at the right host-specific moment: AppShell `Loaded`, MainPage `OnAppearing`, MauiReactor `OnMounted`. All three swallow `NotImplementedException` so the Wave 2 ingest gap doesn't crash launches.

**Build status:** Per Zoe's environmental note, the local box doesn't have the net11 preview SDK + MAUI workload, so `dotnet restore` will surface `NETSDK1139`. Documented in each sample README; not blocking. Once the workload is installed (or CI picks them up) restore should complete.
