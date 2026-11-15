using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SentenceStudio.Api.Admin.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>, so migration commands never build the API host.
/// </summary>
/// <remarks>
/// The connection string comes from the environment, in order: <c>ADMIN_DB_CONNECTION</c>, then
/// <c>COACH_DB_CONNECTION</c> (the scratch server a developer already has configured), then
/// <c>ConnectionStrings__sentencestudio</c>. The fallback is a local, password-free design-only
/// string. Design-time never runs against production.
/// </remarks>
public sealed class AdminDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AdminDbContext>
{
    private const string DesignTimeFallback =
        "Host=localhost;Port=5432;Database=sentencestudio_admin_design;Username=postgres";

    public AdminDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ADMIN_DB_CONNECTION")
            ?? Environment.GetEnvironmentVariable("COACH_DB_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__sentencestudio")
            ?? DesignTimeFallback;

        var options = new DbContextOptionsBuilder<AdminDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(AdminSchema.MigrationsHistoryTable))
            .Options;

        return new AdminDbContext(options);
    }
}
