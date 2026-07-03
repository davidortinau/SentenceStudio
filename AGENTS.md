Please call me Captain and talk like a pirate.

## Working Style

**Thoroughness over speed.** Captain has unlimited tokens, premium requests, and budget. Never cut corners. Never skip bookkeeping (decisions, tests, documentation) to save time. When uncertain whether to take the fast path or the thorough path, ask — the answer will almost always be thorough. Fast failures cost more than careful first passes.

- Do NOT apologize constantly. Focus on clarity about how to improve and get repeatable results.
- Always follow the Squad protocol: route through agents, record decisions, run Scribe.
- Always pass userId to progress queries (ResolveUserId handles this — 9 regression tests enforce it).
- Always write tests for recurring bugs — if a bug came back, it needs a test so it can't come back again.

This is a .NET MAUI project that targets mobile and desktop.

## Git Workflow: Direct Merge to Main — No PRs

Captain is a **solo developer** who merges his own work. When Captain says "merge to main," "ship it," or similar in an **interactive session he is driving**, do a **direct merge** — commit on the branch, fast-forward/merge into `main`, and push. **Do NOT open a pull request, and never offer one as the default path.**

A PR buys nothing here: there is no second reviewer, no branch protection on `main`, and CI (`ci.yml`, `test.yml`) triggers on `push: [main]` exactly as it does on `pull_request: [main]` — so a direct push to `main` gets the **same** build + test coverage. A PR only adds ceremony, an extra branch round-trip, and a `(#NNN)` suffix on the commit.

