using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Proves that forgetting reaches inside a conversation that is already running.
/// </summary>
/// <remarks>
/// <para>
/// A checkpoint is a serialized agent session. Once a remembered preference has been injected into
/// a turn, it may have been absorbed into that serialized state, and nothing outside the framework
/// can inspect it to find out. So forgetting cannot be implemented as "stop selecting it": the
/// checkpoint has to go.
/// </para>
/// <para>
/// The rotation is deliberately coarse — every live checkpoint for that learner, on every kind of
/// change including approval. Reasoning about which checkpoints might contain a given value fails
/// in the direction where a forgotten value quietly survives, and the cost of being wrong the
/// other way is one rebuild from the ledger.
/// </para>
/// </remarks>
public sealed class CoachMemoryCheckpointRotationTests
{
    private static CoachOwner Owner(string userProfileId) =>
        CoachOwner.TryCreate(userProfileId, null, out var owner)
            ? owner
            : throw new InvalidOperationException("bad owner");

    private static CoachAgentTurnResult NoChange() => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = "Understood." },
        AgentSessionJson = """{"messages":["remembered: prepare for a work trip to Seoul"]}"""
    };

    /// <summary>Runs one turn so the session has a checkpoint to rotate.</summary>
    private static async Task<string> SessionWithCheckpointAsync(CoachApplicationHarness harness)
    {
        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Hello."
        });

        (await harness.CheckpointAsync(session)).Should().NotBeNull(
            "the fixture needs a checkpoint before it can prove one was cleared");

        return session;
    }

    private static async Task<CoachMemoryFactRecord> ApproveAsync(CoachApplicationHarness harness)
    {
        var owner = Owner(CoachApplicationHarness.OwnerUserId);

        var created = await harness.Memories!.CreateCandidateAsync(owner, new CreateCoachMemoryCandidateRequest(
            CoachMemoryStoredValue.StudyGoal("Prepare for a work trip to Seoul"),
            CoachMemoryScope.TargetLanguage,
            harness.Languages.Profile.TargetLanguageTag,
            "Remember that I am preparing for a work trip to Seoul.",
            "preparing for a work trip to Seoul"));

        var approved = await harness.Memories.ApproveAsync(
            owner, created.Fact!.Id, created.Fact.Version, null);

        approved.IsSuccess.Should().BeTrue();
        return approved.Fact!;
    }

    [Fact]
    public async Task ForgettingOneFactClearsLiveCheckpoints()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var fact = await ApproveAsync(harness);
        var session = await SessionWithCheckpointAsync(harness);

        await harness.Memories!.ForgetAsync(
            Owner(CoachApplicationHarness.OwnerUserId), fact.Id, fact.Version);

        (await harness.CheckpointAsync(session)).Should().BeNull(
            "a forgotten value must not survive inside a serialized agent session");
    }

    [Fact]
    public async Task ForgetAllClearsLiveCheckpoints()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        await ApproveAsync(harness);
        var session = await SessionWithCheckpointAsync(harness);

        await harness.Memories!.ForgetAllAsync(Owner(CoachApplicationHarness.OwnerUserId));

        (await harness.CheckpointAsync(session)).Should().BeNull();
    }

    [Fact]
    public async Task EditingAFactClearsLiveCheckpoints()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var fact = await ApproveAsync(harness);
        var session = await SessionWithCheckpointAsync(harness);

        // An edit is a forget plus a remember. The old wording is exactly as dangerous to leave
        // behind as a deleted one would be.
        await harness.Memories!.EditActiveAsync(
            Owner(CoachApplicationHarness.OwnerUserId),
            fact.Id,
            fact.Version,
            CoachMemoryStoredValue.StudyGoal("Prepare for a holiday in Jeju"));

        (await harness.CheckpointAsync(session)).Should().BeNull();
    }

    [Fact]
    public async Task ApprovingAFactClearsLiveCheckpoints()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await SessionWithCheckpointAsync(harness);

        await ApproveAsync(harness);

        // Approval rotates too, so the new preference takes effect on the learner's very next
        // message rather than whenever the checkpoint happens to expire.
        (await harness.CheckpointAsync(session)).Should().BeNull();
    }

    [Fact]
    public async Task RotationPreservesConversationAndPlanState()
    {
        using var harness = new CoachApplicationHarness(withHistory: true, withMemory: true);
        var fact = await ApproveAsync(harness);

        var session = await harness.StartSessionAsync();

        // A pending plan suggestion is a decision the learner has not made yet. Forgetting a
        // preference is not an answer to it, so it has to survive.
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            AgentSessionJson = """{"messages":["x"]}""",
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                CoachMessage = "Want to shorten today to ten minutes?",
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 }
            }
        };

        var offered = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Make it 10 minutes."
        });

        offered.Value!.PendingSuggestion.Should().NotBeNull();
        var suggestionId = offered.Value.PendingSuggestion!.SuggestionId;

        await harness.Memories!.ForgetAsync(
            Owner(CoachApplicationHarness.OwnerUserId), fact.Id, fact.Version);

        (await harness.CheckpointAsync(session)).Should().BeNull("the checkpoint is the only thing rotated");

        // The session still exists, still belongs to the learner, and still holds the pending
        // decision. Accepting it must remain possible.
        var accepted = await harness.Service.AcceptSuggestionAsync(
            session, suggestionId, new CoachSuggestionDecisionRequest());

        accepted.IsOk.Should().BeTrue("a memory change must not cancel an unrelated plan decision");
    }

    [Fact]
    public async Task NextTurnAfterRotationRebuildsWithoutTheForgottenValue()
    {
        using var harness = new CoachApplicationHarness(withHistory: true, withMemory: true);
        var fact = await ApproveAsync(harness);
        var session = await SessionWithCheckpointAsync(harness);

        harness.Coach.LastRequest!.MemoryBlock.Should().Contain("Seoul");

        await harness.Memories!.ForgetAsync(
            Owner(CoachApplicationHarness.OwnerUserId), fact.Id, fact.Version);

        harness.Coach.NextResult = NoChange();

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "What should I study today?"
        });

        result.IsOk.Should().BeTrue("the conversation continues, rebuilt rather than lost");

        // No checkpoint carried the value forward, and selection no longer offers it.
        harness.Coach.LastRequest!.AgentSessionJson.Should().BeNull();
        (harness.Coach.LastRequest.MemoryBlock ?? string.Empty).Should().NotContain("Seoul");
    }

    [Fact]
    public async Task RotationDoesNotTouchAnotherLearnersCheckpoint()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        await ApproveAsync(harness);

        var ownerSession = await SessionWithCheckpointAsync(harness);

        harness.UserScope.Current = CoachApplicationHarness.OtherUserId;
        var intruderSession = await SessionWithCheckpointAsync(harness);
        harness.UserScope.Current = CoachApplicationHarness.OwnerUserId;

        await harness.Memories!.ForgetAllAsync(Owner(CoachApplicationHarness.OwnerUserId));

        (await harness.CheckpointAsync(ownerSession)).Should().BeNull();
        (await harness.CheckpointAsync(intruderSession)).Should().NotBeNull(
            "one learner forgetting something cannot disturb another learner's conversation");
    }

    [Fact]
    public async Task ForgettingWithNothingToForgetIsHarmless()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await SessionWithCheckpointAsync(harness);

        var result = await harness.Memories!.ForgetAllAsync(Owner(CoachApplicationHarness.OwnerUserId));

        result.Forgotten.Should().Be(0);

        // Nothing changed, so nothing needed to be rotated. Rebuilding a conversation for no
        // reason is a real cost paid by the learner in latency.
        (await harness.CheckpointAsync(session)).Should().NotBeNull();
    }
}
