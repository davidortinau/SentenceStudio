using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// The feedback migrations applied, reverted, and applied again against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// <c>FeedbackModelMigrationParityTests</c> proves the model and the migrations agree; it never
/// opens a connection, so it cannot prove the SQL runs. This family is the other half, and the
/// round trip is the part that matters. <c>Up</c> alone is exercised by every other PostgreSQL test
/// in the suite simply by starting; <c>Down</c> is exercised by nobody until the first rollback,
/// which is the worst possible moment to discover that a generated <c>DropTable</c> refers to a
/// name the <c>Up</c> did not create, or that re-applying leaves a duplicate index.
/// </para>
/// <para>
/// The history table is asserted explicitly rather than assumed. Three migration sets share one
/// physical database here, and a feedback migration that recorded itself in
/// <c>__EFMigrationsHistory</c> would appear to work — right up until the application migrations
/// read a row they did not write and concluded they were already applied.
/// </para>
/// </remarks>
public sealed class FeedbackMigrationRoundTripPostgresTests : IAsyncLifetime
{
    private const string MigrationId = "20260822002726_InitialFeedbackSchema";

    private FeedbackPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        // Unmigrated: this family drives the migrator itself.
        _harness = await FeedbackPostgresHarness.CreateAsync("migrate", migrate: false);
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// Up creates the schema, Down removes it, and Up creates it again.
    /// </summary>
    /// <remarks>
    /// The second Up is the assertion with teeth. A <c>Down</c> that drops the tables but leaves an
    /// index, a sequence, or a constraint behind passes a "tables are gone" check and then fails on
    /// re-apply with a duplicate-object error — which in a real rollback is a deployment that can
    /// go neither forward nor back.
    /// </remarks>
    [PostgresFact]
    public async Task The_feedback_schema_survives_a_full_down_and_up_round_trip()
    {
        await MigrateToLatestAsync();
        await AssertSchemaPresentAsync();
        (await AppliedMigrationsAsync()).Should().Contain(MigrationId);

        await MigrateToAsync(Migration.InitialDatabase);
        await AssertSchemaAbsentAsync();
        (await AppliedMigrationsAsync()).Should().BeEmpty(
            "a reverted migration must not still claim to be applied");

        await MigrateToLatestAsync();
        await AssertSchemaPresentAsync();
        (await AppliedMigrationsAsync()).Should().ContainSingle().Which.Should().Be(MigrationId);
    }

    /// <summary>
    /// The round trip leaves a schema the production stores can actually use.
    /// </summary>
    /// <remarks>
    /// "The tables came back" is not the same claim as "the constraints came back". A re-applied
    /// schema missing its primary key would pass every structural check above and silently lose the
    /// exactly-once guarantee — the claim would stop being arbitrated and two submissions would
    /// both insert.
    /// </remarks>
    [PostgresFact]
    public async Task After_a_round_trip_the_ledger_still_arbitrates_a_duplicate_claim()
    {
        await MigrateToLatestAsync();
        await MigrateToAsync(Migration.InitialDatabase);
        await MigrateToLatestAsync();

        const string owner = "user-migrate-roundtrip";
        const string jti = "roundtrip-jti";

        await using var first = _harness.NewContext();
        var wonFirst = await _harness.NewLedger(first)
            .TryClaimAsync(FeedbackTestData.Claim(jti, owner));

        wonFirst.Outcome.Should().Be(FeedbackClaimOutcome.Won);

        // A second context on a second connection, exactly as a second replica would be.
        await using var second = _harness.NewContext();
        var wonSecond = await _harness.NewLedger(second)
            .TryClaimAsync(FeedbackTestData.Claim(jti, owner));

        wonSecond.Outcome.Should().NotBe(
            FeedbackClaimOutcome.Won,
            "the primary key restored by the re-applied migration is what makes the claim exclusive");

        await using var check = _harness.NewContext();
        (await check.FeedbackSubmissions.CountAsync(s => s.Jti == jti)).Should().Be(1);
    }

    /// <summary>
    /// The rate window's composite key survives too.
    /// </summary>
    /// <remarks>
    /// Its uniqueness is what makes "create this owner's first window" a decision the database
    /// makes rather than a race the limiter loses.
    /// </remarks>
    [PostgresFact]
    public async Task After_a_round_trip_the_rate_window_still_has_its_composite_key()
    {
        await MigrateToLatestAsync();
        await MigrateToAsync(Migration.InitialDatabase);
        await MigrateToLatestAsync();

        var keyColumns = await _harness.StringsAsync(
            """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(i.indkey)
            WHERE t.relname = 'FeedbackRateWindow' AND i.indisprimary
            ORDER BY a.attname
            """);

        keyColumns.Should().BeEquivalentTo(["Kind", "UserProfileId"]);
    }

    // ------------------------------------------------------------------------ history table

