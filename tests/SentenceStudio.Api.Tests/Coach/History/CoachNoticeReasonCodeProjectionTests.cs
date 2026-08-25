using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// What reason code a notice is durably stored with.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CoachHistoryProjection.ResponseMessages"/> is the only place a turn's notice acquires
/// a durable reason code, and it is the last place the turn is still whole. Once the rows reach the
/// ledger they are renumbered into a flat sequence, so nothing downstream can tell that a receipt
/// row and a notice row came from the same turn — a client reading history back cannot re-derive
/// "the turn stopped badly but it did write something". That resolution has to be correct here or
/// it is not recoverable anywhere.
/// </para>
/// <para>
/// These tests read the stored payload, not the mapping helper: they call the projection with a
/// whole <see cref="CoachTurnResponse"/> and assert on
/// <see cref="CoachStoredNotice.ReasonCode"/>. Substituting
/// <see cref="CoachNoticeReasonCodes.FromStopReason"/> for
/// <see cref="CoachNoticeReasonCodes.ForNotice"/> inside the projection fails every receipt-bearing
/// case below, which is the regression they exist to catch.
/// </para>
/// </remarks>
public sealed class CoachNoticeReasonCodeProjectionTests
{
    private static readonly DateTime At = new(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- receipt precedence

    /// <summary>
    /// Every refusal-shaped stop reason, with nothing written: the notice names the refusal.
    /// </summary>
    [Theory]
    [InlineData(CoachStopReason.Cancelled, CoachNoticeReasonCodes.Cancelled)]
    [InlineData(CoachStopReason.RateLimit, CoachNoticeReasonCodes.RateLimited)]
    [InlineData(CoachStopReason.Timeout, CoachNoticeReasonCodes.Timeout)]
    [InlineData(CoachStopReason.InputRejected, CoachNoticeReasonCodes.InputRejected)]
    [InlineData(CoachStopReason.ValidationFailed, CoachNoticeReasonCodes.ValidationFailed)]
    [InlineData(CoachStopReason.ToolFailure, CoachNoticeReasonCodes.ToolFailure)]
    [InlineData(CoachStopReason.IterationLimit, CoachNoticeReasonCodes.IterationLimit)]
    [InlineData(CoachStopReason.OutputTokenLimit, CoachNoticeReasonCodes.OutputTokenLimit)]
    [InlineData(CoachStopReason.ConcurrencyLimit, CoachNoticeReasonCodes.ConcurrencyLimit)]
    [InlineData(CoachStopReason.SessionExpired, CoachNoticeReasonCodes.SessionExpired)]
    [InlineData(CoachStopReason.Failed, CoachNoticeReasonCodes.Failed)]
    public void A_refusal_that_wrote_nothing_stores_the_code_for_that_refusal(
        CoachStopReason stopReason,
        string expected)
    {
        var stored = StoredNotice(Response(stopReason, receipt: null));

        stored.ReasonCode.Should().Be(expected);
        CoachNoticeReasonCodes.IndicatesNoChange(stored.ReasonCode).Should().BeTrue(
            "a refusal that wrote nothing is exactly the case the no-change marker exists for");
    }

    /// <summary>
    /// The same refusals, with a change receipt on the turn: the notice is informational.
    /// </summary>
    /// <remarks>
    /// This is the discriminating half. A turn that applied a plan change and then failed on a later
    /// step has moved the learner's data; storing the refusal code would make history assert that
    /// nothing happened, about data the learner can go and look at. Reading the stop reason alone
    /// cannot tell these apart from the cases above.
    /// </remarks>
    [Theory]
    [InlineData(CoachStopReason.Cancelled)]
    [InlineData(CoachStopReason.RateLimit)]
    [InlineData(CoachStopReason.Timeout)]
    [InlineData(CoachStopReason.InputRejected)]
    [InlineData(CoachStopReason.ValidationFailed)]
    [InlineData(CoachStopReason.ToolFailure)]
    [InlineData(CoachStopReason.IterationLimit)]
    [InlineData(CoachStopReason.OutputTokenLimit)]
    [InlineData(CoachStopReason.ConcurrencyLimit)]
    [InlineData(CoachStopReason.SessionExpired)]
    [InlineData(CoachStopReason.Failed)]
    public void A_refusal_that_still_wrote_a_receipt_stores_the_informational_code(
        CoachStopReason stopReason)
    {
        var stored = StoredNotice(Response(stopReason, receipt: Receipt()));

        stored.ReasonCode.Should().Be(
            CoachNoticeReasonCodes.Default,
            "the turn changed the plan, so the notice reports a problem rather than a non-event");
        CoachNoticeReasonCodes.IndicatesNoChange(stored.ReasonCode).Should().BeFalse(
            "telling a learner nothing changed after their plan changed is the more dangerous error");
    }

    [Fact]
    public void A_completed_turn_stores_the_informational_code()
    {
        StoredNotice(Response(CoachStopReason.Completed, receipt: null))
            .ReasonCode.Should().Be(CoachNoticeReasonCodes.Default);
    }

    [Fact]
    public void A_clarification_request_stores_the_informational_code()
    {
        StoredNotice(Response(CoachStopReason.ClarificationRequested, receipt: null))
            .ReasonCode.Should().Be(CoachNoticeReasonCodes.Default);
    }

    // ---------------------------------------------------------------- exhaustive

    /// <summary>
    /// Every member of <see cref="CoachStopReason"/>, both with and without a receipt.
    /// </summary>
    /// <remarks>
    /// Enumerates the enum rather than listing it, so a member added later arrives here already
    /// covered instead of quietly falling through to the projection's default arm.
    /// </remarks>
    [Fact]
    public void Every_stop_reason_stores_a_code_in_the_shared_vocabulary()
    {
        foreach (var reason in Enum.GetValues<CoachStopReason>())
        {
            var withoutReceipt = StoredNotice(Response(reason, receipt: null)).ReasonCode;
            var withReceipt = StoredNotice(Response(reason, receipt: Receipt())).ReasonCode;

            CoachNoticeReasonCodes.All.Should().Contain(withoutReceipt,
                $"{reason} without a receipt must store a code every client can interpret");
            CoachNoticeReasonCodes.All.Should().Contain(withReceipt,
                $"{reason} with a receipt must store a code every client can interpret");

            withoutReceipt.Should().Be(
                CoachNoticeReasonCodes.FromStopReason(reason),
                $"{reason} with nothing written is named by the stop reason");
            withReceipt.Should().Be(
                CoachNoticeReasonCodes.Default,
                $"{reason} with a receipt is informational regardless of how the turn ended");
        }
    }

    /// <summary>
    /// The two halves genuinely disagree, so the projection cannot satisfy both with one rule.
    /// </summary>
    /// <remarks>
    /// A guard on the guard: if the vocabulary ever collapsed such that every refusal code equalled
    /// <see cref="CoachNoticeReasonCodes.Default"/>, the theories above would pass while asserting
    /// nothing. This proves at least one reason produces different stored codes on the two paths.
    /// </remarks>
    [Fact]
    public void The_receipt_changes_the_stored_code_for_at_least_one_refusal()
    {
        var divergent = Enum.GetValues<CoachStopReason>()
            .Where(reason =>
                StoredNotice(Response(reason, receipt: null)).ReasonCode !=
                StoredNotice(Response(reason, receipt: Receipt())).ReasonCode)
            .ToList();

        divergent.Should().NotBeEmpty(
            "receipt precedence is only meaningful if the two paths can produce different codes");
        divergent.Should().Contain(CoachStopReason.ValidationFailed);
    }

    // ---------------------------------------------------------------- shape

    [Fact]
    public void A_notice_row_is_stored_as_a_notice_payload_carrying_the_visible_text()
    {
        var payloads = CoachHistoryProjection.ResponseMessages(
            Response(CoachStopReason.ValidationFailed, receipt: null));

        var notice = payloads.Should().ContainSingle(p => p.Kind == CoachMessagePayloadKind.Notice).Subject;

        notice.Notice.Should().NotBeNull();
        notice.Notice!.Text.Should().Be(NoticeText);
        notice.Text.Should().Be(NoticeText, "the flattened text is what a client without the branch renders");
    }

    /// <summary>
    /// A stored notice is accepted by the write-path validator on every reachable path.
    /// </summary>
    /// <remarks>
    /// The projection and the payload validator are the two halves of a single contract: the
    /// projection may only author codes the validator will accept, or a real turn would be refused
    /// at the ledger and the learner would lose the notice entirely.
    /// </remarks>
    [Fact]
    public void Every_projected_notice_passes_payload_validation()
    {
        foreach (var reason in Enum.GetValues<CoachStopReason>())
        {
            foreach (var receipt in new[] { null, Receipt() })
            {
                var payloads = CoachHistoryProjection.ResponseMessages(Response(reason, receipt));

                foreach (var payload in payloads)
                {
                    CoachMessagePayloadSerializer.Validate(payload).IsValid.Should().BeTrue(
                        $"{reason} produced a payload the ledger would refuse");
                }
            }
        }
    }

    /// <summary>The learner's own message is never echoed back into the ledger.</summary>
    [Fact]
    public void The_learners_own_message_is_not_projected_again()
    {
        var response = Response(CoachStopReason.Completed, receipt: null, includeLearnerEcho: true);

        CoachHistoryProjection.ResponseMessages(response)
            .Should().NotContain(p => p.Kind == CoachMessagePayloadKind.LearnerText);
    }

    // ---------------------------------------------------------------- builders

    private const string NoticeText = "Today's Plan is unchanged.";

    private static CoachStoredNotice StoredNotice(CoachTurnResponse response)
    {
        var payloads = CoachHistoryProjection.ResponseMessages(response);
        var notice = payloads.Single(p => p.Kind == CoachMessagePayloadKind.Notice);
        notice.Notice.Should().NotBeNull();
        return notice.Notice!;
    }

    private static CoachTurnResponse Response(
        CoachStopReason stopReason,
        CoachChangeReceiptDto? receipt,
        bool includeLearnerEcho = false)
    {
        var messages = new List<CoachMessageDto>();

        var constraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 10,
            AudioAllowed = true,
            SpeechAllowed = true,
            TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        };

        if (includeLearnerEcho)
        {
            messages.Add(new CoachMessageDto
            {
                MessageId = "m-learner",
                Role = CoachMessageRole.Learner,
                Kind = CoachMessageKind.Text,
                Text = "Shorten today",
                CreatedAtUtc = At
            });
        }

        if (receipt is not null)
        {
            messages.Add(new CoachMessageDto
            {
                MessageId = "m-receipt",
                Role = CoachMessageRole.Coach,
                Kind = CoachMessageKind.Receipt,
                Text = "I shortened today's plan.",
                CreatedAtUtc = At,
                RelatedReceiptId = receipt.ReceiptId
            });
        }

        messages.Add(new CoachMessageDto
        {
            MessageId = "m-notice",
            Role = CoachMessageRole.Coach,
            Kind = CoachMessageKind.Notice,
            Text = NoticeText,
            CreatedAtUtc = At
        });

        return new CoachTurnResponse
        {
            SessionId = "session-1",
            TurnId = "turn-1",
            Status = CoachTurnStatus.Completed,
            StopReason = stopReason,
            SessionStatus = CoachSessionStatus.Active,
            Messages = messages,
            ActiveConstraints = constraints,
            PlanState = new CoachPlanStateDto
            {
                PlanDate = DateOnly.FromDateTime(At),
                PlanVersion = "v1",
                AppliedConstraints = constraints,
                EstimatedTotalMinutes = 10,
                CompletedCount = 0,
                TotalCount = 3,
                CompletionPercentage = 0
            },
            ChangeReceipt = receipt,
            ClarificationsRemaining = 2,
            ExpiresAtUtc = At.AddHours(24)
        };
    }

    private static CoachChangeReceiptDto Receipt() => new()
    {
        ReceiptId = "receipt-1",
        Revision = new CoachRevisionDto
        {
            RevisionId = "rev-1",
            RevisionNumber = 1,
            Source = CoachRevisionSource.DirectRequest,
            Summary = "Shortened remaining items",
            BeforePlanVersion = "v1",
            AfterPlanVersion = "v2",
            CreatedAtUtc = At,
            CanUndo = true
        },
        Summary = "Shortened remaining items",
        AppliedDelta = new CoachConstraintDeltaDto(),
        Diff = new CoachPlanDiffDto
        {
            BeforePlanVersion = "v1",
            AfterPlanVersion = "v2",
            IsPreview = false,
            EstimatedMinutesBefore = 20,
            EstimatedMinutesAfter = 10
        },
        ReplacedItemCount = 3,
        PreservedCompletedItemCount = 2,
        PreservedInProgressItemCount = 0,
        PreservedMinutesSpent = 12,
        CanUndo = true,
        UndoLabel = "Undo"
    };
}
