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
- MauiAuthenticationStateProvider wraps IAuthService for Blazor's auth framework — registered Scoped, IAuthService stays Singleton
- Microsoft.AspNetCore.Components.Authorization NuGet needed in both AppLib and UI projects for AuthorizeRouteView
- Bumped Microsoft.Extensions.Configuration.Binder to 10.0.5 to satisfy transitive dependency from Components.Authorization

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

### 2026-03-16 — Issue #97 (API Error Investigation) and #95 (Password Reset URL Logging)

**Status:** COMPLETED

**Issue #97 - API Error Investigation:**
- Investigated Aspire dashboard API errors as reported by Captain
- Checked Aspire structured logs, console logs, and distributed traces for the API resource
- Found NO errors — API is running healthy with successful requests (OpenAI chat completions, auth flows)
- Logs show normal operation: token refresh cycles, email confirmations, database queries executing successfully
- Recent traces show OpenAI API calls returning 200 OK (1.5-2s response times)
- CORS and auth middleware properly configured
- Conclusion: API is operating as expected; no issues found

**Issue #95 - Password Reset URL Logging:**
- Added development-only logging for password reset URLs in both API and WebApp
- Modified `AuthEndpoints.ForgotPassword` (API) and `AccountEndpoints.ForgotPassword` (WebApp)
- Injected `IWebHostEnvironment` and `ILogger<PasswordResetLogger>` into password reset handlers
- Created nested `PasswordResetLogger` class in both static endpoint classes to provide logger category (workaround for static class limitation)
- Added `env.IsDevelopment()` guard before logging to ensure URLs never leak in production
- Reset URLs now logged at `LogInformation` level with clear "Copy and paste this URL" message
- Logs appear in both console and Aspire structured logs for easy dev access
- ConsoleEmailSender already logs email content; this adds explicit reset URL extraction for faster dev workflow

**Technical Notes:**
- Cannot use static classes as generic type parameters for `ILogger<T>`
- Workaround: Created nested private class `PasswordResetLogger` for logger category
- Development check: `env.IsDevelopment()` ensures production safety
- Format: `--- PASSWORD RESET LINK ---\nFor: {Email}\nReset URL: {ResetUrl}\n--- Copy and paste this URL into your browser ---`
- WebApp Login/Register pages use plain HTML `<form method="post">` (NOT Blazor interactive) -- JS-based interactivity required for things like password toggle
- AuthLayout is minimal (logo + @Body) -- no nav links
- AppRoutes.razor NotAuthorized uses RedirectToLogin component with `forceLoad: true` to redirect unauthenticated users to /Account/Login
- WebApp's ServerAuthService.SignInAsync NEVER checks IsEmailConfirmedAsync — web login always bypasses email confirmation
- API's AuthEndpoints.Login now auto-confirms email in development mode to match WebApp behavior; production still requires email confirmation
- IdentityAuthService (MAUI client) logs response body on login failure for better debugging


### 2026-03-16 — Vocabulary Hierarchy Schema Design

**Status:** PROPOSED  
**Decision Doc:** `.squad/decisions/inbox/wash-vocabulary-hierarchy-proposal.md`

**Problem:** Current flat vocabulary model causes duplication when AI extracts related terms (root word → phrase → idiom). Users must prove mastery redundantly for linguistically connected vocabulary.

**Analysis Completed:**
- Examined current schema: `VocabularyWord` (flat pairs), `VocabularyProgress` (per-word mastery), `ResourceVocabularyMapping` (junction), `VocabularyLearningContext` (practice attempts)
- Evaluated two architectural options:
  - **Option A (Recommended):** Self-referential FK — add `ParentVocabularyWordId` + `RelationType` enum to `VocabularyWord`
  - **Option B:** Separate `VocabularyRelationship` junction table for many-to-many relationships

