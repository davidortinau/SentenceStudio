using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Provider-level behaviour that only a real PostgreSQL server can demonstrate: how the coach's
/// timestamps survive a round trip, how unique and foreign-key violations surface, and how JSON
/// and ciphertext columns are typed.
/// </summary>
/// <remarks>
/// These are the seams where SQLite quietly disagrees with PostgreSQL. SQLite stores a
/// <see cref="DateTime"/> as text and hands it back with <see cref="DateTimeKind.Unspecified"/>;
/// PostgreSQL stores a real instant and, under the legacy timestamp switch the API host enables,
/// hands it back converted into the machine's local zone. Any comparison the stores perform on a
/// value read from the database therefore behaves differently on the two providers, which is
/// precisely the class of bug an in-memory suite cannot see.
/// </remarks>
public sealed class CoachPostgresProviderBehaviorTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;
    private string _conversationId = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("provider");

        await using var db = _harness.NewContext();
        var created = await _harness.NewConversationStore(db).CreateAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.CreateConversation());
        _conversationId = created.Conversation!.Id;
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task A_timestamp_written_by_a_store_comes_back_as_the_same_instant()
    {
        // The conversation store stamps CreatedAt from its TimeProvider as a Kind=Utc DateTime,
        // and the harness freezes that clock, so the exact instant on the wire is known.
        await using var db = _harness.NewContext();
        var readBack = await db.Set<CoachConversation>().AsNoTracking()
            .Where(c => c.Id == _conversationId)
            .Select(c => c.CreatedAt)
            .SingleAsync();

        readBack.ToUniversalTime().Should().Be(_harness.Time.GetUtcNow().UtcDateTime,
            "the stored instant must survive the round trip regardless of how it is presented");
    }

    [PostgresFact]
    public async Task A_persisted_timestamp_compares_correctly_against_the_utc_clock_the_stores_use()
    {
        // The instant being right is not the same as the value being safe to compare. A DateTime
        // comparison ignores Kind entirely and compares raw ticks, so a value read back from
        // PostgreSQL is only safe to compare against a UTC clock if it also comes back as UTC.
        //
        // This matters because the coach stores compare persisted timestamps against
        // TimeProvider.GetUtcNow().UtcDateTime directly -- CoachTurnOperationStore decides whether
        // a lease is still live with `expiry > now`, and ListExpiredAsync selects with
        // `LeaseExpiresAt <= now`. If the round trip does not preserve comparability, both are
        // skewed by the host's UTC offset: leases look dead early west of UTC (two workers claim
        // the same operation) and stay alive too long east of it (crash recovery never fires).
        await using var db = _harness.NewContext();
        var readBack = await db.Set<CoachConversation>().AsNoTracking()
            .Where(c => c.Id == _conversationId)
            .Select(c => c.CreatedAt)
            .SingleAsync();

        var storeClock = _harness.Time.GetUtcNow().UtcDateTime;

        (readBack == storeClock).Should().BeTrue(
            "the coach stores compare persisted lease and expiry timestamps against a UTC clock "
            + "using plain DateTime operators, which ignore Kind. Read back Kind={0} value={1:O}, "
            + "store clock Kind={2} value={3:O}; the difference is {4}.",
            readBack.Kind,
            readBack,
            storeClock.Kind,
            storeClock,
            readBack - storeClock);
    }

    [PostgresFact]
    public async Task A_duplicate_message_sequence_surfaces_as_a_unique_violation_not_a_silent_overwrite()
    {
        await using var db = _harness.NewContext();
        await _harness.NewMessageStore(db).AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(_conversationId, CoachHistorySamples.LearnerText()));

        var duplicate = async () => await _harness.ExecuteAsync(
            $"""
             INSERT INTO "CoachMessage"
               ("Id","UserProfileId","ConversationId","Sequence","Role","Kind","ProtectedPayload",
                "ContentSchemaVersion","ContentProtectionVersion","CreatedAt")
             VALUES ('msg-dup','{CoachHistorySamples.Owner.UserProfileId}','{_conversationId}',1,0,0,'x',1,1,now())
             """);

        var error = (await duplicate.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        error.ConstraintName.Should().Be("IX_CoachMessage_UserProfileId_ConversationId_Sequence");
    }

    [PostgresFact]
    public async Task A_message_whose_owner_does_not_match_its_conversation_is_rejected_by_the_composite_key()
    {
        var orphan = async () => await _harness.ExecuteAsync(
            $"""
             INSERT INTO "CoachMessage"
               ("Id","UserProfileId","ConversationId","Sequence","Role","Kind","ProtectedPayload",
                "ContentSchemaVersion","ContentProtectionVersion","CreatedAt")
             VALUES ('msg-orphan','{CoachHistorySamples.Intruder.UserProfileId}','{_conversationId}',99,0,0,'x',1,1,now())
             """);

        var error = (await orphan.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
            "the foreign key joins on (UserProfileId, ConversationId), so a mismatched owner has no parent to point at");
    }

    [PostgresFact]
    public async Task A_failed_statement_poisons_the_surrounding_transaction()
    {
        // This is the behaviour that separates PostgreSQL from SQLite most sharply, and the reason
        // CoachMessageStore rolls back before it asks the database to disambiguate a failed append.
        // In PostgreSQL every statement after an error in the same transaction is refused with
        // 25P02 until the transaction ends, so "catch the exception and query to find out why"
        // only works if the rollback happens first.
        await using var connection = await _harness.OpenRawAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var bad = new NpgsqlCommand("SELECT * FROM \"NoSuchCoachTable\"", connection, transaction))
        {
            var act = async () => await bad.ExecuteScalarAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.UndefinedTable);
        }

        await using var innocent = new NpgsqlCommand("SELECT 1", connection, transaction);
        var poisoned = async () => await innocent.ExecuteScalarAsync();

        (await poisoned.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InFailedSqlTransaction);

        await transaction.RollbackAsync();

        // And the connection is immediately usable again once the transaction ends.
        await using var recovered = new NpgsqlCommand("SELECT 1", connection);
        (await recovered.ExecuteScalarAsync()).Should().Be(1);
    }

    [PostgresFact]
    public async Task Documents_are_stored_as_jsonb_and_ciphertext_never_is()
    {
        var jsonColumns = await _harness.StringsAsync(
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name LIKE 'Coach%' AND data_type = 'jsonb'
            ORDER BY 1
            """);

        jsonColumns.Should().NotBeEmpty();

        // Ciphertext is opaque bytes rendered as text. Typing it as jsonb would make the database
        // parse it, which both fails and would leak structure if it ever succeeded.
        var protectedAsJson = await _harness.StringsAsync(
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name LIKE 'Coach%'
              AND column_name LIKE 'Protected%' AND data_type = 'jsonb'
            """);

        protectedAsJson.Should().BeEmpty("ciphertext must never be handed to the JSON parser");
    }

    [PostgresFact]
    public async Task A_jsonb_column_refuses_content_that_is_not_json()
    {
        var notJson = async () => await _harness.ExecuteAsync(
            $"""
             INSERT INTO "CoachSession"
               ("Id","UserProfileId","AgentImplementation","AgentName","AgentConfigVersion","SessionSchemaVersion",
                "ActiveConstraintsJson","TurnCount","ClarificationCount","RevisionCount","Status",
                "CreatedAt","UpdatedAt","ExpiresAt")
             VALUES ('sess-bad','{CoachHistorySamples.Owner.UserProfileId}','impl','name',1,1,
                     'this is not json',0,0,0,1,now(),now(),now())
             """);

        (await notJson.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InvalidTextRepresentation);
    }

    [PostgresFact]
    public async Task The_coach_context_is_registered_without_a_retrying_execution_strategy()
    {
        // The coach stores open explicit transactions in almost every write path. EF forbids a
        // manually started transaction under a retrying execution strategy unless the caller wraps
        // the whole unit of work in strategy.ExecuteAsync, so enabling retries later would break
        // those paths rather than harden them. This test pins the current shape so that change is
        // a deliberate one with the wrapping done at the same time.
        await using var db = _harness.NewContext();
        var strategy = db.Database.CreateExecutionStrategy();

        strategy.RetriesOnFailure.Should().BeFalse(
            "the coach write paths begin transactions directly; adding retries requires wrapping them first");

        await using var transaction = await db.Database.BeginTransactionAsync();
        transaction.Should().NotBeNull("an explicit transaction must remain legal for the stores to work");
        await transaction.RollbackAsync();
    }
}
