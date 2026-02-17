# Bootstrap Port Plan: Blazor Hybrid → Native MauiReactor

> **Author:** Mal (Lead/Architect)
> **Date:** 2025-07-25
> **Status:** Draft — awaiting Captain David's sign-off

---

## 1. Executive Summary

We're porting the SentenceStudio UI from the Blazor Hybrid implementation (`feature/blazor-hybrid-conversion`) back to native .NET MAUI using MauiReactor, replacing the hand-rolled `MyTheme` system with two new Bootstrap libraries:

- **Plugin.Maui.BootstrapTheme** — CSS-to-native theming via `StyleClass`
- **IconFont.Maui.BootstrapIcons** — strongly-typed Bootstrap icon font

The goal: native MauiReactor pages that visually match the Blazor Hybrid design, with theme-switching and dark mode support baked in.

---

## 2. Branch Analysis

### 2.1 Main Branch — Existing MauiReactor Pages (29 pages)

| Area | Pages | Notes |
|------|-------|-------|
| **Dashboard** | `DashboardPage` + 5 cards (`TodaysPlanCard`, `PracticeStreakCard`, `SkillProgressCard`, `ResourceProgressCard`, `VocabProgressCard`, `ActivityProgressOverlay`) | Core hub |
| **Learning Resources** | `ListLearningResourcesPage`, `AddLearningResourcePage`, `EditLearningResourcePage`, `VocabularyWordEditorSheet` | CRUD + vocab editor |
| **Activities** | `ClozurePage`, `ConversationPage`, `HowDoYouSayPage`, `ReadingPage`, `ShadowingPage`, `TranslationPage`, `WritingPage`, `DescribeAScenePage`, `VocabularyMatchingPage`, `VocabularyQuizPage` | 10 activity pages |
| **Vocabulary** | `VocabularyManagementPage`, `EditVocabularyWordPage`, `VocabularyLearningProgressPage` | Management & progress |
| **Minimal Pairs** | `MinimalPairsPage`, `CreateMinimalPairPage`, `MinimalPairSessionPage` | Pronunciation training |
| **Skills** | `ListSkillProfilesPage`, `AddSkillProfilePage`, `EditSkillProfilePage` | Skill profiles |
| **Video** | `VideoWatchingPage`, `YouTubeImportPage` | Video import & watching |
| **Account** | `UserProfilePage`, `OnboardingPage`, `SettingsPage` | User management |

**Shared components (main):** `ActivityTimerBar`, `InteractiveTextRenderer`, `RxInteractiveTextRenderer`, `FloatingAudioPlayerComponent`, `SelectableLabel`, `ModeSelector`, `WaveformView`, `VoiceSelectionPopup`, `SyncfusionControls`, `SyncfusionInputs`

### 2.2 Blazor Branch — Target UI (30 razor pages)

