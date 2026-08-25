using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Searches the learner's vocabulary words by query. Returns matches with mastery
/// but never mnemonics, example audio, or raw IDs belonging to another learner.
/// </summary>
/// <remarks>
/// <para>
/// Words that are currently due for review are excluded from every projection this tool
/// produces, with or without a query. A due word's target term is the answer to a review
/// the learner has not taken yet, and <c>get_vocabulary_due_summary</c> exists precisely so
/// the model can reason about due volume without ever seeing those answers. A bulk list that
/// happened to include them would defeat that embargo through the side door — the model would
/// only need to ask for "my vocabulary" to be handed the same terms the due tool withholds.
/// </para>
/// <para>
/// The sanctioned way to see a due word is <c>get_vocabulary_word_detail</c>: the learner names
/// one specific word, which makes the disclosure explicit and auditable rather than incidental
/// to an unrelated browse.
/// </para>
/// </remarks>
public sealed class VocabularySearchTool : CoachToolBase
{
    /// <summary>The largest page this read will return, whatever the caller asks for.</summary>
    /// <remarks>Public so the read-capability metadata cites it rather than restating it.</remarks>
    public const int MaxResults = 25;
    private const int MaxQueryLength = 100;
    private const int MaxTermLength = 80;

    private readonly IVocabularyQueries _vocabulary;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<VocabularySearchTool> _logger;

    public VocabularySearchTool(
        IUserScopeProvider userScope,
        IVocabularyQueries vocabulary,
        IPlanDateContext dates,
        ILogger<VocabularySearchTool> logger)
        : base(userScope)
    {
        _vocabulary = vocabulary;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.ListUserVocabularies;

    public async Task<VocabularySearchResult> SearchAsync(
        string? query = null,
        int maxResults = 10,
        CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();
        maxResults = Math.Clamp(maxResults, 1, MaxResults);
        query = string.IsNullOrWhiteSpace(query) ? null : SanitizeMetadata(query, MaxQueryLength);
        var now = _dates.UtcNow;

        try
        {
            var page = await _vocabulary.SearchUndueWordsAsync(userId, query, maxResults, now, ct);

            var entries = page.Words.Select(r => new VocabularySearchEntry(
                WordId: r.WordId,
                TargetTerm: SanitizeMetadata(r.TargetLanguageTerm, MaxTermLength),
                NativeTerm: SanitizeMetadata(r.NativeLanguageTerm, MaxTermLength),
                Lemma: r.Lemma is null ? null : SanitizeMetadata(r.Lemma, MaxTermLength),
                Language: r.Language is null ? null : SanitizeMetadata(r.Language, 40),
                Tags: SplitTags(r.Tags),
                MasteryScore: Math.Round(r.MasteryScore, 3),
                DaysSinceLastPractice: r.LastPracticedAt is { } lp
                    ? Math.Max(0, (int)(_dates.UtcNow.Date - lp.Date).TotalDays)
                    : null
            )).ToList();

            // Matched counts everything the query found; TotalCount counts what survived the due
            // embargo. Their difference is the number of the learner's own words this answer is
            // refusing to name, which the model may state and must not try to fill in.
            var withheld = Math.Max(0, page.MatchedCount - page.TotalCount);

            return new VocabularySearchResult(
                page.TotalCount,
                entries.Count,
                entries,
                new CoachResultScope
                {
                    Coverage = page.TotalCount > entries.Count
                        ? CoachScopeCoverage.PageOfOwnedSet
                        : CoachScopeCoverage.CompleteOwnedSet,
                    Order = CoachScopeOrder.MasteryDescending,
                    OrderHonored = true,
                    Filters = CoachScopeFilters.OwnerScoped
                        | CoachScopeFilters.ProgressRowExists
                        | CoachScopeFilters.ExcludeDue
                        | (query is null ? CoachScopeFilters.None : CoachScopeFilters.TextQuery),
                    AsOfUtc = now,
                    RequestedCount = maxResults,
                    ReturnedCount = entries.Count,
                    MatchedCount = page.MatchedCount,
                    WithheldCount = withheld,
                    // The embargo is named ahead of the result limit on purpose. Both can be true
                    // at once, and of the two only the embargo means "those words exist and you
                    // may not have them" — which is the one the model must not paper over.
                    WithheldReason = withheld > 0
                        ? CoachScopeWithheldReason.DueReviewEmbargo
                        : CoachScopeWithheldReason.None,
                    Truncated = page.TotalCount > entries.Count,
                    DefinitionCode = CoachScopeDefinition.UndueVocabularySearch,
                    EligiblePopulationCount = page.TotalCount,
                    MinimumEvidence = CoachScopeMinimumEvidence.ProgressRowRequired,
                    // Equal mastery scores have no declared order, so the page boundary between
                    // two tied words is arbitrary and the answer says so.
                    TieBreak = CoachScopeTieBreak.None,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }

    private static List<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => SanitizeMetadata(t, 40))
                .Where(t => t.Length > 0)
                .Take(8)
                .ToList();
}