**Key Technical Decisions:**
1. **Keep separate VocabularyProgress per word** — hierarchy is metadata, NOT for aggregating mastery scores. Phrases are distinct learning targets.
2. **Option A simpler for CoreSync** — single table sync, no new junction table, NULLable FK preserves existing data
3. **Migration risk: LOW** — additive changes only, no data loss, no destructive schema alterations
4. **AI extraction contract change needed** — must return parent-child relationships, not flat list (River's domain)

**VocabularyWordRelationType Enum Proposed:**
- `Inflection` — verb conjugations, noun declensions
- `Phrase` — word + particle/modifier
- `Idiom` — fixed multi-word expressions
- `Compound` — merged words
- `Synonym` / `Antonym` — semantic relationships

**CoreSync Implications:**
- Option A: Single table, existing FK handling works, validate against circular refs
- Option B: New sync table, relationship conflicts independent of word conflicts, cascade deletes

**Migration Phases:**
1. Schema extension (EF Core migration — 2 new columns)
2. Data backfill (optional — AI analysis of existing vocab)
3. CoreSync validation (conflict resolution, cascading deletes)
4. API + UI updates (hierarchy extraction, related words display)

**Team Coordination Required:**
- **Captain:** UX decision — show hierarchy to users immediately or backend-only?
- **River:** AI prompt engineering for parent-child extraction
- **Kaylee:** UI design for hierarchy display (tree view, chips, expandable sections)
- **Jayne:** E2E test scenarios for cross-device sync validation
- **Zoe:** Architecture alignment with multi-tenancy plans

**Recommendation:** Adopt Option A (self-referential) for Phase 1. Revisit Option B if multi-parent expressions become common.


---

## VOCABULARY HIERARCHY TEAM ANALYSIS — SCHEMA FINALIZED (2026-03-17)

**Session:** Vocabulary Hierarchy Analysis & Team Consensus  
**Role:** Backend Developer  
**Status:** PROPOSED — Awaiting Captain Approval

**Schema Decision: Option A (Self-Referential FK) — FINAL**

### Recommended Implementation

**Add to VocabularyWord entity:**
```csharp
// Linguistic hierarchy
public string? ParentVocabularyWordId { get; set; }
public VocabularyWordRelationType? RelationType { get; set; }

// Navigation properties
[JsonIgnore]
public VocabularyWord? ParentWord { get; set; }

[JsonIgnore]
public List<VocabularyWord> ChildWords { get; set; } = new();

public enum VocabularyWordRelationType
{
    Inflection,  // 주문 → 주문하다
    Phrase,      // 대학교 → 대학교 때
    Idiom,       // 주문하다 → 피자를 주문하는 게 어때요
    Compound,    // Two words merged
    Synonym,     // Alternative
    Antonym      // Opposite
}
```

**EF Core Migration (next step):**
```sql
ALTER TABLE VocabularyWord ADD COLUMN ParentVocabularyWordId TEXT NULL;
ALTER TABLE VocabularyWord ADD COLUMN RelationType INTEGER NULL;
CREATE INDEX IX_VocabularyWord_Parent ON VocabularyWord(ParentVocabularyWordId);
```

### Why Option A?
- **CoreSync compatibility:** Single table sync, no junction table, NULLable FK preserves existing data
- **Query simplicity:** Direct FK traversal, fast parent/child queries
- **Migration safety:** Additive changes only, zero data loss
- **Future-proof:** Can evolve to Option B (junction table) if multi-parent needed

### Team Validation
- ✅ Zoe (Architecture): Aligns with design pillars, conservative MVP approach
- ✅ River (AI): Prompt design ready to populate relatedTerms array
- ✅ SLA Expert: Independent mastery tracking preserves SRS spacing effect
- ✅ Learning Design: Supports hierarchy visualization without cognitive overload

### Migration Risk: LOW
- No destructive changes
- Existing vocabulary unaffected (NULLable columns)
- FK reference to same table (standard pattern)
- Cascade deletes optional (recommend ON DELETE SET NULL for safety)

### Immediate Next Steps
1. Captain approval
2. Generate EF Core migration file
3. Write integration tests (FK integrity, cascade rules)
4. CoreSync validation (bi-directional sync with new FK)
5. Repository methods: `GetChildWordsAsync()`, `GetRootWordAsync()`, `GetWordHierarchyAsync()`

### Future Expansion (Phase 2)
- If multi-parent relationships become common (rare), migrate to Option B (junction table)
- Option A→B migration path documented
- Low risk to defer this decision
