using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Drives the coach migrations forward and backward against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite coach tests call <c>EnsureCreated</c>, which builds the schema from the model and
/// never touches a migration file. That is fine for store behaviour and useless for deployment
/// safety: it cannot tell you whether <c>Up</c> runs on the provider that will actually run it,
/// whether <c>Down</c> is reversible, or whether an operator who rolls a release back loses the
/// rows that were there before the release. These tests answer exactly those questions.
/// </para>
/// <para>
/// Legacy rows are seeded at the first migration, before the history, memory, and operation-id
/// migrations exist, and their checksum is compared at every step. A migration that rewrites,
/// truncates, or re-types pre-existing coach data fails here rather than in production.
/// </para>
/// </remarks>
public sealed class CoachPostgresMigrationTests : IAsyncLifetime
{
    private const string Initial = "20260815030125_InitialCoachSchema";
    private const string History = "20260817023329_AddCoachConversationHistory";
    private const string Memory = "20260818012952_AddCoachMemoryFacts";
    private const string OperationId = "20260818023114_AddCoachPlanRevisionOperationId";

    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        // No migrate: these tests are the migrator.
        _harness = await CoachPostgresHarness.CreateAsync("migrations", migrate: false);
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Migrations_apply_in_order_and_record_themselves_in_the_coach_history_table()
    {
        await MigrateToAsync(Initial);
        (await AppliedAsync()).Should().Equal(Initial);

        await MigrateToAsync(History);
        (await AppliedAsync()).Should().Equal(Initial, History);

        await MigrateToAsync(Memory);
        (await AppliedAsync()).Should().Equal(Initial, History, Memory);

        await MigrateToAsync(OperationId);
        (await AppliedAsync()).Should().Equal(Initial, History, Memory, OperationId);

        // The coach context keeps its own history table so a coach rollback can never be
        // confused with an application-schema rollback.
        var historyTable = await _harness.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_name = '__CoachMigrationsHistory'");
        historyTable.Should().Be(1);
    }

    [PostgresFact]
    public async Task Legacy_rows_survive_the_whole_up_down_up_cycle_byte_for_byte()
    {
        await MigrateToAsync(Initial);
        await SeedLegacyAsync();

        var baseline = await LegacyChecksumAsync();

        foreach (var target in new[] { History, Memory, OperationId })
        {
            await MigrateToAsync(target);
            (await LegacyChecksumAsync()).Should().BeEquivalentTo(
                baseline,
                $"applying {target} must not disturb rows written before it existed");
        }

        // Down, one migration at a time, exactly as an operator rolling back release by release.
        foreach (var target in new[] { Memory, History, Initial })
        {
            await MigrateToAsync(target);
            (await LegacyChecksumAsync()).Should().BeEquivalentTo(
                baseline,
                $"reverting to {target} must not disturb rows written before any of it existed");
        }

        (await AppliedAsync()).Should().Equal(Initial);

        // And forward again: a re-deploy after a rollback must be a no-drama replay.
        await MigrateToAsync(OperationId);
        (await AppliedAsync()).Should().Equal(Initial, History, Memory, OperationId);
        (await LegacyChecksumAsync()).Should().BeEquivalentTo(baseline);
    }

    [PostgresFact]
    public async Task History_and_memory_tables_appear_and_disappear_with_their_own_migrations()
    {
        await MigrateToAsync(Initial);
        (await TablesAsync()).Should().BeEquivalentTo(
            "CoachPlanRevision", "CoachSession", "CoachUsage", "__CoachMigrationsHistory");

        await MigrateToAsync(History);
        (await TablesAsync()).Should().Contain(new[] { "CoachConversation", "CoachMessage", "CoachTurnOperation" });
        (await TablesAsync()).Should().NotContain("CoachMemoryFact");

        await MigrateToAsync(Memory);
        (await TablesAsync()).Should().Contain("CoachMemoryFact");

        await MigrateToAsync(History);
        (await TablesAsync()).Should().NotContain("CoachMemoryFact");

        await MigrateToAsync(Initial);
        (await TablesAsync()).Should().BeEquivalentTo(
            "CoachPlanRevision", "CoachSession", "CoachUsage", "__CoachMigrationsHistory");
    }

