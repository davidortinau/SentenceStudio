# Squad Decisions

## Active Decisions

### 1. GitHub Issues Created for Azure + Entra ID Plan (2026-03-13)

**Status:** DOCUMENTED  
**Date:** 2026-03-13  
**Author:** Zoe (Lead)  

27 GitHub issues decompose the Azure deployment + Entra ID authentication plan into actionable work items across 5 phases. All issues linked with dependency references and assigned to team members.

**Key Decisions:**
- **Reframed Issue #39:** User-secrets workflow as team best practice (not security emergency) — no secrets accidentally committed; `appsettings.json` already in `.gitignore`
- **Execution Order:** Phase 2 → Phase 1 → Phase 3 → Phase 4 → Phase 5 (security-first approach)
- **Phase 1 Testable Locally:** Auth flow fully validates on `localhost` without Azure deployment
- **Team Assignments:** Zoe (14 issues, architecture/infra), Kaylee (8 issues, UI/deploy), Captain (1 issue, Azure portal)
- **Critical Path:** CoreSync SQLite→PostgreSQL migration (Phase 3.7, XL complexity)

**Issue Mapping:**
| Phase | Count | Issues |
|-------|-------|--------|
| Phase 1 (Auth) | 7 | #42-47 |
| Phase 2 (Secrets) | 4 | #39-41, #54 |
| Phase 3 (Infrastructure) | 8 | #48-53, #55 |
| Phase 4 (Pipeline) | 4 | #56-59 |
| Phase 5 (Hardening) | 6 | #60-65 |

**Learnings:**
- Aspire-native provisioning (`azd`) generates Bicep — no manual templates needed
- Localhost testing of auth eliminates blocker for early validation
- DevAuthHandler alongside Entra ID maintains developer velocity
- User-secrets as team best practice enables secure local dev

