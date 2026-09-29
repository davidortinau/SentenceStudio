using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace SentenceStudio.WebApp.Tests.Infrastructure;

/// <summary>
/// Accepts only a per-run, explicitly identified loopback PostgreSQL test cluster.
/// </summary>
/// <remarks>
/// <para>
/// These tests boot the real <c>SentenceStudio.WebApp</c> pipeline after the fixture migrates its
/// newly created database. There is no in-memory substitute that would still prove
/// what the family exists to prove — Identity cookie sign-in against the real user store — so a
/// server is required rather than optional.
/// </para>
/// <para>
/// No design-time or coach connection variable is considered. The test cluster must advertise the
/// caller's fresh ownership ID as its server-side cluster_name before any database is created.
/// Test databases are not dropped: cleanup belongs to the explicitly disposable test environment.
/// </para>
/// </remarks>
public static class WebAppPostgresServer
{
    /// <summary>The variable this family prefers.</summary>
    public const string PrimaryVariable = "WEBAPP_PG_TEST_CONNECTION";

    /// <summary>Fresh per-run UUID used to identify the disposable server itself.</summary>
    public const string OwnershipVariable = "WEBAPP_PG_TEST_OWNER_ID";

    /// <summary>Every database this family creates carries this prefix.</summary>
    public const string DatabasePrefix = "webapp_it_";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, TestDatabase> CreatedDatabases = new(StringComparer.Ordinal);

    private sealed record TestDatabase(string AdminConnectionString, string OwnerId);

    static WebAppPostgresServer()
    {
        // Program.cs turns this switch on process-wide for the sync context's SQLite-era DateTime
        // values. The test host runs the same Program.cs, so setting it here only makes the
        // ordering explicit for code that touches Npgsql before the host is built.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    /// <summary>Skip unless a specific disposable test server is explicitly selected.</summary>
    public static string? SkipReason
    {
        get
        {
            return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PrimaryVariable)) ||
                   string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OwnershipVariable))
                ? $"Set both {PrimaryVariable} and {OwnershipVariable} for an owned disposable PostgreSQL test cluster."
                : null;
        }
    }

    /// <summary>Connects only to a database created by this fixture in this process.</summary>
    public static string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(GetCreatedDatabase(database).AdminConnectionString)
        {
            Database = database,
            MaxPoolSize = 40,
            Timeout = 15,
            CommandTimeout = 120
        }.ConnectionString;

    /// <summary>
    /// Creates a database dedicated to one test class. Its name retains the full GUID within
    /// PostgreSQL's identifier limit; CREATE DATABASE refuses an existing name.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(
        string label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var (adminConnectionString, ownerId) = ReadConfiguration();
        var name = $"{DatabasePrefix}{Sanitize(label)}_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AssertClusterIdentityAsync(connection, "postgres", ownerId, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        lock (Gate)
        {
            CreatedDatabases.Add(name, new TestDatabase(adminConnectionString, ownerId));
        }

        return name;
    }

    /// <summary>Attests the open migration or seed connection, not an earlier probe connection.</summary>
    internal static Task AssertOwnedDatabaseAsync(
        DbConnection connection, string database, CancellationToken cancellationToken = default) =>
        AssertClusterIdentityAsync(
            connection, database, GetCreatedDatabase(database).OwnerId, cancellationToken);

    /// <summary>
    /// Rejects unowned hosts before even probing their server identity.
    /// </summary>
    internal static string? ValidateConfiguration(string? connectionString, string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || !Guid.TryParseExact(ownerId, "N", out _))
        {
            return "An explicit connection and fresh per-run owner UUID are required.";
        }

        try
        {
            var parsed = new NpgsqlConnectionStringBuilder(connectionString);
            if (parsed.Host is not ("localhost" or "127.0.0.1" or "::1") ||
                parsed.Database != "postgres")
            {
                return "Only a loopback connection to the disposable cluster's postgres maintenance database is allowed.";
            }
        }
        catch (ArgumentException)
        {
            return "The disposable PostgreSQL test connection is invalid.";
        }

        return null;
    }

    private static string Sanitize(string label)
    {
        var take = Math.Min(label.Length, 20);
        var buffer = new char[take];
        for (var i = 0; i < take; i++)
        {
            var c = char.ToLowerInvariant(label[i]);
            buffer[i] = c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_';
        }

        return new string(buffer);
    }

    private static TestDatabase GetCreatedDatabase(string database)
    {
        lock (Gate)
        {
            return CreatedDatabases.TryGetValue(database, out var created)
                ? created
                : throw new InvalidOperationException(
                    "The WebApp fixture can only connect to databases it created in this process.");
        }
    }

    private static (string ConnectionString, string OwnerId) ReadConfiguration()
    {
        var raw = Environment.GetEnvironmentVariable(PrimaryVariable);
        var ownerId = Environment.GetEnvironmentVariable(OwnershipVariable);
        if (ValidateConfiguration(raw, ownerId) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        return (new NpgsqlConnectionStringBuilder(raw!) { Timeout = 5 }.ConnectionString, ownerId!);
    }

    private static async Task AssertClusterIdentityAsync(
        DbConnection connection, string database, string ownerId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('cluster_name'), current_database()";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != $"webapp-it-{ownerId}" ||
            reader.GetString(1) != database)
        {
            throw new InvalidOperationException(
                "PostgreSQL test connection is not on the owned disposable cluster and database.");
        }
    }
}

/// <summary>Checks the actual connection for every EF migration write, including a new one.</summary>
internal sealed class WebAppPostgresMigrationGuard(string database) : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await WebAppPostgresServer.AssertOwnedDatabaseAsync(
            command.Connection ?? throw new InvalidOperationException("Migration command has no connection."),
            database,
            cancellationToken).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// A fact that skips when no owned test cluster was selected. Explicit misconfiguration fails.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WebAppPostgresFactAttribute : FactAttribute
{
    public WebAppPostgresFactAttribute()
    {
        var reason = WebAppPostgresServer.SkipReason;
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}

/// <summary>The theory counterpart of <see cref="WebAppPostgresFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WebAppPostgresTheoryAttribute : TheoryAttribute
{
    public WebAppPostgresTheoryAttribute()
    {
        var reason = WebAppPostgresServer.SkipReason;
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}
