using System.Reflection;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Guards the hand-written copy methods on <see cref="CoachTurnResponse"/>.
/// </summary>
/// <remarks>
/// The type is a sealed class with init-only members, so <c>with</c> is unavailable and the copies
/// are written by hand. That is a quiet trap: adding a member and forgetting to copy it does not
/// break the build, it just makes the member vanish from every turn that took the copy path — a
/// memory candidate that never reaches the learner, or a receipt that disappears from a durable
/// turn. This test reflects over the members instead of trusting anyone to remember.
/// </remarks>
public sealed class CoachTurnResponseCopyContractTests
{
    [Fact]
    public void WithMessages_PreservesEveryOtherMember()
    {
        var source = Populated();
        var replacement = new[] { Message("replaced") };

        var copy = source.WithMessages(replacement);

        copy.Messages.Should().BeSameAs(replacement);
        AssertAllMembersCopied(source, copy, nameof(CoachTurnResponse.Messages));
    }

    [Fact]
    public void WithMemoryCandidate_PreservesEveryOtherMember()
    {
        var source = Populated();

        var copy = source.WithMemoryCandidate(null);

        copy.MemoryCandidate.Should().BeNull();
        AssertAllMembersCopied(source, copy, nameof(CoachTurnResponse.MemoryCandidate));
    }

    /// <summary>
    /// Fails when a readable member differs between the original and the copy, unless it is the
    /// one member the copy was asked to replace.
    /// </summary>
    private static void AssertAllMembersCopied(
        CoachTurnResponse source,
        CoachTurnResponse copy,
        string replaced)
    {
        var properties = typeof(CoachTurnResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        properties.Should().HaveCountGreaterThan(10, "the type is not expected to shrink");

        foreach (var property in properties.Where(p => p.Name != replaced))
        {
            var expected = property.GetValue(source);
            var actual = property.GetValue(copy);

            actual.Should().Be(
                expected,
                "{0} must be carried across by the copy; a member added to CoachTurnResponse has to " +
                "be added to every hand-written With* method or it silently disappears",
                property.Name);
        }
    }

    private static CoachTurnResponse Populated() => new()
    {
        SessionId = "session-1",
        TurnId = "turn-1",
        Status = CoachTurnStatus.Completed,
        StopReason = CoachStopReason.Completed,
        SessionStatus = CoachSessionStatus.Active,
        Messages = [Message("original")],
        ActiveConstraints = Constraints,
        PlanState = new CoachPlanStateDto
        {
            PlanDate = new DateOnly(2026, 8, 17),
            PlanVersion = "v1:abc",
            Items = [],
            AppliedConstraints = Constraints,
            EstimatedTotalMinutes = 20,
            CompletedCount = 0,
            TotalCount = 0,
            CompletionPercentage = 0
        },
        ClarifyingQuestion = "which one?",
        ClarificationsRemaining = 2,
        RunsRemainingToday = 4,
        ExpiresAtUtc = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)
    };

    private static readonly CoachConstraintSetDto Constraints = new()
    {
        AvailableMinutes = 20,
        AudioAllowed = true,
        SpeechAllowed = true,
        TypingAllowed = true,
        EnergyLevel = CoachEnergyLevel.Normal
    };

    private static CoachMessageDto Message(string text) => new()
    {
        MessageId = text,
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Text,
        Text = text,
        CreatedAtUtc = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)
    };
}
