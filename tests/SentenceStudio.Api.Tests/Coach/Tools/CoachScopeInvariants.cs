using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// The count relationships every scope must satisfy, as a checker rather than as assertions.
/// </summary>
/// <remarks>
/// <para>
/// Written as a function returning violations, for two reasons. The sweep over the live tools can
/// run it against every registered read and report which tool broke which rule; and a mutation
/// test can run it against a hand-built scope describing the shape a tool used to emit, and prove
/// the checker actually rejects it. A rule expressed only as inline assertions inside a
/// <c>foreach</c> can be verified against real data but never against the defect it exists to
/// catch, which is how a sweep ends up passing over a fixture that never exercises it.
/// </para>
/// <para>
/// Every rule is about <b>one</b> population. Which population that is, is named by
/// <see cref="CoachResultScope.Coverage"/>: for most reads it is the rows the answer carries, and
/// for <see cref="CoachScopeCoverage.CompleteAggregateWithBreakdown"/> it is the answer's
/// breakdown sub-list while the aggregates live on the answer body. A scope that mixes two
/// populations across these fields will fail rule 1 or rule 2 as soon as the two populations
/// differ in size, which is exactly the defect.
/// </para>
/// </remarks>
internal static class CoachScopeInvariants
{
    /// <summary>
    /// The coverage values that admit a <see cref="CoachResultScope.Truncated"/> answer.
    /// </summary>
    /// <remarks>
    /// A page can be truncated, and a complete-aggregate answer can have a paged breakdown.
    /// Everything else claims to hold its whole population, so a truncation flag beside it is a
    /// contradiction rather than extra detail.
    /// </remarks>
    private static readonly CoachScopeCoverage[] MayTruncate =
    [
        CoachScopeCoverage.PageOfOwnedSet,
        CoachScopeCoverage.CompleteAggregateWithBreakdown
    ];

    /// <summary>The filter flag that must accompany each withholding reason.</summary>
    private static readonly Dictionary<CoachScopeWithheldReason, CoachScopeFilters> ReasonFilters = new()
    {
        [CoachScopeWithheldReason.DueReviewEmbargo] = CoachScopeFilters.ExcludeDue,
        [CoachScopeWithheldReason.ArchivedExcluded] = CoachScopeFilters.ExcludeArchived,
        [CoachScopeWithheldReason.BelowMinimumEvidence] = CoachScopeFilters.MinimumEvidence
    };

    /// <summary>Every rule <paramref name="scope"/> breaks, in plain sentences.</summary>
    public static IReadOnlyList<string> Violations(CoachResultScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var broken = new List<string>();

        // -- 1. returned <= eligible <= matched -----------------------------
        // The three counts are nested views of one population. Any inversion means at least two
        // of them are counting different things.
        if (scope.EligiblePopulationCount is { } eligible)
        {
            if (scope.ReturnedCount > eligible)
            {
                broken.Add(
                    $"returned {scope.ReturnedCount} rows but says only {eligible} were eligible");
            }

            if (scope.MatchedCount is { } matchedForEligible && eligible > matchedForEligible)
            {
                broken.Add(
                    $"says {eligible} rows were eligible out of {matchedForEligible} matched, which "
                    + "counts two different populations");
            }
        }

        if (scope.MatchedCount is { } matched)
        {
            if (scope.ReturnedCount > matched)
            {
                broken.Add($"returned {scope.ReturnedCount} rows but says only {matched} matched");
            }

            // -- 2. matched == returned + withheld when nothing was paged ----
            // Withholding and paging are the only two ways a matched row fails to be returned. If
            // neither happened, the arithmetic has to close; if it does not, the gap is
            // unexplained and the model will invent an explanation for it.
            if (!scope.Truncated && matched != scope.ReturnedCount + scope.WithheldCount)
            {
                broken.Add(
                    $"matched {matched}, returned {scope.ReturnedCount} and withheld "
                    + $"{scope.WithheldCount} with nothing paged, leaving "
                    + $"{matched - scope.ReturnedCount - scope.WithheldCount} rows unaccounted for");
            }

            if (scope.WithheldCount > matched)
            {
                broken.Add($"withheld {scope.WithheldCount} rows out of {matched} matched");
            }
        }

        // -- 3. truncated describes the population coverage names ------------
        if (scope.Truncated)
        {
            if (!MayTruncate.Contains(scope.Coverage))
            {
                broken.Add(
                    $"reports coverage '{scope.Coverage}' and truncation at the same time; the "
                    + "coverage claims a whole population and the flag says part of it is missing");
            }

            if (scope.EligiblePopulationCount is { } eligibleWhenTruncated
                && eligibleWhenTruncated <= scope.ReturnedCount)
            {
                broken.Add(
                    $"says it truncated but returned all {eligibleWhenTruncated} eligible rows");
            }
        }
        else if (scope.EligiblePopulationCount is { } eligibleWhenWhole
            && eligibleWhenWhole > scope.ReturnedCount)
        {
            broken.Add(
                $"returned {scope.ReturnedCount} of {eligibleWhenWhole} eligible rows without "
                + "reporting truncation");
        }

        if (scope.Coverage == CoachScopeCoverage.CompleteOwnedSet
            && scope.EligiblePopulationCount is { } eligibleForComplete
            && eligibleForComplete != scope.ReturnedCount)
        {
            broken.Add(
                $"claims the complete owned set but returned {scope.ReturnedCount} of "
                + $"{eligibleForComplete} eligible rows");
        }

        // -- 4. withheld count, reason and filter agree ----------------------
        if ((scope.WithheldCount > 0) != (scope.WithheldReason != CoachScopeWithheldReason.None))
        {
            broken.Add(
                $"reports {scope.WithheldCount} withheld for reason '{scope.WithheldReason}'; a "
                + "count with no reason is unexplained and a reason with no count warns about nothing");
        }

        if (scope.WithheldReason != CoachScopeWithheldReason.None
            && ReasonFilters.TryGetValue(scope.WithheldReason, out var requiredFilter)
            && !scope.Filters.HasFlag(requiredFilter))
        {
            broken.Add(
                $"withheld rows for reason '{scope.WithheldReason}' without carrying the "
                + $"'{requiredFilter}' filter that names the predicate responsible");
        }

        // -- 5. a page never returns more than it was asked for --------------
        if (scope.RequestedCount is { } requested && scope.ReturnedCount > requested)
        {
            broken.Add($"returned {scope.ReturnedCount} rows after being asked for {requested}");
        }

        return broken;
    }

    /// <summary>True when <paramref name="scope"/> breaks nothing.</summary>
    public static bool IsConsistent(CoachResultScope scope) => Violations(scope).Count == 0;
}
