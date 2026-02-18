# SegmentedButtonGroup for two-button toggles

## Decision
Use a shared `SegmentedButtonGroup` component for two-button segmented controls to ensure outer rounded corners and a straight inner divider without gaps.

## Details
- Component: `src/SentenceStudio/Pages/Controls/SegmentedButtonGroup.cs`
- Usage: `new SegmentedButtonGroup().Left(leftButton).Right(rightButton).CornerRadius(6).Margin(...)`
- Child buttons should set `.BorderWidth(0).CornerRadius(0)` and use `.Primary()` for active, `.Secondary().Outlined()` for inactive.

## Rationale
This avoids duplicated layout logic and keeps Dashboard/Settings toggles visually consistent with the Blazor button-group pattern.
