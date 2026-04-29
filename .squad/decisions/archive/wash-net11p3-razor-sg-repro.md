# Decision: net11 Preview 3 Razor SG regression — verified bug pattern + repro available

**Author:** Wash (Backend Dev)
**Date:** 2026-04-28
**Status:** Verified, repro packaged, ready to file upstream

## Context
SentenceStudio's `src/SentenceStudio.UI/Pages/ImportContent.razor` fails to compile under .NET 11 Preview 3 (`11.0.100-preview.3.26209.122`) with 31 errors per build. The same file compiles cleanly on .NET 10 GA. Captain hypothesized the regression was pattern-specific rather than SDK-wide. Verified.

## Decision
File a bug against `dotnet/razor` (or `dotnet/sdk` if Razor team redirects) using the minimal MAUI Blazor repro I built.

## The pattern that breaks
A switch expression returning `RenderFragment` where each arm contains an inline `(__builder) => { ... }` lambda with **Razor markup** inside the lambda body, declared inside an `@code` block in a `.razor` file.

```razor
@code {
    private static RenderFragment RenderBadge(SampleType type) => type switch
    {
        SampleType.Alpha => (__builder) => { <span>Alpha</span> },
        SampleType.Beta  => (__builder) => { <span>Beta</span> },
        _                => (__builder) => { <span>Unknown</span> }
    };
}
```

## Diagnostic fingerprint
- `CS0101` with **empty** member name
- `CS0102` with **empty** member name
- Cascading `CS0246` on every type after the switch (including `@inject` services and inline enums)
- `CS9348` ("compilation unit cannot directly contain members") when ≥1 `@inject` is present alongside the broken pattern — the SG appears to drop injected fields at file scope after the switch fails to emit valid member names

## Verified
- **Works:** .NET 10 GA — original SentenceStudio code path
- **Fails:** .NET 11 Preview 3 (`11.0.100-preview.3.26209.122`)
- **Repro project:** PeeThreeRegression (clean `dotnet new maui-blazor`) + 1 added Razor page reproduces 4 errors in the Shared library

## Artifacts
- **Repro zip:** `~/work/peethree-repro-artifacts/peethree-net11p3-repro.zip` (252 KB) — attach to GitHub issue
- **Build log:** `~/work/peethree-repro-artifacts/peethree-net11p3-repro.log` — paste excerpt into issue body
- **Repro file inside zip:** `peethree-net11p3-repro/PeeThreeRegression.Shared/Pages/RazorSgRepro.razor`
- **REPRO.md** at zip root explains pattern, SDKs, expected/actual, steps, workaround

## Recommended SentenceStudio mitigation (until upstream fixes the SG)
For any `.razor` file using the broken pattern, refactor each switch arm to invoke a separately declared `RenderFragment` helper method instead of using inline markup. Single-line helpers using the `@<span ...>` shorthand work fine:

```csharp
private static RenderFragment RenderAlpha() => @<span class="badge bg-primary">Alpha</span>;
```

This is the immediate path to letting Captain run net11p3 SDK in this repo. Kaylee is refactoring `ImportContent.razor` along these lines in parallel — this decision documents the bug she's working around.

## Where to file
- Primary: https://github.com/dotnet/razor/issues
- If redirected: https://github.com/dotnet/sdk/issues

## What to include in the issue body
1. Title: "Razor SG: switch expression returning inline-markup `RenderFragment` lambdas emits members with empty names (net11 Preview 3 regression)"
2. Repro project zip attached
3. The pattern (above)
4. Diagnostic fingerprint (above)
5. SDK info: works on net10 GA, fails on `11.0.100-preview.3.26209.122`
6. Steps: install net11p3 SDK → `dotnet build` the Shared csproj → 4 errors with empty names
7. Workaround (above)
