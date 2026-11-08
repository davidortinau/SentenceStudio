using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The deterministic help route: "what can you help with?" is answered from the tool registry,
/// never reaches the model, and is stored in the conversation like any other answer.
/// </summary>
public class CoachHelpRoutePersistenceTests
{
    [Theory]
    [InlineData("What can you help with?")]
    [InlineData("help")]
    [InlineData("무엇을 도와줄 수 있어요?")]
    public async Task Help_is_answered_without_calling_the_model(string text)
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.TurnAsync(conversationId, text);

        result.IsOk.Should().BeTrue(result.Detail);
        harness.App.Coach.RunCount.Should().Be(0, "the help list is server-owned and costs no model run");

        var response = result.Value!.Result!;
        response.Answer.Should().NotBeNull();
        response.Answer!.Blocks.Count.Should().BeGreaterThan(2, "the answer lists several kinds of help");
        response.Answer.Blocks.Skip(1).Should().OnlyContain(b => b.Kind == CoachAnswerBlockKind.Note);
        response.ChangeReceipt.Should().BeNull("help changes nothing");
        response.PendingSuggestion.Should().BeNull("help proposes nothing");

        var coach = response.Messages.Where(m => m.Role == CoachMessageRole.Coach).ToList();
        coach.Should().ContainSingle();
        coach[0].Kind.Should().Be(CoachMessageKind.PedagogicalAnswer);
    }

    [Fact]
    public async Task Help_answer_is_in_the_ledger_after_a_restart()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await harness.TurnAsync(conversationId, "What can you help with?");
        harness.Restart();

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2, "one learner + one assistant");
        ledger[1].Payload!.Kind.Should().Be(CoachMessagePayloadKind.StructuredAnswer);
        ledger[1].Payload!.Answer!.Blocks.Should().HaveCountGreaterThan(2);
    }

    [Fact]
    public async Task A_language_request_that_mentions_help_still_goes_to_the_model()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await harness.TurnAsync(conversationId, "Can you help me understand -았-?");

        harness.App.Coach.RunCount.Should().Be(1);
    }
}
