# Settings Page — Appearance Section

**Date:** 2026-02-17  
**Author:** Kaylee (UI Dev)  
**Status:** Implemented ✅  

## Decision

Added an Appearance section to the native MauiReactor SettingsPage matching the Blazor Hybrid version's design. The section includes:

1. **Theme Swatches** — 12 registered themes displayed in a FlexLayout grid with two-tone color previews
2. **Light/Dark Mode Toggle** — Two-button group with sun/moon BootstrapIcons
3. **Text Size Slider** — Range 85%-150% with 5% step rounding

## Implementation Details

- Theme metadata (display names, swatch colors) added as static methods on `NativeThemeService` rather than a separate class, keeping theme logic centralized
- FlexLayout used for theme swatches (first usage in codebase) — allows natural wrapping across screen widths
- Conditional button styling extracted to `RenderModeButton()` helper since `.Apply()` fluent pattern is unreliable in MauiReactor
- Selected theme indicated by blue border highlight (consistent with Blazor version)

## Patterns Reused

- **Button toggle group**: Selected = `.Primary()`, Deselected = `.Secondary().Outlined()` (same as Dashboard mode toggle)
- **Section structure**: `Label().H3()` heading + content (same as other settings sections)
- **Localization**: All user-visible strings use `_localize["Key"]` with entries in both `.resx` files

## Files Changed

| File | Change |
|------|--------|
| `SettingsPage.cs` | Added Appearance section (3 render methods, 3 state props, DI injection) |
| `NativeThemeService.cs` | Added `GetThemeDisplayName()` and `GetThemeSwatchColors()` static methods |
| `AppResources.resx` | 7 new localization keys |
| `AppResources.ko-KR.resx` | Matching Korean translations |
