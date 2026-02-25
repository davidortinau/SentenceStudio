# SentenceStudio — Distributed Architecture Plan

*Three-model comparison (Opus 4.6 · Gemini 3 Pro · Codex 5.3) + synthesized recommendation*
*Generated 2026-02-25*

---

## TL;DR

Transform SentenceStudio from a local MAUI app into a distributed, multi-tenant platform with:
- **sentence.studio** → public marketing site
- **app.sentence.studio** → authenticated Blazor web app
- **API backend** → Minimal APIs hosting AI/TTS/sync
- **MAUI clients** → continue offline-first with auth + sync

---

## Individual Plans at a Glance

### Opus 4.6
- 8 new projects (full Clean Architecture)
- Entra External ID auth
- Azure SQL + RLS for tenancy
- 6 phases

### Gemini 3 Pro
- 3 new projects (minimal churn)
- ASP.NET Core Identity (built-in)
- **SQLite-per-tenant** on server (creative! preserves CoreSync)
- 5 phases

### Codex 5.3
- 7 new projects + Workers
- Entra External ID auth + BFF pattern
- PostgreSQL + RLS, ULID IDs
- 7 milestones with clear dependencies

---

## Comparison Matrix

| Dimension | Opus | Gemini | Codex | **Winner** |
|-----------|------|--------|-------|------------|
| Auth | Entra External ID | ASP.NET Identity | Entra External ID | **Entra** (social login, MSAL for MAUI) |
| Server DB | Azure SQL + RLS | SQLite-per-tenant | PostgreSQL + RLS | **PostgreSQL + RLS** (ops-friendly) |
| Tenancy | Shared schema | DB-per-tenant | Shared schema | **Shared schema** (simpler migrations) |
| Blazor | Interactive Server | Interactive Server | Interactive Server + BFF | **Server + BFF** (most secure) |
| Projects | 8 new | 3 new | 7 new | **6 new** (balanced) |
| Sync | CoreSync → custom | CoreSync unchanged | CoreSync → Sync v2 | **Phased** (bridge then v2) |
| Jobs | Service Bus | (none) | Workers project | **Workers project** |
| Marketing | Static SSR | Razor Pages | Razor Pages + CDN | **Razor Pages + CDN** |
| Phases | 6 | 5 | 7 | **7 milestones** (most granular) |

---

## Synthesized Architecture

```
                     [Public Internet]
                            |
     +--------------+       |       +---------------------+
     |sentence.studio|      |       | MAUI Clients        |
     | Marketing    |       |       | iOS/Android/Mac/Win |
     | (Razor Pages)|       |       | Local SQLite + Sync |
     +------+-------+       |       +---------+-----------+
            |               v                 |
            v                                 v (MSAL Bearer)
     +------------------------------------------+
     |      CDN / WAF / Azure Front Door        |
     +------------------------------------------+
            |               |                 |
            v               v                 v
     +----------+   +--------------+   +------+
     | Marketing|   | WebApp       |   |      |
     | (static  |   | Blazor       |   |      |
     |  SSR)    |   | Server + BFF |   |      |
     +----------+   +------+-------+   |      |
                           |           |      |
                    cookie | JWT/OBO   |Bearer |
                           v           v      v
                    +---------------------------+
                    | SentenceStudio.Api         |
                    | Minimal APIs, net10.0      |
                    | auth · users · vocabulary  |
                    | plans · ai · speech · sync |
                    +----+----------+----------++
                         |          |          |
                   +-----+    +----+----+  +--+--------+
                   |          |         |  |            |
             +-----v----+ +--v----+ +--v--v------+
             |PostgreSQL | |Redis  | | Workers    |
             |Tenant RLS | |Cache  | | AI/TTS/    |
             |EF Core    | |Session| | Import     |
             +----------++ +-------+ +------+-----+
                                            |
                                      +-----v-----+
                                      |Blob Storage|
                                      | (media)    |
                                      +-----------+
```

---

## Project Structure

```
src/
  # ── CORE ──
  SentenceStudio.Domain/          # Entities, interfaces, rules
  SentenceStudio.Contracts/       # API DTOs, sync contracts
  SentenceStudio.Infrastructure/  # EF Core (Postgres+SQLite), repos

  # ── SERVER ──
  SentenceStudio.Api/             # Minimal API backend
  SentenceStudio.Workers/         # Background jobs (AI/TTS/import)
  SentenceStudio.WebApp/          # Blazor Web App + BFF
  SentenceStudio.Marketing/       # Public site (Razor Pages)

  # ── CLIENT ──
  SentenceStudio.AppLib/          # MAUI business logic
  SentenceStudio.UI/              # Shared Blazor RCL
  SentenceStudio.Shared/          # Cross-client primitives (slim down)

  # ── PLATFORM HEADS ──
  SentenceStudio.iOS/
  SentenceStudio.Android/
  SentenceStudio.MacCatalyst/
  SentenceStudio.Windows/
  SentenceStudio.MacOS/

  # ── ORCHESTRATION ──
  SentenceStudio.AppHost/         # Aspire (net10.0)
  SentenceStudio.ServiceDefaults/ # OpenTelemetry + resilience
```

