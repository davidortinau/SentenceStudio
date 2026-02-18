# Project Context

- **Owner:** David Ortinau (david.ortinau@microsoft.com)
- **Project:** SentenceStudio — language learning app, porting Blazor Hybrid UI back to native MauiReactor with Bootstrap theming
- **Stack:** .NET MAUI, MauiReactor (MVU), C#, MauiBootstrapTheme, IconFont.Maui.BootstrapIcons
- **Created:** 2026-02-17

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

📌 Team update (2026-02-17): Bootstrap port plan established — decided by Mal

### 2025-02-17: Bootstrap Native Port Infrastructure Setup (WI-0)

**Context:** Setting up foundation for porting Blazor Hybrid pages to native MauiReactor with Bootstrap theming.

**Key decisions:**
- Used preview NuGet packages for Bootstrap theme libraries (Plugin.Maui.BootstrapTheme 0.1.0-preview.5, IconFont.Maui.BootstrapIcons 1.0.0-preview.1)
- IconFont.Maui.BootstrapIcons has build issues in preview.1 (font file not found during Resizetizer step)
- Decision: Set up infrastructure WITHOUT the actual Bootstrap packages for now — add NativeThemeService stub that can be integrated later when stable versions are available

**Files created:**
- `src/SentenceStudio/Services/NativeThemeService.cs` — Theme/mode/font-scale management service
  - Stores theme preferences via Preferences API
  - Supports theme switching, light/dark mode, font scale
  - Currently logs theme changes (Bootstrap integration pending stable package releases)
  - Event-based notification pattern for theme changes

**Files modified:**
- `src/SentenceStudio/SentenceStudio.csproj` — No Bootstrap packages added yet (preview versions have issues)
- `src/SentenceStudio/MauiProgram.cs` — Registered NativeThemeService in DI container
- `src/SentenceStudio/_usings.cs` — No Bootstrap usings added yet

**Architecture notes:**
- NativeThemeService.AvailableThemes dictionary contains 6 theme names (seoul-pop, ocean, forest, sunset, monochrome, bootstrap)
- Theme application via BootstrapTheme.Apply() is stubbed out for now
- Dark/light mode switching works via Application.Current.UserAppTheme
- Font scale is tracked but not yet applied (requires theme system integration)

**Next steps:**
- Monitor Bootstrap library stable releases (Plugin.Maui.BootstrapTheme, MauiBootstrapTheme.Reactor, MauiBootstrapTheme.Themes.Default)
- When stable, add packages, update NativeThemeService.ApplyTheme() to call BootstrapTheme.Apply()
- Create custom theme providers (SeoulPopTheme, OceanTheme, etc.) implementing IBootstrapThemeProvider
- Add .UseBootstrapTheme() and .UseBootstrapIcons() to MauiProgram.cs builder chain
- Test theme switching in SettingsPage

**Build status:** ✅ Builds successfully for net10.0-maccatalyst (0 errors, 81 warnings — all pre-existing AsyncFixer warnings)


### 2026-02-17: Dashboard Bootstrap Port - IN PROGRESS

**Context:** Rewriting Dashboard page (from main branch) to match Blazor Hybrid Bootstrap design.

**Status:** Foundation complete, fixing compilation errors.

**Key decisions:**
- Replaced Sync fusion SfSegmentedControl with Bootstrap button group (two Primary buttons side-by-side)
- Replaced MyTheme styling with Bootstrap fluent methods (`.Primary()`, `.H1()`, etc.)
- Replaced FluentUI icons with BootstrapIcons
- Kept all existing data loading logic, state management, and navigation intact
- Removed old ActivityBorder/ConversationActivityBorder components - activities now rendered inline

**Compilation errors to fix:**
- MarginLevel only takes 1 param (spacing level 0-5), not 4 params  
- No HSpaceBetween method - use Grid or explicit spacing
- DynamicResourceExtension doesn't exist in MauiReactor - use direct colors
- BootstrapIcons.Grid3x3Gap doesn't exist - need to find correct icon name
- VocabularyFilterType values are: All, Known, Learning, Unknown (not New/Review)

**Next steps:**
1. Fix MarginLevel calls - use `.Margin(0, 0, 0, 16)` for explicit margin values
2. Fix HSpaceBetween - replace with Grid layout or explicit Spacer()
3. Fix DynamicResourceExtension - use direct color refs or Bootstrap variant methods
4. Find correct grid icon name in BootstrapIcons
5. Fix vocabulary filter type mapping
6. Rebuild and validate on Mac Catalyst


### 2026-02-17: Dashboard Styling Fixes

