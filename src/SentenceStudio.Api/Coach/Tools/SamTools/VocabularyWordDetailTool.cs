using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Reads detail for one vocabulary word the learner owns.
/// Never exposes mnemonics. Authenticates from <see cref="IUserScopeProvider"/>.
/// </summary>
public sealed class VocabularyWordDetailTool : CoachToolBase
{
    private const int MaxTermLength = 80;

    private readonly IVocabularyQueries _vocabulary;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<VocabularyWordDetailTool> _logger;

    public VocabularyWordDetailTool(
        IUserScopeProvider userScope,
        IVocabularyQueries vocabulary,
        IPlanDateContext dates,
        ILogger<VocabularyWordDetailTool> logger)
        : base(userScope)
    {
        _vocabulary = vocabulary;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetVocabularyWordDetail;

    public async Task<VocabularyWordDetail> GetAsync(string wordId, CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();

        if (string.IsNullOrWhiteSpace(wordId))
            throw InvalidArgument("The word identifier is required.");

        try
        {
            // Ownership runs through progress, so a word the learner has never met is unreachable
            // by guessing its identifier.
            var row = await _vocabulary.GetTrackedWordAsync(userId, wordId, ct);

            if (row is null)
                throw InvalidArgument("The word does not exist or does not belong to this learner.");

            return new VocabularyWordDetail(
                WordId: row.WordId,
                TargetTerm: SanitizeMetadata(row.TargetLanguageTerm, MaxTermLength),
                NativeTerm: SanitizeMetadata(row.NativeLanguageTerm, MaxTermLength),
                Lemma: row.Lemma is null ? null : SanitizeMetadata(row.Lemma, MaxTermLength),
                Language: row.Language is null ? null : SanitizeMetadata(row.Language, 40),
                Tags: SplitTags(row.Tags),
                MasteryScore: Math.Round(row.MasteryScore, 3),
                DaysSinceLastPractice: row.LastPracticedAt is { } lp
                    ? Math.Max(0, (int)(_dates.UtcNow.Date - lp.Date).TotalDays)
                    : null,
                TotalAttempts: row.TotalAttempts,
                CorrectAttempts: row.CorrectAttempts,
                IsLearnerAdded: row.IsUserDeclared,
                Scope: new CoachResultScope
                {
                    Coverage = CoachScopeCoverage.SingleItem,
                    Order = CoachScopeOrder.NotApplicable,
                    OrderHonored = true,
                    // Deliberately no ExcludeDue. This is the sanctioned route to a due word: the
                    // learner named one, which makes the disclosure explicit and auditable rather
                    // than incidental to a browse.
                    Filters = CoachScopeFilters.OwnerScoped
                        | CoachScopeFilters.ProgressRowExists
                        | CoachScopeFilters.SingleIdentifier,
                    AsOfUtc = _dates.UtcNow,
                    ReturnedCount = 1,
                    DefinitionCode = CoachScopeDefinition.TrackedVocabularyDetail,
                    MinimumEvidence = CoachScopeMinimumEvidence.ProgressRowRequired,
                    TieBreak = CoachScopeTieBreak.NotApplicable,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                });
        }
        catch (CoachToolException) { throw; }
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
