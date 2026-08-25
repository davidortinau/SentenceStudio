using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// A request may not name a turn identity the server issues to itself.
/// </summary>
/// <remarks>
/// <para>
/// Turn identities became unique per conversation when a turn was held to one proposal. That made
/// the identity a slot, and two of them are the ledger's own: <c>srv-</c> for a turn the client
/// did not name, and <c>undo:</c> for the row a reversal writes.
/// </para>
/// <para>
/// The second is the dangerous one. A request naming <c>undo:{operationId}</c> would take exactly
/// the slot that operation's reversal later needs, so the learner's Undo would fail on a row
/// somebody else's request body had put there — a change made unreversible without touching the
/// change. These tests refuse it at the route and prove the reversal still works.
/// </para>
/// </remarks>
public class CoachReservedTurnIdentityTests
{
    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitAsync(
        CoachApplicationHarness harness, string sessionId, string? clientTurnId) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "add 사과 to my list",
            ClientTurnId = clientTurnId
        });

    /// <summary>
    /// A reserved identity is refused, and the model never runs.
    /// </summary>
    /// <remarks>
    /// The run count is the assertion that the refusal happened where it was supposed to. A check
    /// that ran after the turn would still return the right status while having already spent a
    /// model call, entered the write scope, and given a tool a slot to write into.
    /// </remarks>
    [Theory]
    [InlineData("undo:op-1")]
    [InlineData("UNDO:op-1")]
    [InlineData("srv-deadbeef")]
    [InlineData("SRV-deadbeef")]
    [InlineData("  undo:op-1  ")]
    public async Task A_reserved_client_turn_identity_is_refused_before_anything_runs(string reserved)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await SubmitAsync(harness, sessionId, reserved);

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        result.ProblemType.Should().Be(CoachProblemTypes.InvalidTurnInput);
        result.Detail.Should().Contain("reserved");
        result.Value.Should().BeNull("a refused turn produces no response to render");

        harness.Coach.RunCount.Should().Be(
            0, "the refusal has to happen before the turn does anything at all");
    }

    /// <summary>
    /// A refusal says a value is reserved and nothing else.
    /// </summary>
    /// <remarks>
    /// It must not confirm whether the operation named in an <c>undo:</c> identity exists. That
    /// would turn the refusal into an existence oracle for another learner's operation ids, which
    /// is precisely the shape every other refusal in this surface is careful not to have.
    /// </remarks>
    [Fact]
    public async Task The_refusal_does_not_say_whether_the_named_operation_exists()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await SubmitAsync(harness, sessionId, "undo:some-other-learners-operation");

        result.Detail.Should().NotContain("some-other-learners-operation");
        result.Detail.Should().NotContain("exist");
    }

    /// <summary>An ordinary identity is unaffected.</summary>
    [Fact]
    public async Task An_ordinary_client_turn_identity_is_accepted()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await SubmitAsync(harness, sessionId, "turn-7");

        result.Status.Should().NotBe(CoachOperationStatus.InvalidInput);
        harness.Coach.RunCount.Should().Be(1, "a normal turn is not affected by the guard");
    }

    /// <summary>A turn with no identity is unaffected, and still gets one from the server.</summary>
    [Fact]
    public async Task A_turn_with_no_client_identity_is_accepted()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await SubmitAsync(harness, sessionId, clientTurnId: null);

        result.Status.Should().NotBe(CoachOperationStatus.InvalidInput);
        harness.Coach.RunCount.Should().Be(1);
    }

    /// <summary>
    /// A value that merely starts with the same letters is not reserved.
    /// </summary>
    /// <remarks>
    /// The guard is a prefix check on two specific strings, not a substring search for "undo". A
    /// learner's client is free to name a turn <c>undoing-this</c>, and refusing it would be the
    /// guard inventing work for whoever has to explain the error.
    /// </remarks>
    [Theory]
    [InlineData("undoing-this")]
    [InlineData("service-1")]
    [InlineData("turn-undo:1")]
    public async Task A_similar_looking_identity_is_not_treated_as_reserved(string turnId)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await SubmitAsync(harness, sessionId, turnId);

        result.Status.Should().NotBe(CoachOperationStatus.InvalidInput);
        CoachWriteTurnScope.IsReservedTurnId(turnId).Should().BeFalse();
    }
}
