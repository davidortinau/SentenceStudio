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

## Testing Strategy

### Unit Tests
- **Domain/Contracts**: Pure logic, no infrastructure. xUnit + FluentAssertions.
- **Infrastructure**: EF Core tests against SQLite in-memory provider (fast) + PostgreSQL testcontainer (accurate).
- **Api endpoints**: `WebApplicationFactory<Program>` with test auth handler (fake JWT claims). No real Entra needed.
- **Workers**: Test job logic in isolation; mock external APIs (OpenAI, ElevenLabs).

### Integration Tests
- **Tenant isolation (CRITICAL)**: Seed two tenants, verify Tenant A cannot read Tenant B's data. Run on every endpoint.
  ```csharp
  [Fact]
  public async Task TenantA_Cannot_See_TenantB_Vocabulary()
  {
      var clientA = CreateAuthenticatedClient(tenantId: "A");
      var clientB = CreateAuthenticatedClient(tenantId: "B");
      await clientB.PostAsync("/api/v1/vocabulary", wordPayload);
      var result = await clientA.GetAsync("/api/v1/vocabulary");
      Assert.Empty(result.Words); // Must not leak
  }
  ```
- **Auth flow**: Test JWT validation, expired tokens, missing scopes, tenant mismatch.
- **Sync round-trip**: Push from client → pull from another client → verify consistency.
- **Database migrations**: Apply migrations to empty Postgres, verify schema matches expectations.

### End-to-End Tests
- **API E2E**: Docker Compose spins up Api + Postgres + Redis. HTTP client hits real endpoints with real DB.
- **Web App E2E**: Playwright against Blazor Server WebApp running in Docker.
  - Login flow (mock Entra via test identity provider or Entra test tenant)
  - Navigate dashboard → start activity → verify AI response renders
  - Verify tenant isolation in browser (switch users, confirm data separation)
- **MAUI E2E**: Appium automation (already have this skill) against iOS Simulator / Android Emulator.
  - Login → onboarding → dashboard → conversation activity
  - Verify sync: create word on device → sync → verify in API
- **Cross-platform E2E**: Create data in WebApp → sync to MAUI → verify round-trip consistency.

### Test Infrastructure
```
tests/
  SentenceStudio.Domain.Tests/          # Unit tests
  SentenceStudio.Infrastructure.Tests/  # EF Core + repo tests
  SentenceStudio.Api.Tests/             # WebApplicationFactory integration
  SentenceStudio.Api.E2E/               # Docker Compose full-stack
  SentenceStudio.WebApp.E2E/            # Playwright browser tests
  SentenceStudio.Sync.Tests/            # Sync round-trip tests
```

### CI Pipeline
```yaml
# GitHub Actions
jobs:
  unit-tests:        # Fast, no containers. Domain + Contracts.
  integration-tests: # Testcontainers (Postgres). Api + Infrastructure.
  e2e-api:           # Docker Compose. Full Api stack.
  e2e-web:           # Docker Compose + Playwright. WebApp in browser.
  maui-build:        # Build MAUI heads (no device tests in CI).
```

### Key Packages
| Purpose | Package |
|---------|---------|
| Test framework | `xUnit` + `FluentAssertions` |
| API integration | `Microsoft.AspNetCore.Mvc.Testing` |
| Containers in tests | `Testcontainers` (PostgreSQL, Redis) |
| Browser E2E | `Microsoft.Playwright` |
| MAUI E2E | Appium (existing skill) |
| Fake auth | Custom `TestAuthHandler` (no real Entra in CI) |
| Test data | `Bogus` (faker library) |

---

## Local Development (No Azure Required)

### Everything runs locally via Aspire + Docker

The entire distributed stack runs on your Mac with **zero Azure dependency** during development. Aspire orchestrates Docker containers for infrastructure.

### What runs where

| Component | Local Setup | Azure (Production) |
|-----------|------------|-------------------|
| **PostgreSQL** | Docker container via Aspire | Azure Database for PostgreSQL |
| **Redis** | Docker container via Aspire | Azure Cache for Redis |
| **Blob Storage** | Azurite emulator (Docker) or local filesystem | Azure Blob Storage |
| **Queue/Workers** | In-process or Docker container | Azure Queue Storage / Service Bus |
| **Api** | `dotnet run` (Kestrel) | Azure Container Apps |
| **WebApp** | `dotnet run` (Kestrel) | Azure Container Apps |
| **Marketing** | `dotnet run` (Kestrel) | Azure Static Web Apps / Container Apps |
| **Auth (Entra)** | Test identity provider *or* free Entra test tenant | Entra External ID |
| **MAUI clients** | iOS Sim / Android Emu / Mac Catalyst | App Store / Play Store |

