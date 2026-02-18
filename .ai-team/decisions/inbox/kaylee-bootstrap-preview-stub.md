# Bootstrap Infrastructure Setup — Preview Package Issues

**Date:** 2025-02-17  
**By:** Kaylee (UI Dev)

## Decision

Set up `NativeThemeService` as a stub without integrating Bootstrap NuGet packages due to preview version instability.

## Context

WI-0 (Bootstrap Native Port infrastructure) requires adding:
- `Plugin.Maui.BootstrapTheme` (v0.1.0-preview.5)
- `MauiBootstrapTheme.Reactor` (not found in NuGet)
- `MauiBootstrapTheme.Themes.Default` (not found in NuGet)
- `IconFont.Maui.BootstrapIcons` (v1.0.0-preview.1)

**Issue encountered:**
- `IconFont.Maui.BootstrapIcons` preview.1 fails during build with MSB3030 error (font file not found in buildTransitive/Fonts/)
- `MauiBootstrapTheme.Reactor` and `MauiBootstrapTheme.Themes.Default` do not exist on NuGet
- Only `Plugin.Maui.BootstrapTheme` is available but requires companion packages

## What We Did

Created `NativeThemeService.cs` with full theme/mode/font-scale management API, but stubbed out the actual Bootstrap integration:

```csharp
private void ApplyTheme(string themeName)
{
    _logger.LogInformation("Theme '{Theme}' would be applied (Bootstrap integration pending)", themeName);
}
```

Registered the service in DI so pages can reference it, but didn't add the Bootstrap NuGet packages.

## Why

- Build must succeed for infrastructure work to be considered "done"
- Preview packages are unstable and may change API surface
- The service API is well-defined and can be integrated later without changing consuming code
- Dark/light mode switching works without Bootstrap (via `Application.Current.UserAppTheme`)

## Next Steps

1. Monitor Bootstrap library releases — check for stable 1.0 versions or updated previews with bug fixes
2. When stable packages are available:
   - Add NuGet references
   - Create custom theme providers (SeoulPopTheme, OceanTheme, Forest, Sunset, Monochrome)
   - Update `NativeThemeService.ApplyTheme()` to call `BootstrapTheme.Apply(provider)`
   - Add `.UseBootstrapTheme()` and `.UseBootstrapIcons()` to MauiProgram.cs
3. WI-1 (AppShell port) and WI-2 (DashboardPage port) can proceed using the existing MyTheme system — we'll swap in Bootstrap styling incrementally as pages are ported

## Impact

- Pages can reference `NativeThemeService` for theme switching UI (Settings page)
- Theme persistence works (Preferences API)
- Light/dark mode works
- Actual Bootstrap theme application is deferred until stable packages are available
- No blocker for starting page porting work — old theme system remains functional

## Risk Mitigation

If Bootstrap packages remain unstable:
- Alternative: Manually define Bootstrap styles in ResourceDictionary (more work, but avoids library dependency)
- Alternative: Continue using MyTheme system and defer Bootstrap port until .NET 11 / MAUI 11 when libraries may be more mature
