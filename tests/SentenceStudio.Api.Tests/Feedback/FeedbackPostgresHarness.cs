using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Api.Tests.Coach.Postgres;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// Owns one throwaway PostgreSQL database, migrates it with the production feedback migrations,
/// and builds the production feedback services over it.
/// </summary>
/// <remarks>
/// <para>
/// The exactly-once guarantee is a claim about the database, so it cannot be demonstrated
/// anywhere else. <see cref="NewContext"/> hands out a context over its <em>own</em> connection
/// every time it is called, which is what makes "two submissions race" a real race: the callers
/// share no change tracker, no transaction, and no lock that lives inside the process. Two API
/// replicas behind a load balancer contend exactly this way, and an in-process guard that passed a
/// single-context test fails here.
/// </para>
/// <para>
/// The server probe and the create/drop helpers come from <see cref="CoachPostgresServer"/> rather
/// than being duplicated. It is the same scratch server, the same environment variable, and the
/// same "only ever drop databases we created" guard; a second copy would mean a developer had to
/// configure two variables and would produce two different skip messages for one missing server.
/// </para>
/// <para>
/// The schema comes from <c>MigrateAsync</c>, never <c>EnsureCreated</c>, so what is under test is
/// the migration a deployment will actually run — including its own
/// <c>__FeedbackMigrationsHistory</c> table, without which these tests would migrate a different
/// table than production does.
/// </para>
/// </remarks>
internal sealed class FeedbackPostgresHarness : IAsyncDisposable
{
    private readonly List<FeedbackDbContext> _contexts = new();

    private FeedbackPostgresHarness(
        string databaseName,
        string connectionString,
        TimeProvider time,
        FeedbackOptions options)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Time = time;
        Options = options;

        DbOptions = new DbContextOptionsBuilder<FeedbackDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(FeedbackSchema.MigrationsHistoryTable))
            // Deliberately no PendingModelChangesWarning suppression: production does not suppress
            // it for this context, and a harness that did would migrate happily against a model the
            // migrations no longer describe.
            .Options;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public DbContextOptions<FeedbackDbContext> DbOptions { get; }

    public TimeProvider Time { get; }

    public FeedbackOptions Options { get; }

    public static async Task<FeedbackPostgresHarness> CreateAsync(
        string label,
        TimeProvider? time = null,
        FeedbackOptions? options = null,
        bool migrate = true,
        CancellationToken cancellationToken = default)
    {
        var database = await CoachPostgresServer.CreateDatabaseAsync($"fb{label}", cancellationToken)
            .ConfigureAwait(false);

        var harness = new FeedbackPostgresHarness(
            database,
            CoachPostgresServer.ConnectionStringFor(database),
            time ?? TimeProvider.System,
            options ?? new FeedbackOptions());

        if (migrate)
        {
            await using var db = harness.NewContext();
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        return harness;
    }

    /// <summary>
    /// A context over its own connection. Tests hold several at once on purpose — that is what
    /// makes a race a race rather than two objects taking turns on one handle.
    /// </summary>
    public FeedbackDbContext NewContext()
    {
        var db = new FeedbackDbContext(DbOptions);
        lock (_contexts)
        {
            _contexts.Add(db);
        }

        return db;
    }

    public FeedbackSubmissionLedger NewLedger(FeedbackDbContext db, TimeProvider? time = null) =>
        new(db,
            time ?? Time,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<FeedbackSubmissionLedger>.Instance);

    public FeedbackRateLimiter NewRateLimiter(FeedbackDbContext db, TimeProvider? time = null) =>
        new(db,
            time ?? Time,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<FeedbackRateLimiter>.Instance);

    public FeedbackRetentionSweep NewRetentionSweep(FeedbackDbContext db, TimeProvider? time = null) =>
        new(db,
            time ?? Time,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<FeedbackRetentionSweep>.Instance);

    public FeedbackDataDeletionService NewDeletionService(FeedbackDbContext db) =>
        new(db, NullLogger<FeedbackDataDeletionService>.Instance);

    /// <summary>Opens a raw ADO connection, for schema and type introspection.</summary>
    public async Task<NpgsqlConnection> OpenRawAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task<List<string>> StringsAsync(string sql, CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        FeedbackDbContext[] contexts;
        lock (_contexts)
        {
            contexts = _contexts.ToArray();
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            try
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // A test may already have disposed it; cleanup must not mask the real failure.
            }
        }

        await CoachPostgresServer.DropDatabaseAsync(DatabaseName).ConfigureAwait(false);
    }
}

/// <summary>Helpers for building the claim requests these tests need.</summary>
internal static class FeedbackTestData
{
    public static FeedbackClaimRequest Claim(
        string jti,
        string owner,
        string digest = "digest",
        DateTimeOffset? expires = null) =>
        new(jti,
            owner,
            digest,
            SentenceStudio.Contracts.Feedback.FeedbackRouteCategory.Activity,
            SentenceStudio.Contracts.Feedback.FeedbackPlatform.Web,
            "1.2.3",
            expires ?? DateTimeOffset.UtcNow.AddMinutes(10));

    public static TestTimeProvider Clock(DateTimeOffset? start = null) =>
        new(start ?? new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
}
