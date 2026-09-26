using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Data;
using SentenceStudio.Data.AppOperations;

namespace SentenceStudio.Api.Tests.AppOperations;

internal static class ApplicationOperationPostgresServer
{
    internal const string ConnectionVariable = "APP_OPERATION_PG_TEST_CONNECTION";
    private const string DatabasePrefix = "appop_it_";
    private static readonly object Gate = new();
    private static bool _probed;
    private static string? _adminConnectionString;
    private static string? _skipReason;

    static ApplicationOperationPostgresServer()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    internal static string? SkipReason
    {
        get
        {
            Probe();
            return _skipReason;
        }
    }

    internal static async Task<(string Name, string ConnectionString)> CreateDatabaseAsync(
        string label,
        CancellationToken cancellationToken = default)
    {
        Probe();
        var admin = _adminConnectionString
            ?? throw new InvalidOperationException(_skipReason ?? "No disposable database server is configured.");
        var safeLabel = new string(label
            .Take(20)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_')
            .ToArray());
        var name = $"{DatabasePrefix}{safeLabel}_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var builder = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = name,
            MaxPoolSize = 40,
            Timeout = 15,
            CommandTimeout = 60
        };
        return (name, builder.ConnectionString);
    }

    internal static async Task DropDatabaseAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!name.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to drop '{name}': it was not created by this test family.");
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            var raw = Environment.GetEnvironmentVariable(ConnectionVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                _skipReason =
                    $"No disposable PostgreSQL server configured. Set {ConnectionVariable} to run these provider tests.";
                return;
            }

            try
            {
                var builder = new NpgsqlConnectionStringBuilder(raw) { Timeout = 5 };
                builder.Database = string.IsNullOrWhiteSpace(builder.Database)
                    ? "postgres"
                    : builder.Database;
                using var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                command.ExecuteScalar();
                _adminConnectionString = builder.ConnectionString;
            }
            catch (Exception exception)
            {
                _skipReason =
                    $"Disposable PostgreSQL server unavailable: {exception.GetType().Name}: {exception.Message}";
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class ApplicationOperationPostgresFactAttribute : FactAttribute
{
    public ApplicationOperationPostgresFactAttribute()
    {
        Skip = ApplicationOperationPostgresServer.SkipReason;
    }
}

internal sealed class ApplicationOperationPostgresHarness : IAsyncDisposable
{
    private ApplicationOperationPostgresHarness(string databaseName, string connectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Protector = new DataProtectionApplicationOperationContentProtector(
            new EphemeralDataProtectionProvider());
    }

    internal string DatabaseName { get; }

    internal string ConnectionString { get; }

    internal IApplicationOperationContentProtector Protector { get; }

    internal static async Task<ApplicationOperationPostgresHarness> CreateAsync(
        string label,
        bool migrate = true)
    {
        var database = await ApplicationOperationPostgresServer.CreateDatabaseAsync(label);
        var harness = new ApplicationOperationPostgresHarness(database.Name, database.ConnectionString);
        if (migrate)
        {
            await using var db = harness.NewContext();
            await db.Database.MigrateAsync();
        }

        return harness;
    }

    internal ApplicationDbContext NewContext(params IInterceptor[] interceptors) =>
        new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(ConnectionString)
                .AddInterceptors(interceptors)
                .Options);

    internal ApplicationDbContext NewRetryingContext(params IInterceptor[] interceptors) =>
        new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(
                    ConnectionString,
                    npgsql => npgsql.EnableRetryOnFailure(
                        maxRetryCount: 1,
                        maxRetryDelay: TimeSpan.FromMilliseconds(1),
                        errorCodesToAdd: null))
                .AddInterceptors(interceptors)
                .Options);

    internal EfApplicationOperationStore NewStore(ApplicationDbContext db) =>
        new(db, Protector);

    public async ValueTask DisposeAsync()
    {
        await ApplicationOperationPostgresServer.DropDatabaseAsync(DatabaseName);
    }
}
