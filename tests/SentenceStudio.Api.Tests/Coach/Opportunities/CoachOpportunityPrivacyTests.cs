using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// A sentinel string the learner "typed" must appear in no column and no log line.
/// </summary>
/// <remarks>
/// The same technique the write-audit and sanitized-logging tests use. The shape tests prove no
/// member <em>could</em> hold learner text; these prove that the values which do reach the
/// database and the logger are the closed-vocabulary ones they are supposed to be, end to end.
/// </remarks>
public class CoachOpportunityPrivacyTests
{
    private const string Sentinel = "SENTINEL-사과-forty-five-minutes-please";
    private const string Owner = "learner-a";
    private const string Conversation = "conv-privacy";

    [Fact]
    public async Task ALearnersWordsReachNoColumn()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness);

        await using (var db = harness.NewContext())
        {
            var detector = harness.NewDetector(harness.NewMessageStore(db));
            var loss = await detector.DetectAsync(
                CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
                null, false, false, false, CoachStopReason.ClarificationRequested,
                CoachIntentKind.AskClarification);

            loss.Should().NotBeNull();

            var signal = Api.Coach.Opportunities.Mapping.CoachTurnOutcomeOpportunityMapper.Map(
                loss, CoachStopReason.ClarificationRequested, null, null,
                Conversation, "turn-1", null);

            await harness.Recorder.RecordAsync(signal!.Value);
        }

        var rows = await harness.RowsAsync();
        rows.Should().ContainSingle();

        foreach (var value in Stringify(rows[0]))
        {
            value.Should().NotContain("SENTINEL");
            value.Should().NotContain("사과");

            // Deliberately not a bare number: the row's own identifier and fingerprint are hex,
            // so asserting on "45" would fail on a coincidence rather than on a leak. The
            // sentinel carries a token that cannot occur in hex.
            value.Should().NotContain("forty-five");
        }
    }

    [Fact]
    public async Task ALearnersWordsReachNoLogLine()
    {
        using var harness = new CoachOpportunityHarness();

        var logger = new CapturingLogger<CoachOpportunityRecorder>();
        var recorder = harness.RecorderWithLogger(logger);

        await recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            CoachOpportunityCapabilityCodes.EntityLookupByName,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            // Hostile input on every string the signal accepts. Each is validated against a
            // closed set, so none of them can reach a column — and none may reach a log line
            // either, because a warning that quoted the rejected value would be the leak.
            ToolName: Sentinel,
            FailureCode: Sentinel,
            Evidence: new CoachOpportunityEvidencePointer(Conversation)));

        logger.Messages.Should().NotBeEmpty();

        foreach (var message in logger.Messages)
        {
            message.Should().NotContain("SENTINEL");
            message.Should().NotContain("사과");
        }
    }

    [Fact]
    public async Task ARejectedSignalLogsNoValue()
    {
        using var harness = new CoachOpportunityHarness();
        var logger = new CapturingLogger<CoachOpportunityRecorder>();
        var recorder = harness.RecorderWithLogger(logger);

        await recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            Sentinel,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product));

        (await harness.RowsAsync()).Should().BeEmpty();
        logger.Messages.Should().NotBeEmpty("the drop is worth knowing about");
        logger.Messages.Should().AllSatisfy(m => m.Should().NotContain("SENTINEL"));
    }

    [Fact]
    public async Task TheDetectorLogsNoLearnerText()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness);

        var logger = new CapturingLogger<CoachUnboundAnswerDetector>();
        await using var db = harness.NewContext();

        var detector = new CoachUnboundAnswerDetector(
            new Api.Coach.Application.CoachExplicitAcceptanceClassifier(),
            logger,
            harness.NewMessageStore(db));

        await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            null, false, false, false, CoachStopReason.ClarificationRequested,
                CoachIntentKind.AskClarification);

        foreach (var message in logger.Messages)
        {
            message.Should().NotContain("SENTINEL");
            message.Should().NotContain("사과");
            message.Should().NotContain("yes");
        }
    }

    [Fact]
    public void TheFingerprintIsSafeToPasteIntoADecisionRecord()
    {
        // Every input is a closed enum or a closed-vocabulary constant, so the digest cannot be
        // inverted into anything a learner typed — because nothing a learner typed was an input.
        var fingerprint = CoachOpportunityFingerprint.Compute(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachToolNames.ProposePreferenceChange,
            Api.Coach.Operations.CoachWriteFailureCodes.InvalidArguments,
            CoachStopReason.ClarificationRequested,
            CoachOpportunityOfferLink.PriorCoachQuestion);

        fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        CoachOpportunityFingerprint.Describe(fingerprint)
            .Should().StartWith("coach-opportunity://");
    }

    [Fact]
    public async Task TheRenderedMarkdownBlockCarriesNoLearnerText()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness);

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            StopReason: CoachStopReason.ClarificationRequested,
            Evidence: new CoachOpportunityEvidencePointer(Conversation, "msg-2", 2, "msg-1", 1)));

        var row = (await harness.RowsAsync()).Single();
        var markdown = CoachOpportunityMarkdown.Render(row, null);

        markdown.Should().NotContain("SENTINEL");
        markdown.Should().NotContain("사과");
        markdown.Should().Contain(CoachOpportunityCapabilityCodes.ReferentLostAfterOffer);
        markdown.Should().Contain("coach-opportunity://");
        markdown.Should().Contain("not reproduced here");
    }

    private static IEnumerable<string> Stringify(CoachOpportunity row) =>
        new[]
        {
            row.Id, row.UserProfileId, row.TenantId, row.ConversationId, row.TurnId,
            row.TurnOperationId, row.CapabilityCode, row.ToolName, row.FailureCode,
            row.EvidenceMessageId, row.EvidenceOfferMessageId, row.WriteOperationId,
            row.RelatedOpportunityId, row.Fingerprint, row.LinkedSpecPath
        }.Where(value => value is not null)!;

    private static async Task SeedAsync(CoachOpportunityHarness harness)
    {
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var owner = CoachOwner.ForUser(Owner);

        await conversations.CreateAsync(
            owner,
            new CreateCoachConversationRequest(
                "Privacy", CoachConversationTitleSource.Generated, null, Conversation));

        await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Coach, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.CoachText,
                Text = $"Your study time is 10 minutes. Change it to {Sentinel}?",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));

        await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Learner, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.LearnerText,
                Text = "yes",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));
    }
}

/// <summary>Captures every formatted log message, for leak assertions.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));

        // The exception's own text is captured too. If a code path ever passed the exception
        // object to the logger, its message and inner chain would show up here — and on a coach
        // path those carry prompt and learner text.
        if (exception is not null)
        {
            Messages.Add(exception.ToString());
        }
    }
}
