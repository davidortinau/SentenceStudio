using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Tests.Coach.Application;

/// <summary>
/// "Start a conversation in German about getting directions" from the dashboard opens that
/// conversation (Captain, 2026-11-17). The server checks every part of what the model asked for
/// and passes on only what it could check; it opens by itself only when the learner said start.
/// </summary>
public class CoachActivityLaunchTests
{
    [Fact]
    public async Task A_german_directions_conversation_opens_with_its_language_and_scenario()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = Launch(new CoachActivityLaunchIntent
        {
            Activity = "Conversation", Language = "german", Scenario = "getting directions"
        });

        var result = await SubmitAsync(harness, sessionId, "Start a conversation in German about getting directions");

        var launch = result.Value!.ActivityLaunch!;
        launch.Activity.Should().Be(CoachActivityKind.Conversation);
        launch.Language.Should().Be("German");
        launch.ScenarioId.Should().Be("scn:asking-for-directions");
        launch.ScenarioName.Should().Be("Asking for Directions");
        launch.AutoStart.Should().BeTrue("the learner said start");
    }

    [Fact]
    public async Task A_launch_the_learner_did_not_ask_for_never_opens_by_itself()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = Launch(new CoachActivityLaunchIntent { Activity = "Reading" });

        var result = await SubmitAsync(harness, sessionId, "What could I practise next?");

        result.Value!.ActivityLaunch!.AutoStart.Should().BeFalse("the model offered it; the learner did not ask");
    }

    [Fact]
    public async Task Unchecked_parts_are_dropped_not_passed_on()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = Launch(new CoachActivityLaunchIntent
        {
            Activity = "Reading", Language = "Klingon", ResourceId = "someone-elses", SkillId = "not-mine",
            Scenario = "directions"
        });

        var result = await SubmitAsync(harness, sessionId, "start a reading activity");

        var launch = result.Value!.ActivityLaunch!;
        launch.Language.Should().BeNull();
        launch.ResourceIds.Should().BeEmpty();
        launch.SkillId.Should().BeNull();
        launch.ScenarioId.Should().BeNull("only a conversation takes a scenario");
    }

    [Fact]
    public async Task An_activity_the_app_does_not_have_opens_nothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = Launch(new CoachActivityLaunchIntent { Activity = "Karaoke" });

        var result = await SubmitAsync(harness, sessionId, "start karaoke");

        result.Value!.ActivityLaunch.Should().BeNull();
    }

    [Fact]
    public async Task A_question_never_opens_by_itself_and_a_native_language_is_dropped()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = Launch(new CoachActivityLaunchIntent { Activity = "Conversation", Language = "English" });

        var result = await SubmitAsync(harness, sessionId, "Do I have any conversation scenarios?");

        result.Value!.ActivityLaunch!.AutoStart.Should().BeFalse("a question is not a request to start");
        result.Value.ActivityLaunch.Language.Should().BeNull("practising in your own language teaches nothing");
    }

    [Fact]
    public async Task An_owned_resource_is_kept()
    {
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.OwnedResourceIds.Add("res-1");
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = Launch(new CoachActivityLaunchIntent { Activity = "Cloze", ResourceId = "res-1" });

        var result = await SubmitAsync(harness, sessionId, "open a cloze on this resource");

        result.Value!.ActivityLaunch!.ResourceIds.Should().Equal("res-1");
    }

    [Theory]
    [InlineData("getting directions", "Asking for Directions")]
    [InlineData("ordering coffee at a cafe", "Ordering Coffee")]
    [InlineData("주말 계획", "Weekend Plans")]
    [InlineData("talking about the weather", null)]
    [InlineData("a hot oatmeal breakfast", null)]
    [InlineData("주말에 뭐 할지", "Weekend Plans")]
    [InlineData("길어서 힘든 대화", null)]
    public void A_scenario_is_matched_from_the_learners_words(string words, string? expected)
    {
        BuiltInConversationScenarios.Match(words)?.Name.Should().Be(expected);
        if (expected is null)
            BuiltInConversationScenarios.Match(words).Should().BeNull();
    }

    [Fact]
    public void The_built_in_scenarios_are_the_ones_every_client_seeds()
    {
        var source = File.ReadAllText(FindSource("src/SentenceStudio.AppLib/Services/ScenarioService.cs"));
        foreach (var entry in BuiltInConversationScenarios.All)
            source.Should().Contain($"Name = \"{entry.Name}\"", "the server and the clients must name the same built-ins");
    }

    [Theory]
    [InlineData("Conversation", CoachActivityKind.Conversation)]
    [InlineData("vocab quiz", CoachActivityKind.VocabularyQuiz)]
    [InlineData("Describe a scene", CoachActivityKind.Scene)]
    [InlineData("How do you say", CoachActivityKind.HowDoYouSay)]
    [InlineData("", null)]
    public void Activity_names_parse_to_the_closed_set(string name, CoachActivityKind? expected) =>
        CoachSessionService.ParseActivity(name).Should().Be(expected);

    [Fact]
    public void Every_activity_has_a_route_and_a_title()
    {
        foreach (var activity in Enum.GetValues<CoachActivityKind>().Where(a => a != CoachActivityKind.Unknown))
        {
            CoachActivityRoutes.Route(activity).Should().NotBeNullOrWhiteSpace();
            CoachActivityRoutes.TitleKey(activity).Should().StartWith("Activity_");
        }
    }

    private static CoachAgentTurnResult Launch(CoachActivityLaunchIntent launch) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "Opening it now.",
            ActivityLaunch = launch
        }
    };

    private static Task<SentenceStudio.Api.Coach.Application.CoachOperationResult<CoachTurnResponse>> SubmitAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = text });

    private static string FindSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException(relative), relative);
    }
}
