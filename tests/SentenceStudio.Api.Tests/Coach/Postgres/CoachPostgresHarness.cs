using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Cleanup;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Owns one throwaway PostgreSQL database, migrates it with the production migrations, and
/// builds the production coach stores over it.
/// </summary>
/// <remarks>
/// <para>
/// This is the PostgreSQL counterpart of <see cref="CoachPersistenceHarness"/>. The differences
/// are deliberate and are the whole point of the family: the schema comes from
/// <c>MigrateAsync</c> rather than <c>EnsureCreated</c>, so the migrations are what is under
/// test; and every store is handed a context over a distinct pooled connection when a test asks
/// for one, so "two workers race" means two real connections rather than two objects sharing a
/// single SQLite handle.
/// </para>
/// <para>
/// The data protection provider is created once per harness and shared by every store it builds,
/// which is what production does through a single key ring. Tests that need to prove key
/// persistence across a process boundary build their own provider instead.
/// </para>
/// </remarks>
internal sealed class CoachPostgresHarness : IAsyncDisposable
{
    /// <summary>Mirrors the constant in the API host's coach registration.</summary>
    public const string CoachMigrationsHistoryTable = "__CoachMigrationsHistory";

    private readonly List<CoachDbContext> _contexts = new();
    private readonly IDataProtectionProvider _dataProtection;

    /// <summary>
    /// False for a view onto another harness's database, which must not drop it on dispose.
    /// </summary>
    private bool _ownsDatabase = true;

    private CoachPostgresHarness(
        string databaseName,
        string connectionString,
        TestTimeProvider time,
        CoachPersistenceOptions options,
        IDataProtectionProvider dataProtection)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Time = time;
        Options = options;
        _dataProtection = dataProtection;