**Context:** Captain reviewed the Bootstrap-ported Dashboard and identified specific styling issues.

**Issues fixed:**
1. **Page padding** — Changed from 12px to 16px (Bootstrap p-3 = level 3 = 16px) to match Blazor layout
2. **Mode toggle deselected state** — Changed from `.Primary().Outlined()` to `.Secondary().Outlined()` for proper light-on-light visibility
3. **Mode toggle icons removed** — Bootstrap icon unicode glyphs embedded in button text won't render (need BootstrapIcons font family). Removed icon chars from button text.
4. **Heading labels** — Replaced `.H4()` and `.H6()` with explicit `.FontSize(17)` + `.FontAttributes(FontAttributes.Bold)` for body-weight headings, kept `.H3()` for larger section headings
5. **Muted text standardized** — Replaced mixed `.Muted()` with `.FontSize(14)` to consistent `.Muted().Small()` pattern
6. **Vocabulary stat card labels** — Already had `.TextColor(variant)` on numbers, kept consistent `.Muted().Small()` on labels

**Bootstrap theme insights:**
- `.H1()` through `.H6()` DO set font sizes: H1=40px, H2=32px, H3=28px, H4=24px, H5=20px, H6=16px (from BootstrapTheme.cs)
- Typography attached properties work via `ApplyLabelTypography()` handler
- `.Muted()` sets `TextColor = theme.Muted` (not just opacity)
- `.Small()` sets `FontSize = theme.FontSizeSmall` (12.8px = 0.8rem)
- `.Primary().Outlined()` on light background = transparent bg + primary border/text (works)
- `.Secondary().Outlined()` on light background = transparent bg + secondary border/text (better contrast)

**Pattern established:**
- Section headings: Use `.H3()` (28px, bold)
- Body-weight headings (for card titles, labels): Use `.FontSize(17).FontAttributes(FontAttributes.Bold)`
- Muted secondary text: Use `.Muted().Small()` consistently
- Button toggle groups: Selected = `.Primary()`, Deselected = `.Secondary().Outlined()`

**Build status:** ✅ Builds successfully (0 errors, 67 warnings — all pre-existing AsyncFixer)


### 2026-02-17: Settings Page — Appearance Section

**Context:** Added Appearance section to SettingsPage matching the Blazor Hybrid version. Includes theme swatches, light/dark mode toggle, and text size slider.

**Files modified:**
- `src/SentenceStudio/Pages/AppSettings/SettingsPage.cs` — Added RenderAppearanceSection(), RenderThemeSwatch(), RenderModeButton() methods; 3 new state properties (SelectedTheme, IsDarkMode, FontScale); NativeThemeService injection
- `src/SentenceStudio/Services/NativeThemeService.cs` — Added GetThemeDisplayName() and GetThemeSwatchColors() static methods for theme metadata
- `src/SentenceStudio/Resources/Strings/AppResources.resx` — 7 new keys (Appearance, ChooseTheme, DisplayMode, Light, Dark, TextSize, AdjustTextSize already existed)
- `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx` — Matching Korean translations

