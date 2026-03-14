# Decision: JWT Bearer Authentication for API (#43)

**Date:** 2026-03-13
**Author:** Wash (Backend Dev)
**Status:** IMPLEMENTED
**Branch:** `feature/43-api-jwt-bearer`

## Context

The API used only `DevAuthHandler` — a hardcoded auth handler that injects fake claims for local development. Issue #43 requires adding real JWT Bearer authentication via Microsoft Entra ID while keeping DevAuthHandler for local dev.

## Decision

### Conditional Authentication via Config Flag

- `Auth:UseEntraId` (bool, default `false`) controls which auth scheme is active.
- When `true`: Microsoft.Identity.Web validates JWT Bearer tokens against Entra ID.
- When `false`: DevAuthHandler provides fake dev claims (existing behavior).

### Scope-Based Authorization Policies

Four policies defined matching the Entra ID app registration scopes:
- `RequireUserRead` → `user.read`
- `RequireUserWrite` → `user.write`
- `RequireAiAccess` → `ai.access`
- `RequireSyncReadWrite` → `sync.readwrite`

Endpoints currently use `.RequireAuthorization()` (any authenticated user). Scope-based policies are available for endpoints to opt into as needed.

### TenantContextMiddleware Dual Claim Mapping

The middleware now checks both Entra ID claims (`tid`, `oid`, `name`, `preferred_username`) and DevAuthHandler claims (`tenant_id`, `NameIdentifier`, `Name`, `Email`). Entra ID claims take precedence.

### AzureAd Configuration

Public IDs (tenant, client, audience) are in `appsettings.Development.json` (tracked in git). The `appsettings.json` file is gitignored. AppHost also passes these as environment variables.

## Files Changed

| File | Change |
|------|--------|
| `SentenceStudio.Api.csproj` | Added `Microsoft.Identity.Web` v3.8.2 |
| `Program.cs` | Conditional auth registration, scope policies |
| `TenantContextMiddleware.cs` | Dual claim mapping (Entra ID + Dev) |
| `appsettings.Development.json` | AzureAd section with public IDs |
| `appsettings.json` | AzureAd section (local only, gitignored) |
| `AppHost.cs` | AzureAd env vars passed to API service |

## Consequences

- **No breaking change** — defaults to DevAuthHandler (`UseEntraId: false`)
- **Ready for production** — flip `Auth:UseEntraId` to `true` and tokens are validated
- **Single-tenant** — `AzureADMyOrg` via Microsoft.Identity.Web defaults
- **Next steps:** Apply scope policies to specific endpoints (#44+), add MAUI client MSAL (#45)
