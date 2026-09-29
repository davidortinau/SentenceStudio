using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Startup;

/// <summary>Startup-only contexts share the open, externally owned lock connection.</summary>
internal sealed class StartupDbContextFactory(PostgresApplicationStartupLock startupLock)
{
    public const string CoachMigrationsHistoryTable = "__CoachMigrationsHistory";

    public ApplicationDbContext CreateApplication()
    {
        startupLock.EnsureActive();
        var db = new ApplicationDbContext(
            ApplicationOptions(startupLock.Connection, startupLock.DbInterceptors));
        VerifyContext(db);
        return db;
    }

    public CoachDbContext CreateCoach()
    {
        startupLock.EnsureActive();
        var db = new CoachDbContext(
            CoachOptions(startupLock.Connection, startupLock.DbInterceptors));
        VerifyContext(db);
        return db;
    }

    public FeedbackDbContext CreateFeedback()
    {
        startupLock.EnsureActive();
        var db = new FeedbackDbContext(
            FeedbackOptions(startupLock.Connection, startupLock.DbInterceptors));
        VerifyContext(db);
        return db;
    }

    internal static DbContextOptions<ApplicationDbContext> ApplicationOptions(
        NpgsqlConnection connection, IEnumerable<IInterceptor> interceptors)
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(interceptors)
            .Options;

    internal static DbContextOptions<CoachDbContext> CoachOptions(
        NpgsqlConnection connection, IEnumerable<IInterceptor> interceptors)
        => new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable(CoachMigrationsHistoryTable))
            .AddInterceptors(interceptors)
            .Options;

    internal static DbContextOptions<FeedbackDbContext> FeedbackOptions(
        NpgsqlConnection connection, IEnumerable<IInterceptor> interceptors)
        => new DbContextOptionsBuilder<FeedbackDbContext>()
            .UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable(FeedbackSchema.MigrationsHistoryTable))
            .AddInterceptors(interceptors)
            .Options;

    internal static void RequireSingleDatabase(IConfiguration configuration)
    {
        var primary = configuration.GetConnectionString("sentencestudio");
        if (string.IsNullOrWhiteSpace(primary))
        {
            throw new InvalidOperationException("The API startup database connection is not configured.");
        }

        // A session-level advisory lock is scoped to one database and one backend.
        // Do not quietly migrate an optional feature against a second database.
        foreach (var alternate in new[] { "coach", "feedback" })
        {
            var configured = configuration.GetConnectionString(alternate);
            if (configured is not null && !string.Equals(configured, primary, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{alternate} must match ConnectionStrings:sentencestudio for fenced API startup.");
            }
        }
    }

    private void VerifyContext(DbContext db)
    {
        try
        {
            startupLock.EnsureActive();
            if (!ReferenceEquals(db.Database.GetDbConnection(), startupLock.Connection)
                || db.Database.CreateExecutionStrategy().RetriesOnFailure)
            {
                startupLock.AbortStartup();
                throw new InvalidOperationException(
                    "A startup EF context must use only the locked connection with retries disabled.");
            }
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }
}
