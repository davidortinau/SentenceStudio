# Project Context

- **Owner:** David Ortinau (david.ortinau@microsoft.com)
- **Project:** SentenceStudio — language learning app, porting Blazor Hybrid UI back to native MauiReactor with Bootstrap theming
- **Stack:** .NET MAUI, MauiReactor (MVU), C#, MauiBootstrapTheme, IconFont.Maui.BootstrapIcons
- **Created:** 2026-02-17

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

📌 Team update (2026-02-17): Bootstrap port plan established — decided by Mal

### 2026-02-17: Bootstrap→MauiReactor Translation Patterns

**Key mappings established:**

1. **Layout**: Bootstrap flexbox (`d-flex`, `gap-3`, `align-items-center`) maps to MauiReactor stacks (`HStack(spacing: 16, ...).VCenter()`). Grid system requires FlexLayout or adaptive Grid with runtime device detection.

2. **Components**: Direct fluent method mapping — `btn btn-primary` → `Button().Primary()`, `badge bg-warning` → `Label().Badge(BootstrapVariant.Warning)`, `card p-3` → `Border().StyleClass("card").PaddingLevel(3)`.

3. **Icons**: Bootstrap Icons `<i class="bi bi-house-door">` becomes `BootstrapIcons.Create(BootstrapIcons.HouseDoor, color, size)` — **PascalCase** constants, not kebab-case like CSS.

4. **Typography**: SentenceStudio's custom classes (`ss-title1`, `ss-body1`) map to MauiReactor headings (`.H1()`, `.H2()`) or manual font sizes. Bootstrap native classes (`h1-h6`, `.lead`, `.text-muted`) have direct fluent equivalents.

5. **Spacing**: Bootstrap 0-5 scale maps to `.MarginLevel(0-5)` and `.PaddingLevel(0-5)`. Directional spacing (like `me-2`, `mb-3`) requires manual `Thickness(left, top, right, bottom)`.

6. **Colors**: Bootstrap variants (`bg-success`, `text-danger`) map to `.Background(BootstrapVariant.Success)`, `.TextColor(BootstrapVariant.Danger)`. Theme colors accessible via `BootstrapTheme.Current.Primary`, etc.

7. **Responsive**: Bootstrap's `d-none d-md-block` requires conditional rendering or `OnIdiom` in native. Responsive grid columns need runtime device detection with `DeviceInfo.Current.Idiom`.

8. **Navigation**: Blazor sidebar → Shell FlyoutItems. All navigation uses `Shell.GoToAsync()`, never `Navigation.PushAsync()`.

**Critical gotchas:**
- Icon names: CSS kebab-case → C# PascalCase (`house-door` → `HouseDoor`)
- No auto-layout: explicit sizing with `.HFill()`, `.WidthRequest()` required
- Font scale: Don't replicate CSS scaling — respect OS accessibility settings
- Scrolling: Wrap pages in ScrollView, use Grid for CollectionView constraints
- Spacing: `MarginLevel(3)` applies to all sides — use manual `Thickness` for directional

**Documentation:** Created comprehensive guide at `docs/bootstrap-to-maui-mapping.md` with 12 sections covering layout, components, typography, icons, colors, responsive patterns, page structure, navigation, real-world examples, gotchas, migration checklist, and resources.
