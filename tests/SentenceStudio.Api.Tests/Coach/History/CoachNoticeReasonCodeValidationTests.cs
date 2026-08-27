using System;
using System.Linq;
using FluentAssertions;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// What reason codes the ledger will accept on a new notice.
/// </summary>
/// <remarks>
/// <para>
/// A notice is the only message kind that can stand for "your plan did not change", and the code is
/// the whole of that meaning — the text is prose, and clients localize from the code. An empty code
/// therefore reads back as a malformed record, and an invented one leaves every client silent about
/// data the learner will draw conclusions from. Both are refused on the way in, where the caller
/// still exists to be told.
/// </para>
/// <para>
/// Refused on <b>write</b> only. The read path never validates, so a row already on disk carrying a
/// code this build does not recognize stays readable; refusing to render a learner's own history is
/// the worse of the two failures, and
/// <see cref="CoachNoticeReasonCodes.IndicatesNoChange"/> is already closed against codes it does
/// not know.
/// </para>
/// </remarks>
public sealed class CoachNoticeReasonCodeValidationTests
{
    private static readonly DateTime At = new(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- accepted

    /// <summary>Every code the shared vocabulary publishes is writable.</summary>
    /// <remarks>
    /// Enumerated from <see cref="CoachNoticeReasonCodes.All"/> so a code added to the vocabulary
    /// without being admitted by the validator fails here rather than at a learner's turn.
    /// </remarks>
    [Fact]
    public void Every_code_in_the_shared_vocabulary_is_accepted()
    {
        foreach (var code in CoachNoticeReasonCodes.All)
        {
            CoachMessagePayloadSerializer.Validate(Notice(code)).IsValid.Should().BeTrue(
                $"'{code}' is published in the vocabulary, so a notice may be written with it");
        }
    }

    [Fact]
    public void The_recovered_code_is_writable_and_never_marks_a_turn_as_no_change()
    {
        CoachMessagePayloadSerializer.Validate(Notice(CoachNoticeReasonCodes.Recovered))
            .IsValid.Should().BeTrue();

        CoachNoticeReasonCodes.IndicatesNoChange(CoachNoticeReasonCodes.Recovered).Should().BeFalse(
            "a recovered turn is the one case where the plan definitely moved and the account of it did not");

        CoachNoticeReasonCodes.All.Should().Contain(CoachNoticeReasonCodes.Recovered,
            "the server authors this code, so every client must be able to interpret it");
    }

    [Fact]
    public void The_informational_default_never_marks_a_turn_as_no_change()
    {
        CoachNoticeReasonCodes.IndicatesNoChange(CoachNoticeReasonCodes.Default).Should().BeFalse();
        CoachMessagePayloadSerializer.Validate(Notice(CoachNoticeReasonCodes.Default))
            .IsValid.Should().BeTrue();
    }

    // ---------------------------------------------------------------- refused

    [Fact]
    public void An_empty_reason_code_is_refused()
    {
        var result = CoachMessagePayloadSerializer.Validate(Notice(string.Empty));

        result.IsValid.Should().BeFalse("a notice with no code reads back as a malformed record");
        result.Error.Should().Be(CoachPayloadValidationError.InvalidReasonCode);
        result.Field.Should().Be(nameof(CoachStoredNotice.ReasonCode));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("Cancelled")]
    [InlineData("CANCELLED")]
    [InlineData("cancelled ")]
    [InlineData("MetadataUnavailable")]
    [InlineData("no_change")]
    [InlineData("coach-notice")]
    public void An_off_vocabulary_reason_code_is_refused(string code)
    {
        var result = CoachMessagePayloadSerializer.Validate(Notice(code));

        result.IsValid.Should().BeFalse($"'{code}' is not a code any client can interpret");
        result.Error.Should().Be(CoachPayloadValidationError.InvalidReasonCode);
    }

    /// <summary>Case and whitespace are significant; the vocabulary is compared ordinally.</summary>
    /// <remarks>
    /// Near-misses are refused rather than repaired. Silently coercing "Cancelled" to "cancelled"
    /// would let a caller author codes it never verified, and the next near-miss would be one the
    /// coercion did not anticipate.
    /// </remarks>
    [Fact]
    public void A_code_that_differs_only_in_case_is_not_the_same_code()
    {
        CoachNoticeReasonCodes.IsKnown(CoachNoticeReasonCodes.Cancelled).Should().BeTrue();
        CoachNoticeReasonCodes.IsKnown("Cancelled").Should().BeFalse();
        CoachNoticeReasonCodes.IsKnown(null).Should().BeFalse();
    }

    /// <summary>A code past the length bound is still refused, by whichever rule catches it.</summary>
    [Fact]
    public void An_over_long_reason_code_is_refused()
    {
        var result = CoachMessagePayloadSerializer.Validate(
            Notice(new string('x', CoachHistoryLimits.ErrorCodeMaxLength + 1)));

        result.IsValid.Should().BeFalse();
        result.Field.Should().Be(nameof(CoachStoredNotice.ReasonCode));
    }

    [Fact]
    public void Serializing_an_invalid_notice_throws_rather_than_persisting_it()
    {
        var act = () => CoachMessagePayloadSerializer.Serialize(Notice("not_a_code"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{CoachPayloadValidationError.InvalidReasonCode}*");
    }

    /// <summary>The validation message names the field and never the value.</summary>
    /// <remarks>
    /// The field name is logged on rejection. A reason code is content-free by design, but the
    /// habit of never putting a payload value into a log line is what keeps it that way.
    /// </remarks>
    [Fact]
    public void The_rejection_names_the_field_and_not_the_value()
    {
        var result = CoachMessagePayloadSerializer.Validate(Notice("secret_looking_code"));

        result.Field.Should().Be(nameof(CoachStoredNotice.ReasonCode));
        result.Field.Should().NotContain("secret_looking_code");
    }

    // ---------------------------------------------------------------- read compatibility

    /// <summary>
    /// A row written by an older or newer build stays readable, code and all.
    /// </summary>
    /// <remarks>
    /// Deliberate asymmetry: writes are closed, reads are open. A learner opening last month's
    /// conversation must see what they were shown, even if this build cannot say whether that
    /// notice meant "no change applied".
    /// </remarks>
    [Fact]
    public void A_stored_row_carrying_an_unknown_code_is_still_readable()
    {
        const string json = """
            {"SchemaVersion":1,"Kind":"Notice","CreatedAtUtc":"2026-08-19T09:00:00Z",
             "Text":"Today's Plan is unchanged.",
             "Notice":{"ReasonCode":"legacy_code_from_another_build","Text":"Today's Plan is unchanged."}}
            """;

        CoachMessagePayloadSerializer.TryDeserialize(json, out var payload).Should().BeTrue(
            "history the learner already saw does not become unreadable because the vocabulary moved");

        payload!.Notice!.ReasonCode.Should().Be("legacy_code_from_another_build");

        CoachNoticeImplications(payload.Notice.ReasonCode).Should().BeFalse(
            "an unrecognized code leaves the client silent rather than asserting a non-event");
    }

    private static bool CoachNoticeImplications(string? code) =>
        CoachNoticeReasonCodes.IndicatesNoChange(code);

    // ---------------------------------------------------------------- other branches

    /// <summary>The new rule applies to notices only.</summary>
    [Fact]
    public void A_non_notice_payload_is_unaffected_by_the_reason_code_rule()
    {
        var text = new CoachMessagePayload
        {
            Kind = CoachMessagePayloadKind.CoachText,
            CreatedAtUtc = At,
            Text = "Understood."
        };

        CoachMessagePayloadSerializer.Validate(text).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_notice_payload_with_no_notice_branch_is_still_refused_as_a_missing_branch()
    {
        var payload = new CoachMessagePayload
        {
            Kind = CoachMessagePayloadKind.Notice,
            CreatedAtUtc = At,
            Text = "Today's Plan is unchanged."
        };

        var result = CoachMessagePayloadSerializer.Validate(payload);

        result.Error.Should().Be(CoachPayloadValidationError.MissingBranch);
    }

    private static CoachMessagePayload Notice(string reasonCode) => new()
    {
        Kind = CoachMessagePayloadKind.Notice,
        CreatedAtUtc = At,
        Text = "Today's Plan is unchanged.",
        Notice = new CoachStoredNotice
        {
            ReasonCode = reasonCode,
            Text = "Today's Plan is unchanged."
        }
    };
}
