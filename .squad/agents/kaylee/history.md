# Kaylee's History

## Core Context

**2025–2026 Overview:**
- Shipped Activity Log feature: Strava-inspired Practice Calendar UI (ActivityLog.razor, ActivityDot, PlanSummaryCard), Bootstrap-based responsive styling, weekly pagination with expandable day details
- Platforms: Both Blazor (SentenceStudio.UI) and native MAUI (HelpKit samples)
- Key patterns: Responsive dot sizing by time (sm/md/lg), filtering via service layer (not client-side), CardStyle + Typography (ss-body1, ss-caption1), Bootstrap utilities for mobile/desktop
- Role in HelpKit: Primary architect of native chat UI — `HelpKitPage` (CollectionView, streaming mutation, auto-scroll), `HelpKitMessageViewModel` (immutable user, mutable assistant messages), `DefaultPresenterSelector` (Transient for freshness), Shell/Window/MauiReactor presenters
- HelpKit platform detection: Dynamic `Type.GetType()` reflection (NavMenu.razor) keeps UI portable; NavMenu.razor Help button shown only in MAUI via `IHelpKit` availability check
- **2025-01-24:** Vocabulary Classification & Constituents — Added Word/Phrase/Sentence picker and autocomplete constituent editor to VocabularyWordEdit.razor and bulk-add preview table to ResourceAdd.razor. Applied heuristic classification on paste/import. EF Core-based PhraseConstituent persistence. See `.squad/decisions/inbox/kaylee-ui-import-edit.md`

## Learnings