---

## Authentication

### Web (app.sentence.studio)
```
Browser → WebApp (OIDC via Entra) → Cookie
WebApp → Api (OBO token) → JWT Bearer
```

### MAUI
```
App → MSAL system browser (PKCE) → Entra token
App → Api (Bearer token) → JWT validation
```

### Packages
| Project | Package |
|---------|---------|
| Api | `Microsoft.Identity.Web` + `JwtBearer` |
| WebApp | `Microsoft.Identity.Web` + `OpenIdConnect` |
| MAUI | `Microsoft.Identity.Client` (MSAL.NET) |

---

## Database

### Server: PostgreSQL + shared schema + RLS

**New tables:**
```sql
CREATE TABLE tenants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE users (
    id UUID PRIMARY KEY, -- Entra oid
    tenant_id UUID REFERENCES tenants(id),
    display_name TEXT NOT NULL,
    email TEXT
);
```

**All tenant tables get:**
```sql
ALTER TABLE vocabulary_words ADD COLUMN tenant_id UUID NOT NULL;
ALTER TABLE vocabulary_words ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON vocabulary_words
    USING (tenant_id = current_setting('app.tenant_id')::uuid);
```

**IDs:** Move to ULIDs (globally unique, sortable, sync-safe)

### Client: Keep SQLite (offline-first)

---

## API Endpoints

```
/api/v1/
├── auth/      bootstrap, me
├── users/     profile, preferences
├── resources/ CRUD, youtube import
├── vocabulary/ words, progress, review queue
├── plans/     generate, today, complete
├── ai/        chat, evaluate, scenario, correct
├── speech/    voices, synthesize, score
├── sync/      pull, push, checkpoint
└── admin/     health, metrics
```

---

## Implementation Milestones

### M0: Foundations
- Upgrade all server projects to net10.0
- Set up Entra External ID tenant
- Consolidate ServiceDefaults
- **Output:** Clean solution, Entra provisioned

### M1: Identity + Tenant Skeleton
- JWT Bearer in Api, OIDC cookies in WebApp
- Tenant middleware (resolve from claims)
- `tenants` + `users` tables
- `POST /auth/bootstrap`
- **Depends on:** M0

### M2: Server Extraction
- Move AI/TTS/planning from AppLib → Api
- Create Workers for YouTube import + async AI
- AppLib calls Api via HttpClient
- **Depends on:** M1

### M3: PostgreSQL + Tenancy
- EF Core migrations for Postgres
- `tenant_id` on all tables + RLS
- Global query filters
- ULID IDs
- Tenant-leak integration tests
- **Depends on:** M1, M2

### M4: Web App Launch
- Blazor Web App (Interactive Server + BFF)
- Import UI RCL
- Wire pages to Api
- Deploy to Azure Container Apps
- **Depends on:** M2, M3

### M5: MAUI Auth + Sync Bridge
- MSAL login flow in MAUI
- Bearer tokens on all API calls
- CoreSync with auth headers
- First-login: upload local SQLite → server merge
- **Depends on:** M1, M3

### M6: Sync v2
- Tenant-aware delta sync (server-authoritative)
- Change feed with per-tenant checkpoints
- Conflict resolution workflows
- Deprecate CoreSync
- **Depends on:** M5

### M7: Marketing + Hardening
- Public site + CDN/WAF
- Load testing + security audit
- Per-tenant AI budgets + rate limiting
- SLO dashboards + runbooks
- **Depends on:** M4

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Tenant data leakage | **Critical** | RLS + query filters + leak tests on every endpoint |
| Sync conflicts | High | Shadow writes + replay validation |
| Auth UX on MAUI | Medium | MSAL system browser (native experience) |
| CoreSync limits | Medium | Interface isolation → Sync v2 |
| Blazor Server scaling | Medium | Redis backplane + ACA autoscale |
| AI cost abuse | High | Per-tenant budgets + rate limiting |

---

## Credit — Where Each Model Won

| Decision | Winner | Reason |
|----------|--------|--------|
| Auth provider | Opus + Codex | Entra = social login + enterprise ready |
| Server DB | Opus + Codex | PostgreSQL + RLS = ops-sound |
| BFF pattern | Codex | Most secure web auth |
| Workers project | Codex | Background jobs need separation |
| Milestone granularity | Codex | 7 milestones, clearest deps |
| Clean Architecture | Opus | Domain + Contracts + Infra |
| SQLite-per-tenant | Gemini | Creative fallback option |
| Minimal churn | Gemini | Kept project count reasonable |
| ULID IDs | Codex | Sync safety across tenants |
