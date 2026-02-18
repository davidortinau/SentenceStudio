# Decision: Bootstrap→MauiReactor Icon Name Convention

**By:** Inara  
**Date:** 2026-02-17

## What

Bootstrap Icons CSS class names use **kebab-case** (`bi-house-door`, `bi-calendar-check`), but the source-generated C# constants in `IconFont.Maui.BootstrapIcons` use **PascalCase** (`BootstrapIcons.HouseDoor`, `BootstrapIcons.CalendarCheck`).

## Why This Matters

When translating from Blazor's `<i class="bi bi-arrow-clockwise">` to MauiReactor, developers must mentally convert the casing. The pattern is:

1. Split on hyphens: `arrow-clockwise` → `["arrow", "clockwise"]`
2. Capitalize each part: `["Arrow", "Clockwise"]`
3. Join: `ArrowClockwise`

## Recommendation

**Always document the conversion** when showing icon examples. In code reviews, watch for incorrect casing like `BootstrapIcons.arrow_clockwise` or `BootstrapIcons.ArrowClockWise` (wrong capitalization).

**Common icons reference:**

| Bootstrap CSS | C# Constant |
|---------------|-------------|
| `bi-house-door` | `BootstrapIcons.HouseDoor` |
| `bi-calendar-check` | `BootstrapIcons.CalendarCheck` |
| `bi-arrow-clockwise` | `BootstrapIcons.ArrowClockwise` |
| `bi-x-circle` | `BootstrapIcons.XCircle` |
| `bi-check-circle` | `BootstrapIcons.CheckCircle` |
| `bi-box-arrow-in-down` | `BootstrapIcons.BoxArrowInDown` |

## Alternatives Considered

- **Keep kebab-case in C#**: Not valid C# identifier syntax.
- **Use underscores**: Doesn't match .NET naming conventions.
- **Create a lookup dictionary**: Unnecessary overhead — the pattern is simple enough once documented.

## Impact

Low friction once developers learn the pattern. The comprehensive mapping guide (`docs/bootstrap-to-maui-mapping.md`) includes a "Bootstrap Icon Names" section with conversion rules and a quick reference table.
