using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// A small recorded corpus that both arms must read the same way.
/// </summary>
/// <remarks>
/// This is the seed of the trajectory evaluation, not the evaluation itself. It fixes the
/// model output and asserts that the baseline and the harness produce the same typed
/// decision, so a later live comparison measures tool selection, termination, and grounding
/// rather than a difference in how the two arms parse an answer.
/// </remarks>
public class CoachArmParityCorpusTests
{
    public static TheoryData<string, string> RecordedTurns => new()
    {
        {
            "direct constraint change",
            """
            {
              "Kind": "DirectConstraintChange",
              "ConstraintDelta": { "AvailableMinutes": 10, "AudioAllowed": false },
              "AcceptanceState": "NotApplicable",
              "CoachMessage": "Today's Plan now fits 10 minutes and uses no audio.",
              "EvidenceReferences": []
            }
            """
        },
        {
            "coach suggestion",
            """
            {
              "Kind": "SuggestConstraintChange",
              "ConstraintDelta": { "SkillEmphasis": "Speaking" },
              "AcceptanceState": "NotApplicable",
              "CoachMessage": "Your last 14 days were mostly input. Add a short speaking activity?",
              "EvidenceReferences": [ { "Kind": "PracticeBalance", "WindowDays": 14 } ]
            }
            """
        },
        {
            "clear acceptance",
            """
            {
              "Kind": "AcceptPendingSuggestion",
              "PendingSuggestionId": "suggestion-1",
              "AcceptanceState": "Accepted",
              "CoachMessage": "Added 4 minutes of speaking.",
              "EvidenceReferences": []
            }
            """
        },
        {
            "clear rejection",
            """
            {
              "Kind": "RejectPendingSuggestion",
              "PendingSuggestionId": "suggestion-1",
              "AcceptanceState": "Rejected",
              "CoachMessage": "Kept today's plan.",
              "EvidenceReferences": []
            }
            """
        },
        {
            "ambiguous answer",
            """
            {
              "Kind": "AskClarification",
              "PendingSuggestionId": "suggestion-1",
              "AcceptanceState": "Ambiguous",
              "ClarifyingQuestion": "Should I add the speaking activity to Today's Plan now?",
              "CoachMessage": "I need one answer first.",
              "EvidenceReferences": []
            }
            """
        },
        {
            "off topic",
            """
            {
              "Kind": "OffTopic",
              "AcceptanceState": "NotApplicable",
              "CoachMessage": "I can help with your study plan only.",
              "EvidenceReferences": []
            }
            """
        },
        {
            "no change",
            """
            {
              "Kind": "NoChange",
              "AcceptanceState": "NotApplicable",
              "CoachMessage": "Today's Plan already fits that time.",
              "EvidenceReferences": []
            }
            """
        }
    };

    [Theory]
    [MemberData(nameof(RecordedTurns))]
    public async Task BothArmsReadTheSameRecordedAnswerTheSameWay(string scenario, string recordedJson)
    {
        var baseline = await RunAsync(CoachImplementation.Baseline, recordedJson);
        var harness = await RunAsync(CoachImplementation.Harness, recordedJson);

        baseline.Outcome.Should().Be(CoachAgentOutcome.Completed, scenario);
        harness.Outcome.Should().Be(baseline.Outcome, scenario);

        harness.Intent!.Kind.Should().Be(baseline.Intent!.Kind, scenario);
        harness.Intent.AcceptanceState.Should().Be(baseline.Intent.AcceptanceState, scenario);
        harness.Intent.PendingSuggestionId.Should().Be(baseline.Intent.PendingSuggestionId, scenario);
        harness.Intent.ClarifyingQuestion.Should().Be(baseline.Intent.ClarifyingQuestion, scenario);
        harness.Intent.CoachMessage.Should().Be(baseline.Intent.CoachMessage, scenario);
        harness.Intent.ConstraintDelta.Should().BeEquivalentTo(baseline.Intent.ConstraintDelta, scenario);
        harness.Intent.EvidenceReferences.Should().BeEquivalentTo(baseline.Intent.EvidenceReferences, scenario);
    }

    [Theory]
    [MemberData(nameof(RecordedTurns))]
    public async Task BothArmsAgreeOnWhetherATurnMayWrite(string scenario, string recordedJson)
    {
        var baseline = await RunAsync(CoachImplementation.Baseline, recordedJson);
        var harness = await RunAsync(CoachImplementation.Harness, recordedJson);

        // The reducer authorises a write from the kind and the acceptance state only, so
        // matching both here means the two arms cannot disagree about a plan change.
        MayWrite(harness.Intent!).Should().Be(MayWrite(baseline.Intent!), scenario);
    }

    [Fact]
    public async Task BothArmsReportTheSameOutcomeForAMalformedAnswer()
    {
        var baseline = await RunAsync(CoachImplementation.Baseline, "not json at all");
        var harness = await RunAsync(CoachImplementation.Harness, "not json at all");

        baseline.Outcome.Should().Be(CoachAgentOutcome.InvalidOutput);
        harness.Outcome.Should().Be(baseline.Outcome);
        harness.Intent.Should().BeNull();
    }

    [Fact]
    public async Task BothArmsReportTheSameOutcomeWithNoChatClient()
    {
        var baseline = await CoachAgentTestDoubles
            .CreateCoach(CoachImplementation.Baseline, chatClient: null)
            .RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));
        var harness = await CoachAgentTestDoubles
            .CreateCoach(CoachImplementation.Harness, chatClient: null)
            .RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        baseline.Outcome.Should().Be(CoachAgentOutcome.ModelUnavailable);
        harness.Outcome.Should().Be(baseline.Outcome);
    }

    [Fact]
    public async Task BothArmsProduceResumableSessionState()
    {
        var recorded = """{"Kind":"NoChange","CoachMessage":"ok"}""";

        var baseline = await RunAsync(CoachImplementation.Baseline, recorded);
        var harness = await RunAsync(CoachImplementation.Harness, recorded);

        baseline.AgentSessionJson.Should().NotBeNullOrWhiteSpace();
        harness.AgentSessionJson.Should().NotBeNullOrWhiteSpace();
    }

    private static Task<CoachAgentTurnResult> RunAsync(CoachImplementation implementation, string recordedJson) =>
        CoachAgentTestDoubles
            .CreateCoach(implementation, new ScriptedChatClient(recordedJson))
            .RunTurnAsync(CoachAgentTestDoubles.NewRequest("recorded turn"));

    /// <summary>Whether the application reducer could apply a plan change from this intent.</summary>
    private static bool MayWrite(CoachTurnIntent intent) =>
        (intent.Kind == CoachIntentKind.DirectConstraintChange && intent.ConstraintDelta is not null)
        || (intent.Kind == CoachIntentKind.AcceptPendingSuggestion
            && intent.AcceptanceState == CoachAcceptanceState.Accepted
            && !string.IsNullOrWhiteSpace(intent.PendingSuggestionId));
}