### Local auth options (no Entra needed for dev)

**Option A: Fake auth handler (simplest)**
```csharp
// In Api and WebApp during development
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication("Dev")
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("Dev", null);
}
```
A `DevAuthHandler` auto-creates a claim with a dev tenant/user. No login screen, no Entra config. Just works.

**Option B: Entra test tenant (free)**
- Create a free Entra External ID tenant (Azure portal, no credit card)
- Register two app registrations (Api + WebApp)
- Test with real OIDC login flows locally
- This is the recommended approach once auth UI work begins (M1)

### Aspire AppHost (local dev)

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// All infrastructure is Docker containers — no Azure account needed
var postgres = builder.AddPostgres("db")
    .WithPgAdmin()                    // pgAdmin UI at localhost:port
    .AddDatabase("sentencestudio");

var redis = builder.AddRedis("cache")
    .WithRedisInsight();              // Redis Insight UI

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator();                 // Azurite in Docker

// Application services run as .NET projects
var api = builder.AddProject<Projects.SentenceStudio_Api>("api")
    .WithReference(postgres)
    .WithReference(redis)
    .WithReference(storage);

var workers = builder.AddProject<Projects.SentenceStudio_Workers>("workers")
    .WithReference(postgres)
    .WithReference(redis);

builder.AddProject<Projects.SentenceStudio_WebApp>("webapp")
    .WithReference(api)
    .WithReference(redis);

builder.AddProject<Projects.SentenceStudio_Marketing>("marketing");

builder.Build().Run();
```

### What you get with `dotnet run` in AppHost

```
Aspire Dashboard:     https://localhost:18888
  ├── Api:            https://localhost:5001
  ├── WebApp:         https://localhost:5002
  ├── Marketing:      https://localhost:5003
  ├── Workers:        (background, no HTTP)
  ├── PostgreSQL:     localhost:5432
  ├── pgAdmin:        http://localhost:5050
  ├── Redis:          localhost:6379
  ├── Redis Insight:  http://localhost:8001
  └── Azurite:        localhost:10000 (blobs)
```

All with distributed tracing, logs, and metrics visible in the Aspire Dashboard.

### MAUI client pointing to local API

```csharp
// In MAUI head MauiProgram.cs (development)
#if DEBUG
    // Android emulator uses 10.0.2.2 to reach host localhost
    // iOS simulator uses localhost directly
    services.AddHttpClient("Api", client =>
    {
        client.BaseAddress = DeviceInfo.Platform == DevicePlatform.Android
            ? new Uri("https://10.0.2.2:5001")
            : new Uri("https://localhost:5001");
    });
#endif
```

### Docker Compose for CI/E2E (no Aspire needed)

```yaml
# docker-compose.test.yml — runs in CI without Aspire SDK
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_DB: sentencestudio
      POSTGRES_PASSWORD: testpass
    ports: ["5432:5432"]

  redis:
    image: redis:7
    ports: ["6379:6379"]

  api:
    build: { context: ., dockerfile: src/SentenceStudio.Api/Dockerfile }
    environment:
      ConnectionStrings__db: Host=postgres;Database=sentencestudio;Username=postgres;Password=testpass
      ConnectionStrings__cache: redis:6379
      ASPNETCORE_ENVIRONMENT: Testing
    ports: ["5001:8080"]
    depends_on: [postgres, redis]

  webapp:
    build: { context: ., dockerfile: src/SentenceStudio.WebApp/Dockerfile }
    environment:
      services__api__https__0: https://api:8080
    ports: ["5002:8080"]
    depends_on: [api]
```

### When do you actually need Azure?

| Phase | Azure needed? | What for? |
|-------|--------------|-----------|
| M0–M3 | **No** | Everything local via Aspire + Docker |
| M4 (Web launch) | **Yes** | First production deployment (Container Apps, Postgres, DNS) |
| M5 (MAUI auth) | **Maybe** | Free Entra test tenant works; production needs real Entra |
| M7 (Production) | **Yes** | CDN, WAF, monitoring, backups, scaling |

You can build M0 through M3 entirely on your Mac with Docker Desktop. No Azure subscription, no cloud costs.

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
