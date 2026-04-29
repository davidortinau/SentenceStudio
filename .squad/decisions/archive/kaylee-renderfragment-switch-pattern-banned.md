# Decision: Ban switch-expression-returning-RenderFragment-with-inline-markup in Razor

**Author:** Kaylee
**Status:** Proposed (inbox)
**Date:** 2025

## Context

While preparing for the .NET 11 Preview 3 SDK swap (used during iOS device publish to DX24), Captain hit a catastrophic build failure in `src/SentenceStudio.UI/Pages/ImportContent.razor` — 31 errors including CS9348 on every `@inject` and CS0101/CS0102 with empty type/member names. The same file builds clean on net10 GA.

Wash isolated the trigger to a single Razor pattern in that file, and reproduced the regression in a fresh MAUI Blazor project (`PeeThreeRegression`) for an upstream dotnet/razor issue.

## The banned pattern

```csharp
private static RenderFragment RenderTypeBadge(LexicalUnitType type) => type switch
{
    LexicalUnitType.Word => (__builder) =>
    {
        <span class="badge bg-primary"><i class="bi bi-fonts me-1"></i>Word</span>
    },
    LexicalUnitType.Phrase => (__builder) =>
    {
        <span class="badge bg-secondary"><i class="bi bi-chat-quote me-1"></i>Phrase</span>
    },
    ...
};
```

A C# **switch expression** whose arms are `RenderFragment` lambdas (`(__builder) => { ... }`) with **inline Razor markup** inside each arm. The Razor SG in net11p3 miscompiles this into a malformed partial class.

## Decision

**Don't use this pattern.** Prefer one of:

### Preferred: tuple-meta + inline markup (Option A)

```csharp
private static (string CssClass, string IconClass, string Label) GetTypeBadgeMeta(LexicalUnitType type) => type switch
{
    LexicalUnitType.Word => ("bg-primary bg-opacity-10 text-primary", "bi-fonts", "Word"),
    LexicalUnitType.Phrase => ("bg-secondary bg-opacity-10 text-secondary", "bi-chat-quote", "Phrase"),
    ...
};
```

```razor
@{ var typeMeta = GetTypeBadgeMeta(item.Type); }
<span class="badge @typeMeta.CssClass"><i class="bi @typeMeta.IconClass me-1"></i>@typeMeta.Label</span>
```

Pros: no SG indirection, smaller diff, easier to localize/style, cleaner DOM authorship.

### Acceptable fallback: single RenderFragment with parameter (Option B)

If markup is genuinely complex and you must extract it, write **one** `RenderFragment` parameterized by the meta tuple — not a switch expression of multiple RenderFragment arms:

```csharp
private static RenderFragment RenderBadge((string CssClass, string IconClass, string Label) meta) => __builder =>
{
    <span class="badge @meta.CssClass"><i class="bi @meta.IconClass me-1"></i>@meta.Label</span>
};
```

The single-arm shape doesn't trip the SG bug.

## Rationale

1. **Avoids the net11p3 regression** entirely. We swap to the net11p3 SDK during iOS device publish — any file with this pattern blocks the publish.
2. **Cleaner separation of data vs. markup.** Tuple meta is trivially testable; the markup stays where Razor expects markup.
3. **No measurable runtime cost.** Both shapes lower to roughly equivalent IL after the SG processes them.

## Scope

Applies to all `.razor` files under `src/SentenceStudio.UI/`. Files reviewed in this pass:
- `src/SentenceStudio.UI/Pages/ImportContent.razor` (refactored — was the only offender)

## Action items

- [x] Refactor `ImportContent.razor` (done)
- [ ] When net11p3 SDK is the daily driver, sweep the rest of the .razor files for any new instances and refactor proactively.
- [ ] Mention in the iOS publish runbook (`docs/deploy-runbook.md`) that this pattern blocks the SDK swap, so future Razor authors get the heads-up.