    [PostgresFact]
    public async Task OperationId_migration_adds_only_its_column_and_index_and_removes_only_those()
    {
        await MigrateToAsync(Memory);

        (await ColumnExistsAsync("CoachPlanRevision", "OperationId")).Should().BeFalse();
        (await IndexExistsAsync("IX_CoachPlanRevision_UserProfileId_OperationId")).Should().BeFalse();

        await MigrateToAsync(OperationId);

        (await ColumnExistsAsync("CoachPlanRevision", "OperationId")).Should().BeTrue();
        (await IndexExistsAsync("IX_CoachPlanRevision_UserProfileId_OperationId")).Should().BeTrue();

        // Nullable, so existing revisions do not need a backfill to satisfy the new column.
        var nullable = await _harness.ScalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name='CoachPlanRevision' AND column_name='OperationId'");
        nullable.Should().Be("YES");

        await MigrateToAsync(Memory);

        (await ColumnExistsAsync("CoachPlanRevision", "OperationId")).Should().BeFalse();
        (await IndexExistsAsync("IX_CoachPlanRevision_UserProfileId_OperationId")).Should().BeFalse();
        (await IndexExistsAsync("IX_CoachPlanRevision_UserProfileId_SessionId_RevisionNumber")).Should().BeTrue(
            "the revision-number uniqueness predates the operation-id migration and must outlive its revert");
    }

    [PostgresFact]
    public async Task Head_schema_carries_every_index_the_stores_depend_on()
    {
        await MigrateToAsync(OperationId);

        var indexes = await IndexDefinitionsAsync();

        // The gap-free ledger backstop.
        indexes.Should().ContainKey("IX_CoachMessage_UserProfileId_ConversationId_Sequence");
        indexes["IX_CoachMessage_UserProfileId_ConversationId_Sequence"].Should().Contain("UNIQUE");

        // The idempotency key.
        indexes.Should().ContainKey("IX_CoachTurnOperation_UserProfileId_ConversationId_KeyDigest");
        indexes["IX_CoachTurnOperation_UserProfileId_ConversationId_KeyDigest"].Should().Contain("UNIQUE");

        // Crash recovery scans expired leases, so it needs its own index.
        indexes.Should().ContainKey("IX_CoachTurnOperation_LeaseExpiresAt");

        // The composite alternate key children point at, which is what makes a cross-owner row
        // unrepresentable rather than merely unreachable.
        indexes.Should().ContainKey("AK_CoachConversation_UserProfileId_Id");
        indexes["AK_CoachConversation_UserProfileId_Id"].Should().Contain("UNIQUE");

        // One active fact per owner, kind, and scope — and only Active rows participate.
        indexes.Should().ContainKey("UX_CoachMemoryFact_UserProfileId_Kind_ScopeKey_Active");
        var filtered = indexes["UX_CoachMemoryFact_UserProfileId_Kind_ScopeKey_Active"];
        filtered.Should().Contain("UNIQUE");
        filtered.Should().Contain("WHERE (\"Status\" = 1)");

        indexes.Should().ContainKey("IX_CoachPlanRevision_UserProfileId_OperationId");
        indexes["IX_CoachPlanRevision_UserProfileId_OperationId"].Should().Contain("UNIQUE");

        indexes.Should().ContainKey("IX_CoachUsage_UserProfileId_LocalDate");
        indexes["IX_CoachUsage_UserProfileId_LocalDate"].Should().Contain("UNIQUE");
    }