**Next Steps:**
1. Captain: Register Entra ID app registrations (#42)
2. Zoe: Begin user-secrets setup (#39-40)
3. Kaylee: Begin CI workflow (#56)
4. All phases proceed in parallel where dependencies allow

---

### 2. Architecture Plan: Azure Deployment with Entra ID Authentication (2026-03-13)

**Status:** REFERENCE  
**Date:** 2026-03-13  
**Author:** Zoe (Lead)  

Comprehensive architecture plan for transitioning SentenceStudio from local-dev-only to production-ready Azure deployment with real authentication. Covers 5 phases from secret management through hardening, with technical decisions, risk register, and cost estimates.

**Key Technical Decisions:**
1. **Aspire-Native Provisioning over Raw Bicep** — AppHost defines resources; `azd` generates Bicep
2. **Keep DevAuthHandler Alongside Entra ID** — Developer velocity: use DevAuthHandler for local dev, Entra ID for production
3. **PostgreSQL over Azure SQL** — Aligns with AppHost declaration and CoreSync support
4. **Single-Tenant First** — Start with single Entra ID tenant; multi-tenant support added later
5. **Token Caching:** SecureStorage (MAUI) and Redis (WebApp)

**3 App Registrations Required:**
- SentenceStudio API (Web API — resource server)
- SentenceStudio WebApp (Web app, confidential — Blazor Server)
- SentenceStudio Native (Mobile/Desktop, public — MAUI clients)

**Scopes Exposed:**
- `api://sentencestudio/user.read` — user profile, vocabulary
- `api://sentencestudio/user.write` — modify user data, submit answers
- `api://sentencestudio/ai.access` — AI chat, speech synthesis, image analysis
- `api://sentencestudio/sync.readwrite` — CoreSync bi-directional sync

**Estimated Monthly Cost (Production):** ~$107-252

### 3. WebApp OIDC Authentication (#44) (2026-03-14)

**Status:** IMPLEMENTED  
**Date:** 2026-03-14  
**Author:** Kaylee (Full-Stack Dev)  
**Branch:** `feature/44-webapp-oidc`  
**PR:** #70  

Added Microsoft.Identity.Web OIDC authentication to the Blazor WebApp with the same conditional `Auth:UseEntraId` pattern used in the API (Wash's #43 work).

**Key Decisions:**
- **Conditional auth pattern:** DevAuthHandler for local dev (zero friction), Entra ID for production
- **Redis token cache:** WebApp is server-rendered, needs shared distributed token cache; Redis already in AppHost
- **DelegatingHandler architecture:** `AuthenticatedApiDelegatingHandler` via `ConfigureHttpClientDefaults` ensures all API calls get Bearer tokens automatically
- **Bootstrap icons only:** bi-person, bi-box-arrow-right per team standards

**NuGet Packages Added:**
- `Microsoft.Identity.Web` — OIDC/OpenID Connect integration
- `Microsoft.Identity.Web.UI` — Sign-in/sign-out controller endpoints
- `Microsoft.Identity.Web.DownstreamApi` — Token acquisition for downstream API
- `Aspire.StackExchange.Redis.DistributedCaching` (v13.3.0-preview.1.26156.1) — Redis-backed cache

**New Files:**
- `Auth/AuthenticatedApiDelegatingHandler.cs` — DelegatingHandler using ITokenAcquisition
- `Components/Layout/LoginDisplay.razor` — Sign-in/sign-out UI

**Production Configuration Required:**
1. Set `Auth:UseEntraId` to `true`
2. Store client secret: `dotnet user-secrets set "AzureAd:ClientSecret" "<value>"`
3. Redis running (Aspire AppHost already configured)

**Dependencies:** #42 (Entra ID registrations), #43 (API JWT Bearer), Redis in AppHost

---

### 4. MAUI MSAL Authentication (#45) (2026-03-14)

**Status:** IMPLEMENTED  
**Date:** 2026-03-14  
**Author:** Kaylee (Full-Stack Dev)  
**Branch:** `feature/45-maui-msal`  
**PR:** #71  

Implemented MSAL.NET authentication for MAUI native clients (mobile + desktop) with secure token persistence.

**Key Decisions:**
- **PublicClientApplication:** MSAL.NET for public clients (MAUI — desktop/mobile cannot securely store secrets)
- **WebAuthenticator:** Interactive login flow through system browser
- **SecureStorage:** Token persistence using MAUI's platform-native secure storage
- **Bootstrap icons:** bi-person, bi-box-arrow-right per team standards
- **Bearer token injection:** Automatic application to API HttpClient calls

**AuthService Pattern:**
- Encapsulates MSAL logic, token caching, and refresh flow
- Dependency-injected for easy testing and platform-specific registration

**Dependencies:** #42 (Entra ID app registration with MSAL configuration), #43 (API JWT Bearer), Kaylee #44 (UX consistency)

---

### 5. CI Workflow Setup (#56) (2026-03-14)

**Status:** IMPLEMENTED (flags noted)  
**Date:** 2026-03-14  
**Author:** Kaylee (Full-Stack Dev)  
**Branch:** `feature/56-ci-workflow`  
**PR:** #69  

Set up GitHub Actions CI workflow for automated testing and multi-platform builds.

**Key Decisions:**
- **Multi-platform matrix:** .NET 10 on macOS runner (minimum for MAUI builds)
- **Build artifact publishing:** Deployment readiness
- **Job dependencies:** Sequential execution for build → test → artifact stages

**Flagged Issue:**
- Workflow references `IntegrationTests` project — verify project exists or adjust workflow before merge

---
### 3. User-Secrets Workflow for Local Development (2026-03-14)

**Status:** IMPLEMENTED  
**Date:** 2026-03-14  
**Author:** Wash (Backend Dev)  
**Issue:** #39  

Established .NET user-secrets pattern for secure local development across all server-side projects.

**Key Decisions:**
- AppHost uses Aspire Parameters (`builder.AddParameter("openaikey", secret: true)`) resolving from AppHost user-secrets under `Parameters:openaikey`
- Parameters passed to child projects via `.WithEnvironment("AI__OpenAI__ApiKey", openaikey)`
- Aspire normalizes `__` to `:` in configuration across services
- Three paths documented in README:
  - **Option A:** Aspire (recommended) — set secrets in AppHost, flow to all services
  - **Option B:** Standalone projects — per-project `dotnet user-secrets`
  - **Option C:** MAUI mobile/desktop — gitignored `appsettings.json` in AppLib

**Projects with UserSecretsId:**
| Project | UserSecretsId |
|---------|---------------|
| AppHost | d8521a4e-969b-4696-9990-45dea324bda8 |
| Api | 9ae3953f-a490-41b3-a2b8-a8e2555b4615 |
| WebApp | 33f95f89-d495-4311-b6cb-53a47b5c34e6 |
| Workers | dotnet-SentenceStudio.Workers-8ded0183-d135-40b2-b2d4-b49b096922b8 |

**Secrets Inventory:**
| Secret | AppHost Parameter | Api Key | WebApp Key |
|--------|-------------------|---------|------------|
| OpenAI | Parameters:openaikey | AI:OpenAI:ApiKey | Settings:OpenAIKey |
| ElevenLabs | Parameters:elevenlabskey | ElevenLabsKey | Settings:ElevenLabsKey |
| Syncfusion | Parameters:syncfusionkey | N/A | N/A |

**No Data Impact:** No database changes, no secret migrations, AppHost user-secrets remain intact.

---

### 4. Security Headers and HTTPS Enforcement (2026-03-14)

**Status:** IMPLEMENTED  
**Date:** 2026-03-14  
**Author:** Kaylee (Full-stack Dev)  
**Issue:** #41  

Added security hardening across API, WebApp, and Marketing services.

**Security Headers (all services):**
- Shared extension `UseSecurityHeaders()` in `src/Shared/SecurityHeadersExtensions.cs`
- Linked via `<Compile Include>` to prevent ambiguous call errors with MAUI defaults
- Headers: X-Content-Type-Options: nosniff, X-Frame-Options: DENY, Referrer-Policy: strict-origin-when-cross-origin, Permissions-Policy: camera=(), microphone=(), geolocation=()

**HTTPS and HSTS:**
- HTTPS redirect environment-aware: skipped in Development (Aspire terminates TLS at proxy)
- API explicit HSTS: 365-day max-age, includeSubDomains, preload
- WebApp and Marketing HSTS unchanged (already configured in non-dev block)

**CORS (API only):**
- `AllowWebApp` policy: restricts to `Cors:AllowedOrigins` config
- `AllowDevClients` policy: dev-only, localhost with credentials
- Production origins in `appsettings.Production.json`
- MAUI clients unaffected (service discovery, not browser CORS)

**AllowedHosts:** Production `appsettings.json` files restrict to specific domains, not wildcard.

**Deferred:** Production CORS fine-tuning (#62), CSP header (Blazor inline scripts), production auth (still DevAuthHandler).

### 6. JWT Bearer Authentication for API (#43) (2026-03-13)

**Status:** IMPLEMENTED  
**Date:** 2026-03-13  
**Author:** Wash (Backend Dev)  
**Branch:** `feature/43-api-jwt-bearer`  

Added real JWT Bearer authentication via Microsoft Entra ID while keeping DevAuthHandler for local dev.

**Key Decisions:**
- **Conditional authentication:** `Auth:UseEntraId` config flag (default `false`) controls DevAuthHandler vs. real auth
- **Scope-based policies:** Four policies defined matching Entra ID app registration scopes (user.read, user.write, ai.access, sync.readwrite)
- **TenantContextMiddleware dual mapping:** Checks both Entra ID claims (`tid`, `oid`, `name`, `preferred_username`) and DevAuthHandler claims
- **Public configuration:** Tenant, client, audience IDs in `appsettings.Development.json` (tracked git); secrets in gitignored `appsettings.json`

**Files Changed:**
- `SentenceStudio.Api.csproj` — Microsoft.Identity.Web v3.8.2
- `Program.cs` — Conditional registration + scope policies
- `TenantContextMiddleware.cs` — Dual claim mapping
- `appsettings.Development.json` — AzureAd config

**Consequences:**
- No breaking change (defaults to DevAuthHandler)
- Ready for production (`Auth:UseEntraId = true`)
- Single-tenant (Microsoft.Identity.Web `AzureADMyOrg`)

---

### 7. Auth Integration Test Infrastructure (#47) (2026-03-13)

**Status:** IMPLEMENTED  
**Date:** 2026-03-13  
**Author:** Jayne (Tester)  
**Branch:** `feature/47-auth-tests`  

Created `tests/SentenceStudio.Api.Tests/` — integration test infrastructure for API authentication flows.

**Key Decisions:**
- **TestJwtGenerator:** Generates HMAC-SHA256 signed tokens with Entra ID claims (tid, oid, scp, name, email); supports expired and custom tokens
- **JwtBearerApiFactory:** WebApplicationFactory simulating `Auth:UseEntraId=true` with JWT Bearer validation
- **DevAuthApiFactory:** WebApplicationFactory simulating `Auth:UseEntraId=false` with DevAuthHandler
- **CI-compatible:** No Azure/Entra ID credentials required

**Test Coverage:** 11 tests, all passing
- 7 JWT Bearer mode tests (auth, expiry, tenant context)
- 4 DevAuthHandler mode tests

**Impact:**
- Validates conditional auth when `feature/43-api-jwt-bearer` lands
- Reusable infrastructure for future API tests
- Fast execution (~0.8s)

---

### 8. CoreSync Auth — Bearer Token on Sync Client (#46) (2026-03-14)

**Status:** IMPLEMENTED  
**Date:** 2026-03-14  
**Author:** Wash (Backend Dev)  
**Branch:** `feature/46-coresync-auth`  

Added Bearer token authentication to CoreSync HTTP sync channel (MAUI clients ↔ Web server).

**Key Decisions:**
- **Client:** `AuthenticatedHttpMessageHandler` already wired to `"HttpClientToServer"` named HttpClient (Kaylee's MSAL work #45)
- **Server:** `Auth:UseEntraId` config flag (same pattern as API); Entra ID validates tokens, DevAuthHandler creates synthetic identity
- **Graceful fallback:** Client omits auth header if no token available; server accepts unauthenticated requests (no `RequireAuthorization()` yet)
- **Offline-friendly:** Keeps sync working in offline/dev scenarios

**AzureAd Configuration:** Shared with API (TenantId, ClientId, Audience)

**Dependencies:** Merges `feature/43-api-jwt-bearer` + `feature/45-maui-msal`

---

### 9. CI Workflow Setup (#56) (2026-03-14)

**Status:** IMPLEMENTED  
**Date:** 2026-03-14  
**Author:** Kaylee (Full-stack Dev)  
**Branch:** `feature/56-ci-workflow`  

GitHub Actions CI workflow for automated testing and multi-platform builds.

**Pipeline:**
| Project | Notes |
|---------|-------|
| Api | ASP.NET Core Web API |
| WebApp | Blazor web app |
| AppLib | MAUI shared library (installs MAUI workload) |
| UnitTests | xUnit, net10.0 |
| IntegrationTests | xUnit, net10.0 |

**Key Decisions:**
- **.NET SDK from global.json** — `actions/setup-dotnet` reads version
- **NuGet caching** — keyed by csproj/NuGet.config hashes
- **Local NuGet source removal** — `sed` strips dev-machine-only source before restore
- **DevAuthHandler in CI** — `Auth__UseEntraId=false` env var
- **MAUI workload conditional** — Only installed for AppLib entry
- **fail-fast: false** — All matrix entries run for maximum signal
- **Test reporting** — TRX artifacts + `dorny/test-reporter` for PR inline results

---

### 10. Mobile Auth Guard — Validate Tokens, Not Preferences (#2026-03-15)

**Status:** IMPLEMENTED  
**Date:** 2026-03-15  
**Author:** Kaylee (Full-stack Dev)  

Fixed critical mobile authentication bypass vulnerability where auth gate only checked a boolean preference flag, not actual token state.

**Key Decisions:**
- **MainLayout.razor:** Single auth enforcement point. On initialization: verify `IAuthService.IsSignedIn`, attempt silent refresh, redirect to `/auth` if unsigned
- **Auth.razor:** Profile selection and "Create Local User" both enforce server authentication via `LoginAsAsync` before preference is set
- **No local-only bypass:** Users must authenticate with API server before accessing content

**Rationale:**
- Boolean preferences are convenience hints, not security mechanisms
- Mobile JWT tokens expire; app restarts lose in-memory cache
- WebApp cookie auth unaffected (server-rendered)
- Aligns with "DevAuthHandler for dev, real auth for production" strategy

**Files Changed:**
- `src/SentenceStudio.UI/Layout/MainLayout.razor` — Async auth verification
- `src/SentenceStudio.UI/Pages/Auth.razor` — Server auth gating

**Impact on Other Agents:**
- **Wash (API):** No API changes needed
- **Zoe (Arch):** Consistent with Phase 1 architecture
- **Jayne (QA):** Auth gate tests required

---

### 11. CRUD Feedback Standard (2026-03-14)

**Status:** PROPOSED  
**Date:** 2026-03-14  
**Author:** Zoe (Lead)  

Uniform CRUD feedback pattern across all pages (Resources, Skills, Vocabulary, Profile, Settings).

**Standard Pattern:**
| Operation | Feedback | Method |
|-----------|----------|--------|
| Success | Toast (3000ms auto-dismiss) | `Toast.ShowSuccess()` |
| Error | Toast (5000ms auto-dismiss) | `Toast.ShowError()` with details |
| Warning | Toast (4000ms auto-dismiss) | `Toast.ShowWarning()` |
| Destructive (Delete) | Bootstrap modal BEFORE + Toast AFTER | Modal → ConfirmDelete → Toast |
| Info/Status | Toast (3000ms auto-dismiss) | `Toast.ShowInfo()` |

**Issues to Fix:**
1. Replace JS `confirm()` dialogs with Bootstrap modals (5 pages)
2. Fix Profile.razor inconsistencies (load/save/delete feedback)
3. Remove jsModule interop dependency

**Rationale:**
- Consistency — users learn one pattern
- Accessibility — Bootstrap modals keyboard-accessible, screen-reader friendly
- Clarity — color/icon-coded (success=green, error=red, warning=yellow, info=blue)
- Non-intrusive — toasts auto-dismiss
- Safety — destructive ops require explicit confirmation
- Velocity — clear patterns reduce decision fatigue

**Approval Required:** Captain

**Next Steps:**
1. Kaylee: Implement Bootstrap delete modals in 5 pages
2. Kaylee: Fix Profile.razor feedback
3. Zoe: Code review for pattern adherence

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
