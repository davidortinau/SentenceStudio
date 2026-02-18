# Dashboard Bootstrap Port Strategy

**By:** Kaylee (UI Dev)  
**Date:** 2026-02-17

## Decision: Port Dashboard UI to Bootstrap theming while keeping data layer intact

**What:** Rewrite Dashboard page Render() methods to use MauiBootstrapTheme.Reactor fluent API + BootstrapIcons instead of MyTheme + FluentUI icons.

**Why:** 
- The Blazor Hybrid version already has a working Bootstrap design
- Porting the UI layer is a visual refresh - all 50+ services, repositories, and business logic stay exactly the same
- Bootstrap fluent methods (`.Primary()`, `.H6()`, `.ShadowSm()`) are simpler than hand-coded theme constants
- BootstrapIcons provides 2,000+ glyphs vs 611-line hand-coded FluentUI registry

**Key patterns established:**
- Mode toggle: Bootstrap button group (two Primary buttons, one Outlined) instead of SfSegmentedControl
- Cards: `Border().Background(BootstrapVariant.Light).ShadowSm().StrokeThickness(0).StrokeShape(RoundRectangle 8px)`
- Typography: `.H1()`, `.H4()`, `.H6()`, `.Muted()`
- Icons: `BootstrapIcons.Create(glyph, color, size)` - inline creation
- Spacing: `.PaddingLevel(3)` for 16px, `.MarginLevel(3)` for 16px vertical margin (Bootstrap spacing scale)
- Layout: Use explicit `.Margin()` for complex spacing, not multi-param MarginLevel

**Blockers found:**
- MarginLevel/PaddingLevel only take 1 param (spacing level 0-5), not 4 params like Thickness
- No HSpaceBetween extension - use Grid or Spacer() for space-between layout
- DynamicResourceExtension doesn't exist - use direct colors or Bootstrap variant methods
- Some BootstrapIcons constants don't match Blazor names (e.g., Grid3x3Gap vs Grid3x3)
- VocabularyFilterType enum values differ from Blazor (All/Known/Learning/Unknown vs New/Review/Learning/Known)

**Next:** Fix compilation errors, validate on Mac Catalyst, compare to Blazor visually.

