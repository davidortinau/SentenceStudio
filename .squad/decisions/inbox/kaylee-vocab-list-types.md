# Decision: Type filter uses dropdown pattern (not segmented control)

**Author:** Kaylee
**Date:** 2025-07-25
**Scope:** Vocabulary list page type filter

## Decision

The LexicalUnitType filter on the Vocabulary list page uses the same `<select>` dropdown pattern as all other filters (Association, Status, Encoding, etc.) rather than a segmented control or pill toggle group.

## Rationale

- Consistency with the 6 existing filter dropdowns on the page
- Scales if more types are added later (Unknown is excluded from filter options since it's a fallback — users wouldn't intentionally filter for it)
- Works identically in both desktop filter row and mobile offcanvas panel
- Integrates with the search-query-driven filter system (`type:word` in the search bar) for free

## Impact

If the team later decides pill toggles or segmented controls are better for enum-style filters across the app, this would be the first candidate to convert — but for now, uniformity wins.
