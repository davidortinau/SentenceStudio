using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The encryption-at-rest guarantee: a raw read of the coach session row must never
/// reveal the learner's conversation.
/// </summary>
public class CoachAgentSessionProtectionTests
{
    private const string AgentSessionJson =
        "{\"messages\":[{\"role\":\"user\",\"content\":\"" + CoachPersistenceSamples.LearnerSentinel + "\"}]}";

    [Fact]
    public async Task StoredAgentSession_IsCiphertextInTheDatabase()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId,
            CoachPersistenceSamples.CreateRequest(AgentSessionJson));

        // Read the raw column through ADO, bypassing every EF conversion.
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"ProtectedAgentSession\" FROM \"CoachSession\" WHERE \"Id\" = $id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = created.Id;
        command.Parameters.Add(parameter);

        var raw = (string?)await command.ExecuteScalarAsync();

        raw.Should().NotBeNullOrEmpty();
        raw.Should().NotContain(CoachPersistenceSamples.LearnerSentinel,
            "the serialized agent session must be encrypted before it reaches the database");
        raw.Should().NotContain("\"messages\"");
    }

    [Fact]
    public async Task LoadAsync_ReturnsTheDecryptedAgentSession()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId,
            CoachPersistenceSamples.CreateRequest(AgentSessionJson));

        var loaded = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        loaded.AgentSessionJson.Should().Be(AgentSessionJson);
    }

    [Fact]
    public async Task UpdateAsync_ReencryptsTheReplacementAgentSession()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await store.UpdateAsync(CoachPersistenceSamples.OwnerUserId, created.Id, new CoachSessionUpdate
        {
            AgentSessionJson = AgentSessionJson
        });

        var row = await db.CoachSessions.AsNoTracking().SingleAsync();
        row.ProtectedAgentSession.Should().NotContain(CoachPersistenceSamples.LearnerSentinel);

        var loaded = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);
        loaded.AgentSessionJson.Should().Be(AgentSessionJson);
    }

    [Fact]
    public async Task UnreadablePayload_IsRejectedInsteadOfResumed()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId,
            CoachPersistenceSamples.CreateRequest(AgentSessionJson));

        // Simulate key rotation / tampering.
        var tracked = await db.CoachSessions.SingleAsync(s => s.Id == created.Id);
        tracked.ProtectedAgentSession = "not-a-valid-protected-payload";
        await db.SaveChangesAsync();

        var result = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        result.Status.Should().Be(CoachSessionLoadStatus.Unreadable);
        result.Session.Should().BeNull();
    }

    [Fact]
    public void Protector_RoundTripsAndRejectsGarbage()
    {
        using var harness = new CoachPersistenceHarness();
        var context = new CoachAgentSessionContext(CoachPersistenceSamples.OwnerUserId, "session-1");

        var payload = harness.Protector.Protect(context, AgentSessionJson);
        payload.Should().NotBeNull().And.NotBe(AgentSessionJson);

        harness.Protector.TryUnprotect(context, payload, out var roundTripped).Should().BeTrue();
        roundTripped.Should().Be(AgentSessionJson);

        harness.Protector.TryUnprotect(context, "garbage", out _).Should().BeFalse();
        harness.Protector.TryUnprotect(context, null, out _).Should().BeFalse();
        harness.Protector.Protect(context, null).Should().BeNull();
    }
}
