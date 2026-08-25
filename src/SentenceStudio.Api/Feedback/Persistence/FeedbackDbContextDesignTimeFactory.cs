using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SentenceStudio.Api.Feedback.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>, so migration commands never build the API host.
/// </summary>
/// <remarks>
/// The connection string comes from the environment, in order: <c>FEEDBACK_DB_CONNECTION</c>, then
/// <c>COACH_DB_CONNECTION</c> (the scratch server a developer already has configured), then
/// <c>ConnectionStrings__sentencestudio</c>. The fallback is a local, password-free design-only
/// string — no secret is committed here, and design-time never runs against production.
/// </remarks>
public sealed class FeedbackDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FeedbackDbContext>
{
    private const string DesignTimeFallback =
        "Host=localhost;Port=5432;Database=sentencestudio_feedback_design;Username=postgres";

    public FeedbackDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("FEEDBACK_DB_CONNECTION")
            ?? Environment.GetEnvironmentVariable("COACH_DB_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__sentencestudio")
            ?? DesignTimeFallback;

        var options = new DbContextOptionsBuilder<FeedbackDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(FeedbackSchema.MigrationsHistoryTable))
            .Options;

        return new FeedbackDbContext(options);
    }
}

/// <summary>Schema-level constants shared by the host, the design-time factory, and the tests.</summary>
public static class FeedbackSchema
{
    /// <summary>
    /// The feedback migration set's own history table, so it never shares bookkeeping with the
    /// application or coach migrations even though all three live in one database.
    /// </summary>
    public const string MigrationsHistoryTable = "__FeedbackMigrationsHistory";
}
