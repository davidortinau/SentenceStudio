# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- Project uses MauiReactor for native pages: `VStart()` not `Top()`, `VEnd()` not `Bottom()`, `HStart()`/`HEnd()` not `Start()`/`End()`
- NEVER use emojis in UI — use Bootstrap icons (bi-*) or text. Non-negotiable.
- The user goes by "Captain" and prefers pirate talk
- All entities synced via CoreSync use string GUID PKs
- Database migrations MUST use `dotnet ef`, never raw SQL ALTER TABLE
- NEVER delete user data or database files
- Build with TFM: `dotnet build -f net10.0-maccatalyst`
- E2E testing is mandatory for every feature/fix
- Activities follow pattern: `activity-page-wrapper` → `PageHeader` → `activity-content` → `activity-input-bar`

## Work Sessions

### 2026-03-13 — GitHub Issues Created for Azure + Entra ID Plan

**Status:** Complete  
**Issues Created:** 27 issues (#39–#65)  
**Dependencies:** All cross-referenced with dependency links  

**Cross-Team Impact:**
- **Kaylee:** 8 issues assigned (#44–45, #56–59, #60)
- **Captain:** 1 issue assigned (#42)
- Issues propagated to respective agent history files

See `.squad/decisions.md` for full decision record.

### 2025-07-22 — Created GitHub Issues for Azure Deployment + Entra ID Plan

**Status:** Complete  
**Issues Created:** 27 issues (#39-#65)  
**Decision:** Reframed issue #39 (2.1) from "security emergency" to "best practices" — no secrets were committed to git history.

**Issue Mapping to Plan:**

- **Phase 1 (Auth):** #42 (Entra registrations) → #43 (JWT API) → #44 (WebApp OIDC) → #45 (MAUI MSAL) → #46 (CoreSync) → #47 (Integration tests)
- **Phase 2 (Secrets):** #39 (user-secrets) → #40 (config all projects) → #41 (HTTPS/headers) → #54 (Key Vault integration)
- **Phase 3 (Infrastructure):** #48 (azure.yaml) → #49 (PostgreSQL) → #50 (Redis) → #51 (Blob) → #52 (Container Apps) → #53 (Key Vault) → #55 (CoreSync DB migration)
- **Phase 4 (Pipeline):** #56 (CI) → #57 (Deploy) → #58 (Staging) → #59 (Migrations)
- **Phase 5 (Hardening):** #60 (Monitoring) → #61 (Rate limit) → #62 (CORS) → #63 (Health) → #64 (Scaling) → #65 (Audit logging)

**Team Assignments:**
- Zoe (Lead): 14 issues (auth foundational work, infra decisions, hardening architecture)
- Kaylee (Full-stack): 8 issues (WebApp OIDC, MAUI MSAL, CI/deploy workflows, monitoring)
- Captain (David): 1 issue (#42 - requires Azure portal/Entra ID access)

**Dependencies Validated:** All 27 issues cross-referenced with dependency links. Phase order preserved for execution.

**Key Learnings:**
- No security emergency: appsettings.json with secrets already in .gitignore
- User-secrets workflow as team best practice (Phase 2.1)
- Phase 1 testable entirely on localhost with Entra ID redirecting to `http://localhost`
- CoreSync SQLite→PostgreSQL migration is critical path item (Phase 3.7, XL size)
- Aspire-native provisioning via `azd` avoids manual Bicep maintenance