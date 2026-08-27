using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Reads the durable tables through raw ADO, bypassing every store and every decryption path, and
/// asserts that nothing a learner typed is legible in the database. A store-level test cannot
/// prove this: it always looks through the protector.
/// </summary>
public sealed class CoachHistoryAtRestTests
{
    private const string TitleSentinel = "SENTINEL_TITLE_9c2b";
    private const string RequestSentinel = "SENTINEL_REQUEST_4d81";
    private const string OutcomeSentinel = "SENTINEL_OUTCOME_2e57";
    private const string IdempotencySentinel = "SENTINEL_IDEMPOTENCY_a13f";

    private static async Task<string> SeedAsync(CoachPersistenceHarness harness, CoachDbContext db)
    {
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var turns = harness.NewTurnOperationStore(db);

        var created = await conversations.CreateAsync(
            CoachHistorySamples.Owner,
            new CreateCoachConversationRequest(TitleSentinel, CoachConversationTitleSource.Learner, "ko"));
        var id = created.Conversation!.Id;

        await messages.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(id, CoachHistorySamples.LearnerText()));
        await messages.AppendAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Append(
                id,
                CoachHistorySamples.StructuredAnswer(CoachPersistenceSamples.LearnerSentinel),
                CoachMessageRole.Coach,
                CoachMessageKind.PedagogicalAnswer));

        var claim = await turns.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(id, key: IdempotencySentinel, payload: RequestSentinel));
        await turns.CompleteAsync(
            CoachHistorySamples.Owner,
            claim.Operation!.Id,
            "worker-a",
            claim.FencingVersion,
            OutcomeSentinel,
            CoachHistorySchema.TurnOutcomeVersion,
            1,
            2);

        return id;
    }

    private static async Task<string> DumpAsync(CoachPersistenceHarness harness, string table)
    {
        using var command = harness.NewRawCommand($"SELECT * FROM \"{table}\";");
        await using var reader = await command.ExecuteReaderAsync();

        var text = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!await reader.IsDBNullAsync(i))
                {
                    text.Append(reader.GetValue(i)).Append('\u001f');
                }
            }
        }

        return text.ToString();
    }

    [Theory]
    [InlineData("CoachConversation")]
    [InlineData("CoachMessage")]
    [InlineData("CoachTurnOperation")]
    public async Task NoLearnerContentIsLegibleInAnyHistoryTable(string table)
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        await SeedAsync(harness, db);

        var dump = await DumpAsync(harness, table);

        Assert.NotEmpty(dump);
        Assert.DoesNotContain(CoachPersistenceSamples.LearnerSentinel, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(TitleSentinel, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(RequestSentinel, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(OutcomeSentinel, dump, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client's retry key is a caller-chosen token that can carry meaning; only its bound
    /// digest is durable.
    /// </summary>
    [Fact]
    public async Task TheIdempotencyKeyIsNeverStoredInTheClear()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        await SeedAsync(harness, db);

        var dump = await DumpAsync(harness, "CoachTurnOperation");

        Assert.DoesNotContain(IdempotencySentinel, dump, StringComparison.Ordinal);
    }

    /// <summary>
    /// Owner and sequence stay legible on purpose: they are the scoping and ordering keys the
    /// database must index. This pins that intent so a later change cannot quietly widen it.
    /// </summary>
    [Fact]
    public async Task ScopingColumnsRemainQueryable()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await SeedAsync(harness, db);

        using var command = harness.NewRawCommand(
            "SELECT COUNT(*) FROM \"CoachMessage\" WHERE \"UserProfileId\" = $owner AND \"ConversationId\" = $conversation;");
        command.Parameters.AddWithValue("$owner", CoachPersistenceSamples.OwnerUserId);
        command.Parameters.AddWithValue("$conversation", conversationId);

        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    /// <summary>
    /// The owner-aware composite foreign key must refuse to let a message drift into another
    /// learner's scope on its own. This is the structural half of the defence: even before
    /// encryption is considered, a half-finished retag cannot commit.
    /// </summary>
    [Fact]
    public async Task RetaggingOnlyTheConversationIsRefusedByTheOwnerAwareForeignKey()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var conversationId = await SeedAsync(harness, db);

        using var retag = harness.NewRawCommand(
            "UPDATE \"CoachConversation\" SET \"UserProfileId\" = $other WHERE \"Id\" = $id;");
        retag.Parameters.AddWithValue("$other", CoachPersistenceSamples.OtherUserId);
        retag.Parameters.AddWithValue("$id", conversationId);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => retag.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// Ciphertext stolen from one learner and planted in another learner's own rows must not become
    /// legible. This is the database-level version of the protector's owner-swap test: the thief
    /// owns the destination rows outright, so no constraint stands in the way and only the
    /// owner-bound key derivation is left to refuse the read.
    /// </summary>
    [Fact]
    public async Task CiphertextPlantedInAnotherLearnersRowsStaysUnreadable()
    {
        using var harness = new CoachPersistenceHarness();
        await using var db = harness.NewContext();
        var victimId = await SeedAsync(harness, db);

        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);

        // The thief's own conversation, created legitimately through the store.
        var thiefConversation = await conversations.CreateAsync(
            CoachHistorySamples.Intruder,
            new CreateCoachConversationRequest("thief", CoachConversationTitleSource.Learner, "ko"));
        var thiefId = thiefConversation.Conversation!.Id;

        // Copy the victim's title ciphertext onto the thief's row.
        using (var steal = harness.NewRawCommand(
            """
            UPDATE "CoachConversation"
            SET "ProtectedTitle" = (SELECT "ProtectedTitle" FROM "CoachConversation" WHERE "Id" = $victim)
            WHERE "Id" = $thief;
            """))
        {
            steal.Parameters.AddWithValue("$victim", victimId);
            steal.Parameters.AddWithValue("$thief", thiefId);
            await steal.ExecuteNonQueryAsync();
        }

        // Move the victim's message ciphertext wholesale into the thief's conversation. Owner and
        // conversation move together, so the owner-aware foreign key stays satisfied.
        using (var steal = harness.NewRawCommand(
            """
            UPDATE "CoachMessage"
            SET "UserProfileId" = $thiefOwner, "ConversationId" = $thief
            WHERE "ConversationId" = $victim;
            """))
        {
            steal.Parameters.AddWithValue("$thiefOwner", CoachPersistenceSamples.OtherUserId);
            steal.Parameters.AddWithValue("$thief", thiefId);
            steal.Parameters.AddWithValue("$victim", victimId);
            await steal.ExecuteNonQueryAsync();
        }

        db.ChangeTracker.Clear();

        var stolen = await conversations.GetAsync(CoachHistorySamples.Intruder, thiefId);
        Assert.Equal(CoachHistoryStatus.Success, stolen.Status);
        Assert.False(stolen.Conversation!.IsTitleReadable);

        var page = await messages.GetLatestAsync(CoachHistorySamples.Intruder, thiefId);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.UnreadableCount);
        Assert.All(page.Items, m => Assert.False(m.IsReadable));
    }
}