        DbOptions = new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                // The coach context keeps its own migrations history table so the coach and
                // application migration sets never see each other's rows. Tests that do not
                // mirror this would migrate a different table than production does, and would
                // report success against a schema no deployment will ever produce.
                npgsql.MigrationsHistoryTable(CoachMigrationsHistoryTable))
            // Deliberately no PendingModelChangesWarning suppression. Production no longer
            // suppresses it for this context, and a harness that did would migrate happily against
            // a model the migrations no longer describe — hiding here exactly the drift the host
            // would refuse to start with.
            .Options;

        Protector = new DataProtectionCoachAgentSessionProtector(
            dataProtection,
            NullLogger<DataProtectionCoachAgentSessionProtector>.Instance);
        ContentProtector = new DataProtectionCoachContentProtector(
            dataProtection,
            NullLogger<DataProtectionCoachContentProtector>.Instance);
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public DbContextOptions<CoachDbContext> DbOptions { get; }

    /// <summary>
    /// Options for the application context over the <em>same</em> database, when the harness was
    /// asked to create its schema. Null otherwise, so a test that needs it fails loudly rather
    /// than quietly running against a database that has no legacy tables in it.
    /// </summary>
    public DbContextOptions<ApplicationDbContext>? ApplicationDbOptions { get; private set; }

    public TestTimeProvider Time { get; }

    public CoachPersistenceOptions Options { get; }

    public ICoachAgentSessionProtector Protector { get; }

    public ICoachContentProtector ContentProtector { get; }

    /// <summary>
    /// Creates the database, applies every coach migration, and returns a ready harness.
    /// </summary>
    /// <param name="label">A short tag mixed into the generated database name, for diagnosis.</param>
    /// <param name="migrate">
    /// False leaves the database empty so a migration test can drive the migrator itself.
    /// </param>
    /// <param name="withApplicationSchema">
    /// True also creates the application schema in the same database, which is what production
    /// looks like: coach state and the legacy activity tables share one PostgreSQL database behind
    /// two contexts. Only a harness built this way can prove anything about a transaction that has
    /// to span both.
    /// </param>
    public static async Task<CoachPostgresHarness> CreateAsync(
        string label,
        DateTimeOffset? start = null,
        CoachPersistenceOptions? options = null,
        IDataProtectionProvider? dataProtection = null,
        bool migrate = true,
        bool withApplicationSchema = false,
        CancellationToken cancellationToken = default)
    {
        var database = await CoachPostgresServer.CreateDatabaseAsync(label, cancellationToken).ConfigureAwait(false);
        var harness = new CoachPostgresHarness(
            database,
            CoachPostgresServer.ConnectionStringFor(database),
            new TestTimeProvider(start ?? new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero)),
            options ?? new CoachPersistenceOptions(),
            dataProtection ?? new EphemeralDataProtectionProvider());

        if (withApplicationSchema)
        {
            // Before the coach migrations, because EnsureCreated declines to do anything once the
            // database has any tables in it at all.
            harness.ApplicationDbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(harness.ConnectionString)
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using var application = harness.NewApplicationContext();
            await application.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (migrate)
        {
            await using var db = harness.NewContext();
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        return harness;
    }

    /// <summary>An application context over this harness's database, on its own connection.</summary>
    public ApplicationDbContext NewApplicationContext() =>
        new(ApplicationDbOptions
            ?? throw new InvalidOperationException(
                "This harness was created without the application schema. Pass withApplicationSchema: true."));

    /// <summary>
    /// A context over its own connection. Tests hold several at once on purpose: that is what
    /// makes a race a real race rather than two objects taking turns on one handle.
    /// </summary>
    public CoachDbContext NewContext()
    {
        var db = new CoachDbContext(DbOptions);
        lock (_contexts)
        {
            _contexts.Add(db);
        }

        return db;
    }

    /// <summary>
    /// Another harness over the same database and clock but a different key ring. This is how a
    /// test stands in for a restarted process, or for a backup restored without its key vault:
    /// the rows are identical, only the protector changed.
    /// </summary>
    public CoachPostgresHarness WithDataProtection(IDataProtectionProvider dataProtection) =>
        new(DatabaseName, ConnectionString, Time, Options, dataProtection) { _ownsDatabase = false };

    public CoachSessionStore NewSessionStore(CoachDbContext db) =>
        new(db, Protector, Microsoft.Extensions.Options.Options.Create(Options), Time, NullLogger<CoachSessionStore>.Instance);

    public CoachUsageStore NewUsageStore(CoachDbContext db) =>
        new(db, Time, NullLogger<CoachUsageStore>.Instance);

    public CoachConversationStore NewConversationStore(CoachDbContext db) =>
        new(db, ContentProtector, Time, NullLogger<CoachConversationStore>.Instance);

    public CoachMessageStore NewMessageStore(CoachDbContext db) =>
        new(db, ContentProtector, Time, NullLogger<CoachMessageStore>.Instance);

    public CoachTurnOperationStore NewTurnOperationStore(CoachDbContext db) =>
        new(db, ContentProtector, Time, NullLogger<CoachTurnOperationStore>.Instance);

    public CoachHistoryExportReader NewExportReader(CoachDbContext db) =>
        new(db, ContentProtector, NullLogger<CoachHistoryExportReader>.Instance);

    public CoachHistoryDeletionContributor NewHistoryDeletionContributor(CoachDbContext db) =>
        new(db, NullLogger<CoachHistoryDeletionContributor>.Instance);

    public CoachMemoryStore NewMemoryStore(
        CoachDbContext db,
        ICoachMemoryChangedNotifier notifier,
        CoachMemoryOptions? options = null) =>
        new(db,
            ContentProtector,
            Time,
            Microsoft.Extensions.Options.Options.Create(options ?? new CoachMemoryOptions { Enabled = true }),
            notifier,
            NullLogger<CoachMemoryStore>.Instance);

    public CoachMemoryContextSelector NewMemorySelector(
        ICoachMemoryStore store,
        CoachMemoryOptions? options = null) =>
        new(store,
            Microsoft.Extensions.Options.Options.Create(options ?? new CoachMemoryOptions { Enabled = true }),
            NullLogger<CoachMemoryContextSelector>.Instance);

    /// <summary>The production advisory-lock cleanup lease, over the supplied context.</summary>
    public PostgresCoachCleanupLease NewCleanupLease(CoachDbContext db) =>
        new(db, NullLogger<PostgresCoachCleanupLease>.Instance);

    /// <summary>Opens a raw ADO connection to this database, for ciphertext and type scans.</summary>
    public async Task<NpgsqlConnection> OpenRawAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Runs a scalar query straight against the database, bypassing EF entirely.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T))!;
    }

    /// <summary>Reads a single string column into a list, for schema introspection assertions.</summary>
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

    /// <summary>Executes a statement straight against the database.</summary>
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        CoachDbContext[] contexts;
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

        if (_dataProtection is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (_ownsDatabase)
        {
            await CoachPostgresServer.DropDatabaseAsync(DatabaseName).ConfigureAwait(false);
        }
    }
}
