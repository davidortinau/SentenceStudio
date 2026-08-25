using FluentAssertions;
using SentenceStudio.Api.Coach.Operations;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Every write status is classified, and classified once.
/// </summary>
/// <remarks>
/// <para>
/// The ledger decides whether a repeated request is answered from an existing row or recorded as a
/// new one by asking whether that row still holds its request. That question has to have an answer
/// for every status, including one added next year, and the dangerous failure is silence: a new
/// member that falls out of both predicates would be treated as closed, its slot released, and a
/// second proposal recorded for something that might already be under way.
/// </para>
/// <para>
/// So the enumeration is the test. It walks the enum rather than a list somebody typed, which is
/// what makes it fail on the addition rather than on the bug the addition causes.
/// </para>
/// </remarks>
public class CoachWriteStatusClassificationTests
{
    public static TheoryData<CoachWriteOperationStatus> AllStatuses()
    {
        var data = new TheoryData<CoachWriteOperationStatus>();
        foreach (var status in Enum.GetValues<CoachWriteOperationStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Every_status_is_either_holding_its_request_or_closed_without_effect(
        CoachWriteOperationStatus status)
    {
        var holds = CoachWriteOperationStates.HoldsRequest(status);
        var closed = CoachWriteOperationStates.IsClosedWithoutEffect(status);

        (holds ^ closed).Should().BeTrue(
            $"{status} must be classified exactly once — a status in neither set is treated as closed "
            + "by default, which would release the idempotency slot of an operation that may still run");
    }

    /// <summary>
    /// The three statuses that answer for a request are the three that can.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived, so a change of policy has to be a change to this list and
    /// is read by whoever reviews it. <c>Executing</c> is the one worth pausing on: it holds the
    /// request not because it can be approved but because it might already have written.
    /// </remarks>
    [Theory]
    [InlineData(CoachWriteOperationStatus.Proposed)]
    [InlineData(CoachWriteOperationStatus.Executing)]
    [InlineData(CoachWriteOperationStatus.Executed)]
    public void A_status_that_can_still_speak_for_the_request_holds_it(CoachWriteOperationStatus status) =>
        CoachWriteOperationStates.HoldsRequest(status).Should().BeTrue();

    [Theory]
    [InlineData(CoachWriteOperationStatus.Undone)]
    [InlineData(CoachWriteOperationStatus.Rejected)]
    [InlineData(CoachWriteOperationStatus.Expired)]
    [InlineData(CoachWriteOperationStatus.Failed)]
    public void A_status_that_left_no_effect_releases_the_request(CoachWriteOperationStatus status) =>
        CoachWriteOperationStates.IsClosedWithoutEffect(status).Should().BeTrue();

    /// <summary>
    /// Only an executed operation is in effect.
    /// </summary>
    /// <remarks>
    /// This is what <c>AlreadyExecuted</c> is computed from, in the tool result the model reads
    /// and on the operation route the card reads. <c>Undone</c> is the member that used to be
    /// counted here, and counting it meant telling a learner a change was done while they were
    /// looking at the thing they had just put back.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Only_executed_is_in_effect(CoachWriteOperationStatus status) =>
        CoachWriteOperationStates.IsEffective(status)
            .Should().Be(status == CoachWriteOperationStatus.Executed);

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Only_proposed_is_open(CoachWriteOperationStatus status) =>
        CoachWriteOperationStates.IsOpen(status)
            .Should().Be(status == CoachWriteOperationStatus.Proposed);

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Only_executing_is_in_flight(CoachWriteOperationStatus status) =>
        CoachWriteOperationStates.IsInFlight(status)
            .Should().Be(status == CoachWriteOperationStatus.Executing);
}

/// <summary>
/// The turn a write proposal is counted against.
/// </summary>
/// <remarks>
/// The per-turn write budget is the tighter of the two bounds on a turn, and the only one that is
/// write-only. It counts by turn identity, so a turn with no identity is a turn with no cap — and
/// the identity arrives from the client, which means omitting it was a way for a caller to pick
/// the looser bound for itself. The scope closes that by minting one.
/// </remarks>
public class CoachWriteTurnScopeTests
{
    [Fact]
    public void A_client_turn_identity_is_used_as_given()
    {
        var scope = new CoachWriteTurnScope();

        scope.Enter("conv-1", "turn-7");

        scope.IsActive.Should().BeTrue();
        scope.ConversationId.Should().Be("conv-1");
        scope.TurnId.Should().Be("turn-7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_client_turn_identity_is_minted_by_the_server(string? turnId)
    {
        var scope = new CoachWriteTurnScope();

        scope.Enter("conv-1", turnId);

        scope.TurnId.Should().NotBeNullOrWhiteSpace(
            "a turn with no identity would skip the per-turn write budget entirely");
        scope.TurnId.Should().StartWith(
            CoachWriteTurnScope.ServerTurnPrefix,
            "an operator reading an audit has to be able to tell a client turn from a server one");
        scope.TurnId!.Length.Should().BeLessThanOrEqualTo(
            CoachWriteLimits.IdMaxLength, "the value is stored in a bounded column");
    }

    /// <summary>
    /// A turn identity the server issues to itself is not honoured when a request supplies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>undo:</c> case is the one with teeth. Turn identities are unique per conversation,
    /// so a request naming <c>undo:{operationId}</c> would occupy exactly the slot that
    /// operation's reversal needs, and the learner's Undo would fail for as long as the row
    /// existed — their change made unreversible by somebody else's request body.
    /// </para>
    /// <para>
    /// The route refuses such a request outright; this is the scope refusing to honour it anyway,
    /// so no path that reaches the scope without that check can pre-claim a slot. It replaces
    /// rather than throws, because failing towards a value the server chose is the safe direction
    /// and the loud answer belongs to the route.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("undo:op-1")]
    [InlineData("UNDO:op-1")]
    [InlineData("srv-deadbeef")]
    [InlineData("SRV-deadbeef")]
    public void A_reserved_client_turn_identity_is_replaced_not_honoured(string reserved)
    {
        var scope = new CoachWriteTurnScope();

        scope.Enter("conv-1", reserved);

        scope.TurnId.Should().NotBe(reserved, "a request may not claim an identity the server issues");
        scope.TurnId.Should().StartWith(CoachWriteTurnScope.ServerTurnPrefix);
        scope.TurnId!.Length.Should().BeLessThanOrEqualTo(CoachWriteLimits.IdMaxLength);
    }

    /// <summary>Both reserved prefixes are recognised, and an ordinary identity is not.</summary>
    [Theory]
    [InlineData("undo:op-1", true)]
    [InlineData("UnDo:op-1", true)]
    [InlineData("srv-1234", true)]
    [InlineData("SRV-1234", true)]
    [InlineData("turn-7", false)]
    [InlineData("undoing-my-mistake", false)]
    [InlineData("service-1", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Reserved_turn_identities_are_recognised_by_prefix(string? turnId, bool reserved)
    {
        CoachWriteTurnScope.IsReservedTurnId(turnId).Should().Be(reserved);
    }

    /// <summary>
    /// The reversal prefix the ledger writes and the prefix the scope reserves are the same value.
    /// </summary>
    /// <remarks>
    /// Two copies of this string would be a hole that opens quietly: the ledger would keep writing
    /// reversal identities the guard no longer recognised, and the pre-claim would be available
    /// again with nothing failing.
    /// </remarks>
    [Fact]
    public void The_reversal_prefix_is_reserved()
    {
        CoachWriteTurnScope.UndoTurnPrefix.Should().Be("undo:");
        CoachWriteTurnScope.IsReservedTurnId(CoachWriteTurnScope.UndoTurnPrefix + "op-1")
            .Should().BeTrue();
    }

    /// <summary>
    /// The minted identity is one value for the whole scope, not one per read.
    /// </summary>
    /// <remarks>
    /// The point of minting is to give the budget something to count against. An identity that
    /// changed between two proposals in the same turn would count each of them against a bucket of
    /// its own, which is the uncapped behaviour wearing a different name.
    /// </remarks>
    [Fact]
    public void A_minted_turn_identity_is_stable_for_the_request()
    {
        var scope = new CoachWriteTurnScope();
        scope.Enter("conv-1", turnId: null);

        var first = scope.TurnId;
        var second = scope.TurnId;

        second.Should().Be(first);
    }

    [Fact]
    public void Two_requests_get_different_minted_identities()
    {
        var first = new CoachWriteTurnScope();
        var second = new CoachWriteTurnScope();

        first.Enter("conv-1", turnId: null);
        second.Enter("conv-1", turnId: null);

        second.TurnId.Should().NotBe(
            first.TurnId, "one turn's budget must not be spent by another turn's proposals");
    }

    /// <summary>
    /// Outside a turn there is nothing to mint an identity for.
    /// </summary>
    /// <remarks>
    /// A scope with no conversation is inactive, and the write tool refuses before the ledger is
    /// reached. Minting a turn identity there would manufacture the appearance of a turn around a
    /// call that is not in one.
    /// </remarks>
    [Fact]
    public void A_scope_with_no_conversation_mints_nothing()
    {
        var scope = new CoachWriteTurnScope();

        scope.Enter(conversationId: null, turnId: null);

        scope.IsActive.Should().BeFalse();
        scope.TurnId.Should().BeNull();
    }
}
