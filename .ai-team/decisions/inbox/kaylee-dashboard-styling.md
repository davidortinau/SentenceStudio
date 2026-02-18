# Decision: Bootstrap Button Toggle Pattern & Typography Conventions

**Date:** 2026-02-17  
**Author:** Kaylee (UI Dev)  
**Status:** Implemented  
**Affects:** Dashboard styling, future Bootstrap page ports

## Context

During Dashboard port review, Captain identified several styling issues:
- Light-on-light illegibility for deselected mode toggle
- Inconsistent heading styles (some too small, some visually same as body)
- Mixed font sizing approaches (some `.H6()`, some `.FontSize(14)`)

## Decision

### 1. Button Toggle Groups (like mode switcher)
```csharp
// Selected button
Button("Text").Primary()

// Deselected button  
Button("Text").Secondary().Outlined()
```

**Rationale:** `.Primary().Outlined()` on light background has poor contrast. `.Secondary().Outlined()` provides better text visibility while still being clearly deselected.

### 2. Typography Hierarchy

| Use Case | Pattern | Visual | Notes |
|----------|---------|--------|-------|
| **Section headings** | `.H3()` | 28px, bold | Major sections (Activities, Vocabulary Progress) |
| **Card/component titles** | `.FontSize(17).FontAttributes(Bold)` | 17px, bold | Today's Progress, Total words |
| **Body text** | Default Label | 16px, normal | Primary content |
| **Muted/secondary text** | `.Muted().Small()` | 12.8px, muted color | Timestamps, metadata, counts |

**Why explicit font sizes for mid-level headings?**
- `.H6()` = 16px (same as body text, no visual hierarchy)
- `.H4()` = 24px (too large for card titles)
- 17px explicit sizing bridges the gap and matches iOS body-strong conventions

### 3. Bootstrap Icon Glyphs in Buttons

❌ **Don't embed icon glyphs in button text:**
```csharp
Button($"{BootstrapIcons.CalendarCheck} Today's Plan")
// Won't render - needs BootstrapIcons font family
```

✅ **Options:**
1. Use `.ImageSource()` (if Button supports it in MauiBootstrapTheme)
2. Use `ImageButton` with icon
3. Skip icons on text buttons (simplest for toggle groups)

**Decision for mode toggle:** Skip icons. The text is clear enough.

## Exceptions

- **Large title headings** — Use `.H1()` or `.H2()` for page titles, hero text
- **Display text** — Use manual sizing (e.g., 60px) for special cases like splash screens
- **Badges** — Continue using `.Badge(variant)` for streak/notification badges

## References

- `MauiBootstrapTheme/src/MauiBootstrapTheme/Theming/BootstrapTheme.cs` — Font size constants
- `MauiBootstrapTheme/src/MauiBootstrapTheme/Theming/BootstrapAttachedProperties.cs` — Typography handlers
- Blazor Hybrid Dashboard (`feature/blazor-hybrid-conversion` branch) — Design reference
