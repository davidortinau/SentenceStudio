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

## Work Sessions

### 2026-03-13 — Cross-Agent Update: Azure Deployment Issues

**Status:** In Progress  
**GitHub Issues:** #39-#65 created by Zoe (Lead)  
**Wash's Role:** Deployment orchestration support  

**Phase Execution Order:** Phase 2 (Secrets) → Phase 1 (Auth, localhost-testable) → Phase 3 (Infra) → Phase 4 (Pipeline) → Phase 5 (Hardening)

**Wash Coordination Points:**
- Phase 4 (Pipeline) — CI/deploy workflows — coordinate with Kaylee's automation
- Phase 3.5 (Container Apps) — deployment target provisioning
- Critical Path: CoreSync SQLite→PostgreSQL migration (#55, XL) — coordinate safe data migration in production

**Key Dependencies:** Zoe coordinates Phase 1-3 decisions; Kaylee implements CI/deploy automation; Captain provides Azure portal access.

