---
name: "project-conventions"
description: "Core conventions and patterns for SentenceStudio"
domain: "project-conventions"
confidence: "high"
source: "codebase-audit-2026-03-26"
---

## Context

SentenceStudio is a .NET MAUI Blazor Hybrid language learning app. .NET 10 SDK (10.0.101), multi-platform (iOS, Android, Mac Catalyst), with .NET Aspire orchestration.

## Project Structure

| Project | Purpose | Build |
|---------|---------|-------|
| `SentenceStudio.Shared` | Core: DbContext, repos, services, models, migrations | Multi-TFM |
| `SentenceStudio.AppLib` | Blazor Hybrid library, Scriban templates | Multi-TFM |
| `SentenceStudio.UI` | Blazor RCL — web pages, components | `dotnet build` (no -f) |
| `SentenceStudio.MacCatalyst/iOS/Android` | MAUI app heads | `-f net10.0-{platform}` |
| `SentenceStudio.Api` | ASP.NET Core backend | net10.0 |
| `SentenceStudio.WebApp` | Blazor Server frontend | net10.0 |
| `SentenceStudio.AppHost` | Aspire orchestration | net10.0 |
| `SentenceStudio.Infrastructure` | Server-side persistence (ServerDbContext) | net10.0 |
| `SentenceStudio.Workers` | Background services | net10.0 |

## Build Commands

```bash
# MAUI (NEVER use dotnet run)
dotnet build -f net10.0-maccatalyst
dotnet build -t:Run -f net10.0-maccatalyst

# UI project (no TFM flag)
dotnet build src/SentenceStudio.UI/SentenceStudio.UI.csproj

# Tests
dotnet test
```

## Testing

- **Framework:** xUnit + Moq + FluentAssertions (Unit), AutoFixture (Integration), AspNetCore.Mvc.Testing (API)
- **Projects:** `tests/SentenceStudio.UnitTests`, `tests/SentenceStudio.IntegrationTests`, `tests/SentenceStudio.Api.Tests`
- **Target:** net10.0

## Blazor Page Patterns

- Bootstrap icons only: `<i class="bi bi-{name}"></i>` — **NEVER emojis**
- Layout: `<PageHeader>` → `<ToolbarActions>` → main content with `card card-ss`
- Spinners: `<span class="spinner-border spinner-border-sm">`
- Alerts: `<div class="alert alert-{danger|warning|success}">`
- Auth: `@attribute [Authorize]` on protected pages
- Cleanup: `@implements IAsyncDisposable`

## Database Patterns

- **DbContext:** `ApplicationDbContext` — SQLite on mobile, PostgreSQL on server
- **Table names:** SINGULAR (configured in OnModelCreating)
- **Synced entities:** String GUID PKs with `ValueGeneratedNever()`
- **Non-synced:** Int auto-increment PKs
- **Migrations:** Always via `dotnet ef migrations add` — NEVER hand-write, NEVER raw SQL ALTER TABLE
- **Data isolation:** Filter by `UserProfileId`
- **CRITICAL:** Never call `EnsureCreatedAsync` before `MigrateAsync`
- **Migration location:** `Migrations/` (PostgreSQL), `Migrations/Sqlite/` (mobile)

## AI/Prompt Patterns

- **Templates:** 24+ Scriban templates in `src/SentenceStudio.AppLib/Resources/Raw/*.scriban-txt`
- **Client:** `IChatClient` (Microsoft.Extensions.AI) via `AiService.SendPrompt<T>()`
- **DTOs:** Use `[Description]` attributes on properties — no `[JsonPropertyName]`
- **Connectivity:** Check `_connectivity.IsInternetAvailable` before AI calls
- **Gateway:** Optional `IAiGatewayClient` for server routing

## Service Patterns

- Constructor injection via `IServiceProvider`
- All methods async (`Task<T>`)
- `ILogger<T>` for structured logging
- `WeakReferenceMessenger.Default.Send()` for cross-component messaging
- Scoped DbContext access: `_serviceProvider.CreateScope()` → `GetRequiredService<ApplicationDbContext>()`

## Error Handling

- Structured logging: `_logger.LogError(ex, "context {Field}", value)`
- `InvalidOperationException` for state violations
- `ArgumentException` for invalid arguments
- Default/null returns for external API failures (TTS, image)
- Connectivity resilience via `ConnectivityChangedMessage`

## MauiReactor Conventions

- Use `VStart()` / `VEnd()` not `Top()` / `Bottom()`
- Use `HStart()` / `HEnd()` not `Start()` / `End()`
- NEVER use `FillAndExpand` — legacy pattern
- NEVER put CollectionView inside scrollable containers

## Anti-Patterns (CRITICAL)

- ❌ NEVER uninstall/reinstall apps (destroys user data)
- ❌ NEVER delete database files without permission + backup
- ❌ NEVER use `dotnet run` for MAUI apps
- ❌ NEVER use emoji in UI/code/logs
- ❌ NEVER use raw SQL ALTER TABLE — always EF migrations
- ❌ NEVER suppress PendingModelChangesWarning
- ❌ NEVER put CollectionView inside VStack/scrollable containers
- ❌ NEVER use inline FontImageSource — define in ApplicationTheme.Icons.cs
- ❌ All documentation files go in `/docs/` not repo root
