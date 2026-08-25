using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Api.Coach.Opportunities.Mapping;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// Mutation coverage for the four decisions that would be silently wrong if inverted.
/// </summary>
/// <remarks>
/// <para>
/// A passing test suite proves the code does what the tests say. These prove something narrower
/// and more useful: that flipping any single conjunct in the referent detector, dropping the
/// owner filter from a query, removing the aggregate-only strip, or giving the recorder a way to
/// signal back to its caller would be <em>caught</em> — rather than passing because no test
/// happened to exercise that mutant.
/// </para>
/// <para>
/// Written as explicit mutant tables rather than as a mutation-testing tool run, so the evidence
/// lives in the repository and re-runs on every build.
/// </para>
/// </remarks>
public class CoachOpportunityMutationTests
{
    private const string Owner = "learner-a";
    private const string Conversation = "conv-mutation";

    // ---------------------------------------------------------------- the detector

    /// <summary>
    /// Each row inverts exactly one conjunct of the detector's predicate. Every one must flip the
    /// answer from "record" to "record nothing".
    /// </summary>
    /// <remarks>
    /// A conjunct that could be deleted without failing a test is a conjunct that will eventually
    /// be deleted, and each of these is load-bearing: dropping the open-suggestion check would
    /// record every accepted suggestion as a lost referent, and dropping the classifier check
    /// would record every message.
    /// </remarks>
    public static TheoryData<string, CoachTurnInputKind, string?, string?, bool, bool, bool, CoachStopReason, CoachIntentKind?> Mutants() =>
        new()
        {
            { "input kind", CoachTurnInputKind.Chip, "yes", null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification },
            { "decisive answer", CoachTurnInputKind.Text, "maybe later actually", null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification },
            { "no open suggestion", CoachTurnInputKind.Text, "yes", "suggestion-1", false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification },
            { "no open proposal", CoachTurnInputKind.Text, "yes", null, true, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification },
            { "nothing applied", CoachTurnInputKind.Text, "yes", null, false, true, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification },
            { "nothing proposed", CoachTurnInputKind.Text, "yes", null, false, false, true, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification },
            { "turn did not act", CoachTurnInputKind.Text, "yes", null, false, false, false, CoachStopReason.Timeout, CoachIntentKind.AskClarification },

            // The stop reason still says no on its own for anything outside the two accepted
            // branches, whatever the intent claims.
            { "stop reason is accepted", CoachTurnInputKind.Text, "yes", null, false, false, false, CoachStopReason.Failed, CoachIntentKind.DirectConstraintChange }
        };

    [Theory]
    [MemberData(nameof(Mutants))]
    public void InvertingAnySingleConjunctSuppressesTheDetection(
        string conjunct,
        CoachTurnInputKind inputKind,
        string? text,
        string? pendingSuggestionId,
        bool hasOpenWriteProposal,
        bool hasChangeReceipt,
        bool hasWriteOperation,
        CoachStopReason stopReason,
        CoachIntentKind? intent)
    {
        using var harness = new CoachOpportunityHarness();
        var detector = harness.NewDetector();

        // The unmutated case: every conjunct holds.
        detector.IsUnboundDecisiveAnswer(
                CoachTurnInputKind.Text, "yes", null, false, false, false,
                CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification)
            .Should().BeTrue("the baseline must detect, or the mutants prove nothing");

        detector.IsUnboundDecisiveAnswer(
                inputKind, text, pendingSuggestionId, hasOpenWriteProposal,
                hasChangeReceipt, hasWriteOperation, stopReason, intent)
            .Should().BeFalse($"the '{conjunct}' conjunct is load-bearing and must not be droppable");
    }

    /// <summary>
    /// The same mutant table for the completed-turn branch, where the intent is the discriminant.
    /// </summary>
    /// <remarks>
    /// The reproduced defect lives on this branch: a completed turn that declared a settings
    /// change and produced nothing. Every other conjunct still has to hold, so each row here
    /// inverts one of them against a baseline that does detect.
    /// </remarks>
    public static TheoryData<string, string?, bool, bool, bool, CoachIntentKind?> CompletedBranchMutants() =>
        new()
        {
            { "no open suggestion", "suggestion-1", false, false, false, CoachIntentKind.DirectConstraintChange },
            { "no open proposal", null, true, false, false, CoachIntentKind.DirectConstraintChange },
            { "nothing applied", null, false, true, false, CoachIntentKind.DirectConstraintChange },
            { "nothing proposed", null, false, false, true, CoachIntentKind.DirectConstraintChange },

            // The discriminant itself. These are the intents that make a completed turn ordinary
            // tutoring rather than a dropped answer.
            { "settings-change intent", null, false, false, false, CoachIntentKind.PedagogicalAnswer },
            { "settings-change intent", null, false, false, false, CoachIntentKind.NoChange },
            { "settings-change intent", null, false, false, false, CoachIntentKind.OffTopic },
            { "settings-change intent", null, false, false, false, CoachIntentKind.AskClarification },
            { "settings-change intent", null, false, false, false, null }
        };

