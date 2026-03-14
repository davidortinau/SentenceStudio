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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
