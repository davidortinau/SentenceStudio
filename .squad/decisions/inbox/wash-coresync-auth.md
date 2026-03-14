# Decision: CoreSync Auth — Bearer Token on Sync Client (#46)

**Date:** 2026-03-14  
**Author:** Wash (Backend Dev)  
**Status:** IMPLEMENTED  
**Branch:** `feature/46-coresync-auth`

## Context

CoreSync HTTP sync between MAUI clients and the Web server was unauthenticated. With JWT Bearer auth on the API (#43) and MSAL auth on MAUI clients (#45), the sync channel needed the same protection.

## Decision

### Client Side (AppLib)
- **Already wired by Kaylee (#45):** `AuthenticatedHttpMessageHandler` is attached to the `"HttpClientToServer"` named HttpClient that CoreSync uses via `.AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()`.
- The handler gracefully handles no-token scenarios: if `IAuthService.IsSignedIn` is false or token acquisition fails, the request proceeds without a Bearer header.

### Server Side (SentenceStudio.Web)
- Added `Microsoft.Identity.Web` package to the Web project.
- Added `Auth:UseEntraId` configuration flag (same pattern as API project).
- **Entra ID mode:** Bearer tokens validated via `AddMicrosoftIdentityWebApi()`, `RequireSyncReadWrite` policy available for `sync.readwrite` scope.
- **Dev mode:** `DevAuthHandler` creates a synthetic `dev-user` identity for all requests (mirrors API behavior).
- `UseAuthentication()` + `UseAuthorization()` run before `UseCoreSyncHttpServer()` so user identity is populated for downstream handlers.

### Offline / No-Token Fallback
- Client: `AuthenticatedHttpMessageHandler` catches all token errors and proceeds without auth header.
- Server: Authentication middleware validates tokens when present but does not reject unauthenticated requests (no `RequireAuthorization()` on CoreSync middleware endpoints). This keeps sync working in offline/dev scenarios.

## AzureAd Configuration
Shared with the API project:
- **TenantId:** `49c0cd14-bc68-4c6d-b87b-9d65a56fa6df`
- **ClientId:** `8c051bcf-bd3a-4051-9cd3-0556ba5df2d8`
- **Audience:** `api://8c051bcf-bd3a-4051-9cd3-0556ba5df2d8`

## Files Changed
- `src/SentenceStudio.Web/Auth/DevAuthHandler.cs` — New (dev auth for sync server)
- `src/SentenceStudio.Web/Program.cs` — Auth services + middleware
- `src/SentenceStudio.Web/SentenceStudio.Web.csproj` — Microsoft.Identity.Web
- `src/SentenceStudio.Web/appsettings.Development.json` — Auth + AzureAd config

## Dependencies
- Merges `feature/43-api-jwt-bearer` (API JWT) and `feature/45-maui-msal` (MAUI MSAL + handler)

## Future Work
- Add `RequireAuthorization()` enforcement on sync endpoints when ready to enforce auth in production
- Refactor `DevAuthHandler` to a shared project (currently duplicated in API and Web)
