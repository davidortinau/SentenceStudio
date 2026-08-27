using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// Pins the wire contract for a proposed change: what it carries, how it serializes, and what an
/// unset value means.
/// </summary>
/// <remarks>
/// <para>
/// The card a learner approves is built entirely from these fields, so a member that silently
/// stops round-tripping does not produce a compile error or a failed request — it produces a
/// button that does the wrong thing, or a receipt that describes nothing. That is the failure
/// class these exist to catch.
/// </para>
/// <para>
/// The privacy and enum rules that apply to every coach contract are enforced by
/// <c>CoachContractPrivacyTests</c> and <c>CoachEnumContractTests</c>, which discover these types
/// by namespace. Nothing is repeated here.
/// </para>
/// </remarks>
public class CoachWriteContractSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static CoachWriteOperationDto Proposal() => new()
    {
        OperationId = "op-1",
        ConversationId = "conv-1",
        TurnId = "turn-1",
        MessageId = "msg-9",
        ChangeKind = CoachWriteChangeKind.VocabularyAdd,
        RiskClass = CoachWriteRiskClass.WriteSoft,
        Status = CoachWriteStatus.Proposed,
        ApprovalMode = "accept",
        Summary = "Add 사과 to your words",
        Lines = ["Term: 사과", "Meaning: apple"],
        ExpiresAtUtc = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc),
        RequiresConfirmation = false,
        IsReversible = true
    };

    private static CoachWriteReceiptDto Receipt() => new()
    {
        OperationId = "op-1",
        ChangeKind = CoachWriteChangeKind.VocabularyAdd,
        RiskClass = CoachWriteRiskClass.WriteSoft,
        Status = CoachWriteStatus.Executed,
        TargetKind = CoachWriteTargetKind.VocabularyWord,
        TargetId = "word-7",
        Summary = "Added 사과 to your words",
        Lines = ["Term: 사과"],
        ExecutedAtUtc = new DateTime(2026, 8, 19, 11, 30, 0, DateTimeKind.Utc),
        CanUndo = true,
        UndoExpiresAtUtc = new DateTime(2026, 8, 19, 11, 35, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void A_proposal_round_trips_without_loss()
    {
        var original = Proposal();

        var restored = JsonSerializer.Deserialize<CoachWriteOperationDto>(
            JsonSerializer.Serialize(original, Options), Options);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void A_proposal_carrying_its_receipt_round_trips_without_loss()
    {
        var original = Proposal();

        var executed = new CoachWriteOperationDto
        {
            OperationId = original.OperationId,
            ConversationId = original.ConversationId,
            TurnId = original.TurnId,
            MessageId = original.MessageId,
            ChangeKind = original.ChangeKind,
            RiskClass = original.RiskClass,
            Status = CoachWriteStatus.Executed,
            ApprovalMode = original.ApprovalMode,
            Summary = original.Summary,
            Lines = original.Lines,
            ExpiresAtUtc = original.ExpiresAtUtc,
            IsReversible = true,
            AlreadyExecuted = true,
            Receipt = Receipt()
        };

        var restored = JsonSerializer.Deserialize<CoachWriteOperationDto>(
            JsonSerializer.Serialize(executed, Options), Options);

        restored.Should().BeEquivalentTo(executed);
        restored!.Receipt!.CanUndo.Should().BeTrue();
    }

    [Fact]
    public void A_turn_response_carries_its_proposal_across_the_wire()
    {
        var turn = CoachContractSamples.TurnResponse();
        var withWrite = turn.WithWriteOperation(Proposal());

        var restored = JsonSerializer.Deserialize<CoachTurnResponse>(
            JsonSerializer.Serialize(withWrite, Options), Options);

        restored!.WriteOperation.Should().BeEquivalentTo(Proposal());
    }

    /// <summary>
    /// The one member that makes a card survive a reload. A turn response is transient; the
    /// durable history row is what a second device, a refresh, and a route change all read.
    /// </summary>
    [Fact]
    public void A_history_message_carries_its_proposal_across_the_wire()
    {
        var item = new CoachHistoryMessageDto
        {
            Message = new CoachMessageDto
            {
                MessageId = "msg-9",
                Role = CoachMessageRole.Coach,
                Kind = CoachMessageKind.Text,
                Text = "I can add that word for you.",
                CreatedAtUtc = new DateTime(2026, 8, 19, 11, 0, 0, DateTimeKind.Utc)
            },
            Sequence = 4,
            WriteOperation = Proposal()
        };

        var restored = JsonSerializer.Deserialize<CoachHistoryMessageDto>(
            JsonSerializer.Serialize(item, Options), Options);

        restored!.WriteOperation!.OperationId.Should().Be("op-1");
        restored.WriteOperation.MessageId.Should().Be("msg-9",
            "the anchor is what places the card back in the exchange that produced it");
    }

    [Fact]
    public void Enums_travel_as_names_not_numbers()
    {
        var json = JsonSerializer.Serialize(Proposal(), Options);

        json.Should().Contain("\"VocabularyAdd\"").And.Contain("\"WriteSoft\"").And.Contain("\"Proposed\"");
        json.Should().NotContain("\"changeKind\":0");
    }

    /// <summary>
    /// The zero value of every write enum has to be the one that offers nothing.
    /// </summary>
    /// <remarks>
    /// An unset status must not read as applied, an unset risk class must not read as an ordinary
    /// acceptance, and an unrecognised kind must not read as a specific change. All three would be
    /// silent: the card would render, look plausible, and describe something that is not true.
    /// </remarks>
    [Fact]
    public void The_unset_value_of_every_write_enum_is_the_one_that_offers_nothing()
    {
        default(CoachWriteStatus).Should().Be(CoachWriteStatus.Unknown);
        default(CoachWriteRiskClass).Should().Be(CoachWriteRiskClass.Unknown);
        default(CoachWriteChangeKind).Should().Be(CoachWriteChangeKind.Unknown);
        default(CoachWriteTargetKind).Should().Be(CoachWriteTargetKind.None);
    }

    /// <summary>
    /// A status this build does not know is refused outright rather than coerced.
    /// </summary>
    /// <remarks>
    /// The set is closed on purpose, and the client's fail-closed behaviour is one layer up: a
    /// payload that will not deserialize leaves the card absent, which is honest. Silently mapping
    /// an unknown name onto a known one is the failure this refuses.
    /// </remarks>
    [Fact]
    public void An_unknown_status_name_is_refused()
    {
        var act = () => JsonSerializer.Deserialize<CoachWriteStatus>("\"Applied\"", Options);

        act.Should().Throw<JsonException>();
    }

    /// <summary>
    /// Nothing on the wire may carry, or look like it carries, the one-use confirmation.
    /// </summary>
    /// <remarks>
    /// The general contract scan already refuses credential-shaped member names on every coach
    /// contract. This is the same rule stated where a reader of the write feature will look for
    /// it, over a fully-populated instance rather than over the type: it fails if a future member
    /// starts carrying the value under an innocuous name.
    /// </remarks>
    [Fact]
    public void A_serialized_proposal_carries_nothing_that_could_approve_it()
    {
        var json = JsonSerializer.Serialize(
            new CoachWriteOperationDto
            {
                OperationId = "op-1",
                ConversationId = "conv-1",
                TurnId = "turn-1",
                ChangeKind = CoachWriteChangeKind.VocabularyRemove,
                RiskClass = CoachWriteRiskClass.WriteHard,
                Status = CoachWriteStatus.Proposed,
                ApprovalMode = "confirm",
                Summary = "Remove a word",
                Lines = ["This cannot be undone."],
                ExpiresAtUtc = DateTime.UtcNow,
                RequiresConfirmation = true,
                ConfirmationExpiresAtUtc = DateTime.UtcNow.AddMinutes(2)
            },
            Options);

        foreach (var word in new[] { "secret", "token", "credential", "password", "digest", "protected" })
        {
            json.ToLowerInvariant().Should().NotContain(
                word,
                "a proposal must never carry anything that could approve it, or any hint of how it is stored");
        }
    }
}
