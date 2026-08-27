using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// Fixture builders for the honesty rules.
/// </summary>
/// <remarks>
/// Every rule test needs an answer, some evidence, and a trace, and hand-rolling those three at
/// each call site is how a suite ends up asserting against a shape nobody meant. These builders
/// make the <em>difference</em> between a passing and a failing fixture the only thing visible at
/// the call site, which is what lets a negative test read as a negative test.
/// </remarks>
internal static class ClaimFixture
{
    internal static readonly DateTime AsOf = new(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc);

    /// <summary>An answer with one span, in the given block kind and language role.</summary>
    internal static CoachAnswerDto Answer(
        string text,
        CoachAnswerBlockKind kind = CoachAnswerBlockKind.Answer,
        CoachLanguageRole language = CoachLanguageRole.Display) =>
        new()
        {
            Topic = CoachAnswerTopic.Vocabulary,
            Blocks =
            [
                new CoachAnswerBlockDto
                {
                    Kind = kind,
                    Spans =
                    [
                        new CoachAnswerSpanDto { Text = text, Language = language, LanguageTag = "en" }
                    ]
                }
            ],
            PlainText = text,
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en"
        };

    /// <summary>An answer with several display spans in one Answer block.</summary>
    internal static CoachAnswerDto AnswerWith(params string[] texts) =>
        new()
        {
            Topic = CoachAnswerTopic.Vocabulary,
            Blocks =
            [
                new CoachAnswerBlockDto
                {
                    Kind = CoachAnswerBlockKind.Answer,
                    Spans = [.. texts.Select(text => new CoachAnswerSpanDto
                    {
                        Text = text,
                        Language = CoachLanguageRole.Display,
                        LanguageTag = "en"
                    })]
                }
            ],
            PlainText = string.Join(" ", texts),
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en"
        };

    /// <summary>One evidence item, with the coverage and counts the caller cares about.</summary>
    internal static CoachEvidenceDto Evidence(
        CoachEvidenceCoverage? coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        CoachEvidenceOrder? order = CoachEvidenceOrder.Unordered,
        int? matched = null,
        int? returned = null,
        int? withheld = null,
        CoachWithheldReason? withheldReason = null) =>
        new()
        {
            Kind = CoachEvidenceKind.VocabularyDue,
            Label = "Vocabulary",
            Summary = "Words you are tracking.",
            WindowStartDate = new DateOnly(2026, 8, 1),
            WindowEndDate = new DateOnly(2026, 8, 21),
            Coverage = coverage,
            Order = order,
            MatchedCount = matched,
            ReturnedCount = returned,
            WithheldCount = withheld,
            WithheldReason = withheldReason,
            AsOfUtc = AsOf
        };

    /// <summary>A trace containing one successful read.</summary>
    internal static CoachTurnTraceSummary SuccessfulTrace(
        int? matched = null,
        int? returned = null,
        int? withheld = null) =>
        new(
            [
                new CoachTurnTraceEntry(
                    Ordinal: 1,
                    ToolName: "get_vocabulary_due_summary",
                    Outcome: CoachToolCallOutcome.Succeeded,
                    FailureKind: null,
                    ArgumentMask: CoachToolArgumentMask.None,
                    ElapsedMs: 12,
                    Coverage: CoachScopeCoverage.CompleteAggregateWithBreakdown,
                    DefinitionCode: CoachScopeDefinition.TrackedVocabularyDueSummary,
                    WithheldReason: CoachScopeWithheldReason.None,
                    MatchedCount: matched,
                    ReturnedCount: returned,
                    WithheldCount: withheld,
                    Truncated: false)
            ],
            BudgetUsed: 1,
            BudgetLimit: 6);

    /// <summary>A trace whose only call faulted. The turn recorded, and nothing was learned.</summary>
    internal static CoachTurnTraceSummary FailedTrace() =>
        new(
            [
                new CoachTurnTraceEntry(
                    Ordinal: 1,
                    ToolName: "get_vocabulary_due_summary",
                    Outcome: CoachToolCallOutcome.Faulted,
                    FailureKind: CoachToolFailureKind.DataAccess,
                    ArgumentMask: CoachToolArgumentMask.None,
                    ElapsedMs: 4,
                    Coverage: CoachScopeCoverage.Unspecified,
                    DefinitionCode: CoachScopeDefinition.Unspecified,
                    WithheldReason: CoachScopeWithheldReason.None,
                    MatchedCount: null,
                    ReturnedCount: null,
                    WithheldCount: null,
                    Truncated: false)
            ],
            BudgetUsed: 1,
            BudgetLimit: 6);

    /// <summary>An empty trace. The turn recorded and called nothing.</summary>
    internal static CoachTurnTraceSummary EmptyTrace() => new([], BudgetUsed: 0, BudgetLimit: 6);
}

/// <summary>
/// A resolver a test can steer, so a capability rule runs without the frozen registry.
/// </summary>
/// <remarks>
/// Plan §14 is explicit that AC-F1, AC-F2, AC-F3 and AC-F5 run against a <b>synthetic</b> handshake
/// and synthetic registrations, because no shipped client advertises a client capability until
/// after the gate. Reading them as production preconditions makes the gate circular.
/// </remarks>
internal sealed class StubCapabilityResolver : ICoachCapabilityResolver
{
    private readonly Dictionary<string, CoachCapabilityAvailability> _answers = new(StringComparer.Ordinal);

    internal StubCapabilityResolver Declare(string name, CoachCapabilityAvailability availability)
    {
        _answers[name] = availability;
        return this;
    }

    public CoachCapabilityAvailability Resolve(
        string name,
        CoachCapabilityStage currentStage,
        CoachClientCapabilityHandshake? handshake) =>
        _answers.TryGetValue(name, out var availability)
            ? availability
            : CoachCapabilityAvailability.AbsentUnimplemented;
}

/// <summary>A manifest a test can populate with synthetic descriptors.</summary>
internal sealed class StubCapabilityManifest : ICoachCapabilityManifest
{
    private readonly List<CoachCapabilityDescriptor> _descriptors = [];

    internal StubCapabilityManifest Declare(string name, CoachCapabilityEffectClass effectClass)
    {
        _descriptors.Add(new CoachCapabilityDescriptor
        {
            Name = name,
            IsToolBacked = true,
            EffectClass = effectClass,
            Surface = CoachCapabilitySurface.Server,
            MaxAvailability = CoachCapabilityAvailability.Present,
            RequiredStage = CoachCapabilityStage.Read,
            Reversal = CoachCapabilityReversal.None,
            Confirmation = CoachCapabilityConfirmation.None,
            ReceiptKind = CoachCapabilityReceiptKind.None,
            Scope = CoachCapabilityScope.Session,
            DeclaredStepCount = 1,
            RiskClass = CoachToolRiskClass.Read
        });

        return this;
    }

    public IReadOnlyList<CoachCapabilityDescriptor> All => _descriptors;

    public CoachCapabilityDescriptor? Find(string name) =>
        _descriptors.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Name, name, StringComparison.Ordinal));

    public bool Contains(string name) => Find(name) is not null;
}