**Key learnings:**
- **FlexLayout in MauiReactor**: Use `.Wrap(Microsoft.Maui.Layouts.FlexWrap.Wrap)` with `.JustifyContent()` and `.AlignItems()` — first FlexLayout usage in the codebase
- **Conditional button styling**: Can't use `.Apply()` fluent extension (may not exist in MauiReactor). Solution: extract to helper method returning either `btn.Primary()` or `btn.Secondary().Outlined()`
- **BootstrapTheme.Current**: Returns `BootstrapTheme` type (NOT `BootstrapThemeInstance` — that doesn't exist)
- **BootstrapIcons API**: SunFill, MoonFill, BrightnessHighFill, Fonts, CheckLg confirmed available via XML docs in NuGet package
- **MauiDevFlow REST API vs CLI**: REST API (`http://localhost:9347/api/...`) is more reliable than CLI commands for navigation (`POST /api/action/navigate` with `{"route":"///PageName"}`) and screenshots (`GET /api/screenshot`)
- **Theme swatch colors**: Some themes have different colors for light vs dark mode (seoul-pop, ocean, forest, sunset, monochrome)

**Pattern established:**
- Theme swatches: FlexLayout grid with Border wrapping colored boxes, selected state shown with blue highlight border
- Mode toggle: Two buttons side-by-side, selected = `.Primary()`, deselected = `.Secondary().Outlined()` (consistent with Dashboard pattern)
- Slider with percentage display: Map float (0.85-1.5) to percentage text (85%-150%) with 5% step rounding

**Build status:** ✅ Builds and renders correctly (verified via screenshot on Mac Catalyst)


### 2026-02-17: Vocabulary Management Page — P0 Gaps Fixed (Stats, Search, Filter)

**Context:** Audit found 3 P0 gaps in VocabularyManagementPage.cs where Blazor features were missing from the native port: stats bar, visible search input, and filter dropdown.

**Files modified:**
- `src/SentenceStudio/Pages/VocabularyManagement/VocabularyManagementPage.cs` — Added `RenderTopSection()`, `RenderStatsBadge()`, `RenderFilterToggle()` methods; restructured Grid from 2-row to 3-row layout (Auto,*,Auto); refactored `ApplyFilters()` to apply SelectedFilter independently of ParsedQuery; removed hardcoded GridRow from child render methods
- `src/SentenceStudio/Resources/Strings/AppResources.resx` — Added "Associated" key
- `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx` — Added "Associated" Korean translation

**Key learnings:**
- **ApplyFilters refactoring**: The original code applied SelectedFilter only in the "else" branch (when no ParsedQuery existed). The top-level filter toggle needs to work alongside search — refactored so SelectedFilter is always applied first, then ParsedQuery or legacy search on top
- **GridRow management**: When restructuring Grid layouts, remember to remove hardcoded `.GridRow(n)` from child render methods when applying GridRow at the call site instead
- **Bootstrap badge pattern**: `Border(Label(...).FontSize(12).TextColor(White).Padding(8,4)).Background(color).StrokeThickness(0).StrokeShape(RoundRectangle().CornerRadius(12))` for pill badges
- **Filter toggle pattern**: Use `btn.Primary()` for selected, `btn.Secondary().Outlined()` for deselected — consistent with Dashboard mode toggle pattern

**Build status:** ✅ Builds successfully (0 errors, 67 warnings — all pre-existing)


### 2026-02-17: Settings Page — P0 Gap Fixes (Quiz Direction, Slider Range, Save Button)

**Context:** Audit found 3 P0 gaps between Blazor and native Settings page. Fixed all three.

**Gap 1 — Quiz Direction: 2-way Switch → 3-way segmented control**
- Replaced `Switch` toggle with 3 `Button` controls in an `HStack` (Forward / Reverse / Mixed)
- Changed `SettingsPageState.QuizDirection` from `bool` to `string` (values: "TargetToNative", "NativeToTarget", "Mixed")
- Added `RenderDirectionButton()` helper using same selected/deselected pattern as mode toggle: `.Primary()` for active, `.Secondary().Outlined()` for inactive
- Updated `VocabularyQuizPreferences.DisplayDirection` to accept "Mixed" as a valid value

**Gap 2 — Auto-advance slider range mismatch**
- Changed Slider from `0.5–5.0s` to `1.0–10.0s` (matching Blazor's `min=1 max=10`)
- Changed step from arbitrary rounding to `0.5s` increments (`Math.Round(value * 2) / 2.0`)
- Updated `VocabularyQuizPreferences.AutoAdvanceDuration` clamp max from 5000ms to 10000ms

**Gap 3 — Missing "Save Preferences" button**
- Added `.Primary()` styled "Save Preferences" button above the "Reset to Defaults" button
- Shows toast confirmation via `AppShell.DisplayToastAsync()` using localized "PreferencesSaved" string
- Auto-save behavior on each control change is preserved (the button is a UX confirmation)

**Files modified:**
- `src/SentenceStudio/Pages/AppSettings/SettingsPage.cs` — State type change, 3-button segmented control, slider range, save button, new helper method
- `src/SentenceStudio/Services/VocabularyQuizPreferences.cs` — Accept "Mixed" direction, increase clamp max to 10000ms, updated doc comment
- `src/SentenceStudio/Resources/Strings/AppResources.resx` — 5 new keys: QuizDirectionForward, QuizDirectionReverse, QuizDirectionMixed, PreferencesSaved
- `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx` — Matching Korean translations

**Key learnings:**
- Segmented controls in MauiReactor: Use `HStack(spacing: 0)` with buttons styled `.Primary()` (selected) vs `.Secondary().Outlined()` (deselected) — same pattern as the Light/Dark mode toggle
- Slider step snapping: Use `Math.Round(value * N) / N` where N is 1/step (e.g., N=2 for step=0.5)
- VocabularyQuizPreferences uses string-based validation, so adding new valid values requires updating the setter's validation guard

**Build status:** ✅ Builds successfully (0 errors, 84 warnings — all pre-existing)

