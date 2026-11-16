using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// A report on a real turn says how that turn ended.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes was found on a real report (issue 227). A learner flagged the notice
/// <i>"I couldn't finish that reply safely, so nothing was changed"</i>, and the report's stop
/// reason was empty, as it was on every other report in production. The report read the stop
/// reason from the root of the stored outcome, and since outcome version 2 the response sits in
/// the outcome's answer section.
/// </para>
/// <para>
/// The stop reason is the one code that separates the two different endings that share that
/// notice: a reply the server refused, and a clarifying question asked after the session's
/// questions were used up. So these tests write the outcome with the real conversation service
/// and read it with the real report service; a hand-built outcome is what let the two drift
/// apart.
/// </para>
/// </remarks>
public class CoachResponseReportStopReasonTests
{
    [Fact]
    public async Task AReportOnAnAnsweredTurnSaysItCompleted()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = General("You have 342 words in total.");

        (await harness.TurnAsync(conversationId, "How many words do I have?")).IsOk.Should().BeTrue();

        var row = await ReportLatestResponseAsync(harness, conversationId);

        row.TurnStatus.Should().Be(CoachTurnOperationStatus.Completed);
        row.StopReason.Should().Be(CoachStopReason.Completed,
            "the stop reason is in the stored outcome, and the report is where a reviewer reads it");
    }

    [Fact]
    public async Task AReportOnARefusedReplySaysValidationFailed()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        // Longer than a reply may be, so the server refuses it and shows the neutral notice.
        harness.Coach.NextResult = General(new string('a', 401));

        var turn = await harness.TurnAsync(conversationId, "Tell me about my words.");
        turn.Value!.Result!.StopReason.Should().Be(CoachStopReason.ValidationFailed);

        var row = await ReportLatestResponseAsync(harness, conversationId);

        row.ResponseKind.Should().Be(CoachMessageKind.Notice);
        row.StopReason.Should().Be(CoachStopReason.ValidationFailed);
    }

    [Fact]
    public async Task AReportOnAQuestionPastTheSessionBudgetSaysClarificationRequested()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = Clarify("Which part do you mean?");

        // The session's questions are used up, then the model asks one more.
        for (var asked = 0; asked < CoachConstraintLimits.MaxClarificationsPerSession; asked++)
        {
            (await harness.TurnAsync(conversationId, "Sort it out for me.")).IsOk.Should().BeTrue();
        }

        var turn = await harness.TurnAsync(conversationId, "Sort it out for me.");
        turn.Value!.Result!.StopReason.Should().Be(CoachStopReason.ClarificationRequested);

        var row = await ReportLatestResponseAsync(harness, conversationId);

        row.ResponseKind.Should().Be(CoachMessageKind.Notice,
            "the question is withheld and the learner reads a notice, the same one a refused reply shows");
        row.StopReason.Should().Be(CoachStopReason.ClarificationRequested,
            "this is the only code that tells a reviewer the notice was not a refused reply");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Files a report on the conversation's newest coach response and returns its row.</summary>
    private static async Task<CoachResponseReport> ReportLatestResponseAsync(
        CoachConversationHarness harness, string conversationId)
    {
        var response = (await harness.LedgerAsync(conversationId)).Last(m => m.Role == CoachMessageRole.Coach);

        var service = new CoachResponseReportService(
            harness.Db,
            harness.App.UserScope,
            harness.Operations,
            harness.App.ToolRegistry,
            new RecordingCoachOpportunityRecorder(),
            new TestOptionsMonitor<CoachResponseReportOptions>(new CoachResponseReportOptions { Enabled = true }),
            harness.Time,
            NullLogger<CoachResponseReportService>.Instance);

        var result = await service.ReportAsync(
            conversationId, response.Id, new CoachResponseReportRequest { Reason = CoachResponseReportReason.DidNotAnswer });
        result.IsOk.Should().BeTrue(result.Detail);

        return await harness.Db.CoachResponseReports.AsNoTracking().SingleAsync(r => r.CoachMessageId == response.Id);
    }

    private static CoachAgentTurnResult General(string message) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = CoachIntentKind.GeneralAnswer, CoachMessage = message }
    };

    private static CoachAgentTurnResult Clarify(string question) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = CoachIntentKind.AskClarification, ClarifyingQuestion = question }
    };
}