| Blazor Page | MauiReactor Equivalent | Status |
|-------------|----------------------|--------|
| `Index.razor` | `DashboardPage` | Exists — needs UI refresh |
| `Resources.razor` | `ListLearningResourcesPage` | Exists — needs UI refresh |
| `ResourceAdd.razor` | `AddLearningResourcePage` | Exists — needs UI refresh |
| `ResourceEdit.razor` | `EditLearningResourcePage` | Exists — needs UI refresh |
| `Vocabulary.razor` | `VocabularyManagementPage` | Exists — needs UI refresh |
| `VocabularyWordEdit.razor` | `EditVocabularyWordPage` | Exists — needs UI refresh |
| `VocabularyProgress.razor` | `VocabularyLearningProgressPage` | Exists — needs UI refresh |
| `Cloze.razor` | `ClozurePage` | Exists — needs UI refresh |
| `Conversation.razor` | `ConversationPage` | Exists — needs UI refresh |
| `HowDoYouSay.razor` | `HowDoYouSayPage` | Exists — needs UI refresh |
| `Reading.razor` | `ReadingPage` | Exists — needs UI refresh |
| `Shadowing.razor` | `ShadowingPage` | Exists — needs UI refresh |
| `Translation.razor` | `TranslationPage` | Exists — needs UI refresh |
| `Writing.razor` | `WritingPage` | Exists — needs UI refresh |
| `Scene.razor` | `DescribeAScenePage` | Exists — needs UI refresh |
| `VocabMatching.razor` | `VocabularyMatchingPage` | Exists — needs UI refresh |
| `VocabQuiz.razor` | `VocabularyQuizPage` | Exists — needs UI refresh |
| `VideoWatching.razor` | `VideoWatchingPage` | Exists — needs UI refresh |
| `Import.razor` | `YouTubeImportPage` | Exists — needs UI refresh |
| `MinimalPairs.razor` | `MinimalPairsPage` | Exists — needs UI refresh |
| `MinimalPairCreate.razor` | `CreateMinimalPairPage` | Exists — needs UI refresh |
| `MinimalPairSession.razor` | `MinimalPairSessionPage` | Exists — needs UI refresh |
| `Skills.razor` | `ListSkillProfilesPage` | Exists — needs UI refresh |
| `SkillAdd.razor` | `AddSkillProfilePage` | Exists — needs UI refresh |
| `SkillEdit.razor` | `EditSkillProfilePage` | Exists — needs UI refresh |
| `Profile.razor` | `UserProfilePage` | Exists — needs UI refresh |
| `Onboarding.razor` | `OnboardingPage` | Exists — needs UI refresh |
| `Settings.razor` | `SettingsPage` | Exists — needs UI refresh (theme-switching is new) |

**Shared Blazor components:** `PageHeader`, `ActivityTimer`, `AudioPlayer`, `InteractiveText`, `ToastContainer`, `WaveformDisplay`

### 2.3 What's New in Blazor (not in MauiReactor)

