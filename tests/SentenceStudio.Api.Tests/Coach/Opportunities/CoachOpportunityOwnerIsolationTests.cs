using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>Operator isolation against two owners' rows and encrypted messages in relational SQLite.</summary>
public sealed class CoachOpportunityOwnerIsolationTests
{
    private const string OwnerA = "operator-owner-a";
    private const string OwnerB = "operator-owner-b";
    private const string Conversation = "shared-conversation-id";

    [Theory]
    [InlineData(OwnerA, OwnerB)]
    [InlineData(OwnerB, OwnerA)]
    public async Task EveryOperatorReadAndActionIsScopedToTheCurrentOwner(
        string currentOwner, string otherOwner)
    {
        using var harness = new CoachOpportunityHarness(userProfileId: OwnerA);
        await SeedOwnerAsync(harness, OwnerA, CoachOpportunityCapabilityCodes.EntityLookupByName);
        await SeedOwnerAsync(harness, OwnerB, CoachOpportunityCapabilityCodes.WriteToolsDisabled);

        var rows = await harness.RowsAsync();
        rows.Should().HaveCount(4, "each owner wrote a shared-fingerprint row and a distinct row");

        var owned = rows.Where(row => row.UserProfileId == currentOwner).ToList();
        var foreign = rows.Where(row => row.UserProfileId == otherOwner).ToList();
        var ownEvidence = owned.Single(row => row.Kind == CoachOpportunityKind.AmbiguousFollowUp);
        var foreignEvidence = foreign.Single(row => row.Kind == CoachOpportunityKind.AmbiguousFollowUp);
        var foreignOnly = foreign.Single(row => row.Kind == CoachOpportunityKind.UnsupportedCapability);
        ownEvidence.Fingerprint.Should().Be(foreignEvidence.Fingerprint,
            "the same problem across owners must still produce an owner-only rollup");

        await using var db = harness.NewContext();
        var service = NewService(harness, db, currentOwner);

        var rollup = await service.RollupAsync(null);
        rollup.IsOk.Should().BeTrue();
        rollup.Value.Should().HaveCount(2);
        var shared = rollup.Value!.Single(line => line.Fingerprint == ownEvidence.Fingerprint);
        shared.DistinctLearners.Should().Be(1);
        shared.RowCount.Should().Be(1);
        shared.TotalOccurrences.Should().Be(1);
        rollup.Value.Should().NotContain(line => line.Fingerprint == foreignOnly.Fingerprint);

        var page = await service.ListAsync(null, null, null, null, 0, 50);
        page.Value!.Total.Should().Be(2);
        page.Value.Items.Select(row => row.Id)
            .Should().BeEquivalentTo(owned.Select(row => row.Id));

        (await service.GetAsync(ownEvidence.Id)).IsOk.Should().BeTrue();
        (await service.GetAsync(foreignEvidence.Id)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
        (await service.GetAsync(foreignOnly.Id)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);

        (await service.ReviewAsync(foreignEvidence.Id,
            new CoachOpportunityReviewRequest(CoachOpportunityStatus.Accepted))).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);

        var acknowledged = new CoachOpportunityEvidenceRequest(
            CoachOpportunityLimits.EvidenceRevealAcknowledgement);
        (await service.RevealEvidenceAsync(foreignEvidence.Id, acknowledged)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);

        var review = await service.ReviewAsync(ownEvidence.Id,
            new CoachOpportunityReviewRequest(CoachOpportunityStatus.Reviewed));
        review.IsOk.Should().BeTrue();
        review.Value!.MarkdownBlock.Should().Contain("1 distinct learner(s)");
        review.Value.MarkdownBlock.Should().NotContain("2 distinct learner(s)");

        var reveal = await service.RevealEvidenceAsync(ownEvidence.Id, acknowledged);
        reveal.IsOk.Should().BeTrue();
        reveal.Value!.EvidenceState.Should().Be(CoachOpportunityEvidenceState.Available);
        reveal.Value.LearnerMessageText.Should().Be($"private-learner-{currentOwner}");
        reveal.Value.PriorCoachMessageText.Should().Be($"private-coach-{currentOwner}");
        reveal.Value.CrossOwner.Should().BeFalse();
        reveal.Value.LearnerMessageText.Should().NotContain(otherOwner);

        var after = await harness.RowsAsync();
        var untouched = after.Single(row => row.Id == foreignEvidence.Id);
        untouched.Status.Should().Be(foreignEvidence.Status);
        untouched.Version.Should().Be(foreignEvidence.Version);
        untouched.EvidenceRevealCount.Should().Be(0);
        after.Single(row => row.Id == ownEvidence.Id).EvidenceRevealCount.Should().Be(1);
    }

