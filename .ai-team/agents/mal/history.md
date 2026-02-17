# Project Context

- **Owner:** David Ortinau (david.ortinau@microsoft.com)
- **Project:** SentenceStudio — language learning app, porting Blazor Hybrid UI back to native MauiReactor with Bootstrap theming
- **Stack:** .NET MAUI, MauiReactor (MVU), C#, MauiBootstrapTheme, IconFont.Maui.BootstrapIcons
- **Created:** 2026-02-17

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2025-07-25: Initial codebase analysis

**Key file paths:**
- Theme system: `src/SentenceStudio/Resources/Styles/ApplicationTheme.cs` (root), `.Colors.cs` (200 lines), `.Icons.cs` (611 lines), `.Styles.cs` (357 lines)
- AppShell: `src/SentenceStudio/AppShell.cs` (199 lines) — Shell + tabs + route registration
- MauiProgram: `src/SentenceStudio/MauiProgram.cs` — entry point, DI config
- Data layer: `src/SentenceStudio/Data/` — 11 repositories
- Services: `src/SentenceStudio/Services/` — 50+ services
- Pages: `src/SentenceStudio/Pages/` — 29 page files across 15 subdirectories
- Shared components: `src/SentenceStudio/Components/` — ActivityTimerBar, InteractiveTextRenderer, RxInteractiveTextRenderer
- Blazor target UI: `feature/blazor-hybrid-conversion` branch, `src/SentenceStudio/WebUI/` — 30 razor pages, 6 shared components, 6 services

**Architecture patterns:**
- MauiReactor MVU with `Component` base class and `SetState()` for state updates
- Shell navigation exclusively (`Shell.Current.GoToAsync()`)
- `[Inject]` attribute for DI in components
- `IParameter<AppState>` for cross-component state sharing
- `OnAppearing` for data refresh on page return
- Theme uses `ThemeKey()` extension + string constants (`MyTheme.Primary`, `MyTheme.Title1`, etc.)
- Icons centralized in `ApplicationTheme.Icons.cs` using FluentUI font glyphs
- net10.0 target framework (MAUI 10.0.31)

**Conventions noted:**
- Project uses Syncfusion toolkit (bottom sheets, inputs)
- UXDivers Popups for popup management
- CommunityToolkit.Maui for media, storage, toast
- SkiaSharp for custom drawing
- Plugin.Maui.Audio for audio playback
- ElevenLabs for TTS
- Shiny for background services (Android/iOS/macCatalyst only)
- Blazor branch has 10 theme variants: 5 custom (seoul-pop, ocean, forest, sunset, monochrome) + 5 Bootswatch (flatly, sketchy, slate, vapor, brite)
- Blazor branch introduced ThemeService, TranslationBridge, WritingBridge (bridges are Blazor-specific, not needed in native port)
- Data layer is shared between branches with 5 minor diffs in repositories