    /// <summary>
    /// The feedback migrations record themselves in their own history table, and nowhere else.
    /// </summary>
    /// <remarks>
    /// The negative half is the important one. Writing into <c>__EFMigrationsHistory</c> would put
    /// a feedback migration id where the application's migrator reads, and a shared history table
    /// is how one migration set concludes another's work is its own.
    /// </remarks>
    [PostgresFact]
    public async Task The_feedback_migrations_record_themselves_in_their_own_history_table()
    {
        await MigrateToLatestAsync();

        var tables = await _harness.StringsAsync(
            """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name LIKE '%MigrationsHistory%'
            ORDER BY table_name
            """);

        tables.Should().BeEquivalentTo([FeedbackSchema.MigrationsHistoryTable]);
        tables.Should().NotContain("__EFMigrationsHistory");
        tables.Should().NotContain("__CoachMigrationsHistory");

        (await AppliedMigrationsAsync()).Should().ContainSingle().Which.Should().Be(MigrationId);
    }

    /// <summary>
    /// The migration is idempotent at the migrator level: applying it twice is a no-op.
    /// </summary>
    /// <remarks>
    /// Every replica runs <c>MigrateAsync</c> on startup, so "already applied" is the normal case
    /// and has to be free. A history table that were not being consulted would show up here as a
    /// duplicate-object failure on the second call.
    /// </remarks>
    [PostgresFact]
    public async Task Applying_the_migration_twice_is_a_no_op()
    {
        await MigrateToLatestAsync();
        await MigrateToLatestAsync();

        (await AppliedMigrationsAsync()).Should().ContainSingle();
        await AssertSchemaPresentAsync();
    }

    /// <summary>Down leaves the history table itself in place, holding no rows.</summary>
    /// <remarks>
    /// EF's own behaviour, asserted so a future change to it is noticed here rather than in a
    /// rollback: the bookkeeping table survives so the next Up has somewhere to record itself.
    /// </remarks>
    [PostgresFact]
    public async Task Down_empties_the_history_table_without_dropping_it()
    {
        await MigrateToLatestAsync();
        await MigrateToAsync(Migration.InitialDatabase);

        var tables = await _harness.StringsAsync(
            $"""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = '{FeedbackSchema.MigrationsHistoryTable}'
            """);

        tables.Should().ContainSingle();
        (await AppliedMigrationsAsync()).Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------- helpers

    private async Task MigrateToLatestAsync()
    {
        await using var db = _harness.NewContext();
        await db.Database.MigrateAsync();
    }

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var db = _harness.NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private async Task<List<string>> AppliedMigrationsAsync()
    {
        await using var db = _harness.NewContext();
        return (await db.Database.GetAppliedMigrationsAsync()).ToList();
    }

    private async Task AssertSchemaPresentAsync()
    {
        var tables = await FeedbackTablesAsync();
        tables.Should().BeEquivalentTo(["FeedbackRateWindow", "FeedbackSubmission"]);

        var indexes = await _harness.StringsAsync(
            """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'public' AND tablename IN ('FeedbackSubmission', 'FeedbackRateWindow')
            ORDER BY indexname
            """);

        indexes.Should().BeEquivalentTo([
            "IX_FeedbackRateWindow_UpdatedAtUtc",
            "IX_FeedbackSubmission_Status",
            "IX_FeedbackSubmission_TokenExpiresAtUtc",
            "IX_FeedbackSubmission_UserProfileId",
            "PK_FeedbackRateWindow",
            "PK_FeedbackSubmission"
        ]);

        // Pinned because the API host turns on Npgsql's legacy timestamp switch process-wide. A
        // column that came back as `timestamp without time zone` would make every insert fail on a
        // type mismatch, and the round trip is exactly where such a drift would be introduced.
        var timestampTypes = await _harness.StringsAsync(
            """
            SELECT DISTINCT data_type FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name IN ('FeedbackSubmission', 'FeedbackRateWindow')
              AND data_type LIKE 'timestamp%'
            """);

        timestampTypes.Should().BeEquivalentTo(["timestamp with time zone"]);
    }

    private async Task AssertSchemaAbsentAsync()
    {
        (await FeedbackTablesAsync()).Should().BeEmpty("Down must remove everything Up created");

        // Scoped to the two tables Up created. A bare '%Feedback%' match would also catch
        // PK___FeedbackMigrationsHistory, which is EF's own bookkeeping and is supposed to survive
        // a Down — failing on it would be the test misreading correct behaviour as a leak.
        var leftovers = await _harness.StringsAsync(
            """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename IN ('FeedbackSubmission', 'FeedbackRateWindow')
            """);

        leftovers.Should().BeEmpty(
            "an index left behind by Down fails the next Up with a duplicate-object error");

        // Nothing else Up created may survive either — a sequence or a constraint left behind is
        // the same duplicate-object failure wearing a different name.
        var relations = await _harness.StringsAsync(
            """
            SELECT c.relname FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relname LIKE '%Feedback%'
              AND c.relname NOT LIKE '%MigrationsHistory%'
            """);

        relations.Should().BeEmpty();
    }

    private Task<List<string>> FeedbackTablesAsync() =>
        _harness.StringsAsync(
            """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name IN ('FeedbackSubmission', 'FeedbackRateWindow')
            ORDER BY table_name
            """);
}