- 2026-07-27: **Import Content Style Normalization** — Audited and cleaned all bespoke inline styles from ImportContent.razor. Key fixes: replaced custom purple hex badge (`#6f42c1` + inline CSS vars) with `bg-secondary bg-opacity-10 text-secondary`; replaced inline `cursor:pointer` with `role="button"` (matching app.css:1360 generic rule); replaced inline `font-size:0.75rem` with Bootstrap `small` class; removed dead empty class expression on mobile card. Also added Duplicate badge column to preview table using `bg-warning bg-opacity-10 text-warning` after Wash landed `IsDuplicate`/`DuplicateReason` on `ImportRow`. 5 new resx keys (EN+KO). Decision doc in `.squad/decisions/inbox/kaylee-import-style-cleanup.md`. Pattern: type badges always use `badge bg-{color} bg-opacity-10 text-{color}`; clickable non-buttons always use `role="button"` not inline cursor; no hex colors in Razor markup.
- 2026-07-14: **Import Complete Per-Row Detail Table + State Preservation** — Built the detailed Import Complete view with per-row results table (Lemma, Native, Type badge, Status pill, Reason column), filter pills (All/Created/Updated/Skipped/Failed), mobile-responsive card fallback, and clickable rows linking to `/vocabulary/edit/{id}`. State preservation across back-nav via `IImportResultStore` (Singleton, ConcurrentDictionary, 30-min TTL) + URL query param `?completed={guid}`. Key pattern: save result to in-memory store on commit, navigate with key in URL, hydrate from store on init if key present. Reusable skill written to `.squad/skills/blazor-nav-state-preservation/SKILL.md`. Decision doc in `.squad/decisions/inbox/kaylee-import-result-store.md`. 8 new resx keys (en + ko).
- 2026-04-27: **Preview-to-Commit DTO Mapping Discipline** -- When constructing a DTO from an API response for a round-trip (preview -> user edit -> commit), EVERY property on the source DTO must be accounted for in the object initializer. Properties that default to a value (like enums defaulting to their first member) will silently swallow data from the backend. Going forward: when writing or reviewing any DTO-to-DTO mapping in ImportContent.razor (or similar round-trip flows), enumerate all source properties and explicitly decide map-or-skip for each one. This prevents the "silent default" class of bug that caused BUG-2 and BUG-3 to persist despite correct backend fixes.
- 2026-04-25: **v1.1 Import Harvest Checkboxes + Auto-detect Banner** — Replaced disabled v2 badges on Phrases/Transcript/Auto options with full enablement. Added three independent harvest checkboxes (Transcript/Phrases/Words) with defaults per content type (Captain's directive). Implemented three-tier confidence gate for auto-detect (high/medium/low bands at 0.85/0.70 thresholds) with always-visible banner and [Change] override. Confidence gate runs BEFORE any DB persistence per D3. Added `HarvestTranscript`/`HarvestPhrases`/`HarvestWords` booleans to `ContentImportCommit` DTO for Wash integration. No emojis — Bootstrap icons only. Documented in `.squad/decisions/inbox/kaylee-v11-ui.md`.
- 2026-04-23: **Word/Phrase Feature Completed** — Completed ui-import-edit todo: added LexicalUnitType dropdown (Word/Phrase/Sentence) to VocabularyWordEdit.razor with constituent word editor (search-as-you-type autocomplete, badge display, remove buttons). Updated ResourceAdd.razor with preview table showing classification + override per row. Applied VocabularyClassificationBackfillService.ClassifyHeuristic() on paste/import. 14 new UI strings pending localization. Feature shipped, 147 tests passing. Documented in `.squad/log/2026-04-23T2219Z-wordphrase-squad-wrap.md`.
- 2026-04-19: **Phase 2 Localization Review Lockout (Reviewer Rejection Protocol)** — After code review rejection, you were frozen from the fix cycle per protocol. Lead (Zoe) took ownership and found 30 missing resx keys + Skills.razor missing IDisposable/CultureChanged. **Key takeaway:** Run grep-all-keys verification before declaring resx batches complete. Verify all Blazor components with `@inject BlazorLocalizationService` have `@implements IDisposable` + event subscription pattern. This would have caught both blockers pre-review.
- 2026-04-17: **Dynamic Platform Detection in Shared UI** — When wiring platform-specific features (HelpKit) into portable UI projects, use runtime type resolution via `Type.GetType()` + reflection to invoke methods. Keeps UI project browser-only (no MAUI refs), works in both MAUI and WebApp contexts, graceful degrade on missing types. Applied in NavMenu.razor for Help button.
- 2026-04-17: HelpKit Alpha — native chat UI + 3 samples (Shell/Plain/MauiReactor) shipped.
- **2025-01-24:** **Blazor Autocomplete Pattern** — Reused Vocabulary.razor's search-as-you-type autocomplete pattern for constituent word picker. Key: debounce on `oninput`, clear suggestions on select, `showAutocomplete` flag for dropdown visibility, scoped queries (language filter + exclude self + already-selected).

## Recent Work

### Vocabulary Classification & Constituents (2025-01-24)

**Scope:** Blazor-only UI update for Word/Phrase/Sentence classification + constituent word editing.

**VocabularyWordEdit.razor:**
- Added LexicalUnitType dropdown (Word/Phrase/Sentence, hiding Unknown) in Encoding & Memory Aids card
- Added constituent editor card (shown only for Phrase/Sentence types):
  - Search-as-you-type autocomplete (2+ chars, filters by language, excludes current word + already-selected)
  - Bootstrap badge display with `bi-x` remove buttons
  - Warning message when changing Phrase/Sentence → Word with existing constituents
- Persistence via EF Core: LoadConstituentsAsync, diff insert/delete, clear all when changed to Word
- Injected `IServiceProvider` for DbContext access to `PhraseConstituents`

**ResourceAdd.razor:**
- Replaced simple count display with preview table (Target/Native/Type/Tags/Remove)
- LexicalUnitType dropdown per row (pre-filled with heuristic classification, user can override)
- Applied `VocabularyClassificationBackfillService.ClassifyHeuristic()` on paste/file import

**Heuristic:** Priority: tags check → terminal punctuation → whitespace/length → default Word. No local duplication — used public static method from Shared.Services.

**Deliverables:**
- `.squad/decisions/inbox/kaylee-ui-import-edit.md` — full writeup with strings-to-localize list
- Build green (SentenceStudio.UI + AppHost)
- No MauiReactor changes (per spec)

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

## Recent Work

### Activity Log UI Implementation (2026-04-16)

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

## Learnings — Blazor locale recon (2026-04-18)

Ground-truth recon for Zoe on why the Profile → Display Language selector is broken post-MauiReactor→Blazor migration. Report in `.squad/decisions/inbox/kaylee-blazor-locale-recon.md`. Key findings:

- **Resx inventory:** Single pair `AppResources.resx` (488 keys, en) + `AppResources.ko-KR.resx` (427 keys — 61-key translation gap) wired as EmbeddedResource in `SentenceStudio.Shared.csproj:61-68` with strongly-typed designer. HelpKit has its own `Strings.{en,ko}.json` in `lib/`.
- **Blazor usage:** ~99% hardcoded English literals. `grep Localize[...]` across `src/SentenceStudio.UI/` returns **2 call sites total**, both in `Layout/NavMenu.razor` for the "Help" label. Zero `@inject IStringLocalizer<T>` in the whole UI project. The resx is effectively orphaned from Blazor.
- **The bug itself:** `Profile.razor:262 SaveProfile()` writes `profile.DisplayLanguage = displayLanguage` → `ProfileRepo.SaveAsync(profile)` and stops. It does NOT call `UserProfileRepository.SaveDisplayCultureAsync()` (which exists at `UserProfileRepository.cs:307` and *would* call `LocalizationManager.Instance.SetCulture(...)`) — `SaveDisplayCultureAsync` has zero callers anywhere in the solution.
- **Startup wiring:** No `AddLocalization()`, no `SupportedCultures`, no `CultureInfo.DefaultThreadCurrentUICulture` at boot, no code path that reads `DisplayLanguage` back from the DB on launch. `LocalizationManager.Initialize(logger)` is also uncalled (dead).
- **Legacy remnants still wired:** `LocalizationManager` still used by MauiReactor-era `AppLib` services (`ScenarioService`, `LocalizeExtension`, `FilterChip`) — so it can't just be deleted. Blazor side uses `BlazorLocalizationService` as a thin wrapper around `LocalizationManager.Instance` (registered Singleton in `BlazorUIServiceExtensions` + `MacOSMauiProgram`) but consumers = 1.
- **Pattern for future fix (not applied):** Either (a) migrate Blazor UI to `IStringLocalizer<T>` idiomatically, or (b) wire `SaveDisplayCultureAsync` into `SaveProfile` AND add startup culture restore AND begin mass-converting hardcoded Razor strings to `@Localize["Key"]`. Either way, the hardcoded-string debt dwarfs the plumbing bug.

- 2026-04-24: **Display Language restoration — Phase 1 delivered.** Wired the full pipeline end-to-end:
  - `BlazorLocalizationService` → scoped per Blazor circuit, holds own CultureInfo, exposes `CultureChanged` event. No process-wide mutation in the WebApp (prevents cross-user bleed).
  - `LocalizationManager.GetString(key, culture?)` public helper added so the scoped Blazor service can reach the internal `AppResources.ResourceManager` without InternalsVisibleTo.
  - `WebApp/Program.cs`: `AddLocalization` + `UseRequestLocalization` with Cookie→AcceptLanguage providers; culture middleware runs BEFORE auth/static-assets.
  - Added `/account-action/SetCulture?culture=ko&returnUrl=/profile` endpoint — Blazor Server circuits can't write cookies over WebSocket, so Profile save redirects through this.
  - `LocalizationInitializer : IMauiInitializeService` registered in `SentenceStudioAppBuilder` applies the saved `UserProfile.DisplayLanguage` on MAUI launch — no more "OS culture wins" on cold start.
  - NavMenu + Profile fully localized with `Nav_*` / `Profile_*` resource keys (56 new pairs in en + ko-KR). Components subscribe to `CultureChanged` and `StateHasChanged`.

- 2026-04-19: **MAUI Locale Fix (Phase 1 Follow-Up).** Fixed parallel bug diagnosed by Wash: `LocalizationInitializer` ran BEFORE `active_profile_id` was set, causing wrong locale on launch. Solution: two-phase restoration (boot-time if preference set, post-login after `IdentityAuthService.StoreTokens`). Extracted `ApplyLocaleFromProfile()` helper to DRY the locale application logic. Added culture validation against supported cultures (`en`, `ko`). Handles all edge cases: fresh login, JWT restore, multi-profile, null DisplayLanguage, unsupported culture. Build: 0 errors. Handoff to Jayne for E2E (6 test scenarios in `.squad/decisions/inbox/kaylee-maui-locale-fix-impl.md`).

## Learnings

- 2026-04-24: **Blazor Server culture: singletons are a bug trap.** The default `LocalizationManager` sets `DefaultThreadCurrentUICulture` globally — fine in a single-user MAUI client, fatal on a multi-user server (user A's language bleeds onto user B's next request on the same thread). On the WebApp side, localization services must be scoped (per circuit), hold their own `CultureInfo`, and never write process-wide statics.
- 2026-04-24: **Blazor Server cookie writes require an HTTP endpoint.** Razor components running over SignalR cannot write Response cookies. Redirect through a MapGet endpoint with `forceLoad:true`; the endpoint writes the cookie and redirects back. See `AccountEndpoints.SetCulture` for the pattern — reuse for any "component wants to set a cookie" need.
- 2026-04-24: **Detect WebApp vs MAUI Blazor Hybrid via `NavigationManager.BaseUri`.** The existing NavMenu uses `!baseUri.StartsWith("app://") && !baseUri.Contains("0.0.0.0")`. Reuse this check to decide between MAUI-style direct `LocalizationManager.Instance.SetCulture` and WebApp-style cookie-redirect.
- 2026-04-24: **Resx access boundary.** `AppResources` is `internal` in `SentenceStudio.Shared`. Any cross-project consumer must go through `LocalizationManager` (same assembly) — don't add `InternalsVisibleTo`; expose a targeted public helper on the manager instead.
- 2026-04-18: **AutoSignIn endpoint state-write timing.** When an HTTP endpoint both queries a user entity AND needs to set client state (cookie, preference), do the state write AFTER the entity is linked/created but BEFORE sign-in. This ensures the cookie is available on the first request after redirect, without requiring a separate middleware pass. Applied in `AccountEndpoints.AutoSignIn` to fix the locale restoration bug — cookie written after `UserProfileId` link (line 144) but before `SignInManager.SignInAsync` (line 172), so the first circuit creation sees the persisted culture.
- 2026-04-19: **MAUI startup service timing matters.** `IMauiInitializeService.Initialize()` fires at `builder.Build()` time, BEFORE preferences are populated by the auth flow. If an initializer depends on user state (like `active_profile_id`), it must check if that state is available and defer to a post-login hook if not. For locale restoration, the fix was two-phase: boot-time if preference set, post-login after `IdentityAuthService.StoreTokens`. Extract shared logic to a static helper so both paths call the same code (DRY).
- 2026-04-19: **Validate user input against known-good whitelists, even for culture strings.** `LocalizationManager.SetCulture()` accepts any `CultureInfo`, but MAUI projects ship with specific resource files (en + ko-KR). Added explicit validation in `ApplyLocaleFromProfile()`: normalize `"ko-KR"` → `"ko"`, reject unsupported cultures (log warning, fall back to OS). Prevents crashes from malformed DB values or future locales not yet shipped.

- 2026-04-24: **Blazor Localization Skill Published** — Scoped BlazorLocalizationService per-circuit with CultureChanged event. GetString override on LocalizationManager to read resx without mutating statics. Cookie persistence via GET /account-action/SetCulture endpoint (pattern matches /SignOut, /AutoSignIn). Culture identifier alignment critical across DB/cookie/whitelist/resx (use neutral ko not ko-KR). Follow-ups: HttpOnly/Secure flags, CSRF on GET, toast timing, async init. Pattern documented in .squad/skills/blazor-localization/SKILL.md for Phase 2 mass-localization.

- 2026-04-18: **Phase 1 Follow-Up: Load-Time Culture Cookie Fix** — Fixed P0 bug where saved `DisplayLanguage` didn't apply on login. Added culture cookie write to `AccountEndpoints.AutoSignIn` endpoint (after `UserProfileId` link, before `SignInAsync`). Extracted `SupportedCultures` array to DRY the whitelist across `SetCulture` and `AutoSignIn`. Cookie options match Phase 1 pattern exactly (1-year expiry, `IsEssential=true`, `SameSite=Lax`, `HttpOnly=false`). Edge cases handled: NULL DisplayLanguage (skip write, fallback to Accept-Language), unsupported culture (skip write), anonymous users (gated by UserProfileId check). Build verified (0 errors). Handoff to Jayne for E2E: fresh login applies locale, cross-user isolation, cookie persistence, Profile save regression check. Phase 3 tech debt: HttpOnly/Secure hardening deferred per Zoe's earlier review.


---

## 2025 — Phase 2 Blazor localization (Batch 1 of 4 shipped)

**Commit:** `9543146` — Dashboard/ActivityLog/MainLayout → Korean (118 keys, 3 files).

**Patterns locked in:**
- Naming: `PageName_*` per file; promote to `Common_*` only at 3+ uses.
- Enum-over-string: switch on typed enums (`PlanActivityType`), not AI-generated `TitleKey` strings — avoids snake_case/PascalCase mismatches.
- `ActivityInfo` record refactor: rename field `Label → LabelKey`, store resx key, look up via `@Localize[activity.LabelKey]`.
- CultureChanged wiring: every localized component needs `@implements IDisposable` + `OnInitialized { Localize.CultureChanged += OnCultureChanged; }` + `OnCultureChanged => InvokeAsync(StateHasChanged)` + `Dispose { Localize.CultureChanged -= OnCultureChanged; }`.
- `whatsNewTitle` pattern: expression-bodied property `=> Localize["MainLayout_WhatsNew"]` beats mutable field + reassignment.
- Legacy unprefixed keys (`Save`, `Reading`, `OK`, `Refresh`) stay — still bound to MauiReactor. Blazor uses prefixed variants only. `Common_OKButton` used to dodge `OK` collision.

**Gotcha — Razor attribute quote nesting:**
Edit tool mangles `title="@Localize["Key"]"` → `title="@Localize[" Key "]"`. Always use single-quoted outer attribute: `title='@Localize["Key"]'`. Also fine inside `@(...)` C# expressions.

**Tooling:** `scripts/i18n-work/add_keys.py` + `batchN.json` — bulk append to both resx files. De-dupes by key. Reusable.

**Build gate:** `dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj` — clean per batch before commit.

**Commit format (per Captain):**
```
feat(i18n): Phase 2 Batch N — {area} strings to Korean
- Adds {N} keys…
- Localizes {files}…
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```
Never push — Captain runs `/review` first.

---

## 2026-04-20 — Potential Parallel Opportunity: Blazor JS Error Bridge (Mobile App Insights)

**Cross-agent note from Scribe (Wash spawn context)**

Wash's mobile observability memo identifies capturing Blazor WebView JavaScript errors as one of five telemetry hooks for App Insights integration. Current scope: Wash handles `.NET-side` wiring (Azure exporter, `MauiExceptions` subscriber, business event extensions).

**Blazor JS error bridge** (separate piece):
- `wwwroot/js/error-bridge.js`: global `window.onerror` + `unhandledrejection` handler
- `JsErrorBridge.cs` service: `[JSInvokable]` method to receive errors from JS layer
- JSInterop registration in DI

**If Captain approves parallel work,** Kaylee could own this independently while Wash does the .NET wiring. Minimal merge conflict surface (JS file + one new service class). Leaves Wash free to focus on HTTP instrumentation + `MauiExceptions` plumbing.

**Current status:** Awaiting Captain decision on full 1-day plan vs. 3-hour small-slice PoC, and answers to open questions. Will be documented in `.squad/decisions.md` once merged.

**Reference:** `.squad/decisions/inbox/wash-mobile-observability.md` (now merged into decisions.md as of 2026-04-20).

## 2026-04-23 — Vocabulary LexicalUnitType UI Review

**Task:** Evaluate UI surfaces for word/phrase plan — LexicalUnitType picker + constituent editor.

**Pages Reviewed:**
- `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor` — Single word edit form
- `src/SentenceStudio.UI/Pages/Vocabulary.razor` — Vocabulary list with advanced search/filter
- `src/SentenceStudio.UI/Pages/ResourceAdd.razor` — Bulk CSV/tab-delimited vocab import
- `src/SentenceStudio.Shared/Models/VocabularyWord.cs` — Current model (no LexicalUnitType yet)
- `src/SentenceStudio.Shared/Models/AutocompleteSuggestion.cs` — Existing autocomplete pattern

**Findings:**
1. **UI surfaces to touch:** Two Blazor pages only (no MauiReactor vocab editing surfaces found):
   - `VocabularyWordEdit.razor` — add LexicalUnitType picker in "Encoding & Memory Aids" section
   - `ResourceAdd.razor` — add auto-classification + override UI in bulk import flow
   
2. **Existing autocomplete pattern:** `Vocabulary.razor` already has full search-as-you-type autocomplete with dropdown suggestions (tag/resource/lemma/status). Reusable for constituent picker.

3. **Constituent editor interaction:** For Phrase/Sentence types, need inline autocomplete to add constituent words from user's existing vocab. Pattern: search-as-you-type → dropdown → click to add → show as removable chip/badge. Similar to existing tag filter chips in Vocabulary.razor but with add/remove within the form.

4. **No MauiReactor surfaces:** All vocabulary editing is Blazor-only. Native app uses embedded Blazor WebView for vocab management.

5. **Bootstrap icon + styling conventions:** Existing pages use `bi-*` icons consistently, `form-control-ss` for inputs, `card-ss` for sections, `ss-body2`/`ss-title3` typography.


## Learnings (continued)

- 2026-04-24: **Import UI Pattern Scout** — Completed planning investigation for Zoe's Import page design. Surveyed existing admin/management pages (Settings, ResourceAdd, ResourceEdit, Resources, Onboarding). Key findings: (1) Blazor-only platform (no MauiReactor parity yet); (2) Settings uses lightweight card sections, ResourceAdd shows multi-card + file import + preview table pattern, Resources shows polished list/lookup with search + filter + dual view modes + Virtualize; (3) File picker abstraction ready (IFilePickerService + WebFilePickerService for Blazor, MauiFilePickerService for MAUI); (4) Form validation is explicit null checks → Toast (no DataAnnotations UI); (5) Import already in top nav (NavMenu.razor line 60, icon bi-box-arrow-in-down); (6) Import.razor exists (28.7 KB YouTube-only template, reusable structure). Documented in `.squad/decisions/inbox/kaylee-import-ui-scout.md`.

---

## 2026-04-24 — Import UI Pattern Scout (Multi-Agent Session)

Surveyed UI patterns, form conventions, file picker abstractions, navigation structure for import feature architecture.

**Key findings:**
- Form patterns: Bootstrap cards + sections (card-ss, form-control-ss, theme typography)
- Multi-step flows: Settings (tabbed), Import (URL → Transcript → Polish → Save), ResourceAdd (card + file + preview)
- File import pattern in ResourceAdd: InputFile + delimiter radios + editable preview table
- Resource lookup: Resources.razor shows search + filter + dual views + Virtualize
- File picker abstraction: IFilePickerService (Blazor: WebFilePickerService, MAUI: MauiFilePickerService)
- Navigation: Import already in top-level nav (bi-box-arrow-in-down)

**Recommendations to Zoe:**
- Keep Import in top-level nav (good positioning)
- Create new `/import-content` page (separate from YouTube)
- Reuse preview table from ResourceAdd.razor
- Use InputFile for file upload
- Toast for notifications

**Reusable components:** PageHeader, card-ss, form-control-ss, preview table pattern

**Coordinated with:** Zoe (architecture), Wash (data layer), River (AI), Copilot

**Next:** Implementation team uses patterns to build ImportContent.razor page.


---

## 2026-04-24 — Media Import Rename (Wave 1 Track B)

Renamed YouTube import page from `/import` to `/media-import` to free up `/import` namespace for upcoming generic content-import feature.

**Files changed:**
1. **Component rename:** `src/SentenceStudio.UI/Pages/Import.razor` → `MediaImport.razor`
2. **Route updates:** 
   - Primary route changed to `/media-import`
   - Added back-compat `/import` route on same component (dual @page directives)
3. **Child route updates:** `src/SentenceStudio.UI/Pages/ChannelDetail.razor`
   - Primary route changed to `/media-import/channel/{Id?}`
   - Added back-compat `/import/channel/{Id?}` route
   - All NavigateTo calls updated to use new `/media-import/*` paths
4. **Navigation menu:** `src/SentenceStudio.UI/Layout/NavMenu.razor`
   - Section key: `import` → `media-import`
   - Icon: `bi-box-arrow-in-down` → `bi-camera-video`
   - Label localization key: `Nav_Import` → `Nav_MediaImport`
5. **Navigation service:** `src/SentenceStudio.UI/Services/NavigationMemoryService.cs`
   - Section definition updated to `("media-import", "/media-import", ["/media-import", "/import"])`
   - Both routes recognized for active-state tracking
6. **Localization:** `src/SentenceStudio.Shared/Resources/Strings/AppResources.{resx,ko.resx}`
   - Added `Nav_MediaImport` → "Media Import" (EN), "미디어 가져오기" (KO)
   - Kept `Nav_Import` for potential future use
7. **Logger type parameter:** Changed `ILogger<Import>` → `ILogger<MediaImport>` in component

**Back-compat strategy:**
- Chose dual @page directives (option 1) over redirect middleware
- Rationale: Simpler, no extra code paths, bookmarks/deep-links resolve immediately
- Both `/import` and `/media-import` work, UI always shows `/media-import` in address bar on fresh navigation
- ChannelDetail also supports both `/import/channel/{Id?}` and `/media-import/channel/{Id?}`

**Tricky cross-references:**
- MediaImport.razor internal NavigateTo calls (AddChannel, EditChannel methods)
- ChannelDetail.razor NavigateTo calls (SaveChannel success, NavigateBack)
- NavigationMemoryService section definition (needed both routes in Prefixes array for sidebar active state)

**Build verification:** `dotnet build src/SentenceStudio.UI/SentenceStudio.UI.csproj` — 267 warnings, **0 errors** ✅

**Not changed:**
- YouTube logic inside MediaImport.razor (scope: rename + reroute only)
- ContentImportService (Wash's domain, Wave 2)
- No new /import-content page yet (Wave 3, Kaylee)

**Next:** Wave 2 (Wash) — ContentImportService + DTOs. Wave 3 (Kaylee) — Build new ImportContent.razor page at `/import-content`.

---

## 2026-04-24 — Import Content Page (Wave 2 Track B)

Built the user-facing `/import-content` page for the data import MVP. This is the comprehensive wizard interface that consumes Wash's `ContentImportService` API.

**Page structure (7-step wizard):**
1. **Source:** Text area for paste (CSV/TSV). File upload deferred to v2 per Captain's prioritization.
2. **Format hints:** Content type dropdown (Vocabulary enabled, Phrases/Transcript/Auto shown disabled with "v2" badge), delimiter dropdown (Auto/Comma/Tab/Pipe), "Has header row" checkbox.
3. **Preview button:** Calls `IContentImportService.ParseContentAsync`, displays editable preview table.
4. **Preview table:** Columns = row #, Target Language Term, Native Language Term, Status (OK/Warning/Error badges). Rows have checkboxes (error rows auto-deselected). Inline editable text inputs per cell. Select-all checkbox in header.
5. **Target resource:** Radio choice between "Create new resource" (with title + description + target/native language dropdowns) OR "Add to existing resource" (dropdown of user's non-smart resources).
6. **Dedup mode:** Radio: Skip (default, safest), Update (with warning text), ImportAll. Help text explains each mode per Captain's ruling.
7. **Commit button:** Calls `IContentImportService.CommitImportAsync`, shows summary panel with counts (created/skipped/updated/failed). "View Resource" and "Import More" buttons.

**Route decision:**
- Primary route: `/import-content`
- Did NOT claim `/import` (MediaImport already has it as back-compat)
- TODO comment left for Captain review — generic import is arguably the better claimant for `/import`, but avoiding conflict for now

**Reused components/patterns:**
- **Resource picker:** Studied Resources.razor search + filter + Virtualize pattern. For MVP, used simple dropdown from `ResourceRepo.GetAllResourcesLightweightAsync()` filtered to non-smart resources.
- **Preview table:** Bootstrap table-sm + table-bordered, similar to ResourceAdd.razor's vocabulary preview (but with status badges + select checkboxes instead of classification dropdown).
- **Styling:** Bootstrap card-ss, ss-title3, ss-body2, form-control-ss, btn-ss-primary/secondary — matching MediaImport.razor and ResourceAdd.razor visual rhythm.
- **Localization:** Injected `BlazorLocalizationService`, subscribed to CultureChanged, added `@implements IDisposable`.

**DTOs consumed:**
- `ContentImportRequest`, `ContentImportPreview`, `ImportRow`, `ContentImportCommit`, `ImportTarget`, `DedupMode`, `ContentType`, `RowStatus`, `ImportTargetMode`, `ContentImportResult`
- All from `src/SentenceStudio.Shared/Services/ContentImportService.cs` (Wash's Wave 1 service)

**Localization (43 keys added):**
- `Nav_ImportContent` → "Import Content" (EN), "콘텐츠 가져오기" (KO)
- `Import_Content_Title`, `Import_Step1_Title` through `Import_Step6_Title`
- `Import_PasteContentLabel`, `Import_PastePlaceholder`, `Import_PasteHint`
- Content type options: `Import_ContentType_Vocabulary`, `Import_ContentType_Phrases`, `Import_ContentType_Transcript`, `Import_ContentType_Auto`
- Delimiter options: `Import_Delimiter_Auto`, `Import_Delimiter_Comma`, `Import_Delimiter_Tab`, `Import_Delimiter_Pipe`
- Table headers: `Import_TargetLanguageTerm`, `Import_NativeLanguageTerm`, `Import_Status`
- Target section: `Import_CreateNewResource`, `Import_AddToExistingResource`, `Import_ResourceTitleLabel`, etc.
- Dedup modes: `Import_DedupMode_Skip`, `Import_DedupMode_Update`, `Import_DedupMode_ImportAll` (+ help text keys for each)
- Result counts: `Import_Created`, `Import_Skipped`, `Import_Updated`, `Import_Failed`
- Validation errors: `Import_ResourceTitleRequired`, `Import_SelectResourceRequired`, `Import_NoRowsSelected`
- Toasts: `Import_ParseError`, `Import_CommitError`, `Import_ImportSuccess`, `Import_UpdateModeWarning`

**Navigation menu:**
- Added entry: `new NavItem("import-content", "bi-box-arrow-in-down", Localize["Nav_ImportContent"])`
- Placed BEFORE `media-import` entry (content import is now the primary import feature; media import is specialized)

**Build verification:**
- Fixed ToastService API: Methods are synchronous (not async) — `ShowError`, `ShowSuccess`, `ShowWarning`, not `ShowErrorAsync`
- `dotnet build src/SentenceStudio.UI/SentenceStudio.UI.csproj` — 267 warnings (all pre-existing), **0 errors** ✅

**Key UX decisions:**
1. **Single-column imports:** Preview shows `RowStatus.Warning` when native term is missing. User sees warning badge + can edit before commit. Wave 2 (River) will backfill AI translation during preview generation.
2. **Error rows auto-deselected:** User CAN re-select them if they fix the inline error (e.g., fill in missing target term).
3. **Update mode confirmation:** MVP shows warning toast. v2 will add modal confirmation (deferred per Captain guidance on taking time to make good decisions).
4. **File upload deferred:** MVP is paste-only. `InputFile` component pattern exists in MediaImport.razor but adds complexity; Captain said "deliver paste-only and write a v2 todo for upload" if needed. Delivered paste-only.
5. **Existing resource picker:** Used simple dropdown (not search+virtualize). 99% of users have <20 resources; search can be added when needed.

**Gotchas / learnings:**
- **ToastService API:** Synchronous methods, not async. Check service interface before assuming `await`.
- **ContentType enum parse:** Needed `Enum.Parse<ContentType>(contentType)` where `contentType` is string bound to select.
- **Delimiter special handling:** `\t` (tab) needs to be string `"\\t"` in dropdown, then parsed to `'\t'` char for `DelimiterOverride`.
- **Preview table state management:** Needed `List<ImportRow> editableRows` separate from `previewResult.Rows` (which is read-only) so user edits don't mutate the original DTO.

**Files changed:**
1. `src/SentenceStudio.UI/Pages/ImportContent.razor` — NEW (27KB, 600+ lines)
2. `src/SentenceStudio.UI/Layout/NavMenu.razor` — added import-content entry
3. `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` — +43 keys
4. `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx` — +43 Korean translations

**Handoff notes for Jayne (E2E testing):**
- **Do NOT run the app** — Captain validates manually after wave lands. Jayne writes the E2E test next.
- Test scenarios:
  1. Paste CSV (2 columns) → Preview → Create new resource → Skip duplicates → Commit
  2. Paste TSV (1 column, header row) → Preview (warnings for missing native terms) → Add to existing → Skip duplicates → Commit
  3. Paste invalid (no delimiter) → Parse error toast
  4. Preview → Edit row → Toggle selection → Commit subset
  5. Update mode → Warning toast shown
- Look for: Row checkboxes functional, inline edits persist, status badges correct, summary counts match, resource created/updated in DB.


---

## 2026-04-25 — Import Scope Correction + v1.1 Architecture (Team Update)

**Event:** Captain's process-correction round + Zoe's architecture spec completion  
**Status:** 🔒 BLOCKED on captain-confirm-scope  

**What happened:**
- Captain identified process issue: Phrases/Transcripts/Auto-detect were silently moved to v2 without asking him by name. Scope corrected; all three are back in v1.1.
- Zoe completed architecture spec and **corrected Squad's Decision #1**: `LexicalUnitType` enum already exists (not a new enum needed). Only a backfill migration required (Unknown→Word).
- New scope flag from Zoe: free-text phrase extraction deferred to v1.2 (CSV + paired-line phrases stay in v1.1).

**For Kaylee specifically:**
- **UI Changes:** Enable Phrases/Transcript/Auto-detect in ImportContent.razor dropdown. Add auto-detect banner UI. Add help text for paired-line phrase format. Ensure CSV parser handles RFC 4180 quoting (Korean commas).
- **Implementation blocked** until Captain confirms. See `.squad/decisions.md` for full spec (section "Import Content — Scope Correction & Expansion" + "Import Content v1.1 Architecture", section E).

**No action needed from you yet.** Read the decisions ledger when Captain unblocks. Zoe's spec has implementation order: River → Wash → Kaylee → Jayne.



---

## 2026-04-25 — v1.1 Data Import UI (Checkboxes + Auto-detect)

**Status:** DELIVERED — Harvest checkboxes + confidence banner + ImportStep.Harvest.

**Deliverables:**
1. Enabled Phrases/Transcript/Auto-detect in content type dropdown (removed v2 disabled badges).
2. Harvest checkbox step — 3 independent checkboxes (Transcript/Phrases/Words) with at-least-one validation and default presets per scenario.
3. Auto-detect confidence banner — 3-tier: High (auto-preview), Medium (confirm gate), Low (manual picker). Bootstrap icons only.
4. ImportStep enum gains `Harvest` between Source and Preview.

**Known limitations:** DetectContentType() still a stub. Harvest labels need localization.

---

## 2026-04-27 — v1.1 Data Import DTO Mapping Fix (Same-Cycle)

**Status:** ✅ FIXED — Frontend defect + lesson learned

**Context:** During Jayne's retest of Simon's backend bug fixes, a frontend DTO mapping gap was discovered: `LexicalUnitType` and `SourceText` were not being round-tripped from preview to commit.

**Outcome:**
- Identified 2 missing property mappings in ImportContent.razor (~line 688 and ~line 853)
- Added `LexicalUnitType = r.LexicalUnitType` to editableRows construction
- Added `SourceText = previewResult.SourceText` to updatedPreview construction
- Audited ALL properties on 3 DTOs (ImportRow, ContentImportPreview, ContentImportCommit) for completeness
- Clean build (0 errors)
- Verified fix in retest (BUG-2 + BUG-3 now pass)
- Final sweep confirmed 10/10 scenarios PASS

**Lesson learned:** DTO discipline — when DTOs carry structured data through a pipeline, audit all properties in the initializer blocks, not just the obvious ones. AI-generated DTO fields (like LexicalUnitType from the prompt) can silently disappear if mapping is implicit. Explicit mapping + audit = defects caught early.

**Adjacent finding (low-risk, future work):** Classification and RequiresUserConfirmation are informational-only and not used by backend. Could optionally round-trip in future for UI symmetry, but not blocking for MVP.

**Ship readiness:** All bugs fixed and verified. Feature shipped clean.


---

## Vocabulary List — Type Filter & Add Button Rename (Round 1)

**Date:** 2025-07-25
**Branch:** feature/import-content
**Scope:** `src/SentenceStudio.UI/Pages/Vocabulary.razor` only

### Changes Made

1. **Type filter dropdown** — Added a new "Type" filter (All Types / Word / Phrase / Sentence) to both the desktop filter row and the mobile offcanvas filter panel. Follows the exact same pattern as the existing Association/Status/Encoding dropdowns: `CurrentType` computed property, `OnTypeDropdown` handler routing through `OnDropdownChanged("type", e)`, and a `"type"` case in `ApplyFilters()` that matches against `VocabularyWord.LexicalUnitType`. Client-side filtering on the already-loaded list — no new service calls needed.

2. **Add button renamed** — Changed "Add Word" → "Add" in both the primary action button and the secondary dropdown menu item. Created a new localization key `Vocabulary_Add` ("Add" / "추가") to keep `Vocabulary_AddWord` as a non-breaking legacy key.

3. **Filter chip support** — Added `"type"` entries to `GetFilterTypeBadgeClass` (bg-primary-subtle) and `GetFilterTypeIcon` (bi-diagram-3) so type chips render correctly in the active filter bar.

4. **Localization** — Added 5 new keys to both `AppResources.resx` and `AppResources.ko.resx`: `Vocabulary_Add`, `Vocabulary_FilterTypeAll`, `Vocabulary_FilterTypeWord`, `Vocabulary_FilterTypePhrase`, `Vocabulary_FilterTypeSentence`.

### Learnings

- **Dropdown filter pattern is clean and scalable:** The search-query-driven filter system (`OnDropdownChanged` → `SearchParser` → `ApplyFilters` switch) made adding a new filter type trivial. Same pattern should be used for any future filter additions.
- **VocabularyWordEdit.razor already has full type support** (lines 117-128): a `<select>` bound to `selectedLexicalUnitType` with Word/Phrase/Sentence options, plus constituent word linking UI for Phrase/Sentence types. No changes needed there.
- **No follow-up needed for the edit page** — it already handles all three types including the add-new flow (id=0). The "Add" button navigation works correctly as-is.

### How to Test

- Navigate to `/vocabulary`
- Verify the header button says "Add" (not "Add Word")
- Verify the "Type" dropdown appears in the desktop filter row (between Association and Status)
- Select "Word" → only Word-type entries shown; select "Phrase" → only Phrases; "Sentence" → only Sentences; "All Types" → everything
- Verify the type filter chip appears in the active filters bar when a type is selected
- On mobile viewport: open the offcanvas filter panel → verify the "Type" section appears
- Combine type filter with other filters (e.g., type:word + status:known) → both should apply

### Import Page: Sentences Content Type Support (Round 2)

**Scope:** Wired Wash's new `ContentType.Sentences` and `HarvestSentences` flag into ImportContent.razor.

**Changes made:**
1. **Content type dropdown**: Added "Sentences" option between Phrases and Transcript
2. **Harvest checkboxes**: Added "Harvest Sentences" checkbox (order: Sentences > Phrases > Words), bound to `harvestSentences`
3. **Type chooser buttons**: Added Sentences button to both low-confidence auto-detect panel and override chooser
4. **FormatContentType helper**: Added `ContentType.Sentences => "Sentences"` case
5. **ValidateHarvestCheckboxes**: Extended to include `harvestSentences` in the "at least one" check
6. **CommitImport**: Wired `HarvestSentences = harvestSentences` into `ContentImportCommit` DTO
7. **RunPreview**: Wired all four harvest flags into `ContentImportRequest` so backend can filter preview rows
8. **StartNewImport**: Resets `harvestSentences = false`

## Learnings

- 2026-07 **Harvest Defaults per Content Type**: Vocabulary = Words only; Phrases = Phrases + Words; Sentences = Sentences + Words; Transcript = Transcript + Words; Auto = all unchecked. The pattern is: primary harvest flag matches content type, Words rides along as secondary (except Auto). User can always override.
- 2026-07 **Validation rule extension**: `ValidateHarvestCheckboxes` uses OR across all four flags (Transcript, Sentences, Phrases, Words). Backend `CommitImportAsync` enforces the same rule independently. Both must stay in sync.
- 2026-07 **RunPreview needs harvest flags too**: The preview request sends harvest flags so the backend's `FilterRowsByHarvestFlags` can filter the preview table. Without this, the preview would show rows the user didn't ask for.
- 2026-04-27: **TEAM CONVERGENCE: Type Filter + Import UI** — Three-agent spawn diagnosed phrase-save bug (generic prompt wired instead of River's dedicated prompt). Kaylee completed Round 1: `Vocabulary.razor` Type filter dropdown added (All/Word/Phrase/Sentence pattern matching Association/Status/Encoding filters). VocabularyWordEdit.razor already supported all types — no changes needed. Client-side filter on loaded list. Round 2 pending: add Sentences button to import content type selector + Sentences harvest checkbox + `ContentTypeToString` case. Team pattern: convergent diagnosis (Wash backend, River prompts, Jayne reproduction) enabled fast Round 1 completion; UI now ready for backend integration.


## Cross-Agent Updates

- 2026-04-27: **Jayne v1.3 Import Detail E2E — SHIPPED** — Jayne validated the Import Complete redesign (commits 35e0ba1, 111418f) with 7/7 E2E tests PASS. Summary cards render correctly, per-row table works, filter pills functional, back-nav state preserved, vocab links navigate correctly, failed rows resilient, zero errors in logs. Feature shipped on feature/import-content. (See: `.squad/log/2026-04-27T14:53:00Z-v13-import-detail.md`)

