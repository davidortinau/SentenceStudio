using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Reads the example sentences on one word the learner owns, named by the caller.
/// </summary>
/// <remarks>
/// <para>
/// The word is found by the one ownership rule (recorded to the learner), practised or not, so a
/// word the learner just added is reachable. Only the learner's own sentences are read.
/// </para>
/// <para>
/// A word that is due for review returns its sentences like any other (issue 258): being due
/// is a scheduling fact the learner can see in their own app. <c>IsDueForReview</c> reports it.
/// </para>
/// </remarks>
public sealed class VocabularyExampleSentencesTool : CoachToolBase
{
    /// <summary>The most sentences one read returns.</summary>
    /// <remarks>Public so the read-capability metadata cites it rather than restating it.</remarks>
    public const int MaxResults = 20;
    private const int MaxSentenceLength = 500;
    private const int MaxTermLength = 80;

    private readonly IVocabularyQueries _vocabulary;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<VocabularyExampleSentencesTool> _logger;

    public VocabularyExampleSentencesTool(
        IUserScopeProvider userScope,
        IVocabularyQueries vocabulary,
        IPlanDateContext dates,
        ILogger<VocabularyExampleSentencesTool> logger)
        : base(userScope)
    {
        _vocabulary = vocabulary;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetVocabularyExampleSentences;

    public async Task<VocabularyExampleSentencesResult> GetAsync(
        string wordId,
        int maxResults = MaxResults,
        CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();

        if (string.IsNullOrWhiteSpace(wordId))
            throw InvalidArgument("The word identifier is required.");

        maxResults = Math.Clamp(maxResults, 1, MaxResults);
        var now = _dates.UtcNow;

        try
        {
            var page = await _vocabulary.GetWordExampleSentencesAsync(userId, wordId, maxResults, now, ct);

            if (page is null)
                throw InvalidArgument("The word does not exist or does not belong to this learner.");

            var entries = page.Sentences
                .Select(s => new VocabularyExampleSentenceEntry(
                    SentenceId: s.SentenceId,
                    TargetSentence: SanitizeMetadata(s.TargetSentence, MaxSentenceLength),
                    NativeSentence: s.NativeSentence is null ? null : SanitizeMetadata(s.NativeSentence, MaxSentenceLength),
                    IsCore: s.IsCore))
                .ToList();

            const int withheld = 0;
            var truncated = page.TotalCount > entries.Count;

            return new VocabularyExampleSentencesResult(
                WordId: page.WordId,
                TargetTerm: page.TargetLanguageTerm is null ? null : SanitizeMetadata(page.TargetLanguageTerm, MaxTermLength),
                IsDueForReview: page.IsDueForReview,
                TotalCount: page.TotalCount,
                Sentences: entries,
                Scope: new CoachResultScope
                {
                    Coverage = truncated ? CoachScopeCoverage.PageOfOwnedSet : CoachScopeCoverage.CompleteOwnedSet,
                    // Main example first, then oldest first; ties between equal creation times
                    // have no declared order, so no order is promised to the caller.
                    Order = CoachScopeOrder.Unordered,
                    OrderHonored = true,
                    Filters = CoachScopeFilters.OwnerScoped
                        | CoachScopeFilters.SingleIdentifier,
                    AsOfUtc = now,
                    RequestedCount = maxResults,
                    ReturnedCount = entries.Count,
                    MatchedCount = page.TotalCount,
                    EligiblePopulationCount = page.TotalCount - withheld,
                    WithheldCount = withheld,
                    WithheldReason = CoachScopeWithheldReason.None,
                    Truncated = truncated,
                    // Reuses the single-word definition: the read is about one word the learner
                    // named, and a new code would be a client wire change for no new meaning.
                    DefinitionCode = CoachScopeDefinition.TrackedVocabularyDetail,
                    MinimumEvidence = CoachScopeMinimumEvidence.None,
                    TieBreak = CoachScopeTieBreak.None,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                });
        }
        catch (CoachToolException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }
}