    [PostgresFact]
    public async Task Head_schema_uses_jsonb_for_documents_and_never_for_ciphertext()
    {
        await MigrateToAsync(OperationId);

        var jsonb = await _harness.StringsAsync(
            "SELECT table_name || '.' || column_name FROM information_schema.columns " +
            "WHERE table_schema='public' AND data_type='jsonb' ORDER BY 1");

        jsonb.Should().BeEquivalentTo(
            "CoachPlanRevision.AcceptedConstraintDeltaJson",
            "CoachPlanRevision.AfterPlanSnapshotJson",
            "CoachPlanRevision.BeforePlanSnapshotJson",
            "CoachSession.ActiveConstraintsJson",
            "CoachSession.PendingSuggestionDeltaJson");

        // Ciphertext is opaque bytes in base64. Typing it as jsonb would make the database try to
        // parse it, and would leak structure the protector exists to hide.
        var protectedColumns = await _harness.StringsAsync(
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_schema='public' AND column_name LIKE 'Protected%'");
        protectedColumns.Should().NotBeEmpty();
        protectedColumns.Should().OnlyContain(t => t == "text" || t == "character varying");
    }

    [PostgresFact]
    public async Task Every_coach_timestamp_column_is_pinned_to_timestamptz()
    {
        await MigrateToAsync(OperationId);

        // The host turns on Npgsql's legacy timestamp behaviour for the sync context. Without the
        // explicit pin in CoachDbContext, that switch would remap these columns to
        // `timestamp without time zone` and every coach insert would fail on a type mismatch.
        var naive = await _harness.StringsAsync(
            "SELECT table_name || '.' || column_name FROM information_schema.columns " +
            "WHERE table_schema='public' AND table_name LIKE 'Coach%' " +
            "AND data_type = 'timestamp without time zone' ORDER BY 1");

        naive.Should().BeEmpty("CoachDbContext pins every DateTime column to timestamp with time zone");

        var aware = await _harness.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema='public' AND table_name LIKE 'Coach%' " +
            "AND data_type = 'timestamp with time zone'");
        aware.Should().BeGreaterThan(20);
    }

    [PostgresFact]
    public async Task Composite_foreign_keys_cascade_from_the_conversation_and_bind_the_owner()
    {
        await MigrateToAsync(OperationId);

        var foreignKeys = await _harness.StringsAsync(
            """
            SELECT child.relname || ': ' ||
                   (SELECT string_agg(att.attname, ',' ORDER BY k.ord)
                      FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k.attnum) ||
                   ' -> ' || parent.relname || ' (' ||
                   CASE con.confdeltype WHEN 'c' THEN 'CASCADE' WHEN 'a' THEN 'NO ACTION'
                        WHEN 'r' THEN 'RESTRICT' WHEN 'n' THEN 'SET NULL' ELSE con.confdeltype::text END || ')'
            FROM pg_constraint con
            JOIN pg_class child ON child.oid = con.conrelid
            JOIN pg_class parent ON parent.oid = con.confrelid
            JOIN pg_namespace ns ON ns.oid = child.relnamespace
            WHERE con.contype = 'f' AND ns.nspname = 'public'
            ORDER BY 1
            """);

        foreignKeys.Should().BeEquivalentTo(
            "CoachMessage: UserProfileId,ConversationId -> CoachConversation (CASCADE)",
            "CoachTurnOperation: UserProfileId,ConversationId -> CoachConversation (CASCADE)");
    }

    private async Task MigrateToAsync(string target)
    {
        await using var db = _harness.NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(target);
    }

    private Task<List<string>> AppliedAsync() =>
        _harness.StringsAsync("SELECT \"MigrationId\" FROM \"__CoachMigrationsHistory\" ORDER BY \"MigrationId\"");

    private Task<List<string>> TablesAsync() =>
        _harness.StringsAsync("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY 1");

    private async Task<bool> ColumnExistsAsync(string table, string column) =>
        await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM information_schema.columns WHERE table_name='{table}' AND column_name='{column}'") == 1;

    private async Task<bool> IndexExistsAsync(string index) =>
        await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_indexes WHERE schemaname='public' AND indexname='{index}'") == 1;