    [Fact]
    public async Task AForeignEvidencePointerOnAnOwnedRowCannotDecryptTheForeignConversation()
    {
        using var harness = new CoachOpportunityHarness(userProfileId: OwnerA);
        await SeedOwnerAsync(harness, OwnerA, CoachOpportunityCapabilityCodes.EntityLookupByName);
        await SeedOwnerAsync(harness, OwnerB, CoachOpportunityCapabilityCodes.WriteToolsDisabled);

        await using var db = harness.NewContext();
        var a = await db.CoachOpportunities.SingleAsync(row =>
            row.UserProfileId == OwnerA && row.Kind == CoachOpportunityKind.AmbiguousFollowUp);
        var b = await db.CoachOpportunities.AsNoTracking().SingleAsync(row =>
            row.UserProfileId == OwnerB && row.Kind == CoachOpportunityKind.AmbiguousFollowUp);

        a.EvidenceMessageId = b.EvidenceMessageId;
        a.EvidenceMessageSequence = b.EvidenceMessageSequence;
        a.EvidenceOfferMessageId = b.EvidenceOfferMessageId;
        a.EvidenceOfferMessageSequence = b.EvidenceOfferMessageSequence;
        await db.SaveChangesAsync();

        var result = await NewService(harness, db, OwnerA).RevealEvidenceAsync(
            a.Id, new CoachOpportunityEvidenceRequest(
                CoachOpportunityLimits.EvidenceRevealAcknowledgement));

        result.IsOk.Should().BeTrue();
        result.Value!.EvidenceState.Should().Be(CoachOpportunityEvidenceState.Unavailable);
        result.Value.LearnerMessageText.Should().BeNull();
        result.Value.PriorCoachMessageText.Should().BeNull();
        result.Value.CrossOwner.Should().BeFalse();
        (await harness.RowsAsync()).Single(row => row.Id == b.Id)
            .EvidenceRevealCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingOwnerRefusesEveryReadAndWriteWithoutQueryingAllRows(string? owner)
    {
        using var harness = new CoachOpportunityHarness(userProfileId: OwnerA);
        await SeedOwnerAsync(harness, OwnerA, CoachOpportunityCapabilityCodes.EntityLookupByName);

        var before = (await harness.RowsAsync())
            .Single(row => row.Kind == CoachOpportunityKind.AmbiguousFollowUp);
        var warnings = new WarningLogger();
        await using var db = harness.NewContext();
        var service = NewService(harness, db, owner, warnings);

        (await service.RollupAsync(null)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
        (await service.ListAsync(null, null, null, null, 0, 50)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
        (await service.GetAsync(before.Id)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
        (await service.ReviewAsync(before.Id,
            new CoachOpportunityReviewRequest(CoachOpportunityStatus.Accepted))).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
        (await service.RevealEvidenceAsync(before.Id, new CoachOpportunityEvidenceRequest(
            CoachOpportunityLimits.EvidenceRevealAcknowledgement))).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);

        warnings.Messages.Should().HaveCount(5);
        warnings.Messages.Should().OnlyContain(message =>
            message.Contains("no active user profile id", StringComparison.Ordinal));

        var after = (await harness.RowsAsync()).Single(row => row.Id == before.Id);
        after.Status.Should().Be(before.Status);
        after.Version.Should().Be(before.Version);
        after.EvidenceRevealCount.Should().Be(0);
    }

    private static CoachOpportunityOperatorService NewService(
        CoachOpportunityHarness harness,
        CoachDbContext db,
        string? owner,
        ILogger<CoachOpportunityOperatorService>? logger = null) =>
        new(db,
            new TestUserScope(owner),
            new TestOptionsMonitor<CoachOpportunityOptions>(new CoachOpportunityOptions
            {
                Enabled = true,
                OperatorSurface = new CoachOpportunityOperatorSurfaceOptions { Enabled = true }
            }),
            new TestOptionsMonitor<CoachOptions>(new CoachOptions
            {
                Enabled = true,
                AllowedUserProfileIds = [OwnerA, OwnerB]
            }),
            harness.Time,
            logger ?? NullLogger<CoachOpportunityOperatorService>.Instance,
            harness.NewMessageStore(db),
            new CoachKeyRingPlan
            {
                Mode = CoachKeyRingMode.AzureBlobConnectionString,
                ApplicationName = "SentenceStudio.Coach",
                ContainerName = "coach-keys",
                BlobName = "keyring.xml"
            });

    private static async Task SeedOwnerAsync(
        CoachOpportunityHarness harness, string userProfileId, string distinctCapability)
    {
        await using var db = harness.NewContext();
        var owner = CoachOwner.ForUser(userProfileId);
        var conversation = $"{Conversation}-{userProfileId}";
        await harness.NewConversationStore(db).CreateAsync(owner,
            new CreateCoachConversationRequest(
                "Operator", CoachConversationTitleSource.Generated, null, conversation));

        var messages = harness.NewMessageStore(db);
        var offer = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            conversation, CoachMessageRole.Coach, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.CoachText,
                Text = $"private-coach-{userProfileId}",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));
        var answer = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            conversation, CoachMessageRole.Learner, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.LearnerText,
                Text = $"private-learner-{userProfileId}",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));

        var recorder = userProfileId == OwnerA ? harness.Recorder : harness.RecorderFor(userProfileId);
        await recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            StopReason: CoachStopReason.ClarificationRequested,
            Evidence: new CoachOpportunityEvidencePointer(
                conversation, answer.Message!.Id, answer.Message.Sequence,
                offer.Message!.Id, offer.Message.Sequence)));

        await recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            distinctCapability,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposeVocabularyRemoval));
    }

    private sealed class WarningLogger : ILogger<CoachOpportunityOperatorService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
