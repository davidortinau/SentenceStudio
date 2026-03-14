# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

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
