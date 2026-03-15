# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- `SentenceStudio.AppLib` has `ImplicitUsings>disable` — must add explicit `using` for System.Net.Http, etc.
- `Auth:UseEntraId` config flag pattern works across Api, WebApp, and now MAUI clients
- MSAL.NET `WithBroker(BrokerOptions)` overload removed in v4.x — just omit it when not using a broker
- `AuthenticatedHttpMessageHandler` is wired into all HttpClient registrations (API + CoreSync) via `AddHttpMessageHandler<T>()`
- Pre-existing build error: `DuplicateGroup` missing in `SentenceStudio.UI/Pages/Vocabulary.razor` — blocks MacCatalyst full build
- `Auth:UseEntraId` config flag controls auth mode in both API and WebApp — false = DevAuthHandler, true = Entra ID OIDC
- Microsoft.Identity.Web OIDC uses `AddMicrosoftIdentityWebApp()` + `EnableTokenAcquisitionToCallDownstreamApi()` chain
- Redis-backed distributed token cache via `Aspire.StackExchange.Redis.DistributedCaching` (match AppHost Aspire version for preview packages)
- `ConfigureHttpClientDefaults` adds DelegatingHandler to ALL HttpClient instances from the factory
- Microsoft.Identity.Web.UI requires `AddControllersWithViews()` + `MapControllers()` for sign-in/sign-out endpoints
- `appsettings.json` is gitignored — config changes there are local-only, use `appsettings.Development.json` for tracked dev config
- Client secrets go in user-secrets, never in tracked config files

- Blazor pages in `src/SentenceStudio.UI/Pages/` — follow `activity-page-wrapper` layout pattern
- MauiReactor conventions: `VStart()` not `Top()`, `VEnd()` not `Bottom()`, `HStart()`/`HEnd()` not `Start()`/`End()`
- NEVER use emojis in UI — use Bootstrap icons (bi-*) or text. Non-negotiable.
- Use `@bind:event="oninput"` for real-time Blazor input binding (default `onchange` fires on blur)
- Activity pages: PageHeader, activity-content area, footer with activity-input-bar
- Word Association activity at `/word-association` — latest activity, has Grade-first UX flow
- Dashboard activities listed in `src/SentenceStudio.UI/Pages/Index.razor`

## Work Sessions

### 2026-03-13 — Cross-Agent Update: Azure Deployment Issues

**Status:** In Progress  
**GitHub Issues:** #39-#65 created by Zoe (Lead)  
**Kaylee's Assignments:** 8 issues  

**Issues Assigned to Kaylee:**
- #44 WebApp OIDC Integration (Phase 1, size:L)
- #45 MAUI MSAL Implementation (Phase 1, size:XL)
- #56 CI Workflow Setup (Phase 4, size:M)
- #57 Deploy Workflow (Phase 4, size:L)
- #58 Staging Environment (Phase 4, size:M)
- #60 Azure Monitor/Application Insights (Phase 5, size:M)
- #62 CORS Configuration (Phase 5, size:S)
- #64 Auto-Scaling Rules (Phase 5, size:M)

**Phase Execution Order:** Phase 2 (Secrets) → Phase 1 (Auth, localhost-testable) → Phase 3 (Infra) → Phase 4 (Pipeline) → Phase 5 (Hardening)