    private async Task<Dictionary<string, string>> IndexDefinitionsAsync()
    {
        var rows = await _harness.StringsAsync(
            "SELECT indexname || '\u0001' || indexdef FROM pg_indexes WHERE schemaname='public'");
        return rows
            .Select(r => r.Split('\u0001', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    /// <summary>
    /// A per-table checksum over only the columns the first migration created, so the value is
    /// comparable at every point of the walk even after later migrations add columns.
    /// </summary>
    private async Task<Dictionary<string, string>> LegacyChecksumAsync()
    {
        const string Sql = """
            SELECT 'CoachSession=' || coalesce(md5(string_agg(x,'|' ORDER BY x)),'') FROM (
              SELECT concat_ws('~', "Id","UserProfileId","AgentImplementation","AgentName","AgentConfigVersion",
                "SessionSchemaVersion","ProtectedAgentSession","ActiveConstraintsJson"::text,"PendingSuggestionId",
                "PendingSuggestionDeltaJson"::text,"PendingSuggestionCreatedAt","TurnCount","ClarificationCount",
                "RevisionCount","Status","StopReason","CreatedAt","UpdatedAt","ExpiresAt") AS x FROM "CoachSession") a
            UNION ALL
            SELECT 'CoachPlanRevision=' || coalesce(md5(string_agg(x,'|' ORDER BY x)),'') FROM (
              SELECT concat_ws('~', "Id","UserProfileId","SessionId","RevisionNumber","Source","IntentKind",
                "AcceptedConstraintDeltaJson"::text,"BeforePlanVersion","AfterPlanVersion","BeforePlanHash",
                "AfterPlanHash","BeforePlanSnapshotJson"::text,"AfterPlanSnapshotJson"::text,
                "PreservedCompletedCount","PreservedInProgressCount","CreatedAt","IsUndone","UndoneAt",
                "UndoneByRevisionId") AS x FROM "CoachPlanRevision") b
            UNION ALL
            SELECT 'CoachUsage=' || coalesce(md5(string_agg(x,'|' ORDER BY x)),'') FROM (
              SELECT concat_ws('~', "Id","UserProfileId","LocalDate","WeekKey","RunCount","InputTokens",
                "OutputTokens","EstimatedCostUsd","CreatedAt","UpdatedAt") AS x FROM "CoachUsage") c
            """;

        var rows = await _harness.StringsAsync(Sql);
        return rows
            .Select(r => r.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes rows with raw SQL against the first migration's shape, which is the only honest way
    /// to seed "legacy" data: the entity types have since grown columns that did not exist when
    /// these rows were written, so saving them through EF would write tomorrow's schema and prove
    /// nothing about yesterday's rows.
    /// </summary>
    private async Task SeedLegacyAsync()
    {
        await _harness.ExecuteAsync(
            """
            INSERT INTO "CoachSession" ("Id","UserProfileId","AgentImplementation","AgentName",
                "AgentConfigVersion","SessionSchemaVersion","ProtectedAgentSession","ActiveConstraintsJson",
                "PendingSuggestionId","PendingSuggestionDeltaJson","PendingSuggestionCreatedAt",
                "TurnCount","ClarificationCount","RevisionCount","Status","StopReason",
                "CreatedAt","UpdatedAt","ExpiresAt")
            VALUES
              ('legacy-session-1','legacy-user-1','baseline','learning-coach','v1',1,
               'CfDJ8-legacy-ciphertext','{"availableMinutes":20,"goalTag":"travel"}'::jsonb,
               'sug-1','{"availableMinutes":12}'::jsonb, timestamptz '2026-08-14 12:00:00+00',
               3,1,2,1,NULL,
               timestamptz '2026-08-14 12:00:00+00', timestamptz '2026-08-14 12:30:00+00',
               timestamptz '2026-09-14 12:00:00+00'),
              ('legacy-session-2','legacy-user-2','baseline','learning-coach','v1',1,
               NULL,'{"availableMinutes":45,"goalTag":"work"}'::jsonb,
               NULL,NULL,NULL,
               1,0,1,4,2,
               timestamptz '2026-08-13 12:00:00+00', timestamptz '2026-08-13 12:05:00+00',
               timestamptz '2026-09-13 12:00:00+00');

            INSERT INTO "CoachPlanRevision" ("Id","UserProfileId","SessionId","RevisionNumber",
                "Source","IntentKind","AcceptedConstraintDeltaJson","BeforePlanVersion","AfterPlanVersion",
                "BeforePlanHash","AfterPlanHash","BeforePlanSnapshotJson","AfterPlanSnapshotJson",
                "PreservedCompletedCount","PreservedInProgressCount","CreatedAt","IsUndone","UndoneAt",
                "UndoneByRevisionId")
            VALUES
              ('legacy-rev-1','legacy-user-1','legacy-session-1',1,0,1,
               '{"availableMinutes":12}'::jsonb,'v1','v2','hash-before-1','hash-after-1',
               '{"planVersion":"v1"}'::jsonb,'{"planVersion":"v2"}'::jsonb,1,2,
               timestamptz '2026-08-14 12:10:00+00', false, NULL, NULL),
              ('legacy-rev-2','legacy-user-1','legacy-session-1',2,0,2,
               '{"availableMinutes":30}'::jsonb,'v2','v3','hash-after-1','hash-after-2',
               '{"planVersion":"v2"}'::jsonb,'{"planVersion":"v3"}'::jsonb,2,1,
               timestamptz '2026-08-14 12:20:00+00', true,
               timestamptz '2026-08-14 12:25:00+00','legacy-rev-3'),
              ('legacy-rev-3','legacy-user-2','legacy-session-2',1,1,0,
               '{"availableMinutes":45}'::jsonb,'v1','v2','hash-before-2','hash-after-2',
               '{"planVersion":"v1"}'::jsonb,'{"planVersion":"v2"}'::jsonb,0,0,
               timestamptz '2026-08-13 12:02:00+00', false, NULL, NULL);

            INSERT INTO "CoachUsage" ("Id","UserProfileId","LocalDate","WeekKey","RunCount",
                "InputTokens","OutputTokens","EstimatedCostUsd","CreatedAt","UpdatedAt")
            VALUES
              ('legacy-usage-1','legacy-user-1', date '2026-08-14','2026-W33',3,12345,6789,0.012345,
               timestamptz '2026-08-14 12:00:00+00', timestamptz '2026-08-14 12:30:00+00'),
              ('legacy-usage-2','legacy-user-2', date '2026-08-13','2026-W33',1,100,200,0.000500,
               timestamptz '2026-08-13 12:00:00+00', timestamptz '2026-08-13 12:05:00+00');
            """);

        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachSession\"")).Should().Be(2);
        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachPlanRevision\"")).Should().Be(3);
        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachUsage\"")).Should().Be(2);
    }
}

