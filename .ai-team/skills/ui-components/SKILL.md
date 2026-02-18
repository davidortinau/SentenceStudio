# UI Components — Segmented toggles & Bootstrap forms

## SegmentedButtonGroup (two-button toggles)
Use the shared component to render two-button segmented controls with rounded outer corners and a straight inner divider.

```csharp
new SegmentedButtonGroup()
    .Left(activeButton)
    .Right(inactiveButton)
    .CornerRadius(6)
    .Margin(0, 0, 0, 16);
```

**Button styling:**
- Active: `.Primary()`
- Inactive: `.Secondary().Outlined()`
- Always override: `.BorderWidth(0).CornerRadius(0)` so only the group border provides rounding.

## Bootstrap form inputs
For forms (resource, vocabulary, skill detail pages), use Bootstrap classes directly on inputs and avoid wrapper borders:

```csharp
Entry().Class("form-control")
Editor().Class("form-control")
Picker().Class("form-select")
```

Use `Label().Class("form-label")` for field labels.
