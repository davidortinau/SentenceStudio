using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Outcome schema version 3: the dispute joins the answer and the trace.
/// </summary>
/// <remarks>
/// <para>
/// <b>The risk a version bump carries here is total data loss, not a missing feature.</b> The reader
/// branches on the stored version, so a bump that forgets an older arm makes every row written
/// before it read back as no answer at all — a learner's completed conversation silently emptying.
/// W4's bump to version 2 had exactly this trap and the version-1 arm is what defused it. This is
/// the same trap one version further along.
/// </para>
/// <para>
/// <b>What made v3 cheap.</b> The v2 reader was already section-scoped: it reads the answer
/// strictly and the trace tolerantly, through a helper that answers null for an absent section.
/// A v2 payload has no dispute section, so it reads as a v3 payload with no dispute, and both
/// versions share one parser. The tolerance was built at v2 and this is the first time it has been
/// collected.
/// </para>
/// </remarks>
public sealed class CoachTurnDisputeSchemaTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A minimal completed turn. Only its presence after a read is asserted.</summary>
    private static CoachTurnResponse Answer()
    {
        var constraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 10,
            AudioAllowed = true,
            SpeechAllowed = true,
            TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        };

        return new CoachTurnResponse
        {
            SessionId = "session-1",
            TurnId = "turn-1",
            Status = CoachTurnStatus.Completed,
            StopReason = CoachStopReason.Completed,
            SessionStatus = CoachSessionStatus.Active,
            ActiveConstraints = constraints,
            PlanState = new CoachPlanStateDto
            {
                PlanDate = new DateOnly(2026, 8, 22),
                PlanVersion = "v1",
                AppliedConstraints = constraints,
                EstimatedTotalMinutes = 10,
                CompletedCount = 0,
                TotalCount = 3,
                CompletionPercentage = 0
            },
            ExpiresAtUtc = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc)
        };
    }

    private static CoachTurnDisputeState Dispute() => new(
        CoachCorrectionSignal.DifferentCohort,
        "3f1c9a44-0d3e-4c1b-9a5e-77b2c1d0e912",
        new DateTime(2026, 8, 22, 2, 5, 0, DateTimeKind.Utc),
        ResolvedAtUtc: null,
        CoachDisputeResolution.Open,
        [CoachScopeDefinition.TrackedVocabularyDueSummary]);

    // ── Every readable version still reads ───────────────────────────────────

    /// <summary>A version-1 row still yields its answer. The arm that must never be deleted.</summary>
    [Fact]
    public void A_version_one_row_still_yields_its_answer()
    {
        var payload = JsonSerializer.Serialize(Answer(), Web);

        var read = CoachConversationService.ReadOutcome(payload, 1);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull(
            "deleting the version-1 arm silently empties every turn stored before W4");
        read.Trace.Should().BeNull();
        read.Dispute.Should().BeNull();
    }

    /// <summary>
    /// A version-2 row reads under the v3 parser with a null dispute.
    /// </summary>
    /// <remarks>
    /// This is the compatibility claim the bump rests on. If it fails, every turn stored between W4
    /// and W8 reads back empty.
    /// </remarks>
    [Fact]
    public void A_version_two_row_reads_with_a_null_dispute()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null), Web);

        var read = CoachConversationService.ReadOutcome(payload, 2);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull("a v2 row must survive the bump to v3 intact");
        read.Dispute.Should().BeNull("a v2 payload has no dispute section, and absent means null");
    }

    [Fact]
    public void A_version_three_row_reads_all_three_sections()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null, Dispute()), Web);

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull();
        read.Dispute.Should().NotBeNull();
        read.Dispute!.Signal.Should().Be(CoachCorrectionSignal.DifferentCohort);
        read.Dispute.IsOpen.Should().BeTrue();
        read.Dispute.DisputedDefinitionCodes.Should().Equal(
            [CoachScopeDefinition.TrackedVocabularyDueSummary]);
    }

    [Fact]
    public void An_unknown_version_is_absent()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null, Dispute()), Web);

        CoachConversationService.ReadOutcome(payload, 99).Should().BeNull(
            "a row written by a build this one does not know is treated as absent, exactly as before");
    }

    /// <summary>The census: three readable versions, and the set is exact.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(4, false)]
    public void The_readable_version_set_is_exact(int version, bool readable)
    {
        var payload = version == 1
            ? JsonSerializer.Serialize(Answer(), Web)
            : JsonSerializer.Serialize(new CoachStoredTurnOutcome(Answer(), null, Dispute()), Web);

        var read = CoachConversationService.ReadOutcome(payload, version);

        if (readable)
        {
            read.Should().NotBeNull("version {0} must remain readable", version);
        }
        else
        {
            read.Should().BeNull("version {0} is not a version this build writes or reads", version);
        }
    }

    // ── The dispute section is tolerated, never fatal ────────────────────────

    /// <summary>
    /// An unreadable dispute must not take the answer down with it.
    /// </summary>
    /// <remarks>
    /// The same rule the trace follows, for the same reason: a dispute is state <em>about</em> the
    /// answer. A learner whose completed turn read back empty because a later build named one enum
    /// member this one does not would have lost the thing they came for, in order to protect a
    /// constraint on the next turn.
    /// </remarks>
    [Fact]
    public void An_unreadable_dispute_leaves_the_answer_intact()
    {
        var payload = "{\"answer\":"
            + JsonSerializer.Serialize(Answer(), Web)
            + ",\"trace\":null,\"dispute\":{\"signal\":\"SomeFutureCorrection\","
            + "\"disputedMessageId\":\"abc\",\"openedAtUtc\":\"2026-08-22T02:05:00Z\","
            + "\"resolvedAtUtc\":null,\"resolution\":\"Open\","
            + "\"disputedDefinitionCodes\":[]}}";

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read.Should().NotBeNull();
        read!.Answer.Should().NotBeNull("the diagnostic must not take the turn down with it");
        read.Dispute.Should().BeNull();
    }

    /// <summary>
    /// An oversized identifier is refused at the read boundary.
    /// </summary>
    /// <remarks>
    /// This is where a foreign payload crosses into the process. A dispute whose message identifier
    /// is longer than the ledger's own identifiers was not written by this system, and reading it
    /// would let a stored blob become a channel for prose the protected outcome may not hold.
    /// </remarks>
    [Fact]
    public void A_dispute_with_an_oversized_identifier_is_refused()
    {
        var oversized = new string('x', CoachTurnDisputeState.MaxDisputedMessageIdLength + 1);

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                Answer(),
                null,
                Dispute() with { DisputedMessageId = oversized }),
            Web);

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Answer.Should().NotBeNull();
        read.Dispute.Should().BeNull(
            "the bound is enforced where a foreign payload enters, not only where this build writes");
    }

    [Fact]
    public void A_dispute_with_an_undefined_resolution_ordinal_is_refused()
    {
        var payload = "{\"answer\":"
            + JsonSerializer.Serialize(Answer(), Web)
            + ",\"dispute\":{\"signal\":\"WrongClaim\",\"disputedMessageId\":\"abc\","
            + "\"openedAtUtc\":\"2026-08-22T02:05:00Z\",\"resolvedAtUtc\":null,"
            + "\"resolution\":97,\"disputedDefinitionCodes\":[]}}";

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Answer.Should().NotBeNull();
        read.Dispute.Should().BeNull(
            "System.Text.Json materialises any integer into an enum without throwing, so an "
            + "undefined ordinal has to be caught by a census rather than by the deserializer");
    }

    [Fact]
    public void A_dispute_with_a_blank_identifier_is_refused()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null, Dispute() with { DisputedMessageId = "  " }),
            Web);

        CoachConversationService.ReadOutcome(payload, 3)!.Dispute.Should().BeNull(
            "an unanchored dispute would constrain the next answer about nothing in particular");
    }

    /// <summary>A corrupt payload is still absent in full. Only sections are tolerated.</summary>
    [Fact]
    public void A_corrupt_payload_is_still_absent()
    {
        CoachConversationService.ReadOutcome("{not json", 3).Should().BeNull();
        CoachConversationService.ReadOutcome("[]", 3).Should().BeNull();
    }

    /// <summary>Round trip, including a resolved dispute with both timestamps.</summary>
    [Fact]
    public void A_resolved_dispute_round_trips()
    {
        var resolved = Dispute() with
        {
            Resolution = CoachDisputeResolution.ResolvedByReRead,
            ResolvedAtUtc = new DateTime(2026, 8, 22, 2, 9, 0, DateTimeKind.Utc)
        };

        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(), null, resolved), Web);

        var read = CoachConversationService.ReadOutcome(payload, 3);

        read!.Dispute.Should().BeEquivalentTo(resolved);
        read.Dispute!.IsOpen.Should().BeFalse();
    }
}
