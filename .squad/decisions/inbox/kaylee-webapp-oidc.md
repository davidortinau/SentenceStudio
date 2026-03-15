# Decision: WebApp OIDC Authentication (#44)

**Date:** 2026-03-14
**Author:** Kaylee (Full-stack Dev)
**Status:** IMPLEMENTED
**Branch:** `feature/44-webapp-oidc`

## Summary

Added Microsoft.Identity.Web OIDC authentication to the Blazor WebApp with the same conditional `Auth:UseEntraId` pattern used in the API (Wash's #43 work).

## What Changed

### NuGet Packages Added
- `Microsoft.Identity.Web` — OIDC/OpenID Connect integration
- `Microsoft.Identity.Web.UI` — Sign-in/sign-out controller endpoints
- `Microsoft.Identity.Web.DownstreamApi` — Token acquisition for API calls
- `Aspire.StackExchange.Redis.DistributedCaching` (v13.3.0-preview.1.26156.1) — Redis-backed distributed token cache

### Auth Flow (Conditional)

When `Auth:UseEntraId` is **true**:
- `AddMicrosoftIdentityWebApp()` configures OIDC with Entra ID
- `EnableTokenAcquisitionToCallDownstreamApi()` acquires tokens for API
- `AddDistributedTokenCaches()` + `AddRedisDistributedCache("cache")` for Redis-backed token cache
- `AuthenticatedApiDelegatingHandler` attaches Bearer tokens to all outgoing HttpClient calls
- `AddMicrosoftIdentityUI()` provides `/MicrosoftIdentity/Account/SignIn` and `SignOut` endpoints

When `Auth:UseEntraId` is **false** (default):
- Existing `DevAuthHandler` provides auto-authenticated dev user (no config needed)

### New Files
- `Auth/AuthenticatedApiDelegatingHandler.cs` — DelegatingHandler using ITokenAcquisition
- `Components/Layout/LoginDisplay.razor` — Sign-in/sign-out UI with Bootstrap icons (bi-person, bi-box-arrow-right)

### Modified Files
- `Program.cs` — Conditional auth registration, HttpClient handler wiring, MapControllers
- `SentenceStudio.WebApp.csproj` — NuGet packages
- `Components/App.razor` — `<CascadingAuthenticationState>` wrapper
- `Components/Layout/MainLayout.razor` — LoginDisplay in top-row
- `Components/_Imports.razor` — `Microsoft.AspNetCore.Components.Authorization` using
- `appsettings.json` (gitignored) — AzureAd, Auth, DownstreamApi sections

### Configuration Required for Production
1. Set `Auth:UseEntraId` to `true`
2. Store client secret in user-secrets: `dotnet user-secrets set "AzureAd:ClientSecret" "<value>"`
3. Redis must be running (Aspire AppHost already configures this)

## Design Rationale

- **Conditional pattern:** Matches API's DevAuthHandler approach — zero friction for local dev
- **Redis token cache:** WebApp is server-rendered, needs shared token cache; Redis already in AppHost
- **DelegatingHandler on all HttpClients:** `ConfigureHttpClientDefaults` ensures all API clients get auth tokens automatically
- **No emojis:** Bootstrap icons only per team standards

## Dependencies
- Requires Entra ID app registration (#42) for production use
- Works alongside API auth (#43) — same tenant/scopes
- Redis resource in AppHost (already configured)
