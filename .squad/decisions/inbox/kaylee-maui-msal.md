# Decision: MSAL.NET Authentication for MAUI Clients

**Date:** 2026-03-13  
**Author:** Kaylee (Full-stack Dev)  
**Issue:** #45  
**Branch:** `feature/45-maui-msal`  
**Status:** IMPLEMENTED

## Summary

Added MSAL.NET public client authentication to the MAUI native clients via `IAuthService` in `SentenceStudio.AppLib`.

## Key Decisions

1. **IAuthService abstraction** — All auth goes through `IAuthService` so the rest of the app never touches MSAL directly.
2. **MsalAuthService** — Uses `PublicClientApplicationBuilder` with the Native client registration (`68d5abeb-...`), PKCE via system browser, silent-first with interactive fallback.
3. **DevAuthService** — No-op implementation for local dev (`Auth:UseEntraId` = false). Reports `IsSignedIn = true` and returns null tokens so UI isn't blocked and the server's DevAuthHandler handles unauthenticated requests.
4. **AuthenticatedHttpMessageHandler** — DelegatingHandler wired into all `HttpClient` registrations (API clients + CoreSync). Attaches Bearer token when available, gracefully proceeds without it.
5. **Config-driven toggle** — `Auth:UseEntraId` (bool) in `IConfiguration` selects MSAL vs DevAuth, matching the same pattern used in Api and WebApp projects.
6. **MacCatalyst URL scheme** — `msal68d5abeb-9ca7-46cc-9572-42e33f15a0ba` registered in `Info.plist` for redirect URI callback.

## Files Changed

| File | Change |
|------|--------|
| `src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj` | Added `Microsoft.Identity.Client` NuGet |
| `src/SentenceStudio.AppLib/Services/IAuthService.cs` | New interface |
| `src/SentenceStudio.AppLib/Services/MsalAuthService.cs` | MSAL implementation |
| `src/SentenceStudio.AppLib/Services/DevAuthService.cs` | No-op dev implementation |
| `src/SentenceStudio.AppLib/Services/AuthenticatedHttpMessageHandler.cs` | Bearer token handler |
| `src/SentenceStudio.AppLib/ServiceCollectionExtentions.cs` | `AddAuthServices()` + handler wiring |
| `src/SentenceStudio.AppLib/Setup/SentenceStudioAppBuilder.cs` | Calls `AddAuthServices()` |
| `src/SentenceStudio.MacCatalyst/Platforms/MacCatalyst/Info.plist` | MSAL URL scheme |

## What's NOT Included (deliberate)

- No sign-in UI yet (that's a separate issue)
- No SecureStorage token cache (in-memory only for now)
- No Android manifest changes (MacCatalyst is primary dev target)
- No `appsettings.json` changes — `Auth:UseEntraId` defaults to `false` when absent

## Risks

- Token cache is in-memory only — users re-authenticate on every app restart. SecureStorage integration is a follow-up.
- Pre-existing build error in `SentenceStudio.UI` (unrelated `DuplicateGroup` reference) blocks full MacCatalyst build. AppLib compiles clean.