    [Theory]
    [MemberData(nameof(CompletedBranchMutants))]
    public void InvertingAnyConjunctOfTheCompletedBranchSuppressesTheDetection(
        string conjunct,
        string? pendingSuggestionId,
        bool hasOpenWriteProposal,
        bool hasChangeReceipt,
        bool hasWriteOperation,
        CoachIntentKind? intent)
    {
        using var harness = new CoachOpportunityHarness();
        var detector = harness.NewDetector();

        detector.IsUnboundDecisiveAnswer(
                CoachTurnInputKind.Text, "yes", null, false, false, false,
                CoachStopReason.Completed, CoachIntentKind.DirectConstraintChange)
            .Should().BeTrue("the baseline must detect, or the mutants prove nothing");

        detector.IsUnboundDecisiveAnswer(
                CoachTurnInputKind.Text, "yes", pendingSuggestionId, hasOpenWriteProposal,
                hasChangeReceipt, hasWriteOperation, CoachStopReason.Completed, intent)
            .Should().BeFalse($"the '{conjunct}' conjunct is load-bearing and must not be droppable");
    }

    /// <summary>
    /// The whole stop-reason by intent truth table, enumerated rather than sampled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard is two expressions and both are one-token edits away from being wrong in a way
    /// no other test would notice. Widening the completed branch to every intent reinstates the
    /// original defect — ordinary tutoring recorded as a lost referent, with decryptable evidence
    /// pointers attached to conversations that worked. Narrowing it back to nothing reinstates the
    /// defect this revision fixes, where the reproduced screenshot flow records no row at all.
    /// </para>
    /// <para>
    /// Enumerating both enums (plus the unset intent) means a member added to either is opted
    /// <em>out</em> by default and this test says so, rather than being silently swept into
    /// whichever branch its ordinal happens to land in.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStopReasonAndIntentGuardAcceptsExactlyTwoShapes()
    {
        using var harness = new CoachOpportunityHarness();
        var detector = harness.NewDetector();

        CoachIntentKind?[] intents = [.. Enum.GetValues<CoachIntentKind>().Cast<CoachIntentKind?>(), null];

        var accepted = (
            from reason in Enum.GetValues<CoachStopReason>()
            from intent in intents
            where detector.IsUnboundDecisiveAnswer(
                CoachTurnInputKind.Text, "yes", null, false, false, false, reason, intent)
            select (reason, intent)).ToList();

        var expected = (
            // A turn that answered a decisive yes/no by asking another question, whatever it
            // declared it was doing.
            from intent in intents
            select (CoachStopReason.ClarificationRequested, intent))
            .Concat(
                // A turn that finished having declared a settings change, and changed nothing.
                from intent in intents
                where CoachActionIntent.IsSettingsChange(intent)
                select (CoachStopReason.Completed, intent))
            .ToList();

        accepted.Should().BeEquivalentTo(expected,
            "exactly two shapes are a lost referent: the turn asked another question, or it " +
            "completed having declared it would change something and then changed nothing");
    }

    /// <summary>
    /// The intent discriminant, enumerated over the whole enum.
    /// </summary>
    /// <remarks>
    /// A member added to <see cref="CoachIntentKind"/> falls into the fail-closed arm of
    /// <see cref="CoachActionIntent.IsSettingsChange"/>, which is the safe default but also a
    /// silent one. This fails until somebody classifies it, so the decision is made deliberately
    /// rather than by omission.
    /// </remarks>
    [Fact]
    public void EveryIntentIsClassifiedAsActingOrAnswering()
    {
        var acting = Enum.GetValues<CoachIntentKind>()
            .Where(intent => CoachActionIntent.IsSettingsChange(intent))
            .ToList();

        acting.Should().BeEquivalentTo(
            [
                CoachIntentKind.DirectConstraintChange,
                CoachIntentKind.SuggestConstraintChange,
                CoachIntentKind.AcceptPendingSuggestion,
                CoachIntentKind.RejectPendingSuggestion
            ],
            "these are the intents CoachSessionService routes into a reducer that can write, " +
            "propose, or resolve a pending decision; every other intent answers rather than acts, " +
            "and a new member must be classified here rather than defaulting into silence");

        CoachActionIntent.IsSettingsChange(null).Should().BeFalse(
            "with no declared intent there is no authoritative signal, and the detector would " +
            "rather miss a row than invent one");
    }

