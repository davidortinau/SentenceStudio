# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- EF Core migrations: `dotnet ef migrations add <Name> --project src/SentenceStudio.Shared --startup-project src/SentenceStudio.Shared`
- NEVER delete database files or user data — fix migrations, not data
- SQLite table names are SINGULAR in ApplicationDbContext.OnModelCreating
- All CoreSync-synced entities use string GUID PKs with ValueGeneratedNever()
- Non-synced entities keep int auto-increment PKs but have string-typed FKs
- DI registration in `SentenceStudioAppBuilder.cs` (AppLib) and `Program.cs` (WebApp)
- Aspire env var config: `builder.Configuration["AI:OpenAI:ApiKey"]` not `["AI__OpenAI__ApiKey"]`
- Server DB at: `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db`
- UserProfileId columns for multi-user data isolation — all repos filter by active_profile_id

- Microsoft.Identity.Web v3.8.2 added to API for Entra ID JWT Bearer auth
- Conditional auth pattern: `Auth:UseEntraId` config flag switches between Entra ID and DevAuthHandler
- TenantContextMiddleware maps both Entra ID claims (tid, oid, name) and DevAuthHandler claims (tenant_id, NameIdentifier, Name) — Entra ID claims take precedence
- appsettings.json is gitignored; use appsettings.Development.json for tracked config and AppHost env vars for runtime
- Scope policies: `RequireScope("user.read")` etc. via Microsoft.Identity.Web authorization helpers
- AzureAd public IDs (TenantId, ClientId, Audience) are NOT secrets — safe to commit
- CoreSync HTTP client uses named HttpClient `"HttpClientToServer"` — auth handler chains via `.AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()`
- CoreSync server (`SentenceStudio.Web`) uses `UseCoreSyncHttpServer()` middleware — auth middleware must run BEFORE it
- DevAuthHandler is duplicated in both API and Web projects — future refactor to shared project
- Web server auth follows same `Auth:UseEntraId` pattern as API server

## Work Sessions

### 2026-03-13 — Cross-Agent Update: Azure Deployment Issues

**Status:** In Progress  
**GitHub Issues:** #39-#65 created by Zoe (Lead)  
**Wash's Role:** Deployment orchestration support  

### 2026-03-15 — Cross-Agent Update: Mobile Auth Guard Bypass Fix (Kaylee)

**Status:** COMPLETED  
**Related Decision:** Mobile Auth Guard — Validate Tokens, Not Preferences  
**Impact on Wash:** No API changes required for this fix

**Summary:** Kaylee fixed critical mobile auth bypass in MainLayout.razor and Auth.razor. The auth gate now validates real token state (`IAuthService.IsSignedIn`) instead of checking a boolean preference flag. This enforces server authentication before any content access.

**What This Means for API Work:**
- Your JWT Bearer implementation (#43) is now critical — mobile clients will call API to validate tokens
- DevAuthHandler fallback keeps dev flow working
- No scope policy changes needed; endpoints using `RequireAuthorization()` work as-is
- Consider testing API token refresh flow with mobile clients (Jayne's E2E plan)

**Learnings Added:**
- Mobile apps cannot rely on persistent local flags for auth state — must validate against server on every session restart
- Preference flags are convenience hints, not security mechanisms
- SecureStorage persistence for MSAL tokens is essential for smooth UX (app restart with valid tokens should work seamlessly)

**Phase Execution Order:** Phase 2 (Secrets) → Phase 1 (Auth, localhost-testable) → Phase 3 (Infra) → Phase 4 (Pipeline) → Phase 5 (Hardening)

**Wash Coordination Points:**
- Phase 4 (Pipeline) — CI/deploy workflows — coordinate with Kaylee's automation
- Phase 3.5 (Container Apps) — deployment target provisioning
- Critical Path: CoreSync SQLite→PostgreSQL migration (#55, XL) — coordinate safe data migration in production

**Key Dependencies:** Zoe coordinates Phase 1-3 decisions; Kaylee implements CI/deploy automation; Captain provides Azure portal access.

### 2026-03-14 — API JWT Bearer Authentication (#43)

**Status:** Complete  
**Branch:** `feature/43-api-jwt-bearer`  
**PR:** #68  

Implemented JWT Bearer token authentication for the API:
- NuGet: Microsoft.Identity.Web (JWT validation + token acquisition)
- Conditional auth via `Auth:UseEntraId` flag (false = DevAuthHandler, true = Entra ID OIDC)
- JwtBearerScheme with token validation, issuer, and audience checks
- AuthorizeAttribute guards on API endpoints (/api/* routes)
- Integrates with Entra ID tenant and app registrations (#42)
- DevAuthHandler for local development (zero friction)
- Ready for WebApp + MAUI clients to call API with Bearer tokens

**Unblocks:** Kaylee's WebApp OIDC (#44), MAUI MSAL (#45), remaining auth work

### 2026-03-14 — CoreSync Auth: Bearer Token on Sync Client (#46)

**Status:** Complete  
**Branch:** `feature/46-coresync-auth`  
**Depends on:** #43 (API JWT), #45 (MAUI MSAL)

**What was done:**
- Merged #43 and #45 into branch as dependencies
- Added JWT Bearer auth to `SentenceStudio.Web` (CoreSync sync server)
- Created `DevAuthHandler` for dev mode (mirrors API pattern)
- `UseAuthentication()` + `UseAuthorization()` before `UseCoreSyncHttpServer()`
- Client side already handled by #45's `AuthenticatedHttpMessageHandler` on `"HttpClientToServer"`
- Graceful fallback: no token → request proceeds without auth header; server doesn't reject

**Key Insight:** CoreSync uses ASP.NET middleware (`UseCoreSyncHttpServer()`), not minimal API endpoints, so `RequireAuthorization()` can't be applied directly. Auth middleware populates identity; future enforcement needs a gating middleware or policy.

### 2026-03-14 — Phase 2 (Secrets) Completion

**Status:** COMPLETED  
**Issues:** #39 (user-secrets setup), #41 (security headers)

**Wash Completed #39:**
- Initialized user-secrets for Api, WebApp
- Created secrets.template.json with full inventory
- Updated README with three secrets management paths
- Documented AppHost → service flow via Aspire Parameters and env var normalization

**Kaylee Completed #41:**
- Added SecurityHeadersExtensions to shared lib (linked to all web projects)
- Implemented HSTS, CORS, AllowedHosts across API/WebApp/Marketing
- Environment-aware HTTPS redirect

**Phase 2 Closed:** Ready to begin Phase 1 (Entra ID) now that Captain has provisioned app registrations.
