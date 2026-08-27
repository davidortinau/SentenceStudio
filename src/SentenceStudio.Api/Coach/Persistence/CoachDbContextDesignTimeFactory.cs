using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>. It exists so migration commands never build
/// the API host (which needs Aspire-injected connection strings and live services).
/// </summary>
/// <remarks>
/// The connection string comes from the environment, in order:
/// <c>COACH_DB_CONNECTION</c>, then <c>ConnectionStrings__coach</c>, then
/// <c>ConnectionStrings__sentencestudio</c>. The fallback is a local, password-free
/// design-only string — no secret is ever committed here. Design-time never runs against
/// production: pass the scratch database explicitly when verifying Up/Down.
/// </remarks>
public sealed class CoachDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CoachDbContext>
{
    private const string DesignTimeFallback =
        "Host=localhost;Port=5432;Database=sentencestudio_coach_design;Username=postgres";

    public CoachDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("COACH_DB_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__coach")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__sentencestudio")
            ?? DesignTimeFallback;

        var options = new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__CoachMigrationsHistory"))
            .Options;

        return new CoachDbContext(options);
    }
}
