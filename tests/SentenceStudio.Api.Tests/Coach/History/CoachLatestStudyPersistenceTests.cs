using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Application.Practice;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Proves that the deterministic latest-study route durably persists its answer through the
/// conversation ledger, exactly as the model-driven pedagogical answer path does.
/// </summary>
/// <remarks>
/// The defect: <c>HandleLatestStudyAsync</c> passed <c>messages: []</c> to
/// <c>BuildTurnResponseAsync</c>, so the <c>CoachHistoryProjection.ResponseMessages</c> loop
/// iterated zero times, the ledger got zero assistant rows, and GET messages showed only the
/// learner's question. The fix passes
/// <c>[CoachMessage(PedagogicalAnswer, answer.PlainText)]</c> — identical to
/// <c>ReduceAnswerAsync</c>.
/// </remarks>
public class CoachLatestStudyPersistenceTests
{
    [Fact]
    public async Task Latest_study_response_carries_answer_and_one_pedagogical_message()
    {
        using var harness = NewHarness(lastPracticeUtc: FiveDaysAgo);
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.TurnAsync(conversationId, "When did I last study?");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.State.Should().Be(CoachTurnOperationState.Completed);

        var response = result.Value.Result!;
        response.Answer.Should().NotBeNull("the structured answer must be on the immediate response");
        response.Answer!.PlainText.Should().NotBeNullOrWhiteSpace();
        response.Answer.Blocks.Should().ContainSingle();

        var assistantMessages = response.Messages
            .Where(m => m.Role == CoachMessageRole.Coach)
            .ToList();
        assistantMessages.Should().ContainSingle("exactly one coach message must be present");
        assistantMessages[0].Kind.Should().Be(CoachMessageKind.PedagogicalAnswer);
        assistantMessages[0].Text.Should().Be(response.Answer.PlainText);

        response.Evidence.Should().NotBeEmpty("evidence must be attached");
    }

    [Fact]
    public async Task Ledger_contains_learner_plus_one_assistant_after_latest_study()
    {
        using var harness = NewHarness(lastPracticeUtc: FiveDaysAgo);
        var conversationId = await harness.CreateConversationAsync();

        await harness.TurnAsync(conversationId, "When did I last study?");

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2, "one learner + one assistant");

        ledger[0].Payload!.Kind.Should().Be(CoachMessagePayloadKind.LearnerText);
        ledger[1].Payload!.Kind.Should().Be(CoachMessagePayloadKind.StructuredAnswer);
        ledger[1].Payload!.Text.Should().NotBeNullOrWhiteSpace();
        ledger[1].Payload!.Answer.Should().NotBeNull("the structured answer block must be stored");
    }

    [Fact]
    public async Task Reload_reproduces_the_same_assistant_message()
    {
        using var harness = NewHarness(lastPracticeUtc: FiveDaysAgo);
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.TurnAsync(conversationId, "When did I last study?");
        var immediateText = result.Value!.Result!.Messages
            .First(m => m.Role == CoachMessageRole.Coach).Text;

        harness.Restart();

        var ledger = await harness.LedgerAsync(conversationId);
        var assistantRow = ledger.Single(m => m.Payload!.Kind == CoachMessagePayloadKind.StructuredAnswer);
        assistantRow.Payload!.Text.Should().Be(immediateText,
            "the ledger must reproduce the exact answer text after a restart");
    }

    [Fact]
    public async Task Idempotent_replay_does_not_duplicate_the_ledger_row()
    {
        using var harness = NewHarness(lastPracticeUtc: FiveDaysAgo);
        var conversationId = await harness.CreateConversationAsync();

        var first = await harness.TurnAsync(conversationId, "When did I last study?", idempotencyKey: "ls-1");
        var second = await harness.TurnAsync(conversationId, "When did I last study?", idempotencyKey: "ls-1");

        first.IsOk.Should().BeTrue(first.Detail);
        second.IsOk.Should().BeTrue(second.Detail);
        second.Value!.OperationId.Should().Be(first.Value!.OperationId);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2, "replay must not append a duplicate assistant row");
    }

    [Fact]
    public async Task Correction_variant_persists_one_assistant_message()
    {
        using var harness = NewHarness(lastPracticeUtc: FiveDaysAgo);
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.TurnAsync(
            conversationId, "That's wrong, I practiced yesterday");

        result.IsOk.Should().BeTrue(result.Detail);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2);
        ledger[1].Payload!.Kind.Should().Be(CoachMessagePayloadKind.StructuredAnswer);
        ledger[1].Payload!.Text.Should().Contain("Let me check again",
            "the English correction preamble must appear in the stored text");
    }

    [Fact]
    public async Task No_data_variant_persists_one_assistant_message()
    {
        using var harness = NewHarness(lastPracticeUtc: null);
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.TurnAsync(conversationId, "When was my last study session?");

        result.IsOk.Should().BeTrue(result.Detail);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2);
        ledger[1].Payload!.Kind.Should().Be(CoachMessagePayloadKind.StructuredAnswer);
        ledger[1].Payload!.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cross_owner_cannot_read_latest_study_messages()
    {
        using var harness = NewHarness(lastPracticeUtc: FiveDaysAgo);
        var conversationId = await harness.CreateConversationAsync();
        await harness.TurnAsync(conversationId, "When did I last study?");

        var intruderPage = await harness.Messages.GetLatestAsync(
            harness.Intruder, conversationId, CoachHistoryLimits.MessagePageMax);

        intruderPage.Items.Should().BeEmpty(
            "a different owner must not see another learner's messages");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly DateTime FiveDaysAgo =
        new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private static CoachConversationHarness NewHarness(DateTime? lastPracticeUtc)
        => new(practiceHistory: new StubPracticeHistoryQueries(lastPracticeUtc));

    /// <summary>
    /// Minimal stub that returns a canned last-practice date.
    /// Only <see cref="GetLastPracticeUtcAsync"/> is called by the deterministic route.
    /// </summary>
    private sealed class StubPracticeHistoryQueries(DateTime? lastPractice) : IPracticeHistoryQueries
    {
        public Task<DateTime?> GetLastPracticeUtcAsync(
            string userProfileId, CancellationToken cancellationToken = default)
            => Task.FromResult(lastPractice);

        public Task<IReadOnlyList<PracticeCompletionFacts>> GetCompletionsInRangeAsync(
            string userProfileId, DateTime startUtcInclusive, DateTime endUtcExclusive,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PracticeCompletionFacts>>([]);

        public Task<int> CountActivityAttemptsAsync(
            string userProfileId, DateTime startUtcInclusive, DateTime endUtcExclusive,
            CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, DateTime>> GetResourceLastUsedAsync(
            string userProfileId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, DateTime>>(
                new Dictionary<string, DateTime>());

        public Task<DateTime?> GetResourceLastUsedAsync(
            string userProfileId, string resourceId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DateTime?>(null);

        public Task<DailyPlanFacts?> GetPlanForDateAsync(
            string userProfileId, DateTime planDateUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DailyPlanFacts?>(null);

        public Task<IReadOnlyList<PlanItemFacts>> GetPlanItemsForDateAsync(
            string userProfileId, DateTime planDateUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlanItemFacts>>([]);
    }
}