1. **Theme-switching UI** — Settings page lets users pick from 10 themes (5 custom + 5 Bootswatch) with light/dark mode toggle and font scale slider
2. **`PageHeader` component** — Standardized header with mobile hamburger/back nav, toolbar actions, primary/secondary action menus, overflow dropdown
3. **Sidebar navigation** — Desktop sidebar + mobile offcanvas nav (via `MainLayout` + `NavMenu`)
4. **`NavigationMemoryService`** — Remembers last-visited page per nav section
5. **Bridge services** — `TranslationBridge`, `WritingBridge` for native↔Blazor communication (these are Blazor-specific and won't be needed)
6. **`ThemeService`** — Manages theme/mode/font-scale preferences (will be adapted for MauiBootstrapTheme)
7. **Toast notifications** — `ToastService` + `ToastContainer`
8. **Responsive layouts** — CSS-driven `d-none d-md-block` patterns for mobile/desktop (needs MauiReactor equivalent)

### 2.4 Shared Code That Carries Over As-Is

These layers are **identical or nearly identical** between branches and should NOT be rewritten:

- **Data layer:** All 11 repositories (`*Repository.cs`)
- **Services:** All 50+ services (AI, audio, speech, plan generation, progress, vocabulary, etc.)
- **Models:** All shared models in `Shared/Models/`
- **Database/Migrations:** SQLite schema and migrations
- **Localization:** `.resx` resource files
- **Platform code:** `Platforms/` directory

Services with Blazor-specific additions that need review:
- `ThemeService.cs` — exists in Blazor branch, adapt for MauiBootstrapTheme
- `TranslationBridge.cs`, `WritingBridge.cs` — Blazor-only, discard
- `WebUI/Services/*` — Blazor-only, discard

---

## 3. New Library Analysis

### 3.1 MauiBootstrapTheme (`Plugin.Maui.BootstrapTheme`)

**What it does:** Parses Bootstrap/Bootswatch CSS files at build time, generates native `ResourceDictionary` with `StyleClass`-based styles.

**Setup:**
```csharp
// MauiProgram.cs
builder.UseBootstrapTheme();

// Apply theme at runtime
BootstrapTheme.Apply("seoul-pop");  // or "darkly", "vapor", etc.
```

**Key API mapping — Bootstrap CSS classes → MauiReactor `StyleClass()`:**

| Bootstrap CSS | MauiReactor | Control |
|---------------|-------------|---------|
| `btn-primary` | `.StyleClass("btn-primary")` | `Button` |
| `btn-outline-danger` | `.StyleClass("btn-outline-danger")` | `Button` |
| `btn-lg`, `btn-sm` | `.StyleClass("btn-primary","btn-lg")` | `Button` |
| `btn-pill` | `.StyleClass("btn-primary","btn-pill")` | `Button` |
| `h1`..`h6` | `.StyleClass("h1")` | `Label` |
| `text-muted` | `.StyleClass("text-muted")` | `Label` |
| `card` | `.StyleClass("card")` | `Border` |
| `shadow` | `.StyleClass("card","shadow")` | `Border` |
| `text-bg-primary` | `.StyleClass("text-bg-primary")` | `Border` |
| `badge,bg-danger` | `.StyleClass("badge","bg-danger")` | `Border` |
| `progress-success` | `.StyleClass("progress-success")` | `ProgressBar` |

**DynamicResource keys:** `Primary`, `Secondary`, `Success`, `Danger`, `Warning`, `Info`, `Light`, `Dark`, `BodyBackground`, `SurfaceColor`, `CornerRadius`, `FontSizeH1`..`FontSizeH6`, etc.

**Impact on existing code:** Replaces `MyTheme` constants and `ThemeKey()` calls entirely. Pages use `StyleClass()` instead of `ThemeKey()`.

### 3.2 IconFont.Maui.BootstrapIcons (`IconFont.Maui.BootstrapIcons`)

**What it does:** Ships the Bootstrap Icons TTF font with 2,000+ strongly-typed glyph constants.

**Setup:**
```csharp
// MauiProgram.cs
builder.UseBootstrapIcons();
```

**Key API:**
```csharp
// Create a FontImageSource
var icon = BootstrapIcons.Create(BootstrapIcons.Search, Colors.White, 20);

// Use glyphs directly
BootstrapIcons.Search       // search icon
BootstrapIcons.House        // home icon
BootstrapIcons.HeartFill    // filled heart
BootstrapIcons.Gear         // settings gear
BootstrapIcons.PersonFill   // user profile
BootstrapIcons.PlusCircle   // add action
BootstrapIcons.FontFamily   // font family string
```

**Icon mapping — Blazor `bi-*` → C# constants:**

| Blazor `<i class="bi ...">` | C# Constant | Usage |
|------------------------------|-------------|-------|
| `bi-house-door` | `BootstrapIcons.HouseDoor` | Dashboard nav |
| `bi-book` | `BootstrapIcons.Book` | Resources nav |
| `bi-card-text` | `BootstrapIcons.CardText` | Vocabulary nav |
| `bi-soundwave` | `BootstrapIcons.Soundwave` | Minimal Pairs nav |
| `bi-bullseye` | `BootstrapIcons.Bullseye` | Skills nav |
| `bi-person` | `BootstrapIcons.Person` | Profile nav |
| `bi-gear` | `BootstrapIcons.Gear` | Settings nav |
| `bi-plus-lg` | `BootstrapIcons.PlusLg` | Add actions |
| `bi-chevron-left` | `BootstrapIcons.ChevronLeft` | Back nav |
| `bi-three-dots-vertical` | `BootstrapIcons.ThreeDotsVertical` | Overflow menu |
| `bi-search` | `BootstrapIcons.Search` | Search |
| `bi-x-lg` | `BootstrapIcons.XLg` | Close/dismiss |
| `bi-fire` | `BootstrapIcons.Fire` | Streak badge |
| `bi-stopwatch` | `BootstrapIcons.Stopwatch` | Timer |
| `bi-graph-up` | `BootstrapIcons.GraphUp` | Progress |
| `bi-translate` | `BootstrapIcons.Translate` | Language |
| `bi-chat-dots` | `BootstrapIcons.ChatDots` | Conversation |
| `bi-play-fill` | `BootstrapIcons.PlayFill` | Play |
| `bi-pause-fill` | `BootstrapIcons.PauseFill` | Pause |
| `bi-stop-fill` | `BootstrapIcons.StopFill` | Stop |

**Impact on existing code:** Replaces `FluentUI.*` glyphs and the entire `ApplicationTheme.Icons.cs` file (611 lines). Icons are created inline with `BootstrapIcons.Create()` or referenced via glyph constants + font family.

---

## 4. Branch Strategy

### Recommendation: Start from `main`, port forward

**Create branch:** `feature/bootstrap-native-port`

**Rationale:**
- `main` has the working MauiReactor codebase with all the plumbing (Shell nav, DI, state management)
- The Blazor branch's `.razor` files are reference material for UI design, not code to convert
- Services/data/models on both branches are nearly identical — `main` is the source of truth
- Bridge services (`TranslationBridge`, `WritingBridge`) and `WebUI/` folder are Blazor-specific artifacts that don't carry over
- Starting from `main` means we never break the working app — we re-skin incrementally

**Process:**
1. Create `feature/bootstrap-native-port` from `main`
2. Add NuGet packages and Bootstrap CSS theme files
3. Replace `MyTheme` system with `MauiBootstrapTheme` configuration
4. Replace `FluentUI` icons with `BootstrapIcons`
5. Re-skin each page's MauiReactor code to match Blazor design using `StyleClass()`
6. Cherry-pick any service changes from the Blazor branch that improve business logic
7. Delete `ApplicationTheme.Icons.cs`, `ApplicationTheme.Colors.cs`, `ApplicationTheme.Styles.cs` once fully migrated

**Shared code handling:**
- Data layer diffs (5 files changed in Blazor branch) should be reviewed and cherry-picked if they contain bug fixes
- `ThemeService.cs` from Blazor branch adapted to call `BootstrapTheme.Apply()` instead of JS interop
- All `WebUI/` content stays in Blazor branch — it's reference only

---

## 5. Prioritized Page Porting Order

### P0 — Core App Flow (must work first)

| # | Page | Size | Dependencies | Notes |
|---|------|------|-------------|-------|
| 0 | **Infrastructure setup** | M | None | Add NuGet refs, Bootstrap CSS files, configure `MauiProgram.cs`, replace `ApplicationTheme.*` |
| 1 | **AppShell** | S | #0 | Update Shell tabs/flyout to use Bootstrap icons |
| 2 | **DashboardPage** + cards | L | #0, #1 | Hub page — mode switcher, today's plan, streaks, progress |
| 3 | **ListLearningResourcesPage** | M | #0 | Resource list with search/filter, card layout |
| 4 | **SettingsPage** | M | #0 | Theme-switching UI is a key new feature |
| 5 | **OnboardingPage** | S | #0 | First-run experience |
| 6 | **UserProfilePage** | S | #0 | Profile display |

### P1 — Activity Pages (core learning features)

| # | Page | Size | Dependencies | Notes |
|---|------|------|-------------|-------|
| 7 | **VocabularyQuizPage** | M | #0 | Most-used activity |
| 8 | **WritingPage** | M | #0 | Writing exercises |
| 9 | **TranslationPage** | M | #0 | Translation drills |
| 10 | **ReadingPage** | M | #0 | Reading comprehension |
| 11 | **ConversationPage** | L | #0 | Chat-style UI, complex state |
| 12 | **ClozurePage** | M | #0 | Fill-in-the-blank |
| 13 | **HowDoYouSayPage** | M | #0 | Speaking practice |
| 14 | **ShadowingPage** | L | #0 | Audio waveform + timing — complex |
| 15 | **VocabularyMatchingPage** | M | #0 | Matching game |
| 16 | **DescribeAScenePage** | M | #0 | Image-based writing |

### P1.5 — Resource Management

| # | Page | Size | Dependencies | Notes |
|---|------|------|-------------|-------|
| 17 | **AddLearningResourcePage** | M | #3 | Form page |
| 18 | **EditLearningResourcePage** | L | #3 | Full editor + vocab generation |
| 19 | **VocabularyManagementPage** | M | #0 | Vocab list with filters |
| 20 | **EditVocabularyWordPage** | S | #19 | Word editor |
| 21 | **VocabularyLearningProgressPage** | M | #0 | Progress visualization |

### P2 — Secondary Features

| # | Page | Size | Dependencies | Notes |
|---|------|------|-------------|-------|
| 22 | **MinimalPairsPage** | S | #0 | List view |
| 23 | **CreateMinimalPairPage** | M | #22 | Creation form |
| 24 | **MinimalPairSessionPage** | M | #22 | Practice session |
| 25 | **ListSkillProfilesPage** | S | #0 | Skills list |
| 26 | **AddSkillProfilePage** | M | #25 | Skill form |
| 27 | **EditSkillProfilePage** | M | #25 | Skill editor |
| 28 | **VideoWatchingPage** | L | #0 | Video player integration |
| 29 | **YouTubeImportPage** | M | #0 | YouTube import flow |

**Total estimates:** ~4 S, ~17 M, ~5 L pages + 1 M infrastructure task

---

## 6. Theming Strategy

### 6.1 Replacing `MyTheme` with `MauiBootstrapTheme`

**Current approach (to be replaced):**
```csharp
// Old: ThemeKey-based styling
Button("Click").ThemeKey(MyTheme.Primary)
Label("Title").ThemeKey(MyTheme.Title1)
Border(...).ThemeKey(MyTheme.CardStyle)
```

**New approach:**
```csharp
// New: StyleClass-based styling
Button("Click").StyleClass("btn-primary")
Label("Title").StyleClass("h1")
Border(...).StyleClass("card","shadow")
```

**Color references:**
```csharp
// Old
.BackgroundColor(MyTheme.PrimaryColor)
.TextColor(MyTheme.DarkOnLightBackground)

// New — use DynamicResource via Application.Current.Resources
.BackgroundColor((Color)Application.Current.Resources["Primary"])
// Or in MauiReactor, reference via DynamicResource binding
```

### 6.2 Theme Files to Add

Place Bootswatch CSS files in `Resources/Themes/`:
```
Resources/Themes/
├── bootstrap.min.css          (default Bootstrap)
├── seoul-pop.min.css          (custom — needs creation)
├── ocean.min.css              (custom — needs creation)
├── forest.min.css             (custom — needs creation)
├── sunset.min.css             (custom — needs creation)
├── monochrome.min.css         (custom — needs creation)
├── flatly.min.css             (from Bootswatch)
├── sketchy.min.css            (from Bootswatch)
├── slate.min.css              (from Bootswatch)
├── vapor.min.css              (from Bootswatch)
└── brite.min.css              (from Bootswatch)
```

Configure in `.csproj`:
```xml
<ItemGroup>
  <BootstrapTheme Include="Resources/Themes/*.min.css" />
</ItemGroup>
```

### 6.3 Adapted `ThemeService`

The Blazor `ThemeService` manages theme/mode/font-scale via JS interop. For native MAUI:

```csharp
public class NativeThemeService
{
    public void SetTheme(string theme)
    {
        BootstrapTheme.Apply(theme);
        Preferences.Set("AppTheme", theme);
    }

    public void SetMode(string mode)
    {
        App.Current.UserAppTheme = mode == "dark" ? AppTheme.Dark : AppTheme.Light;
        Preferences.Set("AppThemeMode", mode);
    }
}
```

### 6.4 Icon Transition: FluentUI → BootstrapIcons

**Current:** 611-line `ApplicationTheme.Icons.cs` with hand-coded `FontImageSource` properties using `FluentUI.*` glyphs.

**New:** Delete `ApplicationTheme.Icons.cs`. Use `BootstrapIcons.Create()` inline or create a thin helper:

```csharp
// Inline usage in MauiReactor
ImageButton()
    .Source(BootstrapIcons.Create(BootstrapIcons.Search, Colors.Gray, 20))

// Or for tab bar icons in AppShell
Tab(BootstrapIcons.Create(BootstrapIcons.HouseDoor, Colors.Gray, 24),
    ShellContent<DashboardPage>().Title("Dashboard"))
```

**Migration pattern:** For each `MyTheme.IconXxx` reference, find the Bootstrap equivalent in the 2,000+ glyph constants.

### 6.5 Files to Delete After Full Migration

- `src/SentenceStudio/Resources/Styles/ApplicationTheme.cs`
- `src/SentenceStudio/Resources/Styles/ApplicationTheme.Colors.cs`
- `src/SentenceStudio/Resources/Styles/ApplicationTheme.Icons.cs`
- `src/SentenceStudio/Resources/Styles/ApplicationTheme.Styles.cs`

---

## 7. Shared Component Strategy

### 7.1 `PageHeader` — New Reusable Component

The Blazor `PageHeader` component standardizes page headers across the app. Create an equivalent MauiReactor component:

```csharp
public partial class PageHeader : Component
{
    // Title, ShowBack, toolbar actions, primary actions, overflow menu
    // Mobile: hamburger/back + title + overflow
    // Desktop: title + action buttons + overflow
}
```

This replaces ad-hoc header construction in each page.

### 7.2 Components to Port

| Blazor Component | MauiReactor Equivalent | Action |
|------------------|----------------------|--------|
| `PageHeader` | New `PageHeader` component | Create new |
| `ActivityTimer` | Existing `ActivityTimerBar` | Re-skin |
| `AudioPlayer` | Existing `FloatingAudioPlayerComponent` | Re-skin |
| `InteractiveText` | Existing `InteractiveTextRenderer` | Re-skin |
| `WaveformDisplay` | Existing `WaveformView` | Re-skin |
| `ToastContainer` | Use CommunityToolkit Toast | Already available |

---

## 8. Responsive Layout Strategy

The Blazor UI uses CSS breakpoints (`d-none d-md-block`) for responsive layouts. In native MAUI:

- **Mobile vs Desktop detection:** Use `DeviceInfo.Idiom == DeviceIdiom.Desktop` or screen width checks
- **Adaptive layouts:** Use `Grid` with conditional column counts based on screen size
- **No CSS media queries:** Handle in state/render logic, not CSS

```csharp
// Example: responsive card grid
int columns = DeviceInfo.Idiom == DeviceIdiom.Desktop ? 3 : 1;
Grid(rows: "Auto", columns: string.Join(",", Enumerable.Repeat("*", columns)),
    // ... cards
)
```

---

## 9. MauiReactor-Specific Patterns (Conventions Reminder)

Every page must follow these conventions:

- **Layout:** `VStart()`, `VEnd()`, `HStart()`, `HEnd()`, `HFill()`, `VFill()`
- **Never:** `HorizontalOptions()`, `VerticalOptions()`, `FillAndExpand`
- **Navigation:** `Shell.Current.GoToAsync()` exclusively
- **Icons:** `BootstrapIcons.Create()` — no inline `FontImageSource`
- **Styling:** `.StyleClass()` — no inline colors/fonts (use `DynamicResource` for dynamic values)
- **Scrolling:** CollectionView inside Grid star-rows, never inside VStack
- **Single child per ContentPage**
- **`OnAppearing` for data refresh**

---

## 10. Risks & Open Questions for Captain David

### Risks

1. **Custom themes (seoul-pop, ocean, etc.):** These 5 themes are CSS-variable overlays in the Blazor version. They need to be actual Bootstrap CSS files for `MauiBootstrapTheme` to parse them. **Risk: Medium.** Either create full Bootstrap CSS variants or find another approach for custom themes.

2. **`StyleClass` + MauiReactor compatibility:** MauiReactor's fluent API supports `.StyleClass()` but needs verification that generated Bootstrap styles are applied correctly at runtime. **Risk: Low** but needs a spike.

3. **Responsive layout without CSS:** Several Blazor pages use `d-none d-md-block` patterns extensively. Reproducing these in native MAUI requires manual idiom checks. **Risk: Low** — it's just work, not a blocker.

4. **Font scale:** The Blazor version has a font-size slider (85%–150%). MauiBootstrapTheme's generated styles use fixed font sizes from CSS. A custom approach for font scaling may be needed. **Risk: Medium.**

5. **Shadow rendering:** MauiBootstrapTheme docs note Shadow via StyleClass may not work on all platforms. May need the `Bootstrap.Shadow` attached property fallback. **Risk: Low.**

### Open Questions for Captain David

1. **Custom themes:** Do you want to invest in creating full Bootstrap CSS files for the 5 custom themes (seoul-pop, ocean, forest, sunset, monochrome)? Or start with just the 5 Bootswatch themes and add custom themes later?

2. **Priority override:** The plan has Dashboard + Settings + Onboarding as P0. Would you prefer a different first page to validate the Bootstrap theming approach?

3. **Syncfusion controls:** The current app uses Syncfusion toolkit controls (bottom sheets, etc.). Should these stay, or should we look for Bootstrap-styled alternatives?

4. **`PageHeader` component:** The Blazor version has a sophisticated responsive header. For native MAUI with Shell, the navigation bar is handled differently. Should we create a custom PageHeader component, or rely on Shell's built-in TitleView?

5. **Font scale feature:** Is the text-size slider from the Blazor Settings page a must-have for the native port, or can it be deferred?

6. **Localization:** The current `copilot-instructions.md` has detailed localization rules. Should the port use the same `.resx` approach, or is this a chance to evaluate a different strategy?

7. **Data layer cherry-picks:** The Blazor branch modified 5 repository files. Should I review those diffs for bug fixes to bring over, or are they Blazor-specific changes?

---

## 11. Definition of Done

A page is "ported" when:

1. ✅ Uses `StyleClass()` for all styling (no `ThemeKey()` or `MyTheme.*` color references)
2. ✅ Uses `BootstrapIcons` for all icons (no `FluentUI.*` references)
3. ✅ Visual appearance matches the Blazor Hybrid version
4. ✅ Dark mode works correctly via `BootstrapTheme.Apply()`
5. ✅ Shell navigation works (no `Navigation.PushAsync`)
6. ✅ `OnAppearing` refreshes data
7. ✅ Builds for `net10.0-maccatalyst` without warnings related to theming
8. ✅ Verified running on device/simulator with screenshot

---

## 12. Estimated Timeline

| Phase | Scope | Estimate |
|-------|-------|----------|
| Infrastructure | NuGet + theme setup + MauiProgram config + Shell update | 1 day |
| P0 pages | Dashboard, Resources, Settings, Onboarding, Profile | 3-4 days |
| P1 activities | 10 activity pages | 5-7 days |
| P1.5 management | Resource/vocab CRUD pages | 3-4 days |
| P2 secondary | MinimalPairs, Skills, Video | 3-4 days |
| Cleanup | Delete old theme files, update `copilot-instructions.md` | 1 day |
| **Total** | | **~16-21 days** |

These are working-day estimates for a focused developer. Parallelization possible across P1 activity pages.
