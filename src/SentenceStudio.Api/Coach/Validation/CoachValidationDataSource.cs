using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// Supplies the server-only facts the coach validators need.
/// </summary>
/// <remarks>
/// <para>
/// The read-only tools deliberately return no target-language term, translation, example, or
/// mnemonic, so the model never receives them. The leak validator still has to know what the
/// embargoed values are, so it reads them here — inside validation, after the model has
/// answered, and never on any path that builds agent context.
/// </para>
/// <para>
/// Scoped, and every query filters by the trusted user resolved from
/// <see cref="IUserScopeProvider"/>. No caller passes a user identifier.
/// </para>
/// </remarks>
public interface ICoachValidationDataSource
{
    /// <summary>
    /// The words the learner must not see repeated: everything due now, plus any word the
    /// preview selected for this session.
    /// </summary>
    Task<IReadOnlyList<CoachEmbargoedItem>> GetEmbargoedItemsAsync(
        string userProfileId,
        IEnumerable<string>? additionalWordIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>Every resource identifier the learner owns, for the ownership check.</summary>
    Task<IReadOnlyCollection<string>> GetOwnedResourceIdsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICoachValidationDataSource"/>
public sealed class CoachValidationDataSource : ICoachValidationDataSource
{
    /// <summary>
    /// Upper bound on the embargo set. A learner with thousands of due words still gets a
    /// bounded validation cost; the cap is far above a single session's focus set, so the
    /// words a turn could plausibly quote are always covered.
    /// </summary>
    private const int MaxEmbargoedWords = 400;

    /// <summary>Examples are the largest text, so they are capped harder.</summary>
    private const int MaxExamplesPerWord = 2;

    private readonly ApplicationDbContext _db;
    private readonly IUserScopeProvider _userScope;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<CoachValidationDataSource> _logger;

    public CoachValidationDataSource(
        ApplicationDbContext db,
        IUserScopeProvider userScope,
        IPlanDateContext dates,
        ILogger<CoachValidationDataSource> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _userScope = userScope ?? throw new ArgumentNullException(nameof(userScope));
        _dates = dates ?? throw new ArgumentNullException(nameof(dates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CoachEmbargoedItem>> GetEmbargoedItemsAsync(
        string userProfileId,
        IEnumerable<string>? additionalWordIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasExactOwner(userProfileId, nameof(GetEmbargoedItemsAsync)))
        {
            return [];
        }

        var now = _dates.UtcNow;

        var extra = additionalWordIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxEmbargoedWords)
            .ToList() ?? [];

        var dueWordIds = await _db.VocabularyProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userProfileId && p.NextReviewDate != null && p.NextReviewDate <= now)
            .OrderBy(p => p.NextReviewDate)
            .Select(p => p.VocabularyWordId)
            .Take(MaxEmbargoedWords)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var wordIds = dueWordIds.Concat(extra).Distinct(StringComparer.Ordinal).ToList();
        if (wordIds.Count == 0)
        {
            return [];
        }

        var words = await _db.VocabularyWords
            .AsNoTracking()
            .Where(w => wordIds.Contains(w.Id))
            .Select(w => new WordRow(
                w.Id,
                w.TargetLanguageTerm,
                w.NativeLanguageTerm,
                w.Lemma,
                w.MnemonicText))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var examples = await _db.ExampleSentences
            .AsNoTracking()
            .Where(e => wordIds.Contains(e.VocabularyWordId))
            .Select(e => new ExampleRow(e.VocabularyWordId, e.TargetSentence, e.NativeSentence))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var examplesByWord = examples
            .GroupBy(e => e.VocabularyWordId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(e => new[] { e.TargetSentence, e.NativeSentence })
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .Take(MaxExamplesPerWord)
                    .ToList(),
                StringComparer.Ordinal);

        return words
            .Select(w => new CoachEmbargoedItem(
                w.TargetLanguageTerm,
                w.NativeLanguageTerm,
                w.Lemma,
                examplesByWord.TryGetValue(w.Id, out var list) ? list : null,
                w.MnemonicText))
            .ToList();
    }

    public async Task<IReadOnlyCollection<string>> GetOwnedResourceIdsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasExactOwner(userProfileId, nameof(GetOwnedResourceIdsAsync)))
        {
            return [];
        }

        var ids = await _db.LearningResources
            .AsNoTracking()
            .Where(r => r.UserProfileId == userProfileId)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private bool HasExactOwner(string? userProfileId, string operation)
    {
        if (string.IsNullOrWhiteSpace(userProfileId)
            || !_userScope.TryGetUserProfileId(out var activeUserProfileId)
            || !string.Equals(userProfileId, activeUserProfileId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "{Operation} called with no matching active userId — returning empty",
                operation);
            return false;
        }

        return true;
    }

    private sealed record WordRow(
        string Id,
        string? TargetLanguageTerm,
        string? NativeLanguageTerm,
        string? Lemma,
        string? MnemonicText);

    private sealed record ExampleRow(string VocabularyWordId, string TargetSentence, string? NativeSentence);
}
