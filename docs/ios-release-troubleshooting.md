# iOS Release Build Troubleshooting Guide

**Date:** December 2024  
**Last Updated:** December 2024

## Overview

This document captures critical issues discovered during iOS Release build and production configuration. These issues primarily affect release builds (AOT compilation), fresh app installations, and authentication workflows. This guide is a reference for future developers to prevent regressions.

---

## Issue 1: Fresh Install Crash — PatchMissingColumnsAsync

### Symptom
App starts but no data loads. Logs show repeated errors: `no such table: LearningResource` and similar table-not-found errors.

### Root Cause
The database initialization sequence is wrong. `PatchMissingColumnsAsync()` runs **before** `MigrateAsync()`. On fresh installs (empty database), it attempts to ALTER TABLE on tables that don't exist yet. The exception is caught silently (around line 203 in SyncService.cs), so `MigrateAsync()` never executes, and the database schema is never created.

### Fix
Added a SQLite `sqlite_master` table existence check before any ALTER TABLE statement. If a table doesn't exist, skip the patch operation — `MigrateAsync()` will create it with all required columns.

**File:** `src/SentenceStudio.Shared/Services/SyncService.cs` → `PatchMissingColumnsAsync()`

```csharp
// Check if table exists in sqlite_master before attempting ALTER TABLE
using var cmd = connection.CreateCommand();
cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=?";
cmd.Parameters.AddWithValue("@table", tableName);
var exists = cmd.ExecuteScalar() != null;

if (!exists) continue; // Skip patch if table doesn't exist yet
```

---

## Issue 2: EF Core 10 Model Building on iOS Release (AOT)

### Symptom
Build succeeds but database queries fail in Release builds with error: `Model building is not supported when publishing with NativeAOT`. All data operations fail.

### Root Cause
iOS Release builds use Mono Full AOT (Ahead-of-Time compilation). In this mode, `RuntimeFeature.IsDynamicCodeSupported` is `false`. EF Core 10 refuses to dynamically build its data model at runtime. The model is **lazily built on first query**, not during `MigrateAsync()`, so Release builds fail when queries execute.

### Fix (Temporary)
Added `<UseInterpreter>true</UseInterpreter>` to the Release PropertyGroup in the iOS .csproj file. This enables the Mono interpreter alongside AOT, allowing dynamic code execution for EF Core model building.

**File:** `src/SentenceStudio.iOS/SentenceStudio.iOS.csproj`

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <UseInterpreter>true</UseInterpreter>
</PropertyGroup>
```

### Fix (Proper - Recommended)
Generate a compiled data model using:
```bash
dotnet ef dbcontext optimize --project src/SentenceStudio.Shared
```
Then configure EF Core to use the compiled model. This eliminates runtime model building entirely. See [Microsoft.EntityFrameworkCore.Tasks](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#using-compiled-models) for details.

---

## Issue 3: Logout Black Screen

### Symptom
Tapping Logout button navigates to a black screen or does nothing. App remains in authenticated state.

### Root Cause
`Logout()` method in NavMenu was synchronous but required async operations:
- Never called `MauiAuthenticationStateProvider.LogOutAsync()` to clear auth state
- Navigated to `/auth` (profile picker) instead of `/auth/login` (actual login page)
- No async handling prevented proper state cleanup

### Fix
1. Made `Logout()` async
2. Injected `AuthenticationStateProvider`
3. Called `MauiAuthenticationStateProvider.LogOutAsync()` to clear auth state
4. Changed navigation target from `/auth` to `/auth/login`

**File:** `src/SentenceStudio.UI/Layout/NavMenu.razor`

```csharp
private async Task Logout()
{
    if (AuthStateProvider is MauiAuthenticationStateProvider provider)
    {
        await provider.LogOutAsync();
    }
    Navigation.NavigateTo("/auth/login", forceLoad: true);
}
```

---

## Issue 4: No Data After Login

### Symptom
Login succeeds and user is authenticated, but dashboard shows "No vocabulary data yet" instead of synced content.

### Root Cause
CoreSync initial sync fires at app startup (before login) and receives HTTP 401 Unauthorized. After successful login, no re-sync is triggered, so local data remains empty.

### Fix
Added post-login sync trigger in `Index.razor`. When the user is authenticated but local data is empty, manually call `SyncService.TriggerSyncAsync()` to pull data immediately after login.

**File:** `src/SentenceStudio.UI/Pages/Index.razor`

```csharp
protected override async Task OnInitializedAsync()
{
    if (IsAuthenticated && !HasLocalData)
    {
        await SyncService.TriggerSyncAsync();
    }
}
```

---

## Issue 5: Production Config Not Loading

### Symptom
Release builds connect to localhost instead of Azure. Production endpoints are ignored.

### Root Cause
`ConfigurationExtensions.cs` only loaded the base `appsettings.json` file. Environment-specific configuration files (`appsettings.Production.json`) were never loaded as embedded resources.

### Fix
Added environment detection and conditional loading:
- Debug builds load `appsettings.Development.json`
- Release builds load `appsettings.Production.json`

Both are added as embedded resources in the project file and overlaid on the base configuration.

**Files:**
- `src/SentenceStudio.AppLib/Setup/ConfigurationExtensions.cs` — Updated to detect environment and load appropriate config
- `src/SentenceStudio.AppLib/appsettings.Production.json` — Contains Azure endpoints and production settings

```csharp
var environmentSpecificFile = 
#if DEBUG
    "appsettings.Development.json";
#else
    "appsettings.Production.json";
#endif

config.AddJsonFile($"SentenceStudio.AppLib.{environmentSpecificFile}", 
    optional: false, reloadOnChange: false);
```

---

## Issue 6: EnableILStrip Error on iOS Release

### Symptom
Build fails with PE file or ILStrip error when building iOS Release on .NET 10.

### Root Cause
iOS Release builds attempt IL stripping by default in .NET 10. Some libraries or configurations conflict with this process.

### Fix
Disable IL stripping by adding `-p:EnableILStrip=false` to the build command.

**Build Command:**
```bash
dotnet build -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -p:EnableILStrip=false
```

---

## Issue 7: Install on Physical Device

### Symptom
Deploying to physical iOS devices via `dotnet build -t:Run` is slow over WiFi.

### Solution
Use Xcode's device control CLI for faster WiFi deployment:

```bash
xcrun devicectl device install app --device {UDID} path/to/SentenceStudio.iOS.app/
```

Retrieve device UDID:
```bash
xcrun devicectl list devices
```

This method is significantly faster than `dotnet build -t:Run` for iterative device testing.

---

## Prevention Checklist

- [ ] **Database Init:** Verify `PatchMissingColumnsAsync()` checks `sqlite_master` before ALTER TABLE
- [ ] **AOT Compatibility:** For EF Core, either use compiled models or enable `UseInterpreter` in Release builds
- [ ] **Authentication:** After logout, always navigate to login page with `forceLoad: true`
- [ ] **Post-Login Sync:** Trigger CoreSync after successful login if local data is empty
- [ ] **Environment Config:** Ensure `appsettings.{Environment}.json` is loaded as embedded resource
- [ ] **Build Flags:** Include `-p:EnableILStrip=false` in iOS Release builds
- [ ] **Device Deployment:** Use `xcrun devicectl` for faster physical device iteration

---

## References

- [EF Core Compiled Models](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#using-compiled-models)
- [.NET MAUI iOS Deployment](https://learn.microsoft.com/en-us/dotnet/maui/ios/deployment/)
- [Xcrun Device Control CLI](https://developer.apple.com/documentation/devicectl)
