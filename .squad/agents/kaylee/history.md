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

### 2026-03-13 — MSAL.NET Authentication for MAUI Clients (#45)

**Status:** Complete  
**Branch:** `feature/45-maui-msal`

Implemented MSAL.NET public client auth in `SentenceStudio.AppLib`:
- `IAuthService` interface + `MsalAuthService` (PKCE via system browser)
- `DevAuthService` no-op for local dev (config-driven via `Auth:UseEntraId`)
- `AuthenticatedHttpMessageHandler` wired into all HttpClient registrations
- MacCatalyst `Info.plist` updated with MSAL redirect URL scheme
- AppLib builds clean; full MacCatalyst build blocked by pre-existing UI error

