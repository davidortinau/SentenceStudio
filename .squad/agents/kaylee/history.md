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

## Core Context

**Overview:** Kaylee is SentenceStudio's Full-stack Developer, handling WebApp (Blazor Server), MAUI mobile/desktop clients, CI/CD pipelines, and deployment infrastructure. Active across Phases 1-5 of the Azure+Entra ID migration.

**Key Skills:** OIDC/MSAL authentication, Blazor Server auth patterns, MAUI native development, GitHub Actions CI/CD, WebApp → API token integration.

**Active Work:** Blazor Hybrid auth research (Decision #13); auth implementations complete (WebApp OIDC #44, MAUI MSAL #45); CI workflow complete #56; awaiting Captain decision on Phase 1 architecture refactor.

**Completed Milestones:**
- 2026-03-13: Azure issue triage (8 issues assigned)
- 2026-03-14: Fixed PR #70 (WebApp OIDC) and PR #71 (MAUI MSAL) review issues; completed CI workflow #56
- 2026-03-14: Phase 2 (Secrets & Security) completion with Wash (#39, #41)
- 2026-03-15: Mobile auth gate vulnerability fix (MainLayout async verification)
- 2026-03-15: Blazor Hybrid auth implementation research (Decision #13, 7-phase roadmap)

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

### Mobile Auth Gate Fix (2026-03-15)

**Status:** Complete

**Problem:** Mobile app (iOS/MacCatalyst) launched straight to Dashboard without requiring authentication. The auth gate in `MainLayout.razor` only checked a boolean preference (`app_is_authenticated`) — a stale flag from a previous session bypassed all token validation. Additionally, `Auth.razor` allowed "Create Local User" and "Select Local Profile" flows that set the preference without any server authentication.

**Root Cause:** `MainLayout.OnInitialized()` was synchronous and only checked `Preferences.Get("app_is_authenticated", false)`. It never consulted `IAuthService.IsSignedIn` or attempted a silent token refresh. After an app restart, the in-memory JWT cache in `IdentityAuthService` was empty, but the preference persisted — resulting in unauthenticated access to all pages.

**Fix:**
1. **MainLayout.razor** — Changed `OnInitialized` to `OnInitializedAsync`. Injected `IAuthService`. When the preference says authenticated, it now verifies `IAuthService.IsSignedIn`, attempts `SignInAsync()` (silent refresh from SecureStorage), and clears the stale preference if auth fails.
2. **Auth.razor** — `LoginAsAsync` made async: verifies server auth before granting access. If no valid session, shows a warning and stays on the auth page. `RegisterLocalAsync` now redirects to `/auth/register` (server registration) instead of setting the preference directly.

**WebApp compatibility:** `ServerAuthService.IsSignedIn` checks `HttpContext.User.Identity.IsAuthenticated` (cookie-based), so the WebApp auth flow is unaffected. `ServerAuthService.SignInAsync()` (no params) is a no-op that returns null — safe to call.

### 2026-03-15 — Blazor Hybrid Auth Implementation Research

**Status:** Complete  
**Output:** `docs/blazor-hybrid-auth-implementation.md` + decision in `.squad/decisions.md` (Decision #13)  

Researched official Blazor Hybrid auth patterns from Microsoft Learn docs. Analyzed why our current MainLayout.razor auth gate is broken and how to migrate to the framework's intended architecture.

**Key Findings:**

1. **Official Pattern:** Custom `AuthenticationStateProvider` + `AuthorizeRouteView` + `[Authorize]` attributes — not manual boolean preferences in MainLayout
2. **Why Our Approach Fails:**
   - NavigateTo() doesn't fire during OnInitializedAsync in Blazor Hybrid WebView
   - MainLayout persists across route changes (auth state can desync)
   - No framework-level auth awareness (cannot use AuthorizeView or [Authorize])
   - Boolean preference is not auth state (stale flags bypass server validation)
3. **Implementation Roadmap:** 7-phase migration from manual gates to framework auth (MauiAuthenticationStateProvider, AuthorizeRouteView, [Authorize] attributes, remove preferences)
4. **Risk Assessment:** Medium risk for MainLayout changes (shared across WebApp and mobile), low risk for Router and new provider

**Concrete Implementation Details:**
- `MauiAuthenticationStateProvider` wraps IdentityAuthService, exposes ClaimsPrincipal from JWT claims
- Routes.razor gets `<AuthorizeRouteView>` with `<NotAuthorized>` fragment rendering Auth page inline
- MainLayout.razor stripped to pure layout (no auth checking)
- Protected pages marked with `@attribute [Authorize]`
- WebApp adds `<CascadingAuthenticationState>` (minimal change, low risk)

**7-Phase Migration:**
| Phase | Component | Complexity | Risk |
|-------|-----------|-----------|------|
| 1 | MauiAuthenticationStateProvider | Medium | Low |
| 2 | Routes.razor (AuthorizeRouteView) | Trivial | Low |
| 3 | MainLayout.razor cleanup | Medium | Medium |
| 4 | Add [Authorize] attributes | Trivial | Low |
| 5 | Auth.razor refactor | Trivial | Medium |
| 6 | WebApp integration | Trivial | High |
| 7 | Remove boolean preferences | Trivial | Low |

**WebApp Integration Approach:** Option A (recommended) = add `<CascadingAuthenticationState>` to App.razor only (minimal, ASP.NET Core Identity middleware provides AuthenticationStateProvider automatically)

**Mitigation Strategy:**
- Feature flag: `Auth:UseFrameworkAuth=true/false`
- Keep boolean preferences as fallback during rollout
- E2E tests required before merge

**Decision Required:** Captain approval on implementation approach

**Learnings:**
- **AuthorizeRouteView is the official auth enforcement point** — not MainLayout.razor. The Router component, not the layout, decides whether to render a page or redirect to login.
- **NavigateTo() in OnInitializedAsync doesn't work in Blazor Hybrid** — the WebView routing stack isn't ready. Use AuthorizeRouteView's `<NotAuthorized>` fragment to render login inline without navigation.
- **AuthenticationStateProvider.NotifyAuthenticationStateChanged()** is the official way to trigger UI updates when auth state changes — not manual StateHasChanged() on layout components.
- **ClaimsPrincipal is auth state** — not boolean preferences. SecureStorage holds tokens for persistence, but the in-memory ClaimsPrincipal (created from JWT claims) is the runtime auth state.
- **WebApp cookie auth vs. mobile JWT auth** — both can use the same Blazor auth primitives (AuthorizeRouteView, AuthorizeView) via their respective AuthenticationStateProvider implementations.
- **Token lifecycle pattern:** App startup → GetAuthenticationStateAsync() → check SecureStorage → silent refresh if token exists → create ClaimsPrincipal from JWT → NotifyAuthenticationStateChanged() on login/logout.

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

### Mobile Auth Gate Fix (2026-03-15)

**Status:** Complete

**Problem:** Mobile app (iOS/MacCatalyst) launched straight to Dashboard without requiring authentication. The auth gate in `MainLayout.razor` only checked a boolean preference (`app_is_authenticated`) — a stale flag from a previous session bypassed all token validation. Additionally, `Auth.razor` allowed "Create Local User" and "Select Local Profile" flows that set the preference without any server authentication.

**Root Cause:** `MainLayout.OnInitialized()` was synchronous and only checked `Preferences.Get("app_is_authenticated", false)`. It never consulted `IAuthService.IsSignedIn` or attempted a silent token refresh. After an app restart, the in-memory JWT cache in `IdentityAuthService` was empty, but the preference persisted — resulting in unauthenticated access to all pages.

**Fix:**
1. **MainLayout.razor** — Changed `OnInitialized` to `OnInitializedAsync`. Injected `IAuthService`. When the preference says authenticated, it now verifies `IAuthService.IsSignedIn`, attempts `SignInAsync()` (silent refresh from SecureStorage), and clears the stale preference if auth fails.
2. **Auth.razor** — `LoginAsAsync` made async: verifies server auth before granting access. If no valid session, shows a warning and stays on the auth page. `RegisterLocalAsync` now redirects to `/auth/register` (server registration) instead of setting the preference directly.

**WebApp compatibility:** `ServerAuthService.IsSignedIn` checks `HttpContext.User.Identity.IsAuthenticated` (cookie-based), so the WebApp auth flow is unaffected. `ServerAuthService.SignInAsync()` (no params) is a no-op that returns null — safe to call.

## Learnings

- `MainLayout.razor` is the shared auth gate for BOTH WebApp and mobile — changes here affect all contexts
- `IdentityAuthService.IsSignedIn` only checks in-memory cache; after app restart it's always false until `SignInAsync()` restores from SecureStorage
- `ServerAuthService.SignInAsync()` (parameterless) returns null — it's a no-op because the WebApp uses cookie auth via ASP.NET Identity middleware
- `ServerAuthService.IsSignedIn` checks `HttpContext.User.Identity.IsAuthenticated` — this is the cookie check for WebApp
- Mobile auth preference (`app_is_authenticated`) must be validated against real token state on every app launch — never trust persisted preferences alone

### 2026-03-15 — Blazor Hybrid Auth Implementation Research

**Status:** Complete  
**Output:** `docs/blazor-hybrid-auth-implementation.md` + decision in `.squad/decisions.md` (Decision #13)  

Researched official Blazor Hybrid auth patterns from Microsoft Learn docs. Analyzed why our current MainLayout.razor auth gate is broken and how to migrate to the framework's intended architecture.

**Key Findings:**

1. **Official Pattern:** Custom `AuthenticationStateProvider` + `AuthorizeRouteView` + `[Authorize]` attributes — not manual boolean preferences in MainLayout
2. **Why Our Approach Fails:**
   - NavigateTo() doesn't fire during OnInitializedAsync in Blazor Hybrid WebView
   - MainLayout persists across route changes (auth state can desync)
   - No framework-level auth awareness (cannot use AuthorizeView or [Authorize])
   - Boolean preference is not auth state (stale flags bypass server validation)
3. **Implementation Roadmap:** 7-phase migration from manual gates to framework auth (MauiAuthenticationStateProvider, AuthorizeRouteView, [Authorize] attributes, remove preferences)
4. **Risk Assessment:** Medium risk for MainLayout changes (shared across WebApp and mobile), low risk for Router and new provider

**Concrete Implementation Details:**
- `MauiAuthenticationStateProvider` wraps IdentityAuthService, exposes ClaimsPrincipal from JWT claims
- Routes.razor gets `<AuthorizeRouteView>` with `<NotAuthorized>` fragment rendering Auth page inline
- MainLayout.razor stripped to pure layout (no auth checking)
- Protected pages marked with `@attribute [Authorize]`
- WebApp adds `<CascadingAuthenticationState>` (minimal change, low risk)

**7-Phase Migration:**
| Phase | Component | Complexity | Risk |
|-------|-----------|-----------|------|
| 1 | MauiAuthenticationStateProvider | Medium | Low |
| 2 | Routes.razor (AuthorizeRouteView) | Trivial | Low |
| 3 | MainLayout.razor cleanup | Medium | Medium |
| 4 | Add [Authorize] attributes | Trivial | Low |
| 5 | Auth.razor refactor | Trivial | Medium |
| 6 | WebApp integration | Trivial | High |
| 7 | Remove boolean preferences | Trivial | Low |

**WebApp Integration Approach:** Option A (recommended) = add `<CascadingAuthenticationState>` to App.razor only (minimal, ASP.NET Core Identity middleware provides AuthenticationStateProvider automatically)

**Mitigation Strategy:**
- Feature flag: `Auth:UseFrameworkAuth=true/false`
- Keep boolean preferences as fallback during rollout
- E2E tests required before merge

**Decision Required:** Captain approval on implementation approach

**Learnings:**
- **AuthorizeRouteView is the official auth enforcement point** — not MainLayout.razor. The Router component, not the layout, decides whether to render a page or redirect to login.
- **NavigateTo() in OnInitializedAsync doesn't work in Blazor Hybrid** — the WebView routing stack isn't ready. Use AuthorizeRouteView's `<NotAuthorized>` fragment to render login inline without navigation.
- **AuthenticationStateProvider.NotifyAuthenticationStateChanged()** is the official way to trigger UI updates when auth state changes — not manual StateHasChanged() on layout components.
- **ClaimsPrincipal is auth state** — not boolean preferences. SecureStorage holds tokens for persistence, but the in-memory ClaimsPrincipal (created from JWT claims) is the runtime auth state.
- **WebApp cookie auth vs. mobile JWT auth** — both can use the same Blazor auth primitives (AuthorizeRouteView, AuthorizeView) via their respective AuthenticationStateProvider implementations.
- **Token lifecycle pattern:** App startup → GetAuthenticationStateAsync() → check SecureStorage → silent refresh if token exists → create ClaimsPrincipal from JWT → NotifyAuthenticationStateChanged() on login/logout.

### Blazor Auth UI Layer Implementation (Phases 2-5)
**Date:** $(date +%Y-%m-%d)
**Decision:** #13 (Adopt official Blazor Hybrid auth pattern)

**Changes Made:**
1. **Routes.razor** — Replaced `<RouteView>` with `<AuthorizeRouteView>` wrapped in `<CascadingAuthenticationState>`. Added `<Authorizing>` spinner and `<NotAuthorized>` renders Auth page inline.
2. **MainLayout.razor** — Stripped all auth gate logic (~80 lines removed). Removed `@inject IAuthService`, `@inject ILogger`, and fields `isAuthGate`, `authCheckComplete`, `showAuthInline`. Kept sidebar toggle, theme JS interop, onboarding redirect, and scroll reset. Changed `OnInitializedAsync` to sync `OnInitialized` since no async auth calls needed.
3. **_Imports.razor** — Added `@using Microsoft.AspNetCore.Authorization` and `@using Microsoft.AspNetCore.Components.Authorization` for framework-wide access to [Authorize]/[AllowAnonymous] attributes and auth components.
4. **27 protected pages** — Added `@attribute [Authorize]` to all non-auth pages (Index, Skills, Profile, Settings, Onboarding, all activity pages, etc.).
5. **3 auth pages** — Added `@attribute [AllowAnonymous]` to Auth.razor, LoginPage.razor, RegisterPage.razor so they remain accessible without auth.
6. **WebApp AppRoutes.razor** — Updated to use `<AuthorizeRouteView>` with a sign-in prompt linking to /Account/Login for the server-rendered path.

**WebApp Compatibility:**
- WebApp App.razor already wraps content in `<CascadingAuthenticationState>` — no change needed there.
- ASP.NET Core Identity middleware provides server-side `AuthenticationStateProvider` automatically.
- AppRoutes.razor updated with AuthorizeRouteView to enforce [Authorize] attributes on web side.

**Kept for Phase 7:**
- `Preferences.Set("app_is_authenticated", true)` in LoginPage/RegisterPage/Auth — boolean preference legacy.
- `forceLoad: true` on NavigateTo after login — needed until MauiAuthenticationStateProvider implements NotifyAuthenticationStateChanged notification.

**Build Verification:** Both SentenceStudio.UI and SentenceStudio.WebApp compile with 0 errors.

**Learnings:**
- `AuthorizeRouteView` checks [Authorize] on route match and calls `GetAuthenticationStateAsync()` — but it caches the result via `CascadingAuthenticationState`. A `forceLoad: true` navigation forces full re-initialization which refreshes the cached auth state.
- For Blazor Hybrid (MAUI), the shared UI project needs `Microsoft.AspNetCore.Components.Authorization` NuGet package explicitly — it's not part of the base Razor class library SDK.
- MainLayout only renders for authorized users when using AuthorizeRouteView — the layout is part of the authorized render path, not the gating mechanism.
- Onboarding gate remains in MainLayout since it's orthogonal to auth (a user can be authenticated but not yet onboarded).

### Remove OpenAI API Key from Mobile UI (2025-07-15)

**What changed:** Removed the OpenAI API key input from both Onboarding.razor (step 4) and Profile.razor (API Configuration card). Onboarding steps renumbered from 7 (0-6) to 6 (0-5). All `openAiApiKey` variable usage, load/save logic, and env-var skip logic removed from both pages. The `UserProfile.OpenAI_APIKey` property was intentionally left untouched to avoid a database migration.

**Learnings:**
- AI calls now route through the web API backend; mobile clients no longer need or store an OpenAI API key.
- When removing an onboarding step, all step index references must be updated: case labels, navigation bounds (`Math.Min`, `currentStep < N`), and the step indicator dot loop (`i <= N`).

### 2026-03-15 — Auth Page UX Polish (Issues #92, #93, #94, #98)

**Status:** Complete

Fixed 4 auth page UX issues fer the Captain:

**Issue #94 — Password visibility toggle:**
- Added show/hide password eye icon to LoginPage.razor and RegisterPage.razor
- Used Bootstrap icons `bi-eye` and `bi-eye-slash` (no emojis, per team standard)
- Wrapped password inputs in `input-group` with toggle button
- LoginPage: one field (`showPassword` bool)
- RegisterPage: two fields (`showPassword` + `showConfirmPassword` bools)

**Issue #93 — Hide hamburger on auth pages:**
- Added `ShowHamburger` parameter to PageHeader.razor (default: true)
- Gated hamburger button render with `else if (ShowHamburger)` condition
- Applied `ShowHamburger="false"` to LoginPage, RegisterPage, and Auth.razor

**Issue #92 — Mobile sign-in layout matches web:**
- Added SentenceStudio branding above the card (h2, centered)
- Added "Remember me" checkbox below password field (stores in preferences when checked)
- Added "Forgot your password?" link below Sign In button (href="/auth/forgot-password")
- Changed "Register" link text to "Create one" to match web
- Changed "Back to user selection" to "Back to home" at bottom

**Issue #98 — Link to /auth from NotAuthorized:**
- Added "Back to user selection" link below Sign In / Create Account buttons in Routes.razor NotAuthorized block
- Existing Auth.razor local user list already checks `_profiles.Count > 0` — works fer both debug and existing users

**Technical Notes:**
- Bootstrap input-group pattern: input + button with outline-secondary styling
- Password toggle uses inline lambda `@onclick="() => showPassword = !showPassword"`
- Remember me stores email in preferences when checked (`remember_me`, `remember_email` keys)
- Forgot password page doesn't exist yet but link is there fer future implementation


### Auth Pages — Web Parity Alignment (Surgical Edits)
**Date:** $(date +%Y-%m-%d)
**Files:** LoginPage.razor, RegisterPage.razor, Routes.razor, Shared/RedirectToLogin.razor (new)

**What Changed:**
- Removed PageHeader from LoginPage and RegisterPage — auth pages are self-contained now
- Replaced `container` wrapper with `d-flex vh-100 align-items-center justify-content-center` + `w-100 max-width:440px` to match web's AuthLayout centering
- Added SentenceStudio branding block with bi-translate icon and text-primary-ss fs-3 fw-bold above the card on both pages
- Changed card from `card-ss p-4` to `card shadow-sm border-0` with inner `card-body p-4` (matching web)
- Centered headings with `card-title text-center mb-4`
- Stripped custom form classes: form-label is now plain, form-control drops form-control-ss, buttons use btn-primary w-100 instead of btn-ss-primary btn-lg
- Removed instruction paragraphs, "Back to home/user selection" links
- Added `<hr />` separator before footer links
- Simplified link classes to plain `<a>` tags without color overrides
- Routes.razor NotAuthorized now uses `<RedirectToLogin />` component instead of inline dead-end card
- Created `Shared/RedirectToLogin.razor` — simple NavigationManager redirect to /auth/login


### 2026-03-16 — Shared UI Audit: WebApp vs Shared UI Page Duplication

**Status:** Complete  
**Requested by:** Captain

Audited all pages in `src/SentenceStudio.WebApp/Components/Pages/` against `src/SentenceStudio.UI/Pages/` for consolidation opportunities.

**Findings:**
- All 6 WebApp Account pages (Login, Register, ForgotPassword, ResetPassword, AccessDenied, ConfirmEmail) must remain separate — they form a cohesive server-side ASP.NET Identity cookie auth workflow
- Login/Register use `<form method="post">` because Blazor Server can't set auth cookies over WebSocket
- ForgotPassword stays because the email link URL is generated relative to the WebApp's own host
- ResetPassword has no shared UI equivalent; email links point to `/Account/ResetPassword`
- AccessDenied/ConfirmEmail are ASP.NET auth infrastructure redirect targets
- Removed 3 Blazor template leftovers: Counter.razor, Weather.razor, Home.razor
- No non-auth page duplication found — all activity pages exist only in shared UI

**Gap identified:** No shared UI `ResetPasswordPage` exists — if a MAUI user receives a password reset email, the link points to `/Account/ResetPassword` which only exists in WebApp.

**Decision written to:** `.squad/decisions/inbox/kaylee-shared-ui-audit.md`

### Mobile UX Wave 1 Fixes (2026-03-16)
**Branch:** `squad/mobile-ux-fixes`  
**Status:** Complete — All 5 issues fixed, committed, and pushed  

Fixed 5 GitHub issues related to mobile user experience:

**Issue #99 — CSS `--bs-spacer` undefined variable:**
- Root cause: CSS used `calc(var(--bs-spacer) * N / 16)` but `--bs-spacer` was never defined
- The multipliers were also wrong (120, 160, 100 instead of 12, 16, 10)
- Fixed by replacing all calc expressions with concrete rem values:
  - Page header margin: `1rem`
  - Main content mobile padding: `0.75rem` (12px)
  - Page header breakout margins: `-0.75rem` and padding: `0.75rem`
  - Card padding on mobile: `0.5rem` (8px)
  - Reading page negative margins: `-0.75rem`
- These values align with existing CSS custom properties defined at lines 759-767 (--ss-layout-padding, --ss-card-padding)

**Issue #100 — Resources page filter row overflow:**
- Added `flex-wrap` to filter container
- Search input uses `w-100 w-md-auto flex-md-grow-1` to take full width on mobile, auto width on desktop
- Filter dropdowns now wrap gracefully on narrow screens

**Issue #102 — Vocabulary page toolbar density:**
- Stats badges now use `overflow-auto flex-nowrap` with `-webkit-overflow-scrolling: touch` for horizontal scrolling on mobile
- Added `flex-shrink-0` to all badges and buttons to prevent squishing
- Keeps all stats visible without overwhelming vertical space

**Issue #103 — Conversation input lacks safe-area-bottom:**
- Added `style="padding-bottom: calc(0.75rem + env(safe-area-inset-bottom, 0px)) !important;"` to the input card
- Matches the `.activity-input-bar` pattern used by other activity pages
- Ensures input isn't obscured by iOS home indicator

**Issue #110 — Dashboard excessive scroll with 12 activity cards:**
- Added `showAllActivities` boolean field (default: false)
- Activity cards beyond index 6 get `d-none d-md-block` class when collapsed
- Added mobile-only "See All / Show Less" toggle button with Bootstrap icons (bi-chevron-up/down)
- Desktop (md+) always shows all 12 cards unchanged
- Implemented `ToggleActivitiesExpanded()` method

**Build Verification:** 0 errors, 279 warnings (pre-existing)

**Learnings:**
- Bootstrap 5.3 CDN doesn't expose `--bs-spacer` as a CSS custom property — it's a Sass variable that compiles away
- When calc expressions look unreasonable (7.5rem mobile padding), the multipliers are probably wrong, not the variable
- Existing CSS custom properties (`--ss-layout-padding: 12px`) are the source of truth for mobile spacing
- Horizontal scroll for stats/badges is better UX than wrapping when space is tight — use `overflow-auto flex-nowrap` with `-webkit-overflow-scrolling: touch`
- Collapsing content on mobile requires both CSS (`d-none d-md-block`) and a toggle button — can't rely on CSS alone for user control

### 2026-03-19 — Mobile UX Fixes Wave 2 (Issues #104, #109, #114, #116, #119, #120)

**Issue #104 — Register page keyboard clipping:**
- Replaced `vh-100` with `min-height: 100dvh` on wrapper div
- Added `overflow-y: auto` to allow scrolling when iOS keyboard opens
- Uses `dvh` (dynamic viewport height) which accounts for mobile browser chrome

**Issue #109 — Cloze font too large for Korean:**
- Added mobile media query override in app.css: `.ss-display { font-size: 1.5rem; }` inside `@media (max-width: 767.98px)`
- Reduces from 42px (desktop) to 1.5rem (~24px) on mobile
- Prevents Korean sentences from wrapping excessively on small screens

**Issue #114 — Profile button groups break on mobile:**
- Replaced `btn-group flex-wrap` with `d-flex flex-column flex-md-row gap-2`
- Buttons now stack vertically on mobile, horizontal row on desktop
- Applied to Session Duration and Target CEFR Level sections

**Issue #116 — Settings exposes Database Migrations to end users:**
- Wrapped Database Migrations card in `#if DEBUG` preprocessor directive
- Card now only renders in Debug builds, hidden from Release/production
- No code-behind changes needed — Blazor Razor supports preprocessor directives

**Issue #119 — Vocabulary bulk edit toolbar overflows:**
- Changed toolbar from `d-flex gap-2` to `d-flex flex-wrap gap-2`
- Separated "Select All"/"Select None" buttons into `w-100 d-flex gap-2 d-md-inline-flex w-md-auto` wrapper
- Buttons drop to new line on mobile, inline on desktop

**Issue #120 — Writing input bar cramped:**
- Made Grade button icon-only on mobile: `<i class="bi bi-send d-md-none"></i>`
- Text label "Grade" uses `d-none d-md-inline` to hide on mobile, show on desktop
- Saves ~60px horizontal space on mobile screens

**Build Verification:** 0 errors, 279 warnings (pre-existing)

**Learnings:**
- `min-height: 100dvh` (dynamic viewport height) is better than `vh-100` for mobile forms — accounts for keyboard and browser chrome
- Blazor Razor files support `#if DEBUG` preprocessor directives — no need for code-behind boolean flags
- `d-flex flex-column flex-md-row gap-2` is the correct Bootstrap 5.3 pattern for responsive button groups — `btn-group flex-wrap` has poor mobile behavior
- Icon-only buttons on mobile with `d-md-none` / `d-none d-md-inline` pattern saves horizontal space without losing desktop clarity
- `flex-wrap` + strategic line-break wrappers (w-100 on mobile, w-md-auto on desktop) gives precise control over toolbar wrapping behavior