    [Fact]
    public void TheOfferGradeIsLoadBearing()
    {
        // Removing the OfferLink != None requirement would turn every decisive answer into a
        // ledger row, which is exactly the noise the design refuses.
        CoachOfferShape.Grade(CoachMessageKind.Text, "Here is your plan for today.")
            .Should().Be(CoachOpportunityOfferLink.None);

        CoachOfferShape.Grade(CoachMessageKind.Text, "Shall I change it to 45 minutes?")
            .Should().Be(CoachOpportunityOfferLink.PriorCoachQuestion);
    }

    [Fact]
    public async Task RemovingTheOfferRequirementWouldRecordOrdinaryConversation()
    {
        using var harness = new CoachOpportunityHarness();

        await SeedAsync(harness, "I updated your plan. Three activities, 10 minutes.", "ok");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        // Every conjunct holds — including the stop reason, which must be the one the guard
        // accepts so the offer grade is genuinely the only thing saying no.
        detector.IsUnboundDecisiveAnswer(
            CoachTurnInputKind.Text, "ok", null, false, false, false,
            CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification).Should().BeTrue();

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "ok",
            null, false, false, false, CoachStopReason.ClarificationRequested,
            CoachIntentKind.AskClarification);

        loss.Should().BeNull("a receipt is a statement, so 'ok' answered nothing");
    }

    // ---------------------------------------------------------------- owner filters

    [Fact]
    public async Task DroppingTheOwnerFilterFromDeletionWouldBeVisible()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal());
        await harness.RecorderFor("learner-b").RecordAsync(Signal());
        await harness.RecorderFor("learner-c").RecordAsync(Signal());

        await using var db = harness.NewContext();
        var contributor = harness.NewDeletionContributor(db);

        var deleted = await contributor.DeleteAllAsync(CoachOwner.ForUser(Owner));

        // An unfiltered delete would return 3. The assertion is on the exact count, so a
        // widened filter cannot pass by deleting "at least" the right rows.
        deleted.Should().Be(1);
        (await harness.RowsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task AnEmptyOwnerCannotBeReadAsMatchEverything()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal());
        await harness.RecorderFor("learner-b").RecordAsync(Signal());

        await using var db = harness.NewContext();

        (await harness.NewDeletionContributor(db).DeleteAllAsync(default)).Should().Be(0);
        (await harness.RowsAsync()).Should().HaveCount(2,
            "the classic multi-tenant bug is a filter that degrades to 'no predicate' when the " +
            "owner is missing");
    }

    [Fact]
    public async Task ARelatedLookupWithoutTheOwnerFilterWouldCrossLearners()
    {
        using var harness = new CoachOpportunityHarness();

        // A refusal owned by somebody else, in a conversation with the same identifier. Only the
        // owner filter keeps the chain from reaching it.
        await harness.RecorderFor("learner-b").RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            "preference_setting_session_minutes",
            CoachOpportunitySurface.ToolInvocation,
            CoachOpportunityDisposition.Product,
            Evidence: new CoachOpportunityEvidencePointer(Conversation)));

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            Evidence: new CoachOpportunityEvidencePointer(Conversation, "msg-2", 2, "msg-1", 1)));

        var mine = (await harness.RowsAsync()).Single(row => row.UserProfileId == Owner);
        mine.RelatedOpportunityId.Should().BeNull();
    }

    // ---------------------------------------------------------------- pointer stripping

    [Fact]
    public void RemovingTheStripWouldLeaveEveryPointerInPlace()
    {
        var withPointers = new CoachOpportunitySignal(
            CoachOpportunityKind.HarmfulOrUnsafeRequest,
            CoachOpportunityCapabilityCodes.DestructiveRequestRefused,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly,
            Evidence: new CoachOpportunityEvidencePointer("conv", "msg", 1, "offer", 0),
            TurnId: "turn",
            TurnOperationId: "op",
            WriteOperationId: "write",
            RelatedOpportunityId: "related");

        // The mutant: the signal as the mapper produced it, before the recorder normalizes.
        withPointers.Evidence.ConversationId.Should().NotBeNull();

        var stripped = withPointers.WithoutPointers();

        stripped.Evidence.Should().Be(CoachOpportunityEvidencePointer.None);
        stripped.TurnId.Should().BeNull();
        stripped.TurnOperationId.Should().BeNull();
        stripped.WriteOperationId.Should().BeNull();
        stripped.RelatedOpportunityId.Should().BeNull();

        // And every other member survives, so the strip cannot be "fixed" by discarding the row.
        stripped.Kind.Should().Be(withPointers.Kind);
        stripped.CapabilityCode.Should().Be(withPointers.CapabilityCode);
        stripped.Disposition.Should().Be(withPointers.Disposition);
    }

    [Fact]
    public void TheStripIsAppliedByTheRecorderNotOnlyByTheMapper()
    {
        using var harness = new CoachOpportunityHarness();

        // A hostile mapper output: aggregate-only with every pointer set. The recorder must
        // normalize it regardless of who produced it.
        var normalized = harness.Recorder.Normalize(new CoachOpportunitySignal(
            CoachOpportunityKind.OutOfScopeRequest,
            CoachOpportunityCapabilityCodes.OffTopic,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly,
            Evidence: new CoachOpportunityEvidencePointer("conv", "msg", 1, "offer", 0),
            TurnId: "turn"));

        normalized.Should().NotBeNull();
        normalized!.Value.Evidence.Should().Be(CoachOpportunityEvidencePointer.None);
        normalized.Value.TurnId.Should().BeNull();
    }

    [Fact]
    public void AProductSignalKeepsItsPointers()
    {
        using var harness = new CoachOpportunityHarness();

        var normalized = harness.Recorder.Normalize(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            Evidence: new CoachOpportunityEvidencePointer("conv", "msg", 1, "offer", 0)));

        normalized.Should().NotBeNull();
        normalized!.Value.Evidence.MessageId.Should().Be("msg",
            "an over-eager strip would make every Product row unreviewable, which is the " +
            "opposite failure and just as bad");
    }

    // ---------------------------------------------------------------- recorder neutrality

    [Fact]
    public void TheRecorderHasNoWayToSignalBackToItsCaller()
    {
        var method = typeof(ICoachOpportunityRecorder)
            .GetMethod(nameof(ICoachOpportunityRecorder.RecordAsync))!;

        method.ReturnType.Should().Be(typeof(ValueTask),
            "a Task<bool> or a result type would give a caller something to branch on, and a " +
            "caller that branches on telemetry is a caller whose response depends on it");

        method.GetParameters().Should().AllSatisfy(parameter =>
            parameter.IsOut.Should().BeFalse("an out parameter is a return value in disguise"));

        method.GetParameters().Should().AllSatisfy(parameter =>
            parameter.ParameterType.IsByRef.Should().BeFalse());
    }

    [Fact]
    public void TheSignalIsAValueTypeSoACallerCannotObserveMutation()
    {
        typeof(CoachOpportunitySignal).IsValueType.Should().BeTrue(
            "a mutable reference passed to the recorder could come back changed, which is one " +
            "more way an observation could influence a decision");

        typeof(CoachOpportunityEvidencePointer).IsValueType.Should().BeTrue();
    }

    [Fact]
    public void EveryRecorderEntryPointIsGuarded()
    {
        // The whole public body is inside try/catch. Verified structurally: the method has
        // exactly one public entry point and it is the interface implementation, so there is no
        // second unguarded path a caller could take.
        var methods = typeof(CoachOpportunityRecorder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        methods.Should().BeEquivalentTo([nameof(ICoachOpportunityRecorder.RecordAsync)]);
    }

    [Fact]
    public void TheTurnMapperNeverProducesMoreThanOneSignal()
    {
        // A turn that matches several rules must produce one row, not several. The return type
        // is what enforces it: there is no shape that could carry two.
        var method = typeof(CoachTurnOutcomeOpportunityMapper)
            .GetMethod(nameof(CoachTurnOutcomeOpportunityMapper.Map))!;

        method.ReturnType.Should().Be(typeof(CoachOpportunitySignal?));
    }

    private static CoachOpportunitySignal Signal() =>
        new(CoachOpportunityKind.UnsupportedCapability,
            CoachOpportunityCapabilityCodes.EntityLookupByName,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposeVocabularyRemoval,
            Evidence: new CoachOpportunityEvidencePointer(Conversation));

    private static async Task SeedAsync(
        CoachOpportunityHarness harness,
        string coachText,
        string learnerText)
    {
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var owner = CoachOwner.ForUser(Owner);

        await conversations.CreateAsync(
            owner,
            new CreateCoachConversationRequest(
                "Mutation", CoachConversationTitleSource.Generated, null, Conversation));

        await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Coach, CoachMessageKind.Receipt,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.CoachText,
                Text = coachText,
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));

        await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Learner, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.LearnerText,
                Text = learnerText,
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));
    }
}
