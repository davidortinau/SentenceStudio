using Npgsql;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Resolves the PostgreSQL server the coach integration tests run against, and probes it once
/// per process so the whole family can skip cleanly on a machine with no server.
/// </summary>
/// <remarks>
/// <para>
/// The coach schema only ever ships on PostgreSQL. Every other coach persistence test in this
/// assembly runs on SQLite, which is enough for provider-independent behaviour but cannot prove
/// the parts that only exist on the real provider: <c>jsonb</c> columns, the filtered unique
/// index, <c>timestamp with time zone</c> pinning under the host's legacy timestamp switch,
/// session advisory locks, and the fact that a failed statement aborts the enclosing
/// transaction. Those are what this family exists for.
/// </para>
/// <para>
/// The connection string is read from <c>COACH_PG_TEST_CONNECTION</c>, falling back to
/// <c>COACH_DB_CONNECTION</c> so a developer who already points the design-time factory at a
/// scratch server does not have to configure a second variable. It must point at a throwaway
/// server: every test class creates and drops its own database on it.
/// </para>
/// </remarks>
public static class CoachPostgresServer
{
    /// <summary>The variable the tests prefer, so it can differ from the design-time one.</summary>
    public const string PrimaryVariable = "COACH_PG_TEST_CONNECTION";

    /// <summary>The design-time variable, reused when the primary is not set.</summary>
    public const string FallbackVariable = "COACH_DB_CONNECTION";

    /// <summary>Every database this family creates carries this prefix, and only these are ever dropped.</summary>
    public const string DatabasePrefix = "coach_it_";

    private static readonly object Gate = new();
    private static bool _probed;
    private static string? _adminConnectionString;
    private static string? _skipReason;

    static CoachPostgresServer()
    {
        // The API host turns this switch on process-wide for the sync context's SQLite-era
        // DateTime values. The coach model compensates by pinning every DateTime column to
        // `timestamp with time zone`. Testing without the switch would exercise a runtime
        // configuration production never runs in, and would silently pass a pin that is only
        // load-bearing when the switch is on.
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

    /// <summary>The banner reported by the server, captured during the probe.</summary>
    public static string? ServerVersion { get; private set; }

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
            // Every concurrency test opens several independent contexts at once, and a starved
            // pool would look like a store bug rather than a harness one.
            MaxPoolSize = 40,
            Timeout = 15,
            CommandTimeout = 60
        }.ConnectionString;

    /// <summary>
    /// Creates a database dedicated to one test class. The name carries a fresh GUID, so this
    /// can never collide with, or overwrite, a database that already exists.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(string label, CancellationToken cancellationToken = default)
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
    public static async Task DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!name.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to drop '{name}': the coach integration tests only ever drop databases they created.");
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
                ?? Environment.GetEnvironmentVariable(FallbackVariable);

            if (string.IsNullOrWhiteSpace(raw))
            {
                _skipReason =
                    $"No PostgreSQL server configured. Set {PrimaryVariable} (or {FallbackVariable}) to a throwaway server to run the coach provider tests.";
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
                ServerVersion = command.ExecuteScalar() as string ?? "unknown";
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
/// A fact that skips itself when no PostgreSQL server is configured, so the suite stays green on
/// a machine without one while still failing loudly when a server is present and the behaviour
/// is wrong.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        var reason = CoachPostgresServer.SkipReason;
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}

/// <summary>The theory counterpart of <see cref="PostgresFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        var reason = CoachPostgresServer.SkipReason;
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}
