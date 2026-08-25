using Npgsql;

namespace SentenceStudio.WebApp.Tests.Infrastructure;

/// <summary>
/// Resolves the PostgreSQL server the WebApp integration tests run against, and probes it once per
/// process so the family skips cleanly on a machine with no server.
/// </summary>
/// <remarks>
/// <para>
/// These tests boot the real <c>SentenceStudio.WebApp</c> pipeline, and that pipeline migrates
/// <c>ApplicationDbContext</c> on startup. There is no in-memory substitute that would still prove
/// what the family exists to prove — Identity cookie sign-in against the real user store — so a
/// server is required rather than optional.
/// </para>
/// <para>
/// The connection string is read from <c>WEBAPP_PG_TEST_CONNECTION</c>, falling back to the two
/// variables the coach PostgreSQL family already uses so a developer configures one throwaway
/// server, not three. Every test class creates and drops its own database on it; nothing here ever
/// touches an existing database.
/// </para>
/// </remarks>
public static class WebAppPostgresServer
{
    /// <summary>The variable this family prefers.</summary>
    public const string PrimaryVariable = "WEBAPP_PG_TEST_CONNECTION";

    /// <summary>The coach test variable, reused when the primary is not set.</summary>
    public const string CoachTestVariable = "COACH_PG_TEST_CONNECTION";

    /// <summary>The design-time variable, reused when neither of the above is set.</summary>
    public const string DesignTimeVariable = "COACH_DB_CONNECTION";

    /// <summary>Every database this family creates carries this prefix, and only these are dropped.</summary>
    public const string DatabasePrefix = "webapp_it_";

    private static readonly object Gate = new();
    private static bool _probed;
    private static string? _adminConnectionString;
    private static string? _skipReason;

    static WebAppPostgresServer()
    {
        // Program.cs turns this switch on process-wide for the sync context's SQLite-era DateTime
        // values. The test host runs the same Program.cs, so setting it here only makes the
        // ordering explicit for code that touches Npgsql before the host is built.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    /// <summary>Why the family is skipping, or null when a server is reachable.</summary>
    public static string? SkipReason
    {
        get
        {
            Probe();
            return _skipReason;
        }
    }

    /// <summary>The connection string for the maintenance database on the scratch server.</summary>
    public static string AdminConnectionString
    {
        get
        {
            Probe();
            return _adminConnectionString
                ?? throw new InvalidOperationException(_skipReason ?? "No PostgreSQL server configured.");
        }
    }

    /// <summary>Rewrites the admin connection string to point at <paramref name="database"/>.</summary>
    public static string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = database,
            MaxPoolSize = 40,
            Timeout = 15,
            CommandTimeout = 120
        }.ConnectionString;

    /// <summary>
    /// Creates a database dedicated to one test class. The name carries a fresh GUID, so this can
    /// never collide with, or overwrite, a database that already exists.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(
        string label, CancellationToken cancellationToken = default)
    {
        var name = $"{DatabasePrefix}{Sanitize(label)}_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return name;
    }

    /// <summary>
    /// Drops a database this family created. Refuses anything that is not one of ours, so a
    /// mistyped name can never take out a database that matters.
    /// </summary>
    public static async Task DropDatabaseAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (!name.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to drop '{name}': these tests only ever drop databases they created.");
        }

        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Sanitize(string label)
    {
        var take = Math.Min(label.Length, 20);
        var buffer = new char[take];
        for (var i = 0; i < take; i++)
        {
            var c = label[i];
            buffer[i] = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_';
        }

        return new string(buffer);
    }

    private static void Probe()
    {
        lock (Gate)
        {
            if (_probed)
            {
                return;
            }

            _probed = true;

            var raw = Environment.GetEnvironmentVariable(PrimaryVariable)
                ?? Environment.GetEnvironmentVariable(CoachTestVariable)
                ?? Environment.GetEnvironmentVariable(DesignTimeVariable);

            if (string.IsNullOrWhiteSpace(raw))
            {
                _skipReason =
                    $"No PostgreSQL server configured. Set {PrimaryVariable} (or {CoachTestVariable}) "
                    + "to a throwaway server to run the WebApp integration tests.";
                return;
            }

            try
            {
                var builder = new NpgsqlConnectionStringBuilder(raw) { Timeout = 5 };
                if (string.IsNullOrWhiteSpace(builder.Database))
                {
                    builder.Database = "postgres";
                }

                using var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT version()";
                command.ExecuteScalar();
                _adminConnectionString = builder.ConnectionString;
            }
            catch (Exception ex)
            {
                _skipReason = $"PostgreSQL server unreachable: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}

/// <summary>
/// A fact that skips itself when no PostgreSQL server is configured, so the suite stays green on a
/// machine without one while still failing loudly when a server is present and the behaviour is
/// wrong.
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
