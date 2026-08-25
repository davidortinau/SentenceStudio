using System.Text.Json;
using System.Text.Json.Serialization;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Pins the identity and role of every row a turn writes, on every surface that returns it.
/// </summary>
/// <remarks>
/// Written after a real end-to-end session reported the first message of a conversation rendering
/// as the coach when the ledger held it as the learner. The server side turned out to be right —
/// there is one <see cref="CoachMessageRole"/>, the entity and the public contract share it, and
/// no numeric cast sits between them — so these tests exist to keep it right and to make the
/// server's answer checkable rather than asserted.
/// <para>
/// They did surface a real defect next door: the same message answered to three different
/// identifiers depending on which surface a client asked. That is fixed, and pinned here.
/// </para>
/// </remarks>
public sealed class CoachHistoryRoleMappingTests
{
    private const string LearnerText = "how do I say hello?";
    private const string AnswerText = "\uC548\uB155\uD558\uC138\uC694 is the polite greeting.";

    // ---------------------------------------------------------------- role and order

    [Fact]
    public async Task Ledger_FirstRowIsLearner_SecondRowIsCoachAnswer()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);

        await harness.TurnAsync(id, LearnerText);

        var ledger = await harness.LedgerAsync(id);
        ledger.Should().HaveCount(2);

        ledger[0].Sequence.Should().Be(1);
        ledger[0].Role.Should().Be(CoachMessageRole.Learner);
        ledger[0].Kind.Should().Be(CoachMessageKind.Text);
        ledger[0].Payload!.Text.Should().Be(LearnerText);

        ledger[1].Sequence.Should().Be(2);
        ledger[1].Role.Should().Be(CoachMessageRole.Coach);
        ledger[1].Kind.Should().Be(CoachMessageKind.PedagogicalAnswer);
    }

    [Fact]
    public async Task MessagesPage_ReturnsLearnerThenCoach_InChronologicalOrder()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);

        await harness.TurnAsync(id, LearnerText);

        var page = await harness.Service.GetMessagesAsync(id, null, null, CancellationToken.None);
        page.IsOk.Should().BeTrue(page.Detail);

        var items = page.Value!.Items;
        items.Should().HaveCount(2);

        items.Select(i => i.Sequence).Should().BeInAscendingOrder();

        items[0].Message.Role.Should().Be(CoachMessageRole.Learner);
        items[0].Message.Kind.Should().Be(CoachMessageKind.Text);
        items[0].Message.Text.Should().Be(LearnerText);
        items[0].Answer.Should().BeNull("the learner's own message carries no structured answer");

        items[1].Message.Role.Should().Be(CoachMessageRole.Coach);
        items[1].Message.Kind.Should().Be(CoachMessageKind.PedagogicalAnswer);
        items[1].Answer.Should().NotBeNull();
    }

    [Fact]
    public async Task MessagesPage_OrderSurvivesSeveralTurns()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();

        for (var i = 1; i <= 3; i++)
        {
            ScriptAnswer(harness);
            await harness.TurnAsync(id, $"question {i}");
        }

        var page = await harness.Service.GetMessagesAsync(id, null, null, CancellationToken.None);
        var items = page.Value!.Items;

        items.Select(i => i.Sequence).Should().BeInAscendingOrder();
        items.Select(i => i.Message.Role).Should().Equal(
            CoachMessageRole.Learner, CoachMessageRole.Coach,
            CoachMessageRole.Learner, CoachMessageRole.Coach,
            CoachMessageRole.Learner, CoachMessageRole.Coach);
    }

    // ---------------------------------------------------------------- the structured answer

    [Fact]
    public async Task StructuredAnswer_IsStoredOnce_AndNotDuplicatedAsText()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);

        await harness.TurnAsync(id, LearnerText);

        var ledger = await harness.LedgerAsync(id);

        ledger.Count(m => m.Kind == CoachMessageKind.PedagogicalAnswer)
            .Should().Be(1, "one answer produces one answer row");

        ledger.Count(m => m.Payload?.Answer is not null)
            .Should().Be(1, "the structured payload rides on that one row and nowhere else");

        ledger.Where(m => m.Role == CoachMessageRole.Coach)
            .Should().ContainSingle("the coach said one thing this turn");
    }

    [Fact]
    public void KindFor_MapsEveryPayloadKindExplicitly()
    {
        // The ordinals of the two enums do not line up, which is exactly why this must stay a
        // switch and never become a cast.
        CoachHistoryProjection.KindFor(CoachMessagePayloadKind.StructuredAnswer)
            .Should().Be(CoachMessageKind.PedagogicalAnswer);
        CoachHistoryProjection.KindFor(CoachMessagePayloadKind.Receipt)
            .Should().Be(CoachMessageKind.Receipt);
        CoachHistoryProjection.KindFor(CoachMessagePayloadKind.SuggestionSnapshot)
            .Should().Be(CoachMessageKind.Suggestion);
        CoachHistoryProjection.KindFor(CoachMessagePayloadKind.Notice)
            .Should().Be(CoachMessageKind.Notice);
        CoachHistoryProjection.KindFor(CoachMessagePayloadKind.LearnerText)
            .Should().Be(CoachMessageKind.Text);
        CoachHistoryProjection.KindFor(CoachMessagePayloadKind.CoachText)
            .Should().Be(CoachMessageKind.Text);
    }

    // ---------------------------------------------------------------- timestamps

    [Fact]
    public async Task Timestamps_AreUtc_AndNonDecreasing()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);
        await harness.TurnAsync(id, LearnerText);
        ScriptAnswer(harness);
        await harness.TurnAsync(id, "and goodbye?");

        var page = await harness.Service.GetMessagesAsync(id, null, null, CancellationToken.None);
        var stamps = page.Value!.Items.Select(i => i.Message.CreatedAtUtc).ToArray();

        stamps.Should().OnlyContain(t => t.Kind == DateTimeKind.Utc);
        stamps.Should().BeInAscendingOrder();
    }

    // ---------------------------------------------------------------- identity

    [Fact]
    public async Task MessageIds_AreStableAcrossReadsAndRestart()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);
        await harness.TurnAsync(id, LearnerText);

        var first = await IdsAsync(harness, id);
        var second = await IdsAsync(harness, id);
        first.Should().Equal(second, "reading twice must not mint new identities");

        harness.Restart();

        var afterRestart = await IdsAsync(harness, id);
        afterRestart.Should().Equal(first, "the identity is durable, not per-process");
    }

    [Fact]
    public async Task MessageId_IsTheSameOnEverySurface()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);

        var turn = await harness.TurnAsync(id, LearnerText);
        turn.IsOk.Should().BeTrue(turn.Detail);

        // What the durable turn returned as its own message list.
        var live = turn.Value!.Result!.Messages.Select(m => m.MessageId).ToArray();
        live.Should().NotBeEmpty();

        // What the durable turn reported as the rows it wrote.
        var written = turn.Value!.Messages.Select(m => m.Message.MessageId).ToArray();

        // What a later page load returns.
        var page = await harness.Service.GetMessagesAsync(id, null, null, CancellationToken.None);
        var reloaded = page.Value!.Items
            .Where(i => i.Message.Role == CoachMessageRole.Coach)
            .Select(i => i.Message.MessageId)
            .ToArray();

        // What the old /sessions surface returns for the same rows.
        var session = await harness.App.Service.GetSessionAsync(id, CancellationToken.None);
        session.IsOk.Should().BeTrue(session.Detail);
        var legacy = session.Value!.Messages
            .Where(m => m.Role == CoachMessageRole.Coach)
            .Select(m => m.MessageId)
            .ToArray();

        live.Should().Equal(written, "the response and the rows it wrote are the same messages");
        live.Should().Equal(reloaded, "a reload must not rename the message the learner just saw");
        live.Should().Equal(legacy, "the compatibility surface answers for the same rows");
    }

    [Fact]
    public async Task LegacySessionSurface_AgreesWithHistoryOnRoleAndId()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);
        await harness.TurnAsync(id, LearnerText);

        var session = await harness.App.Service.GetSessionAsync(id, CancellationToken.None);
        var messages = session.Value!.Messages;

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(CoachMessageRole.Learner);
        messages[0].Text.Should().Be(LearnerText);
        messages[1].Role.Should().Be(CoachMessageRole.Coach);
        messages.Select(m => m.CreatedAtUtc).Should().OnlyContain(t => t.Kind == DateTimeKind.Utc);

        var page = await harness.Service.GetMessagesAsync(id, null, null, CancellationToken.None);
        page.Value!.Items.Select(i => i.Message.MessageId)
            .Should().Equal(messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task OperationId_IsStable_AndMatchesTheRowsItWrote()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);

        var operationId = Guid.NewGuid().ToString("N");
        var turn = await harness.TurnAsync(id, LearnerText, operationId: operationId);

        turn.Value!.OperationId.Should().Be(operationId, "the client chose it, so a lost response can still poll");

        var ledger = await harness.LedgerAsync(id);
        ledger.Should().OnlyContain(m => m.OperationId == operationId);

        harness.Restart();

        var polled = await harness.Service.GetOperationAsync(id, operationId, CancellationToken.None);
        polled.IsOk.Should().BeTrue(polled.Detail);
        polled.Value!.OperationId.Should().Be(operationId);
        polled.Value!.Messages.Select(m => m.Message.MessageId)
            .Should().Equal(turn.Value!.Messages.Select(m => m.Message.MessageId));
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public async Task Wire_WritesRoleAndKindAsNames_NotOrdinals()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);
        await harness.TurnAsync(id, LearnerText);

        var page = await harness.Service.GetMessagesAsync(id, null, null, CancellationToken.None);
        var json = JsonSerializer.Serialize(page.Value, WireOptions);

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("items");

        items.GetArrayLength().Should().Be(2);

        var first = items[0].GetProperty("message");
        first.GetProperty("role").GetString().Should().Be("Learner");
        first.GetProperty("kind").GetString().Should().Be("Text");
        first.GetProperty("text").GetString().Should().Be(LearnerText);

        var second = items[1].GetProperty("message");
        second.GetProperty("role").GetString().Should().Be("Coach");
        second.GetProperty("kind").GetString().Should().Be("PedagogicalAnswer");

        json.Should().NotContain("\"role\":0").And.NotContain("\"role\":1");
    }

    [Fact]
    public async Task Wire_RoundTripsBackToTheSameRoles()
    {
        using var harness = new CoachConversationHarness();
        var id = await harness.CreateConversationAsync();
        ScriptAnswer(harness);
        await harness.TurnAsync(id, LearnerText);

        var page = await harness.Service.GetMessagesAsync(id, null, null, CancellationToken.None);
        var json = JsonSerializer.Serialize(page.Value, WireOptions);
        var back = JsonSerializer.Deserialize<CoachMessagePageDto>(json, WireOptions);

        back.Should().NotBeNull();
        back!.Items.Select(i => i.Message.Role).Should().Equal(
            CoachMessageRole.Learner, CoachMessageRole.Coach);
        back.Items.Select(i => i.Message.Kind).Should().Equal(
            CoachMessageKind.Text, CoachMessageKind.PedagogicalAnswer);
        back.Items[0].Message.Text.Should().Be(LearnerText);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The options the API is configured with, so these assertions describe the bytes a client
    /// actually receives rather than serializer defaults.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<string[]> IdsAsync(CoachConversationHarness harness, string conversationId)
    {
        var page = await harness.Service.GetMessagesAsync(conversationId, null, null, CancellationToken.None);
        page.IsOk.Should().BeTrue(page.Detail);
        return page.Value!.Items.Select(i => i.Message.MessageId).ToArray();
    }

    private static void ScriptAnswer(CoachConversationHarness harness) =>
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                CoachMessage = AnswerText,
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent
                {
                    Topic = CoachAnswerTopic.Vocabulary,
                    Blocks =
                    [
                        new CoachAnswerBlockIntent
                        {
                            Kind = CoachAnswerBlockKind.Answer,
                            Spans =
                            [
                                new CoachAnswerSpanIntent
                                {
                                    Text = AnswerText,
                                    Language = CoachLanguageRole.Display
                                }
                            ]
                        }
                    ]
                }
            }
        };
}
