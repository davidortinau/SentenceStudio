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