**Critical Path:** CoreSync SQLite→PostgreSQL migration (#55, XL).

### 2026-03-14 — Fix Copilot Review Issues on PR #70 and PR #71

**Status:** Complete

**PR #70 (feature/44-webapp-oidc):**
- Pinned Microsoft.Identity.Web packages to 4.5.0 (was floating `*`)
- Removed unused `OpenIdConnect` using from Program.cs
- Replaced hardcoded GUID fallback scope with startup config validation
- Handler now passes `cancellationToken` and propagates exceptions

**PR #71 (feature/45-maui-msal):**
- MsalAuthService reads TenantId, ClientId, RedirectUri, Scopes from IConfiguration
- Fixed IsSignedIn: `_cachedAccount` now updated on every successful token acquisition
- AuthenticatedHttpMessageHandler attempts token unconditionally (no IsSignedIn gate)
- Handler scopes also read from config instead of hardcoded GUIDs

### 2026-03-13 — MSAL.NET Authentication for MAUI Clients (#45)

**Status:** Complete  
**Branch:** `feature/45-maui-msal`

Implemented MSAL.NET public client auth in `SentenceStudio.AppLib`:
- `IAuthService` interface + `MsalAuthService` (PKCE via system browser)
- `DevAuthService` no-op for local dev (config-driven via `Auth:UseEntraId`)
- `AuthenticatedHttpMessageHandler` wired into all HttpClient registrations
- MacCatalyst `Info.plist` updated with MSAL redirect URL scheme
- AppLib builds clean; full MacCatalyst build blocked by pre-existing UI error

### 2026-03-14 — WebApp OIDC Authentication (#44)

**Status:** Complete  
**Branch:** `feature/44-webapp-oidc`  
**PR:** #70  

Added OIDC authentication to the Blazor WebApp:
- NuGet: Microsoft.Identity.Web, .UI, .DownstreamApi, Aspire Redis distributed cache
- Conditional auth via `Auth:UseEntraId` flag (false = DevAuthHandler, true = Entra ID)
- `AuthenticatedApiDelegatingHandler` attaches Bearer tokens to all outgoing API calls
- Redis-backed distributed token cache (Aspire integration, matches AppHost Aspire version)
- `LoginDisplay.razor` with Bootstrap icons (bi-person, bi-box-arrow-right)
- `CascadingAuthenticationState` in App.razor
- Build verified: zero new errors (pre-existing DuplicateGroup issue in SentenceStudio.UI is unrelated)

### 2026-03-14 — MAUI MSAL Authentication (#45)

**Status:** Complete  
**Branch:** `feature/45-maui-msal`  
**PR:** #71  

Implemented MSAL.NET authentication for MAUI native clients:
- NuGet: Microsoft.Identity.Client, MSAL for public client (MAUI cannot securely store secrets)
- PublicClientApplication configured with Entra ID authority
- Interactive login via WebAuthenticator (system browser flow)
- Token persistence using MAUI SecureStorage (platform-native: Keychain/CredentialManager)
- AuthService encapsulates MSAL logic and token caching
- Sign-in/sign-out UI with Bootstrap icons (bi-person, bi-box-arrow-right)
- Bearer token injection to API HttpClient calls
- Completes full auth suite: API + WebApp + MAUI clients

### 2026-03-14 — CI Workflow Setup (#56)

**Status:** Complete (flags noted)  
**Branch:** `feature/56-ci-workflow`  
**PR:** #69  

Set up GitHub Actions CI workflow for automated testing and builds:
- Multi-platform build matrix (.NET 10 on macOS runner)
- NuGet restore, dotnet build, test execution
- Build artifact publishing for deployment readiness
- Job dependencies for sequential execution

**Flagged Issue:** Workflow references `IntegrationTests` project — verify project exists or adjust workflow before merge.

### 2026-03-13 — CI Workflow (#56)

**Status:** Complete  
**Branch:** `feature/56-ci-workflow`

Created `.github/workflows/ci.yml` with:
- Build matrix: Api, WebApp, AppLib (with MAUI workload)
- Test job: UnitTests + IntegrationTests with xUnit TRX reporting
- NuGet caching via `actions/cache`
- DevAuthHandler via `Auth__UseEntraId=false`
- Local NuGet source stripped for CI (dev-machine-only path)
- `dorny/test-reporter` for inline PR test results
- .NET SDK version: 10.0.x (explicit in workflow; global.json is gitignored)

**Discovered:** IntegrationTests references a non-existent `SentenceStudio.csproj` — will fail in CI. Needs follow-up fix.

### 2026-03-14 — Phase 2 (Secrets & Security) Completion

**Status:** COMPLETED  
**Issues:** #39 (user-secrets setup), #41 (security headers)

**Kaylee Completed #41 — Security Headers & HTTPS:**
- Added shared `SecurityHeadersExtensions` in `src/Shared/SecurityHeadersExtensions.cs` (linked to web projects via `<Compile Include>`)
- Security headers: X-Content-Type-Options: nosniff, X-Frame-Options: DENY, Referrer-Policy, Permissions-Policy (camera/mic/geo)
- HTTPS redirect environment-aware (skipped in dev, Aspire proxy terminates TLS)
- API explicit HSTS: 365-day max-age, includeSubDomains, preload
- CORS: AllowWebApp policy (config-driven) + AllowDevClients (dev-only localhost)
- AllowedHosts restrictions in Production appsettings

**Key Decision:** Linked source file instead of WebServiceDefaults to avoid ambiguous call errors with MAUI defaults.

**Wash Completed #39 (user-secrets setup):**
- Kaylee coordination: CORS setup confirmed not required for MAUI clients (use service discovery)
- Phase 2 ready for Phase 1 (Entra ID auth) — Captain has provisioned 3 app registrations
