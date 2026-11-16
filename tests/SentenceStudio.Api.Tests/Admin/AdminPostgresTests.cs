using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Api.Tests.Coach.Postgres;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The admin schema on the provider it ships on: the production migration, on a throwaway
/// PostgreSQL database, under the host's legacy timestamp switch.
/// </summary>
/// <remarks>
/// The server probe and the create/drop helpers are <see cref="CoachPostgresServer"/>'s: the same
/// scratch server and the same "only ever drop databases we created" guard.
/// </remarks>
public sealed class AdminPostgresTests : IAsyncLifetime
{
    private string? _database;
    private DbContextOptions<AdminDbContext> _options = default!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _database = await CoachPostgresServer.CreateDatabaseAsync("admin");
        _options = new DbContextOptionsBuilder<AdminDbContext>()
            .UseNpgsql(CoachPostgresServer.ConnectionStringFor(_database), npgsql =>
                npgsql.MigrationsHistoryTable(AdminSchema.MigrationsHistoryTable))
            .Options;

        await using var db = new AdminDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await CoachPostgresServer.DropDatabaseAsync(_database);
        }
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(CoachPostgresServer.ConnectionStringFor(_database!));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    [PostgresFact]
    public async Task The_migration_creates_the_audit_table_under_its_own_history_table()
    {
        (await ScalarAsync<string>(
            "SELECT \"MigrationId\" FROM \"__AdminMigrationsHistory\" ORDER BY 1 LIMIT 1"))
            .Should().Be("20261115224514_InitialAdminSchema");

        (await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory'"))
            .Should().Be(0, "the admin migrations must not write the application's history table");

        (await ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'AdminAuditEntry' AND column_name = 'OccurredAtUtc'"))
            .Should().Be("timestamp with time zone");

        (await ScalarAsync<string>(
            "SELECT is_identity FROM information_schema.columns "
            + "WHERE table_name = 'AdminAuditEntry' AND column_name = 'Id'"))
            .Should().Be("YES");
    }

    [PostgresFact]
    public async Task The_token_table_holds_a_digest_and_times_with_a_zone_and_nowhere_to_put_a_token()
    {
        (await ScalarAsync<string>(
            "SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY column_name) "
            + "FROM information_schema.columns WHERE table_name = 'AdminAgentToken'"))
            .Should().Be(
                "CreatedAtUtc:timestamp with time zone:NO,"
                + "CreatedByStampDigest:character varying:NO,"
                + "CreatedByUserId:character varying:NO,"
                + "ExpiresAtUtc:timestamp with time zone:NO,"
                + "Id:character varying:NO,"
                + "LastUsedAtUtc:timestamp with time zone:YES,"
                + "Name:character varying:NO,"
                + "Permissions:integer:NO,"
                + "RevokedAtUtc:timestamp with time zone:YES,"
                + "SecretHash:character varying:NO");
    }

    [PostgresFact]
    public async Task Migrating_again_changes_nothing()
    {
        await using var db = new AdminDbContext(_options);

        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        await db.Database.MigrateAsync();

        (await ScalarAsync<long>("SELECT count(*) FROM \"__AdminMigrationsHistory\"")).Should().Be(2);
    }

    [PostgresFact]
    public async Task A_row_is_written_started_then_completed_and_reads_back_in_UTC()
    {
        var instant = new DateTimeOffset(2026, 11, 15, 23, 30, 0, TimeSpan.Zero);
        var writer = new AdminAuditWriter(_options, new TestTimeProvider(instant));
        var actor = new AdminActor(AdminActorKind.Admin, Guid.NewGuid().ToString(), null, null, "digest");

        var id = await writer.BeginAsync(
            actor,
            new AdminActionMetadata("learner.words.read", AdminTargetKinds.Learner, "learnerId"),
            "learner-profile-id",
            CancellationToken.None);

        await using (var db = new AdminDbContext(_options))
        {
            var started = await db.AuditEntries.AsNoTracking().SingleAsync(e => e.Id == id);
            started.Outcome.Should().Be(AdminAuditOutcome.Started);
            started.StatusCode.Should().BeNull();
        }

        await writer.CompleteAsync(id, AdminAuditOutcome.Failed, 403, AdminFailureCodes.StepUpRequired, CancellationToken.None);

        // A second ending must not overwrite the first.
        await writer.CompleteAsync(id, AdminAuditOutcome.Succeeded, 200, null, CancellationToken.None);

        await using (var db = new AdminDbContext(_options))
        {
            var row = await db.AuditEntries.AsNoTracking().SingleAsync(e => e.Id == id);
            row.Outcome.Should().Be(AdminAuditOutcome.Failed);
            row.StatusCode.Should().Be(403);
            row.FailureCode.Should().Be(AdminFailureCodes.StepUpRequired);
            row.ActorId.Should().Be(actor.Id);
            row.TargetKind.Should().Be(AdminTargetKinds.Learner);
            row.TargetId.Should().Be("learner-profile-id");
            row.OccurredAtUtc.Should().Be(instant.UtcDateTime);
            row.OccurredAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        }
    }

    [PostgresFact]
    public async Task The_real_host_writes_its_audit_rows_to_PostgreSQL()
    {
        using var factory = new AdminApiFactory(CoachPostgresServer.ConnectionStringFor(_database!));
        var admin = await factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = factory.CreateClientFor(admin);

        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/admin/audit")).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await factory.ReadAuditAsync();
        rows.Select(r => r.Action).Should().Equal(AdminActions.SessionRead, AdminActions.AuditRead);
        rows.Should().OnlyContain(r => r.Outcome == AdminAuditOutcome.Succeeded && r.ActorId == admin.Id);
    }

    [PostgresFact]
    public async Task Rolling_the_migrations_back_removes_only_the_admin_tables()
    {
        await using var db = new AdminDbContext(_options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);

        // Back one step: the token table goes and the audit log stays.
        await migrator.MigrateAsync("20261115224514_InitialAdminSchema");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('AdminAuditEntry', 'AdminAgentToken')"))
            .Should().Be(1);

        await migrator.MigrateAsync("0");

        (await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('AdminAuditEntry', 'AdminAgentToken')"))
            .Should().Be(0);

        await migrator.MigrateAsync();
        (await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('AdminAuditEntry', 'AdminAgentToken')"))
            .Should().Be(2);
    }
}
