using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The report action itself: what the learner's press does, and what it does not do.
/// </summary>
/// <remarks>
/// Separate from the render tests because these are about the transitions — a failed report that
/// must not settle, a repeat that must not read as an error, and an account switch that must not
/// leave one learner's reported responses in front of another.
/// </remarks>
public class CoachResponseReportStateTests
{
    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> ConversationAsync(
        Action<FakeCoachApiClient>? configure = null)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");
        configure?.Invoke(client);

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        return (state, client);
    }

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task EachReasonReachesTheServerAsItself()
    {
        foreach (var reason in Enum.GetValues<CoachResponseReportReason>())
        {
            var (state, client) = await ConversationAsync();

            var outcome = await state.ReportResponseAsync("m-2", reason);

            outcome.Should().Be(CoachReportOutcome.Recorded);
            client.Reports.Should().ContainSingle()
                .Which.Reason.Should().Be(reason, "the reason is a closed value and is sent verbatim");
        }
    }

    [Fact]
    public async Task ReportingSettlesTheResponseFromTheServersAnswerNotFromTheRequest()
    {
        var (state, _) = await ConversationAsync();

        state.IsResponseReported("m-2").Should().BeFalse();

        await state.ReportResponseAsync("m-2", CoachResponseReportReason.DidNotAnswer);

        state.IsResponseReported("m-2").Should().BeTrue();
    }

    [Fact]
    public async Task ReportingOneResponseLeavesTheOthersAlone()
    {
        var (state, client) = await ConversationAsync(c =>
            _ = c.Seed("c-1", CoachMessageRole.Coach, "And 이/가 marks the subject."));

        await state.ReportResponseAsync("m-2", CoachResponseReportReason.Confusing);

        state.IsResponseReported("m-2").Should().BeTrue();
        state.IsResponseReported("m-3").Should().BeFalse(
            "reporting is per response; one bad answer does not condemn the next one");
        client.Reports.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- repeats

    [Fact]
    public async Task ASecondReportOfTheSameResponseIsASuccessNotAnError()
    {
        var (state, _) = await ConversationAsync(client => client.ReportedResponses.Add("m-2"));

        var outcome = await state.ReportResponseAsync("m-2", CoachResponseReportReason.Other);

        outcome.Should().Be(CoachReportOutcome.AlreadyReported,
            "two devices, a double press and a reload all land here, and every one of them is a learner whose intent was already carried out");
        state.IsResponseReported("m-2").Should().BeTrue();
    }

    [Fact]
    public async Task AResponseAlreadyKnownToBeReportedIsNotSentAgain()
    {
        var (state, client) = await ConversationAsync(c => c.ReportedResponses.Add("m-2"));

        await state.ReportResponseAsync("m-2", CoachResponseReportReason.Other);

        client.Reports.Should().BeEmpty("the client already holds the server's answer for this response");
    }

    // ---------------------------------------------------------------- failure

    [Fact]
    public async Task AFailedReportDoesNotSettleTheResponse()
    {
        var (state, _) = await ConversationAsync(client =>
            client.OnReportResponse = _ => throw FakeCoachApiClient.Gone());

        var outcome = await state.ReportResponseAsync("m-2", CoachResponseReportReason.IncorrectOrMisleading);

        outcome.Should().Be(CoachReportOutcome.Failed);
        state.IsResponseReported("m-2").Should().BeFalse(
            "a control that settles on a failed request claims feedback reached a person when it did not");
    }

    [Fact]
    public async Task AReportAgainstASwitchedOffServerWithdrawsTheControl()
    {
        var (state, client) = await ConversationAsync();
        state.IsReportingAvailable.Should().BeTrue();

        // The switch was flipped between opening the conversation and pressing the control, so
        // the route now answers the 404 shape.
        client.IsReportingAvailable = false;

        var outcome = await state.ReportResponseAsync("m-2", CoachResponseReportReason.DidNotAnswer);

        outcome.Should().Be(CoachReportOutcome.Failed);
        state.IsReportingAvailable.Should().BeFalse(
            "the control stops being offered rather than failing again on the next press");
    }

    // ---------------------------------------------------------------- availability

    [Fact]
    public async Task AServerThatDoesNotAcceptReportsWithholdsTheControl()
    {
        var (state, _) = await ConversationAsync(client => client.IsReportingAvailable = false);

        state.IsReportingAvailable.Should().BeFalse();
        state.ReportedResponses.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportingIsNotOfferedWithoutADurableConversation()
    {
        // Session-only mode: there is no server-side message identity to report against.
        var client = new FakeCoachApiClient { DurableHistoryAvailable = false };
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        state.IsReportingAvailable.Should().BeFalse();
    }

    // ---------------------------------------------------------------- account boundary

    [Fact]
    public async Task AnAccountSwitchClearsTheReportedSetAndTheControl()
    {
        var (state, _) = await ConversationAsync(client => client.ReportedResponses.Add("m-2"));

        state.IsResponseReported("m-2").Should().BeTrue();
        state.IsReportingAvailable.Should().BeTrue();

        state.ResetForAccountBoundary();

        state.IsResponseReported("m-2").Should().BeFalse(
            "leaving the set behind would show one learner's reports in front of another");
        state.IsReportingAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task StartingANewConversationClearsTheReportedSet()
    {
        var (state, _) = await ConversationAsync(client => client.ReportedResponses.Add("m-2"));

        state.Reset();

        state.ReportedResponses.Should().BeEmpty();
        state.IsReportingAvailable.Should().BeFalse();
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task AnUnknownMessageIsNotReported()
    {
        var (state, client) = await ConversationAsync();

        var outcome = await state.ReportResponseAsync(string.Empty, CoachResponseReportReason.Other);

        outcome.Should().Be(CoachReportOutcome.Failed);
        client.Reports.Should().BeEmpty();
    }
}
