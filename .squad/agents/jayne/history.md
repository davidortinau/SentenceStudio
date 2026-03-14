# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- E2E testing skill at `.claude/skills/e2e-testing/SKILL.md` — follow it religiously
- Aspire stack: `cd src/SentenceStudio.AppHost && aspire run`
- Webapp at `https://localhost:7071/`
- Playwright for browser testing: snapshots, clicks, form fills, verification
- Server DB: `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db`
- Three verification levels: UI state, database records, Aspire logs
- "It compiles" is NOT sufficient — must verify in running app
- Must call `CacheService.InvalidateVocabSummary()` after recording attempts or dashboard is stale
- Playwright must use `pressSequentially` not `fill()` for Blazor server-side binding
- Test users: David (Korean, f452438c-...), Jose (Spanish, 8d5f7b4a-...), Gunther (German, c3bb57f7-...)

## Work Sessions

### 2026-03-13 — Cross-Agent Update: Azure Deployment Issues

**Status:** In Progress  
**GitHub Issues:** #39-#65 created by Zoe (Lead)  
**Jayne's Assignment:** N/A (testing/QA support for phase execution)  

**Phase Execution Order:** Phase 2 (Secrets) → Phase 1 (Auth, localhost-testable) → Phase 3 (Infra) → Phase 4 (Pipeline) → Phase 5 (Hardening)

**E2E Testing Support:** Jayne to verify each phase's integration tests per e2e-testing skill (mandatory for every feature/fix).

**Critical Path:** CoreSync SQLite→PostgreSQL migration (#55, XL) — requires comprehensive data migration testing.

