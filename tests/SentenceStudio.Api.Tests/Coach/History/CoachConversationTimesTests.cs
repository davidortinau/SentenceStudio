using SentenceStudio.Api.Coach.Agents;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Issue 268, follow-up. Sam is given the conversation so far, and until now nothing in it said
/// when anything was said: an earlier subject from last week read the same as one from a minute
/// ago. These pin that the message the model receives carries the time of the turn being answered
/// and, when the conversation is rebuilt from the ledger, the time of each earlier message, in the
/// learner's time zone. Every test builds against the code before the change and fails there.
/// </summary>
public class CoachConversationTimesTests
{
    private static readonly DateTime Start = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The message the model is handed for the last turn the harness ran.</summary>
    private static string LastPrompt(CoachConversationHarness harness) =>
        CoachInstructions.BuildTurnMessage(harness.Coach.Requests[^1]);

    [Fact]
    public async Task A_turn_tells_the_model_when_the_learner_sent_it()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        LastPrompt(harness).Should().Contain(
            "This message was sent (learner local time): 2026-08-14 12:00, Friday (UTC+00:00)");
    }

    [Fact]
    public async Task The_time_of_a_turn_is_in_the_learners_time_zone()
    {
        using var harness = new CoachConversationHarness();
        harness.App.DateContext.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        LastPrompt(harness).Should().Contain(
            "This message was sent (learner local time): 2026-08-14 21:00, Friday (UTC+09:00)");
    }

    [Fact]
    public async Task A_rebuilt_conversation_carries_the_time_of_each_earlier_message()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        // Two days on the checkpoint has expired, so this turn is rebuilt from the ledger.
        harness.Time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(185));
        harness.App.DateContext.UtcNow = harness.Time.GetUtcNow().UtcDateTime;

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Second answer.");
        await harness.TurnAsync(conversationId, "Second question");

        harness.Time.GetUtcNow().UtcDateTime.Should().Be(Start.AddDays(2).AddMinutes(185));

        var prompt = LastPrompt(harness);

        prompt.Should().Contain("[2026-08-14 12:00] learner: First question");
        prompt.Should().Contain("[2026-08-14 12:00] coach: First answer.");
        prompt.Should().Contain(
            "This message was sent (learner local time): 2026-08-16 15:05, Sunday (UTC+00:00)",
            "the model needs this turn's time to know how old the earlier lines are");
    }

    [Fact]
    public async Task The_times_of_a_rebuilt_conversation_are_in_the_learners_time_zone()
    {
        using var harness = new CoachConversationHarness();
        harness.App.DateContext.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        harness.Time.Advance(TimeSpan.FromDays(2));

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Second answer.");
        await harness.TurnAsync(conversationId, "Second question");

        var prompt = LastPrompt(harness);

        prompt.Should().Contain("[2026-08-14 21:00] learner: First question");
        prompt.Should().Contain("[2026-08-14 21:00] coach: First answer.");
    }

    [Fact]
    public void The_standing_rules_say_what_the_times_are_and_that_an_old_subject_is_history()
    {
        var rules = CoachInstructions.Instructions;

        rules.Should().Contain("\"This message was sent\"", "the rule names the context line by its own words");
        rules.Should().Contain("begins with when it was sent, in square brackets");
        rules.Should().Contain("is history: when they name nothing, the focus item on screen is what");
    }
}
