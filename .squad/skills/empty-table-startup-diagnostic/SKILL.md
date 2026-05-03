# Empty-Table Startup Diagnostic (Aspire + EF Core)

**When to use:** Any time an Aspire-orchestrated app can silently bind to a fresh database volume / connection where a critical table (users, tenants, license-keys) is unexpectedly empty. The classic footgun: multi-worktree dev → each worktree gets its own persistent volume → wrong DB → 401s with no obvious cause.

## Pattern

Combine **two** read-only signals so the empty state is impossible to miss:

1. **Startup banner** (`LogCritical`) once after `app.Build()` — single unmissable scream when the API first binds to the wrong DB.
2. **Health check** returning `Degraded` (NOT `Unhealthy`) — recurring signal that paints the Aspire dashboard amber without taking the service offline.

Both signals must share the same message body. Build it from a static helper so they cannot drift.

## Recipe (≈ 50 lines)

```csharp
// 1. The health check — cache the count so dashboard polls don't hammer the DB.
public sealed class EmptyTableHealthCheck : IHealthCheck
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly object Lock = new();
    private static HealthCheckResult? _cached;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    private readonly IServiceScopeFactory _scopes;

    public EmptyTableHealthCheck(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext _, CancellationToken ct = default)
    {
        lock (Lock)
            if (_cached is { } c && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return c;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyDbContext>();
        var count = await db.Users.CountAsync(ct);
        var result = count > 0
            ? HealthCheckResult.Healthy($"{count} rows.")
            : HealthCheckResult.Degraded(BuildMessage(db));   // NOT Unhealthy

        lock (Lock) { _cached = result; _cachedAtUtc = DateTime.UtcNow; }
        return result;
    }
}

// 2. Register — failureStatus: Degraded is the critical knob.
builder.Services.AddHealthChecks()
    .AddCheck<EmptyTableHealthCheck>("table-populated", failureStatus: HealthStatus.Degraded);

// 3. Map endpoint (Development only — production observes via OTEL/App Insights).
if (app.Environment.IsDevelopment()) app.MapHealthChecks("/health");

// 4. Startup banner — runs once, after migrations, before app.Run().
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MyDbContext>();
    if (IsTargetProvider(db) && await db.Users.CountAsync() == 0)
        logger.LogCritical("{Banner}", BuildMessage(db));
}
```

## Hard rules

- **READ-ONLY.** `SELECT COUNT(*)` only. No DELETE, INSERT, UPDATE, migration, or seed in the diagnostic path. Ever. Captain's data-preservation rule applies.
- **Degraded, not Unhealthy.** Empty-table is a config issue. `Unhealthy` cascades through `IHostApplicationLifetime` and Aspire orchestration and can take the API offline. We want a warning, not an outage.
- **Skip `Testing` environment.** xUnit / `WebApplicationFactory<Program>` runs spin up empty in-memory or SQLite DBs by design. Don't scare integration tests.
- **Skip non-target providers.** Use `db.Database.ProviderName.Contains("Npgsql")` (or your provider) to defensively skip when EF resolved a different provider in tests.
- **Cache the health check.** Aspire dashboard polls `/health` every few seconds. Uncached, that's a `COUNT(*)` storm. Static `object` lock + `DateTime` field is the simplest correct primitive — no `IMemoryCache` registration required.
- **Connection diagnostics without leaking creds.** Use `db.Database.GetDbConnection().DataSource` and `.Database` — these expose host:port and db name without the raw connection string (no password risk in logs).

## Don't bother with

- An auto-recovery / auto-seed path. Captain explicitly does NOT want this. The whole point is to ALERT the human.
- `MapDefaultEndpoints()` from a stock Aspire `ServiceDefaults` template — if the project's ServiceDefaults is MAUI-safe (no `Microsoft.AspNetCore.App` reference), it won't have `MapDefaultEndpoints`. Always check `ServiceDefaults/Extensions.cs` before assuming `/health` is mapped.
- A periodic `IHostedService` poll. Startup + dashboard health-check polling already covers both first-bind and mid-session scenarios. A timer adds noise without new signal.

## Reference implementation

`src/SentenceStudio.Api/Diagnostics/EmptyUsersHealthCheck.cs` and surrounding edits in `src/SentenceStudio.Api/Program.cs`. Decision: `.squad/decisions/inbox/wash-empty-users-startup-banner.md` (2026-05-02).
