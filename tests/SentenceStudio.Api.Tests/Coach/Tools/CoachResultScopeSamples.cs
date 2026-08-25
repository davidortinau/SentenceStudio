using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Scope values for tests whose subject is something other than the scope.
/// </summary>
/// <remarks>
/// A result envelope cannot be constructed without a scope, which is the point — but a test about
/// intent validation or redaction should not have to describe coverage, tie-breaks, and clock
/// bases to say what it is really about. These samples exist for those tests, and deliberately do
/// not exist for the scope contract tests, which build every scope they assert on by hand so a
/// shared default can never quietly become the thing under test.
/// </remarks>
internal static class CoachResultScopeSamples
{
    /// <summary>A well-formed scope with no claim worth reading.</summary>
    public static CoachResultScope Any(int returnedCount = 0) => new()
    {
        Coverage = CoachScopeCoverage.DerivedProjection,
        Order = CoachScopeOrder.NotApplicable,
        OrderHonored = true,
        Filters = CoachScopeFilters.OwnerScoped,
        AsOfUtc = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc),
        ReturnedCount = returnedCount,
        DefinitionCode = CoachScopeDefinition.DeterministicPlanPreview,
        MinimumEvidence = CoachScopeMinimumEvidence.None,
        TieBreak = CoachScopeTieBreak.NotApplicable,
        ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
        ReferenceMode = CoachScopeReferenceMode.AsOfInstant
    };
}
