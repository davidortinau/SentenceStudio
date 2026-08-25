using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The stored representation of coach enums.
/// </summary>
/// <remarks>
/// <para>
/// <c>CoachDbContext</c> maps <c>CoachSession.StopReason</c> and <c>CoachSession.Status</c> with
/// <c>HasConversion&lt;int&gt;()</c>, so the database holds the <b>ordinal</b>, not the name. That
/// makes member order a persistence contract: inserting a value into the middle of
/// <see cref="CoachStopReason"/> silently re-labels every row already written.
/// </para>
/// <para>
/// This is not theoretical. A live session recorded <c>StopReason = 4</c> for a turn that ran
/// out of output tokens. Reading that row back correctly — as <c>ValidationFailed</c>, the
/// mapping in force at the time, rather than as whatever sits at position 4 today — is the only
/// way to tell that history apart from the <c>OutputTokenLimit</c> the same failure records now.
/// </para>
/// </remarks>
public class CoachStoredEnumContractTests
{
    [Theory]
    [InlineData(CoachStopReason.Failed, 0)]
    [InlineData(CoachStopReason.Completed, 1)]
    [InlineData(CoachStopReason.ClarificationRequested, 2)]
    [InlineData(CoachStopReason.InputRejected, 3)]
    // The value a pre-fix output-token exhaustion was recorded as.
    [InlineData(CoachStopReason.ValidationFailed, 4)]
    [InlineData(CoachStopReason.ToolFailure, 5)]
    [InlineData(CoachStopReason.IterationLimit, 6)]
    // The value the same failure records now.
    [InlineData(CoachStopReason.OutputTokenLimit, 7)]
    [InlineData(CoachStopReason.Timeout, 8)]
    [InlineData(CoachStopReason.RateLimit, 9)]
    [InlineData(CoachStopReason.ConcurrencyLimit, 10)]
    [InlineData(CoachStopReason.Cancelled, 11)]
    [InlineData(CoachStopReason.SessionExpired, 12)]
    public void EveryStopReasonKeepsItsStoredOrdinal(CoachStopReason reason, int stored) =>
        ((int)reason).Should().Be(stored);

    [Theory]
    [InlineData(CoachSessionStatus.Expired, 0)]
    [InlineData(CoachSessionStatus.Active, 1)]
    [InlineData(CoachSessionStatus.AwaitingClarification, 2)]
    [InlineData(CoachSessionStatus.SuggestionPending, 3)]
    [InlineData(CoachSessionStatus.Limited, 4)]
    [InlineData(CoachSessionStatus.Failed, 5)]
    [InlineData(CoachSessionStatus.Closed, 6)]
    public void EverySessionStatusKeepsItsStoredOrdinal(CoachSessionStatus status, int stored) =>
        ((int)status).Should().Be(stored);

    [Fact]
    public void AddingAStopReasonIsOnlySafeAtTheEnd()
    {
        // A guard on the count, so appending stays cheap and inserting cannot pass quietly:
        // a new member in the middle shifts an ordinal and fails the theory above as well.
        Enum.GetValues<CoachStopReason>().Should().HaveCount(13);
        Enum.GetValues<CoachSessionStatus>().Should().HaveCount(7);
    }

    [Fact]
    public async Task AStoredStopReasonRoundTripsThroughTheRealModel()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var session = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        await store.UpdateAsync(
            CoachPersistenceSamples.OwnerUserId,
            session.Id,
            new CoachSessionUpdate { StopReason = CoachStopReason.OutputTokenLimit });

        var stored = await db.CoachSessions.AsNoTracking().SingleAsync();
        stored.StopReason.Should().Be(CoachStopReason.OutputTokenLimit);
    }
}
