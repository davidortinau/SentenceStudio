---
confidence: high
last_validated: 2026-05-10
status: production-proven
pattern: Context-aware picker filtering for activity pages
---

# Activity Picker Gating

## When to Use

Any Blazor activity page (NumberDrill, Quiz, Shadowing, etc.) where the available sub-modes or exercise types depend on the selected context, difficulty, or other setup parameter. Use this pattern when:

- Certain combinations of setup options don't make sense semantically (e.g., "Date + TapTheCounter")
- Certain combinations aren't implemented yet but ARE semantically valid (e.g., "Time + TapTheCounter")
- The backend generator would crash or return nonsensical items for invalid combinations

## Canonical Pattern

### 1. Picker UI with Filter

In the Razor markup, add a `.Where()` LINQ filter to the `@foreach` loop that renders picker buttons:

```razor
<div class="d-flex flex-wrap gap-2">
    @foreach (var mode in availableSubModes.Where(m => IsValidCombo(selectedContext, m.Code)))
    {
        <button type="button" 
                class="btn @(selectedSubMode == mode.Code ? "btn-ss-primary" : "btn-outline-secondary")"
                @onclick="() => selectedSubMode = mode.Code">
            @mode.DisplayName
        </button>
    }
</div>
```

### 2. Validation Predicate

In the `@code` block, add an `IsValidCombo()` method that encodes the business rules:

```csharp
private bool IsValidCombo(string? contextCode, string subModeCode)
{
    // Rule 1: "Any" pseudo-context hides context-specific modes
    bool isAnyContext = string.IsNullOrEmpty(contextCode);
    if (isAnyContext && (subModeCode == "ModeA" || subModeCode == "ModeB"))
        return false;
    
    // Rule 2: ModeA only works for ContextX
    if (subModeCode == "ModeA" && contextCode != "ContextX")
        return false;
    
    // Rule 3: ModeB only works for ContextY
    if (subModeCode == "ModeB" && contextCode != "ContextY")
        return false;
    
    // All other combos are valid
    return true;
}
```

### 3. Context-Change Reset Handler

When the user changes the context picker, reset the sub-mode if it becomes invalid:

```csharp
private void OnContextSelected(string? contextCode)
{
    selectedContext = contextCode;
    
    // Reset sub-mode if it becomes invalid for the new context
    if (!string.IsNullOrEmpty(selectedSubMode) && !IsValidCombo(contextCode, selectedSubMode))
    {
        selectedSubMode = availableSubModes.FirstOrDefault(m => IsValidCombo(contextCode, m.Code))?.Code ?? "";
    }
}
```

Update the context picker buttons to call this handler:

```razor
<button type="button" 
        class="btn @(selectedContext == ctx.Code ? "btn-ss-primary" : "btn-outline-secondary")"
        @onclick="() => OnContextSelected(ctx.Code)">
    @ctx.DisplayName
</button>
```

## Key Principles

1. **UI-level filtering is fastest to ship** — no backend schema migration, no seeder changes, no DB migration
   - Use this for Phase 1 ship when the rule space is small and stable
   - Migrate to backend-level (`SupportedContexts` JSON field on model) in later phase if rules become complex

2. **"Any" pseudo-context handling** — if the picker has an "Any" or "Random" option, hide ALL context-specific modes
   - Random rotation could land on a context where the mode doesn't work
   - Only show modes that work for ALL contexts

3. **Auto-select fallback** — when context change invalidates the current sub-mode, auto-select the first valid mode
   - Don't leave the picker in a "no selection" state — always have a valid default

4. **Document the rules** — add comments explaining WHY each mode is restricted
   - Link to the backend generator code or audit doc that justifies the restriction

## Production Examples

- **NumberDrill.razor** (commit `e8d0fbfe`):
  - TapTheCounter → Counting only (generator has NO TapTheCounter logic for Time/Age/Money/Date/Ordinal)
  - ListenAndPlace → Time only (generator hardcoded Time-only, would crash for non-Time contexts)
  - "Any" context hides both TapTheCounter and ListenAndPlace
  - Decision doc: `.squad/decisions/inbox/kaylee-numberdrill-picker-gated.md`

## When NOT to Use

- **Complex rule matrix** — if you have >10 rules or rules that depend on 3+ parameters, backend-level filtering (Option B) is cleaner
- **Dynamic rules** — if the rules come from user preferences, feature flags, or server config, fetch them from the backend instead of hardcoding
- **Pre-existing backend field** — if the model already has a `SupportedContexts` or `AllowedModes` field, use it instead of duplicating logic in the UI

## Related Patterns

- **Backend-level filtering (Option B):** Add `SupportedContexts: string[]` JSON field to `NumberSubMode` model, populate in seeder, filter in `LoadSetupDataAsync()`
  - Pro: Single source of truth, no UI duplication, easier to unit test
  - Con: 3-day cycle (schema migration + seeder + EF + tests)

## Common Mistakes

1. **Forgetting "Any" context** — hiding context-specific modes under specific contexts but leaving them visible under "Any"
2. **Not resetting sub-mode** — changing context but leaving the now-invalid sub-mode selected → session start fails
3. **Hardcoding mode codes** — using string literals instead of constants → typos break filtering silently
4. **Missing comments** — future maintainers don't know WHY a mode is restricted → remove the filter thinking it's dead code

## Skill Taxonomy

- **Category:** UI patterns
- **Stack:** Blazor, activity pages
- **Related skills:** `activity-audio-playback`, `blazor-nav-state-preservation`