/// <summary>
/// Proves the unique <c>(owner, operation)</c> index behaves the way the plan-write saga assumes
/// it does, which is a PostgreSQL-specific question because it turns on NULL distinctness.
/// </summary>
public sealed class CoachPostgresOperationIdIndexTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("opidx");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Many_revisions_may_have_no_operation_but_one_operation_may_produce_one_revision()
    {
        await using var db = _harness.NewContext();

        // Two revisions with no operation id at all: the saga only stamps an operation on
        // revisions a durable turn produced, and the pre-existing ones must stay legal.
        db.Set<CoachPlanRevision>().AddRange(Revision("r1", 1, null), Revision("r2", 2, null));
        await db.SaveChangesAsync();

        db.Set<CoachPlanRevision>().Add(Revision("r3", 3, "op-a"));
        await db.SaveChangesAsync();

        // The same operation replaying must not be able to write a second revision.
        db.Set<CoachPlanRevision>().Add(Revision("r4", 4, "op-a"));
        var act = () => db.SaveChangesAsync();
        var failure = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        var postgres = failure.InnerException.Should().BeOfType<PostgresException>().Which;
        postgres.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        postgres.ConstraintName.Should().Be("IX_CoachPlanRevision_UserProfileId_OperationId");

        db.ChangeTracker.Clear();

        // The same operation id under a different learner is a different operation.
        await using var other = _harness.NewContext();
        other.Set<CoachPlanRevision>().Add(Revision("r5", 1, "op-a", user: "user-b", session: "session-b"));
        await other.SaveChangesAsync();

        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachPlanRevision\"")).Should().Be(4);
    }

    private static CoachPlanRevision Revision(
        string id,
        int number,
        string? operationId,
        string user = "user-a",
        string session = "session-a") => new()
        {
            Id = id,
            UserProfileId = user,
            SessionId = session,
            RevisionNumber = number,
            AcceptedConstraintDeltaJson = "{}",
            BeforePlanVersion = "v1",
            AfterPlanVersion = "v2",
            BeforePlanHash = "h1",
            AfterPlanHash = "h2",
            BeforePlanSnapshotJson = "{}",
            AfterPlanSnapshotJson = "{}",
            OperationId = operationId,
            CreatedAt = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)
        };
}
