# Decisions

> Shared decision log. All agents read this before starting work.

<!-- Scribe merges decisions from .ai-team/decisions/inbox/ into this file. -->

## 2026-02-17: Bootstrap Port Architecture

### 2025-07-25: Bootstrap port architecture plan
**By:** Mal
**What:** Port SentenceStudio UI from Blazor Hybrid back to native MauiReactor using MauiBootstrapTheme (`StyleClass`-based styling) and IconFont.Maui.BootstrapIcons (strongly-typed glyph constants). Start from `main` branch, port forward. Replace entire `MyTheme`/`ApplicationTheme.*` system with Bootstrap `StyleClass()` calls. Replace FluentUI icons with BootstrapIcons. Adapt `ThemeService` from Blazor branch to call `BootstrapTheme.Apply()` natively. Create reusable `PageHeader` component matching Blazor's responsive header pattern.
**Why:** Starting from `main` preserves the working MauiReactor plumbing (Shell nav, DI, state management) and treats Blazor `.razor` files as design reference only. Using `StyleClass` instead of `ThemeKey` gives us automatic theme-switching from any Bootstrap/Bootswatch CSS file with zero hand-coded styles. Bootstrap Icons provide 2,000+ icons vs maintaining a 611-line hand-coded icon registry. This approach keeps all 50+ services, 11 repositories, and data layer untouched.

### 2025-07-25: Branch strategy — `feature/bootstrap-native-port` from `main`
**By:** Mal
**What:** Create `feature/bootstrap-native-port` from `main`. Do NOT fork from Blazor branch. Cherry-pick data layer fixes from Blazor if applicable.
**Why:** `main` has the stable MauiReactor codebase. The Blazor branch's WebUI/ directory, bridge services, and JS interop are Blazor-specific artifacts that don't port to native MAUI. Starting from main avoids importing unnecessary code that would need deletion.

### 2025-07-25: Theming — `StyleClass()` replaces `ThemeKey()`
**By:** Mal
**What:** All pages switch from `.ThemeKey(MyTheme.Primary)` to `.StyleClass("btn-primary")`. Colors via `DynamicResource` keys (`Primary`, `Secondary`, etc.) instead of `MyTheme.PrimaryColor`. Delete `ApplicationTheme.Icons.cs`, `ApplicationTheme.Colors.cs`, `ApplicationTheme.Styles.cs` after full migration.
**Why:** MauiBootstrapTheme generates native ResourceDictionary styles at build time from CSS. This means any Bootswatch theme "just works" without hand-coding Style objects. The existing 1168-line theme system becomes unnecessary.

### 2025-07-25: Icons — `BootstrapIcons.Create()` replaces `MyTheme.IconXxx`
**By:** Mal
**What:** All icon references change from `MyTheme.IconSearch` (FluentUI-based FontImageSource) to `BootstrapIcons.Create(BootstrapIcons.Search, color, size)`. The centralized `ApplicationTheme.Icons.cs` pattern is replaced by inline `BootstrapIcons.Create()` calls.
**Why:** `IconFont.Maui.BootstrapIcons` provides 2,000+ source-generated glyph constants. Maintaining a centralized icon registry is unnecessary when the library itself is the registry. Visual consistency with the Blazor Hybrid version is guaranteed since both use the same Bootstrap Icons font.
