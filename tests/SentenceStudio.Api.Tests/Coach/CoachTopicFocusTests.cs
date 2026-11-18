using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// "I want to study food and restaurant content" (Captain, 2026-11-17): the model names the
/// learner's own tags and resource titles that fit; the server keeps only names the learner has,
/// selects the words that carry those tags or belong to those resources, and offers the set.
/// </summary>
public class CoachTopicFocusTests
{
    private const string Utterance = "I want to study food and restaurant content";

    [Fact]
    public async Task A_topic_is_offered_with_the_learners_own_names_as_its_label()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = TopicResult("food and restaurant content", "food", "At the restaurant");

        var result = await AskAsync(harness, sessionId);

        var pending = result.Value!.PendingSuggestion;
        pending.Should().NotBeNull("a topic is offered before it is applied, like any focus");
        pending!.VocabularyFocus!.DisplayLabel.Should().Be("words about food and At the restaurant");
        pending.Rationale.Should().Contain("matching words about food and At the restaurant");
        var request = harness.FocusResolver.Requests.Should().ContainSingle().Which;
        request.CategoryTags.Should().Equal("food");
        request.ResourceIds.Should().Equal("res-restaurant");
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_name_the_learner_does_not_have_is_dropped()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = TopicResult("food and restaurant content", "food", "someone-elses-tag", "res-restaurant");

        await AskAsync(harness, sessionId);

        var request = harness.FocusResolver.Requests.Should().ContainSingle().Which;
        request.CategoryTags.Should().Equal("food");
        request.ResourceIds.Should().BeEmpty("a resource is named by its title, never by an id the model typed");
    }

    [Fact]
    public async Task With_no_names_the_learners_words_still_find_a_whole_tag()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = TopicResult("food");

        await AskAsync(harness, sessionId);

        harness.FocusResolver.Requests.Should().ContainSingle().Which.CategoryTags.Should().Equal("food");
    }

    [Fact]
    public async Task A_topic_nothing_matches_asks_and_writes_nothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = TopicResult("space travel", "astronomy");

        var result = await AskAsync(harness, sessionId);

        result.Value!.ClarifyingQuestion.Should().NotBeNullOrEmpty();
        result.Value.PendingSuggestion.Should().BeNull();
        harness.FocusResolver.ResolveCount.Should().Be(0);
    }

    [Fact]
    public void Topic_names_match_whole_tags_and_exact_titles_only()
    {
        var learner = new LearnerTopics(
            [new LearnerTopicTag("food", 3), new LearnerTopicTag("seafood", 2)],
            [new LearnerTopicResource("r1", "At the restaurant", 4), new LearnerTopicResource("r2", "Restaurant reviews", 4)]);

        var match = CoachVocabularyFocusService.MatchTopics("food", ["FOOD", "at the restaurant"], learner);

        match.Tags.Should().Equal("food");
        match.Resources.Select(r => r.ResourceId).Should().Equal("r1");
    }

    [Fact]
    public void Filler_words_in_the_learners_request_match_nothing()
    {
        var learner = new LearnerTopics([], [
            new LearnerTopicResource("r1", "Rock and Roll Lyrics", 4),
            new LearnerTopicResource("r2", "The Little Prince", 4)]);

        var match = CoachVocabularyFocusService.MatchTopics("I want to study food and the words", null, learner);

        match.Resources.Should().BeEmpty("\"and\" and \"the\" are not a topic");
    }

    private static CoachAgentTurnResult TopicResult(string description, params string[] topics) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            CoachMessage = "Focusing Today's Plan on that topic.",
            ConstraintDelta = new CoachConstraintDeltaIntent
            {
                VocabularyFocusDescription = description,
                VocabularyFocusTopics = topics.Length == 0 ? null : topics.ToList()
            }
        }
    };

    private static Task<SentenceStudio.Api.Coach.Application.CoachOperationResult<CoachTurnResponse>> AskAsync(
        CoachApplicationHarness harness, string sessionId) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = Utterance,
            // The dashboard, with Today's Plan on screen: that is where "I want to study" is a plan request.
            ClientScreen = new CoachClientScreenContext
            {
                ScreenId = "dashboard",
                Route = "/",
                Items = [new CoachClientScreenItem { Kind = CoachClientScreenItemKinds.PlanItem, Id = "item-1", Label = "Listening" }]
            }
        });
}
