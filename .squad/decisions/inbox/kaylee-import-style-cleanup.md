# Decision: Import Content page style normalization

**Date:** 2026-07-27
**Author:** Kaylee (Full-stack Dev)
**Branch:** `feature/import-content`

## Problem

ImportContent.razor accumulated bespoke inline styles that didn't match the rest of the webapp: a custom purple hex color (`#6f42c1`) with inline `background-color`/`color` CSS vars on the Phrase type badge, inline `cursor:pointer` on clickable table rows (rest of app uses `role="button"`), and inline `font-size:0.75rem` on a link icon.

## What Changed

### Style cleanup (merged in commit 3130810)

| Before | After | Rationale |
|--------|-------|-----------|
| `bg-purple` + inline `style="--bs-purple:#6f42c1;color:var(--bs-purple);background-color:rgba(111,66,193,0.1);"` | `bg-secondary bg-opacity-10 text-secondary` | Standard Bootstrap 5 color; no custom CSS vars needed |
| `class="cursor-pointer"` + `style="cursor:pointer;"` on `<tr>` | `role="button"` | Matches app-wide pattern; CSS rule at app.css:1360 handles cursor |
| `style="cursor:pointer;"` on mobile card `<div>` | `role="button"` | Same pattern |
| `style="font-size:0.75rem;"` on link icon | Bootstrap `small` class | Utility class, no inline style |
| Empty `@(isClickable ? "" : "")` class expression on mobile card | Removed | Dead code |

### Kept (justified) inline styles

- Table `<th>` `width` values (40px–110px) — functional column sizing; no Bootstrap class equivalent
- `max-width:220px` on truncated reason text — functional, documented with comment

### Duplicate badge column (merged in commit 3130810)

Wash landed `IsDuplicate` and `DuplicateReason` on `ImportRow`; the preview table now includes a "Duplicate" column using `badge bg-warning bg-opacity-10 text-warning` with `bi-files` icon. Rows are still committable (heads-up only). 5 new resx keys (EN + KO).

## Pattern for Future Agents

- **Clickable non-button elements**: Use `role="button"` — never inline `cursor:pointer`
- **Type badges**: Use standard Bootstrap opacity badge pattern: `badge bg-{color} bg-opacity-10 text-{color}` where `{color}` is primary/secondary/info/success/warning/danger
- **Status badges**: `badge bg-{semantic-color}` (solid, not opacity-tinted)
- **No custom hex colors in Razor markup** — use CSS vars or Bootstrap named colors only
