using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Reads the counts, the bands, and the tags for due vocabulary work.
/// The answer never holds a target-language term, a translation, an example,
/// or a memory aid. Only counts and category tags cross this boundary.
/// </summary>
public sealed class VocabularyDueSummaryTool : CoachToolBase
{
    private const int MinTagCount = 1;
    /// <summary>
    /// The largest category-tag list this read will return, whatever the caller asks for.
    /// </summary>
    /// <remarks>
    /// Public for the same reason as the other four bounded reads: the capability metadata cites
    /// this constant instead of restating it. This one is also the constant a hand-transcribed
    /// table got wrong — it recorded no ceiling at all for a read that rejects anything outside
    /// one to twenty.
    /// </remarks>
    public const int MaxTagCount = 20;

    /// <summary>The tag count used when the model sends no value, or an explicit null.</summary>
    public const int DefaultTagCount = 8;

    private const int MaxTagLength = 40;

    private readonly IVocabularyQueries _vocabulary;
    private readonly IPlanDateContext _dates;

    public VocabularyDueSummaryTool(
        IUserScopeProvider userScope,
        IVocabularyQueries vocabulary,
        IPlanDateContext dates)
        : base(userScope)
    {
        _vocabulary = vocabulary;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.GetVocabularyDueSummary;

    /// <summary>Returns the due counts, the mastery bands, the lapse rate, and the category tags.</summary>
    public async Task<VocabularyDueSummary> GetAsync(
        int maxCategoryTags = DefaultTagCount,
        CancellationToken ct = default)
    {
        var userProfileId = RequireUserProfileId();

        if (maxCategoryTags is < MinTagCount or > MaxTagCount)
        {
            throw InvalidArgument($"The tag count must be from {MinTagCount} to {MaxTagCount}.");
        }

        var now = _dates.UtcNow;
        var weekEnd = now.AddDays(7);

        List<VocabularyProgressFacts> rows;
        IReadOnlyList<string?> dueTagValues;
        try
        {
            rows = [.. await _vocabulary.GetProgressFactsAsync(userProfileId, ct)];
            dueTagValues = await _vocabulary.GetDueWordTagsAsync(userProfileId, now, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw DataAccessFailure(ex);
        }

        var bands = rows
            .GroupBy(ToBand)
            .Select(g => new VocabularyBandCount(g.Key.ToString(), g.Count()))
            .OrderBy(b => b.Band, StringComparer.Ordinal)
            .ToList();

        var totalAttempts = rows.Sum(r => (long)Math.Max(0, r.TotalAttempts));
        var correctAttempts = rows.Sum(r => (long)Math.Max(0, r.CorrectAttempts));
        var lapseRate = totalAttempts == 0
            ? 0d
            : Math.Clamp(1d - ((double)correctAttempts / totalAttempts), 0d, 1d);

        var averageMastery = rows.Count == 0
            ? 0d
            : Math.Clamp(rows.Average(r => (double)r.MasteryScore), 0d, 1d);

        var tagCounts = dueTagValues
            .SelectMany(SplitTags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VocabularyTagCount(g.Key, g.Count()))
            .OrderByDescending(t => t.DueCount)
            .ThenBy(t => t.Tag, StringComparer.Ordinal)
            .ToList();

        var distinctTagCount = tagCounts.Count;
        var shownTags = tagCounts.Take(maxCategoryTags).ToList();

        return new VocabularyDueSummary(
            DueNowCount: rows.Count(r => r.NextReviewDate is { } d && d <= now),
            DueThisWeekCount: rows.Count(r => r.NextReviewDate is { } d && d > now && d <= weekEnd),
            NeverPracticedCount: rows.Count(r => r.TotalAttempts == 0),
            TrackedWordCount: rows.Count,
            Bands: bands,
            LapseRate: lapseRate,
            AverageMasteryScore: averageMastery,
            CategoryTags: shownTags,
            Scope: new CoachResultScope
            {
                // Two populations in one answer, so the coverage names both rather than picking
                // one and being wrong about the other. The counts above — tracked, due, never
                // practised, the bands, the lapse rate — cover every word the learner owns. The
                // category-tag list does not: it is the distinct tags found on the due words, and
                // it is paged. Reporting CompleteOwnedSet with Truncated set said both "you have
                // all of it" and "you do not"; reporting PageOfOwnedSet would understate counts
                // that really are complete.
                Coverage = CoachScopeCoverage.CompleteAggregateWithBreakdown,
                Order = CoachScopeOrder.FrequencyDescending,
                OrderHonored = true,
                Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.ProgressRowExists,
                AsOfUtc = now,

                // Every count from here down is about the tag breakdown, which is what the
                // coverage member says. The word-level totals are named fields on the answer body
                // (TrackedWordCount, DueNowCount, and the rest), where they cannot be confused
                // with these.
                RequestedCount = maxCategoryTags,
                ReturnedCount = shownTags.Count,
                MatchedCount = distinctTagCount,
                Truncated = distinctTagCount > shownTags.Count,
                DefinitionCode = CoachScopeDefinition.TrackedVocabularyDueSummary,

                // Nothing is withheld from the tag list, so every matched tag is eligible and the
                // only thing standing between eligible and returned is the page size. This used to
                // report the tracked-word count, which made the scope claim more eligible rows
                // than it had ever matched whenever a learner owned more words than tags.
                EligiblePopulationCount = distinctTagCount,
                MinimumEvidence = CoachScopeMinimumEvidence.ProgressRowRequired,
                TieBreak = CoachScopeTieBreak.TagOrdinal,
                ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                ReferenceMode = CoachScopeReferenceMode.AsOfInstant
            });
    }

    /// <summary>
    /// Maps a progress row onto the app's own learning bands.
    /// The rule stays in <see cref="VocabularyProgress"/>, so the coach and the
    /// rest of the app always report the same band for the same row.
    /// </summary>
    private static LearningStatus ToBand(VocabularyProgressFacts row) => new VocabularyProgress
    {
        MasteryScore = row.MasteryScore,
        ProductionInStreak = row.ProductionInStreak,
        TotalAttempts = row.TotalAttempts,
        IsUserDeclared = row.IsUserDeclared,
        VerificationState = row.VerificationState
    }.Status;

    private static IEnumerable<string> SplitTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            yield break;
        }

        foreach (var raw in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = SanitizeMetadata(raw, MaxTagLength);
            if (cleaned.Length > 0)
            {
                yield return cleaned;
            }
        }
    }
}
