using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Diagnostics;

/// <summary>
/// Reports Degraded (not Unhealthy — we don't want this killing the API) when the
/// AspNetUsers table is empty. This catches the multi-worktree footgun where Aspire
/// provisions a fresh Postgres volume separate from the one holding real user data.
/// Result is cached for ~30 seconds so the check doesn't hammer the DB.
/// See: .squad/decisions.md "AppHost Multi-Worktree Isolation".
/// </summary>
public sealed class EmptyUsersHealthCheck : IHealthCheck
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly object CacheLock = new();
    private static HealthCheckResult? _cachedResult;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmptyUsersHealthCheck> _logger;

    public EmptyUsersHealthCheck(IServiceScopeFactory scopeFactory, ILogger<EmptyUsersHealthCheck> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        lock (CacheLock)
        {
            if (_cachedResult is { } cached && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
            {
                return cached;
            }
        }

        HealthCheckResult result;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userCount = await db.Users.CountAsync(cancellationToken);
            var connectionInfo = DescribeConnection(db);

            if (userCount > 0)
            {
                result = HealthCheckResult.Healthy(
                    $"AspNetUsers populated ({userCount} users). {connectionInfo}",
                    new Dictionary<string, object>
                    {
                        ["userCount"] = userCount,
                        ["connection"] = connectionInfo
                    });
            }
            else
            {
                var message = EmptyUsersDetector.BuildMessage(connectionInfo);
                result = HealthCheckResult.Degraded(
                    "AspNetUsers table is empty — likely bound to a fresh Postgres volume from another worktree.",
                    data: new Dictionary<string, object>
                    {
                        ["userCount"] = 0,
                        ["connection"] = connectionInfo,
                        ["details"] = message
                    });
            }
        }
        catch (Exception ex)
        {
            // Don't promote DB connectivity failures to Degraded here — leave that to the
            // built-in DbContext health check. We only care about the empty-table signal.
            _logger.LogWarning(ex, "EmptyUsersHealthCheck failed to query AspNetUsers; reporting Healthy to avoid masking other DB checks.");
            result = HealthCheckResult.Healthy("AspNetUsers count unavailable (see logs).");
        }

        lock (CacheLock)
        {
            _cachedResult = result;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return result;
    }

    private static string DescribeConnection(ApplicationDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            var dataSource = string.IsNullOrWhiteSpace(conn.DataSource) ? "<unknown-host>" : conn.DataSource;
            var database = string.IsNullOrWhiteSpace(conn.Database) ? "<unknown-db>" : conn.Database;
            return $"{dataSource} db={database}";
        }
        catch
        {
            return "<connection-info-unavailable>";
        }
    }
}

/// <summary>
/// Shared message + helpers for the empty-users diagnostic so the startup banner and the
/// runtime health check stay in sync.
/// </summary>
public static class EmptyUsersDetector
{
    public static bool IsPostgres(ApplicationDbContext db)
    {
        // Defensive: skip the check if EF resolved a non-Postgres provider (SQLite test runs).
        var providerName = db.Database.ProviderName ?? string.Empty;
        return providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeConnection(ApplicationDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            var dataSource = string.IsNullOrWhiteSpace(conn.DataSource) ? "<unknown-host>" : conn.DataSource;
            var database = string.IsNullOrWhiteSpace(conn.Database) ? "<unknown-db>" : conn.Database;
            return $"{dataSource} db={database}";
        }
        catch
        {
            return "<connection-info-unavailable>";
        }
    }

    public static string? TryReadVolumeHashHint()
    {
        // Aspire propagates the resource name via well-known env vars. Surfacing whichever
        // one is populated helps Captain correlate the API's bound volume back to a worktree.
        // Best-effort only — none of these are guaranteed to be set.
        foreach (var key in new[]
                 {
                     "ASPIRE_RESOURCE_NAME",
                     "OTEL_SERVICE_INSTANCE_ID",
                     "ConnectionStrings__sentencestudio"
                 })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return $"{key}={value}";
            }
        }
        return null;
    }

    public static string BuildMessage(string connectionInfo)
    {
        var volumeHint = TryReadVolumeHashHint() ?? "(not available from environment)";
        return $"""

            ============================================================
            EMPTY USER DATABASE DETECTED
            ============================================================
            AspNetUsers table is empty. This usually means the API is
            bound to a fresh Postgres volume — likely a different worktree
            or freshly-provisioned Aspire environment.

            Connection: {connectionInfo}
            Aspire volume hash (if available from env): {volumeHint}

            If you expected existing user data, check:
              1. Are you running the right AppHost worktree?
              2. Did `aspire run` re-provision a volume?
              3. Run `docker volume ls | grep sentencestudio` to see all volumes.
            ============================================================
            """;
    }
}