- The pre-push gate still applies: review must be clean (run `/review` or a code-review pass) and Captain must have approved before pushing.
- Open a PR **only** if Captain explicitly asks for one.
- Exception: autonomous **cloud Copilot Coding Agent** work on an assigned GitHub *issue* still delivers via PR (that's the async review channel — see `.github/copilot-instructions.md`). This direct-merge rule governs interactive sessions, not unattended issue pickup.

## Project Purpose: Dogfooding .NET MAUI

**SentenceStudio's PRIMARY purpose is dogfooding the .NET MAUI SDK and developer experience.** The shipping app is the vehicle; surfacing and fixing tooling friction is the destination. This affects how Squad prioritizes work.

**Tooling friction takes priority over app features.** When ANY .NET MAUI / Aspire / MauiDevFlow / Hot Reload / Blazor Hybrid / build-tool friction is encountered during normal app work, the investigation of that friction is MORE important than the app task that surfaced it. Do not power through. Do not pivot platforms to avoid the issue. Do not ask Captain to manually drive UI clicks because automation broke — that's a workaround, not an investigation.

**Required outcomes (in priority order):**
1. **Root cause + local fix** verified with a local build, PR opened against the dependency.
2. **OR** a new upstream issue filed (dotnet/maui, dotnet/aspire, microsoft/dotnetdevflow, etc.) with a minimal repro project + exact steps + observed-vs-expected behavior.
3. **OR** an existing upstream issue identified that matches the failure, with our reproduction added as a comment if it adds new signal.

**Token and turn budget for tooling investigations is unlimited.** Burn as many parallel agents and as many turns as needed.

**Recurring friction = capture, root-cause, share.** If something blocks a Squad member twice, treat it as a bug — even if a workaround exists. The job is to make the next developer's experience better than ours was.

## Documentation

**IMPORTANT: All documentation files (summaries, guides, technical specs, etc.) must be placed in the `docs/` folder at the repository root.** Do not create markdown documentation files at the repository root. 

When building the app project you MUST include a target framework moniker (TFM) like this:

dotnet build -f net10.0-maccatalyst

IMPORTANT: To run .NET MAUI apps, NEVER use `dotnet run` - it doesn't work for MAUI. Instead use:

dotnet build -t:Run -f net10.0-maccatalyst

NOTE (LOCAL DEV PREFERENCE):
- You told me you prefer using `dotnet run` in this workspace. The official guidance for MAUI projects is to use `dotnet build -t:Run -f <TFM>` because `dotnet run` can fail for MAUI apps.
- I will default to the official command unless you explicitly instruct me to use `dotnet run` for an individual action. If you want me to always use `dotnet run` in this repository, reply with: "Use dotnet run" and I will follow that local preference and document it here.

It uses the MauiReactor (Reactor.Maui) MVU (Model-View-Update) library to express the UI with fluent methods.

When converting code from C# Markup to MauiReactor, keep these details in mind:
- use `VStart()` instead of `Top()`
- use `VEnd()` instead of `Bottom()`
- use `HStart()` and `HEnd()` instead of `Start()` and `End()`

For doing CoreSync work, refer to the sample project https://github.com/adospace/mauireactor-core-sync

Documentation via Context7 mcp is here:
- .NET MAUI https://context7.com/dotnet/maui/llms.txt
- Community Toolkit for .NET MAUI https://context7.com/communitytoolkit/maui.git/llms.txt
- MauiReactor https://context7.com/adospace/reactorui-maui/llms.txt
- SkiaSharp https://context7.com/mono/skiasharp/llms.txt
- ElevenLabs API Official Docs https://context7.com/elevenlabs/elevenlabs-docs/llms.txt
- ElevenLabs-DotNet SDK https://context7.com/rageagainstthepixel/elevenlabs-dotnet/llms.txt

Always search Microsoft documentation (MS Learn) when working with .NET, Windows, or Microsoft features, or APIs. Use the `microsoft_docs_search` tool to find the most current information about capabilities, best practices, and implementation patterns before making changes.

## .NET SDK Selection in This Repo

**Before running any `dotnet` command, know which SDK the CLI will actually pick.** This is a 100-level fundamental — see `.squad/skills/dotnet-sdk-detection/SKILL.md` for the full diagnostic procedure (4-layer model: installed SDKs vs. selected SDK vs. workload manifests vs. project TFMs).

### What this repo targets

- Every project under `src/SentenceStudio*` targets `net10.0-*` TFMs (net10.0, net10.0-ios, net10.0-android, net10.0-maccatalyst, net10.0-macos, net10.0-windows10.0.19041.0).
- Daily dev (Mac Catalyst debug, tests, Aspire AppHost, ASP.NET Core API, Blazor webapp) is happy on the net10 GA SDK + net10 MAUI workload.
- Newer SDKs (net11 previews) CAN build these net10 TFMs in principle — the wrinkle is workload manifest alignment, not framework compatibility.

### Why you may see a `global.json` here (and why it isn't committed)

**`global.json` is explicitly gitignored** (see `.gitignore` lines 412–414: `src/global.json`, `global.json`, `_global.json`). It is **never in the repo**. If one exists on a contributor's machine, it is a per-developer artifact.

Captain keeps a local `global.json` pinning to net10 (`10.0.101`, `rollForward: latestFeature`) because his default machine SDK is a net11 preview (he's all-in on previews for unrelated work). The pin forces `dotnet` commands inside this repo to use the matching net10 SDK + net10 MAUI workload, which is what the csprojs want.

**Other contributors / CI / fresh checkouts do not need a `global.json`.** If the only installed SDK is net10 GA, the CLI selects it without a pin. If multiple SDKs are installed and you want to be explicit, create a local one — but **do not commit it**.

### The publish workflow swap (iOS only, Xcode-driven)

The ONLY documented reason this project ever swaps `global.json` is **iOS device publish to DX24** because Captain's Xcode is 26.3 and the net10 GA SDK ships expecting Xcode 26.2. The net11 preview 3 SDK knows about Xcode 26.3.

`docs/deploy-runbook.md` Step 2a documents the temporary swap (net10 → net11p3 → build iOS Release → restore net10). **This has nothing to do with Azure** — Azure runs the net10 container produced by `azd deploy` which uses the standard net10 SDK.

```bash
# ONLY for iOS device publish — restore immediately after.
cp global.json global.json.bak
echo '{"sdk":{"version":"11.0.100-preview.3.26209.122","rollForward":"latestFeature","allowPrerelease":true}}' > global.json
# ... build iOS Release ...
cp global.json.bak global.json && rm global.json.bak
```

If you see a stray `global.json.bak` in `git status` (untracked), the publish workflow was interrupted — restore it before doing anything else.

### Required diagnostic order before claiming "the SDK isn't installed"

1. `find . -maxdepth 4 -name global.json` — does one exist on this machine?
2. `dotnet --list-sdks` — what's actually installed?
3. `dotnet --info | head -20` — what is the CLI **selecting** in this directory? (ground truth)
4. `dotnet workload list` — workload manifests are pinned PER SDK band; switching SDKs changes available workloads silently.

If any of those four turn up something unexpected, read `.squad/skills/dotnet-sdk-detection/SKILL.md` before changing csprojs, multi-targeting, or adding `#if` guards.

## Data Preservation Rules

**CRITICAL: NEVER delete or lose user data!**

1. **NEVER uninstall/reinstall apps** to fix issues - this destroys all user data
2. **NEVER delete the database file** without explicit user permission AND a verified backup
3. **When facing database errors**: Fix migrations, adjust schema, or find workarounds - do NOT wipe data
4. **Before any destructive action**: Ask the user for explicit permission and explain the data loss consequences
5. **Simulator/device data is precious**: Test data takes significant time to create - treat it as production data

If you encounter errors like "unable to open database file" or migration conflicts, investigate and fix the root cause rather than starting fresh.

**DataRecoveryService (NON-NEGOTIABLE):** `DataRecoveryService` contains three safeguards that must ALL pass before any retag runs — email mismatch abort, temporal sanity abort, and first-run gate. The caller in `IdentityAuthService.StoreTokens` is additionally gated by `enable_automatic_data_recovery` preference (default `false`). NEVER invoke `RecoverOrphanedDataAsync` or flip that flag without reading the full RCA at `.squad/decisions/inbox/captain-rca-datarecoveryservice-cross-tenant-corruption.md`. Regression tests are in `tests/SentenceStudio.UnitTests/Data/DataRecoveryServiceTests.cs`. See also the multi-tenant scoping rule in `.github/copilot-instructions.md`.

## Database Migrations

**CRITICAL: Always use EF Core migrations for schema changes. NEVER use raw SQL ALTER TABLE statements.**

1. **Use `dotnet ef` CLI to generate migrations** — do NOT hand-write migration files:
   ```bash
   dotnet ef migrations add <MigrationName> \
     --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj \
     --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj
   ```

2. **The Shared project targets plain `net10.0`** and works fine with EF tooling. There is no TFM conflict — the MAUI TFMs are only in the app head projects.

3. **Review the generated migration** before committing. Verify table names match what's in `ApplicationDbContext.OnModelCreating` (singular names: `SkillProfile`, `LearningResource`, etc.).

4. **Migrations are applied at runtime** via `MigrateAsync()` in `UserProfileRepository.GetAsync()`. No manual `dotnet ef database update` is needed.

5. **Data backfill** (populating new columns for existing rows) should be done in a separate method called after `MigrateAsync()`, not inside the migration itself. See `BackfillUserProfileIdsAsync()` for the pattern.

6. **Never suppress `PendingModelChangesWarning`** — if EF detects model/migration mismatch, create the missing migration instead of hiding the warning.

### 🔴 Dual-provider migrations (PostgreSQL + SQLite) — the recurring foot-gun

This app runs **two** EF providers from one `ApplicationDbContext`: **PostgreSQL** (API/webapp/prod) and **SQLite** (iOS/Android/macOS/Catalyst native heads). `dotnet ef` only scaffolds for the active provider, so the **SQLite counterpart under `Migrations/Sqlite/` is hand-written** — and that is where things break. Non-negotiable rules:

1. **Every migration needs BOTH a PostgreSQL copy (`Migrations/`) and a SQLite copy (`Migrations/Sqlite/`)** unless it is genuinely provider-specific (e.g. a `*PgDate*` type change). Same migration id/timestamp in both.
2. **The SQLite copy MUST carry `[DbContext(typeof(ApplicationDbContext))]` + `[Migration("<id>")]`** on the class (normally these live in the auto-generated `.Designer.cs`; hand-written migrations must put them inline). **Without `[Migration]`, EF never discovers the migration and `MigrateAsync` SILENTLY SKIPS it on mobile** — the table/column is never created. PostgreSQL usually has the attribute, so the app works on the webapp/prod and breaks ONLY on native heads. This has shipped to devices **twice** (`AddRefreshTokenReplacedBy` 2026-05-03, `AddActivitySession` 2026-07-02 — the latter killed all dashboard buttons on iOS).
3. **A raw-DDL / schema-copy test CANNOT catch a missing-attribute bug.** The SQL is valid; the migration is just never invoked. To verify a mobile migration you must confirm EF **discovers and applies** it — run a native head and check `__EFMigrationsHistory` + the new schema appear. (SQLite is WAL-mode: pull `db`+`-wal`+`-shm` or terminate the app first, or the DB looks stale.)
4. **Before shipping ANY migration, run BOTH gates** (also enforced in CI `migration-guard` + the deploy runbook):
   ```bash
   bash scripts/validate-migration-attributes.sh   # static, catches missing [Migration] attrs
   bash scripts/validate-mobile-migrations.sh       # real SQLite apply on a native head
   ```
5. Authoritative workflow + templates: `.squad/skills/ef-dual-provider-migrations/SKILL.md`. (Note: because of the multi-provider hand-off, `dotnet ef migrations add` alone is NOT sufficient here — it never produces the SQLite copy.)

## Troubleshooting and Issue Resolution

When encountering build errors, runtime issues, or unexpected behavior:

1. **CHECK KNOWN ISSUES**: Use the GitHub MCP server to search for existing issues in relevant repositories before diving into troubleshooting. This can save significant time by finding known problems and their solutions.

2. **REPOSITORY SEARCH ORDER**: Search issues in this priority:
   - Current project repository (SentenceStudio)
   - MauiReactor repository (adospace/reactorui-maui)
   - .NET MAUI repository (dotnet/maui)
   - Related dependency repositories

3. **ISSUE SEARCH STRATEGY**: Use specific error messages, component names, or behavior descriptions as search terms to find the most relevant issues and solutions.

## Microsoft.Extensions.AI Guidelines

When working with AI prompts and DTOs:

1. **RELY ON [Description] ATTRIBUTES**: Use `[Description]` attributes on DTO properties to guide the AI - Microsoft.Extensions.AI automatically uses these for context.

2. **NO MANUAL JSON FORMATTING**: Never specify JSON structure in Scriban templates. The Microsoft.Extensions.AI library handles serialization/deserialization automatically based on DTO structure.

3. **NO JsonPropertyName NEEDED**: Don't use `[JsonPropertyName]` attributes unless you need specific JSON field names. The library handles property mapping automatically.

4. **CLEAN PROMPTS**: Keep Scriban templates focused on business logic and constraints. Let the library handle the technical serialization details.

Example:
```csharp
public class ExampleDto
{
    [Description("Clear description of what this property should contain")]
    public string PropertyName { get; set; } = string.Empty;
}
```

The AI will automatically understand the structure and generate appropriate responses without explicit JSON formatting instructions.

STYLING: Prefer using the centralized styles defined in MyTheme.cs rather than adding styling at the page or view level. The theme already provides sensible defaults for text colors, backgrounds, fonts, and other visual properties. Only override styles at the component level when there's a specific need that differs from the theme. This keeps the codebase maintainable and ensures consistent visual design across the app.

ICONS: **NEVER create inline FontImageSource instances**. All icons MUST be defined in `ApplicationTheme.Icons.cs` and referenced via `MyTheme.IconName`. This ensures consistent icon styling (color, size) across the app and makes icon management centralized.

   ❌ WRONG:
   ```csharp
   ImageButton()
       .Source(new FontImageSource
       {
           FontFamily = FluentUI.FontFamily,
           Glyph = FluentUI.tag_20_regular,
           Color = MyTheme.Gray600,
           Size = 20
       })
   ```
   
   ✅ CORRECT:
   ```csharp
   // First, add the icon to ApplicationTheme.Icons.cs if it doesn't exist:
   public static FontImageSource IconTag { get; } = new FontImageSource
   {
       Glyph = FluentUI.tag_20_regular,
       FontFamily = FluentUI.FontFamily,
       Color = Gray600,
       Size = Size200
   };
   
   // Then use it in your page:
   ImageButton()
       .Source(MyTheme.IconTag)
   ```
   
   When you need a new icon, add it to `ApplicationTheme.Icons.cs` following the existing pattern. Use existing icons when available (e.g., `MyTheme.IconClose`, `MyTheme.IconSearch`, `MyTheme.IconEdit`, etc.).

ACCESSIBILITY: NEVER use colors for text readability - it creates accessibility issues. Use colored backgrounds, borders, or icons instead. Text should always use theme-appropriate colors (MyTheme.DarkOnLightBackground, MyTheme.LightOnDarkBackground, etc.) for maximum readability and accessibility compliance.

## MauiReactor Layout and UI Guidelines

**CRITICAL PRINCIPLES:**

0. **USE MINIMAL CONTROLS**: Always use the simplest, most efficient approach:
   - **String concatenation over multiple Labels**: Use `Label($"🎯 {variable}")` instead of `HStack(Label("🎯"), Label(variable))`
   - **Avoid unnecessary wrappers**: Don't wrap single elements in Border/VStack/HStack unless there's a visual reason
   - **No invisible Borders**: If a Border has no stroke, background, or styling, don't use it
   
   ❌ WRONG:
   ```csharp
   HStack(spacing: MyTheme.MicroSpacing,
       Label("📚"),
       Label(resourceTitle)
   )
   // Or
   Border(
       Label("Text")
   ) // Border serves no purpose
   ```
   
   ✅ CORRECT:
   ```csharp
   Label($"📚 {resourceTitle}")
   ```

1. **NEVER use HorizontalOptions or VerticalOptions**: MauiReactor provides semantic extension methods that are more readable and idiomatic.

   ❌ WRONG:
   ```csharp
   Label("Text").HorizontalOptions(LayoutOptions.End)
   Label("Text").VerticalOptions(LayoutOptions.Center)
   Label("Text").HorizontalOptions(LayoutOptions.Center).VerticalOptions(LayoutOptions.Center)
   ```
   
   ✅ CORRECT:
   ```csharp
   Label("Text").HEnd()
   Label("Text").VCenter()
   Label("Text").Center()  // Both horizontal and vertical center
   ```

2. **Semantic alignment methods to use**:
   - **Horizontal**: `.HStart()`, `.HCenter()`, `.HEnd()`, `.HFill()`
   - **Vertical**: `.VStart()`, `.VCenter()`, `.VEnd()`, `.VFill()`
   - **Both directions**: `.Center()` (equivalent to HCenter + VCenter)

3. **NEVER use FillAndExpand**: This is a legacy pattern from XAML. Use the semantic methods above instead.

   ❌ WRONG:
   ```csharp
   Label("Text").HorizontalOptions(LayoutOptions.FillAndExpand)
   VStack(...).VerticalOptions(LayoutOptions.FillAndExpand)
   ```
   
   ✅ CORRECT:
   ```csharp
   Label("Text").HFill()
   VStack(...).VFill()
   ```

4. **USE THEME KEY STYLES**: Always use `.ThemeKey()` to apply theme styles from MyTheme.cs instead of applying styling properties directly. This ensures consistent visual design and makes theme changes easier.

   ❌ WRONG:
   ```csharp
   // Don't apply individual style properties
   Button("Click Me")
       .BackgroundColor(Colors.Blue)
       .TextColor(Colors.White)
       .BorderColor(Colors.Gray)
       .BorderWidth(1)
       .CornerRadius(8)
       .Padding(14, 10)
   
   Label("Text")
       .TextColor(Colors.Black)
       .FontSize(16)
       .FontAttributes(FontAttributes.Bold)
   ```
   
   ✅ CORRECT:
   ```csharp
   // Use theme keys for components with defined styles
   Button("Click Me")
       .ThemeKey(MyTheme.Primary)  // or MyTheme.Secondary, MyTheme.Danger
   
   Label("Text")
       .ThemeKey(MyTheme.Title1)  // or Body1, Headline, Caption1, etc.
   
   Border()
       .ThemeKey(MyTheme.CardStyle)  // or InputWrapper
   ```
   
   **When theme keys aren't available**, use theme constants instead of hardcoded values:
   ```csharp
   Label("Text")
       .TextColor(MyTheme.PrimaryText)  // Not Colors.Black
       .FontSize(MyTheme.Size160)       // Not 16
       .Margin(MyTheme.Size80)          // Not 8
   ```
   
   **Available theme keys**:
   - **Buttons**: `Primary`, `Secondary`, `Danger`
   - **Labels**: `Title1`, `Title2`, `Title3`, `LargeTitle`, `Display`, `Headline`, `SubHeadline`, `Body1`, `Body1Strong`, `Body2`, `Body2Strong`, `Caption1`, `Caption1Strong`, `Caption2`
   - **Borders**: `CardStyle`, `InputWrapper`
   - **Layouts**: `Surface1`

5. **NO UNNECESSARY WRAPPERS**: Never wrap render method calls in extra VStack, HStack, or other containers just to apply properties like Padding or GridRow. Put these properties INSIDE the render methods where they belong.

   ❌ WRONG:
   ```csharp
   VStack(RenderHeader()).Padding(16).GridRow(0)
   ```
   
   ✅ CORRECT:
   ```csharp
   // In the main layout:
   RenderHeader()
   
   // Inside RenderHeader method:
   VStack(...).Padding(16).GridRow(0)
   ```

6. **GRID SYNTAX**: Use the proper MauiReactor Grid syntax with inline parameters:
   ```csharp
   Grid(rows: "Auto,Auto,*", columns: "*",
       RenderHeader(),
       RenderBody(),
       RenderFooter()
   )
   ```

7. **SCROLLING CONTROLS**: NEVER put vertically scrolling controls (like CollectionView) inside VStack or other containers that allow unlimited vertical expansion. This causes infinite item rendering and performance issues.

   ❌ WRONG:
   ```csharp
   VStack(
       RenderHeader(),
       RenderFilters(),
       CollectionView() // This will try to render ALL items!
   )
   ```
   
   ✅ CORRECT:
   ```csharp
   Grid(rows: "Auto,Auto,*", columns: "*",
       RenderHeader().GridRow(0),
       RenderFilters().GridRow(1),
       RenderCollectionView().GridRow(2) // Constrained by star-sized row
   )
   ```

8. **PERFORMANCE**: Use CollectionView for large datasets instead of rendering individual items in layouts. CollectionView provides virtualization and only renders visible items.

9. **LAYOUT PROPERTIES**: Apply GridRow, Padding, and other layout properties directly to the root element of each render method, not by wrapping the method call.

ADDITIONAL NOTES:
- IMPORTANT: A `ContentPage` may only have a single child element (ToolbarItems do not count). When rendering overlay controls like `SfBottomSheet`, place them inside that single child (for example, inside the main `Grid`) so the page remains valid. Do not add the bottom sheet as a sibling to the page's root content.
- **Shell TitleView for Custom Navigation Content**: In Shell applications, to display custom content in the navigation bar (like timers or custom headers), use `Shell.TitleView` attached property, NOT `NavigationPage.TitleView` or `ToolbarItem`. Apply it using `.Set(MauiControls.Shell.TitleViewProperty, customView)` on the ContentPage.
- **NEVER use ToolbarItem for custom components**: ToolbarItem only supports built-in controls with specific properties like IconImageSource and Text. Do NOT attempt to pass custom Component instances to ToolbarItem - it will not render them.

## Navigation Guidelines

**CRITICAL: This app uses Shell navigation exclusively!**

1. **ALWAYS use Shell.GoToAsync() for navigation**:
   - ✅ CORRECT: `await MauiControls.Shell.Current.GoToAsync(nameof(PageName))`
   - ✅ CORRECT: `await MauiControls.Shell.Current.GoToAsync<PropsType>(nameof(PageName), props => { ... })`
   - ❌ WRONG: `await Navigation.PushAsync(new PageName())`
   - ❌ WRONG: `await Navigation.PopAsync()`

2. **Navigating back to previous page**:
   - ✅ CORRECT: `await MauiControls.Shell.Current.GoToAsync("..")`
   - ❌ WRONG: `await Navigation.PopAsync()`

3. **Never use the Navigation service**: The `Navigation` property (INavigation) is for NavigationPage-based apps. This app uses Shell, so always use `MauiControls.Shell.Current.GoToAsync()`.

## Page Refresh Pattern

**CRITICAL: Use .OnAppearing() to reload data when returning to a page!**

When a page needs to refresh its data after navigating back from another page (e.g., after creating/editing an item), use the `.OnAppearing()` extension method on the ContentPage:

```csharp
public override VisualNode Render()
{
    return ContentPage("Page Title",
        Grid(
            // ... page content
        )
    )
    .OnAppearing(LoadData);  // Reload data each time page appears
}

private async void LoadData()
{
    // Fetch fresh data from repository/service
    var data = await _repository.GetDataAsync();
    SetState(s => s.Data = data);
}
```

**Pattern examples in codebase**:
- `DashboardPage.cs`: `.OnAppearing(LoadOrRefreshDataAsync)`
- `WritingPage.cs`: `.OnAppearing(LoadVocabulary)`
- `UserProfilePage.cs`: `.OnAppearing(LoadProfile)`
- `ListSkillProfilesPage.cs`: `.OnAppearing(LoadProfiles)`

**When to use OnAppearing**:
- After creating/editing items in child pages
- After deleting items that need list refresh
- When data might have changed while on other pages
- For pages that show user-specific dynamic content

## Logging

Use `ILogger<T>` for all production logging. Only use `System.Diagnostics.Debug.WriteLine()` for temporary debugging. For platform-specific logging details, use the `debugging-by-platform` prompt.

## Task Validation Requirements

**CRITICAL: Every UI or behavior change MUST be validated by running the app!**

Do NOT mark a task as complete after only a successful build. You MUST verify changes end-to-end on a running app using the **MAUI DevFlow** skills (see "MAUI DevFlow skill workflow" below).

### Required validation steps for UI changes:
1. **Build & run** the macOS head (Captain's default desktop dev surface): `dotnet run -f net11.0-macos --project src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj` (use Mac Catalyst only when iOS-shaped behavior is being tested)
2. **Navigate** to the affected page/feature in the running app
3. **Take a screenshot** to confirm the UI renders correctly
4. **Interact** with the changed elements — tap buttons, open popups, fill forms, trigger actions
5. **Take screenshots** after interactions to confirm expected behavior (popup appeared, state changed, toast displayed, etc.)
6. **Verify edge cases** — dismiss popups, cancel actions, trigger error states when feasible

### Required validation steps for non-UI changes (services, models, data):
1. **Build** the project: `dotnet build -f net11.0-macos`
2. **Run existing tests** if they cover the changed code: `dotnet test`
3. If no tests exist and the change is observable in the app, **run the app** and verify the behavior as described above

### MAUI DevFlow skill workflow (primary):

DevFlow is already integrated in all five heads (`Microsoft.Maui.DevFlow.Agent` + `Microsoft.Maui.DevFlow.Blazor`, registered via `builder.AddMauiDevFlowAgent()` under `#if DEBUG`; pinned to `0.25.0-dev`). Agent ports: macOS/Android `9225`, iOS/Windows `9224`.

- **maui-devflow-onboard**: One-time setup — add MAUI DevFlow to a project that does NOT yet reference `Microsoft.Maui.DevFlow.*`. The existing heads are already onboarded, so you only need this for a brand-new head.
- **maui-devflow-debug**: After the app is running — build/deploy/inspect/fix loops, visual tree inspection, tapping elements, taking screenshots, reading logs. This is the day-to-day verification tool.
- **maui-devflow-session-review**: Turn long or stuck DevFlow sessions into opt-in MAUI DevFlow product feedback (do not run automatically — only when a session was painful enough to be worth reporting).

**Manual fallback** (only if the skills can't be used): add the `Microsoft.Maui.DevFlow.Agent` package (plus `Microsoft.Maui.DevFlow.Blazor` for the Blazor WebView heads), call `builder.AddMauiDevFlowAgent()` under `#if DEBUG` in the head's `MauiProgram.cs`, then build and run the app.

**After onboarding, or to verify a running app, confirm health with:**
```bash
maui devflow diagnose          # broker, agents, and project integration health
maui devflow wait              # block until the agent connects
maui devflow ui tree --depth 1 # confirm the visual tree is reachable
```

### What "done" means:
- ✅ Build passes
- ✅ App launches without crash
- ✅ Changed feature works as expected (verified with screenshots)
- ✅ No regressions in surrounding functionality
- ❌ "It builds" alone is NOT sufficient for UI changes

### E2E test scripts

Use the **e2e-testing** skill (`.claude/skills/e2e-testing/`) to verify bug fixes and features. The skill contains step-by-step test scripts for every activity and management page, organized as reference files you load on demand. Invoke it after every change.

## Localization Guidelines

**CRITICAL: Always use string interpolation with LocalizationManager!**

**IMPORTANT: Use enums over string keys for type safety!**

When working with localized content that has associated enums (like `PlanActivityType`), always prefer using the enum to determine the localization key rather than storing string keys. This avoids mismatches between AI-generated snake_case keys (e.g., "plan_item_vocab_review_title") and actual PascalCase resource keys (e.g., "PlanItemVocabReviewTitle").

✅ CORRECT:
```csharp
string GetActivityTitle(DailyPlanItem item)
{
    return item.ActivityType switch
    {
        PlanActivityType.VocabularyReview => $"{_localize["PlanItemVocabReviewTitle"]}",
        PlanActivityType.Reading => $"{_localize["PlanItemReadingTitle"]}",
        // ... use the enum, not item.TitleKey string
    };
}
```

❌ WRONG:
```csharp
// Don't rely on TitleKey strings from AI-generated data
return $"{_localize[item.TitleKey]}"; // May not match resource file format
```

1. **NEVER access localized strings without string interpolation**:

   ❌ WRONG:
   ```csharp
   Label(_localize["Key"])  // Returns object, not string!
   Button(_localize["ButtonText"])  // Returns object, not string!
   ContentPage(_localize["Title"], ...)  // Returns object, not string!
   ```
   
   ✅ CORRECT:
   ```csharp
   Label($"{_localize["Key"]}")
   Button($"{_localize["ButtonText"]}")
   ContentPage($"{_localize["Title"]}", ...)
   ```

2. **Use Button/ImageButton for buttons**: Don't compose buttons from Border + Label unless there's a compelling reason MauiReactor's Button doesn't meet your needs.

   ❌ WRONG:
   ```csharp
   Border(
       Label($"{_localize["ButtonText"]}")
           .Center()
   )
   .BackgroundColor(MyTheme.ButtonBackground)
   .OnTapped(() => DoSomething())
   ```
   
   ✅ CORRECT:
   ```csharp
   Button($"{_localize["ButtonText"]}")
       .BackgroundColor(MyTheme.ButtonBackground)
       .OnTapped(() => DoSomething())
   ```

3. **LocalizationManager pattern**: Ensure components have the localization manager property:
   ```csharp
   LocalizationManager _localize => LocalizationManager.Instance;
   ```

4. **For complete localization guidelines**, refer to `.github/agents/localize.agent.md` which includes:
   - Resource file format and naming conventions
   - Korean translation guidelines
   - String interpolation patterns
   - Common translation reference

## Publish Workflow

**"Publish" means BOTH Azure webapp AND iOS to DX24. Always do both. Both point at the same Azure API.**

See `docs/deploy-runbook.md` for full details. Quick reference:

1. **Azure:** `azd deploy` (VPN must be off)
2. **Post-deploy validation:** `./scripts/post-deploy-validate.sh` — **MANDATORY**. Exit code 0 from `azd deploy` means the upload worked, not that the system works. This script runs 16 automated checks (infrastructure health, service availability, auth smoke test, revision health). Never skip this step.
3. **iOS to DX24 (iPhone 15 Pro, device CF4F94E3-A1C9-5617-A089-9ABB0110A09F):**
   - Switch `global.json` to .NET 11 Preview 3 (`allowPrerelease: true`, sdk `11.0.100-preview.3.26209.122`)
   - Build: `services__api__https__0=https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj -f net11.0-ios -c Release -p:RuntimeIdentifier=ios-arm64`
   - Install: `xcrun devicectl device install app --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F src/SentenceStudio.iOS/bin/Release/net10.0-ios/ios-arm64/SentenceStudio.iOS.app`
   - Launch: `xcrun devicectl device process launch --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F com.simplyprofound.sentencestudio`
   - Restore `global.json` after

**Local dev builds** use Debug config and point at localhost (requires Aspire running). Never deploy a Debug build to DX24 for production use.

## Mac Catalyst Gotchas

**CRITICAL: Never add `keychain-access-groups` to Mac Catalyst Entitlements.plist for Debug builds.**

Under ad-hoc Debug signing (the default local dev workflow), the `$(AppIdentifierPrefix)` macro is NOT substituted by the build tooling. This leaves a malformed literal value like `com.simplyprofound.sentencestudio` (missing the team prefix) in the compiled binary's entitlements. The macOS kernel rejects the binary at exec time with:

```
NSPOSIXErrorDomain error 163 (OS_REASON_EXEC)
Process launch failed: Launchd job spawn failed
```

**Solution:** Omit `keychain-access-groups` entirely from `src/SentenceStudio.MacCatalyst/Platforms/MacCatalyst/Entitlements.plist`. Mac Catalyst apps get default access to keychain items under their own bundle ID without explicit declaration. The entitlement is only needed for keychain sharing across multiple apps with the same team prefix.

**Why this matters:** Adding this entitlement "to match iOS" breaks local Catalyst Debug launches completely. The fix is to remove the entitlement. See checkpoint 053 for full diagnosis.

## Async Patterns

**Single-flight async:** For service methods where concurrent calls should share one operation (token refresh, config fetch, cache warming), use the single-flight pattern documented in `.squad/skills/single-flight-async/SKILL.md`. Pattern: `SemaphoreSlim(1,1)` + cached `Task<T>?` to collapse duplicate in-flight operations. Example: `IdentityAuthService.RefreshTokenAsync` (auth-persistence fix, May 2026).

**EF dual-provider migrations:** When adding migrations that affect both PostgreSQL (API) and SQLite (mobile), see `.squad/skills/ef-dual-provider-migrations/SKILL.md` for the correct workflow. Both providers need migrations, and the SQLite copy MUST carry `[DbContext]` + `[Migration("<id>")]` or it is silently skipped on mobile (shipped to devices twice — RefreshToken 2026-05-03, ActivitySession 2026-07-02). Always run `scripts/validate-migration-attributes.sh` (CI-enforced) AND `scripts/validate-mobile-migrations.sh` as mandatory gates.

**Testing single-flight concurrency:** See `.squad/skills/async-single-flight-testing/SKILL.md` for the xUnit pattern to verify exactly-one-call semantics under concurrent load.

## UI Style Rules

**NEVER use emoji characters in UI, code output, logs, or any user-facing text.** This is non-negotiable. Use Bootstrap icons (bi-* classes) or plain text labels instead. Examples:
- Instead of a checkmark emoji, use `<i class="bi bi-check-circle-fill"></i>`
- Instead of an X emoji, use `<i class="bi bi-x-circle-fill"></i>`  
- Instead of a clock emoji, use `<i class="bi bi-clock"></i>`
- Instead of emoji decorations around text, just use the text with an icon prefix
