using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Tenant-scoped, deterministic <see cref="IVocabularyFocusResolver"/>.
/// No LLM, no network — it matches only on persisted canonical metadata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> <c>VocabularyWord</c> rows are global; ownership is derived.
/// A word counts as owned when the learner has a <c>VocabularyProgress</c> row
/// for it, or when it is mapped to one of their <c>LearningResource</c> rows.
/// Both sides are filtered by the trusted scope and the union is deduped.
/// </para>
/// <para>
/// <b>Fail closed.</b> An unresolvable scope logs a warning and returns typed
/// no-data. There is no code path that queries vocabulary without a user filter.
/// </para>
/// </remarks>
public sealed class VocabularyFocusResolver : IVocabularyFocusResolver
{
    /// <summary>
    /// Below this share of classified owned words, a shortfall is more likely a
    /// metadata gap than a real absence, so we report MetadataUnavailable rather
    /// than an authoritative "no matches".
    /// </summary>
    private const double MinimumClassifiedCoverage = 0.20;

    private readonly ApplicationDbContext _db;
    private readonly IUserScopeProvider _scope;
    private readonly IPlanDateContext _dateContext;
    private readonly ILogger<VocabularyFocusResolver> _logger;

    public VocabularyFocusResolver(
        ApplicationDbContext db,
        IUserScopeProvider scope,
        IPlanDateContext dateContext,
        ILogger<VocabularyFocusResolver> logger)
    {
        _db = db;
        _scope = scope;
        _dateContext = dateContext;
        _logger = logger;
    }

    public async Task<VocabularyFocusResult> ResolveAsync(
        VocabularyFocusRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.TryValidate(out var errors))
        {
            _logger.LogInformation(
                "Vocabulary focus rejected: {ErrorCount} invalid field(s).", errors.Count);
            return VocabularyFocusResult.Invalid(request, errors);
        }

        if (!_scope.TryGetUserProfileId(out var userId) || string.IsNullOrEmpty(userId))
        {
            // Fail closed. Never fall through to an unfiltered vocabulary query.
            _logger.LogWarning("Vocabulary focus requested with no active user scope — returning no data.");
            return Empty(request, VocabularyFocusOutcome.NoMatches);
        }

        var today = _dateContext.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Ownership, both derivations, filtered by the trusted scope.
        var progressOwned = _db.VocabularyProgresses
            .Where(p => p.UserId == userId)
            .Select(p => p.VocabularyWordId);

        var resourceOwned = _db.ResourceVocabularyMappings
            .Where(m => _db.LearningResources
                .Any(r => r.Id == m.ResourceId && r.UserProfileId == userId))
            .Select(m => m.VocabularyWordId);

        var ownedIds = await progressOwned
            .Union(resourceOwned)
            .Distinct()
            .ToListAsync(ct);

        if (ownedIds.Count == 0)
        {
            _logger.LogInformation("Vocabulary focus found no owned vocabulary for the active scope.");
            return Empty(request, VocabularyFocusOutcome.NoMatches);
        }

        var ownedWords = await _db.VocabularyWords
            .AsNoTracking()
            .Where(w => ownedIds.Contains(w.Id))
            .Select(w => new WordRow(w.Id, w.TargetLanguageTerm, w.NativeLanguageTerm, w.PartOfSpeech, w.Tags))
            .ToListAsync(ct);

        var classifiedCount = ownedWords.Count(w => w.PartOfSpeech is not null);

        var matches = ownedWords.Where(w => Matches(w, request)).ToList();

        // A part-of-speech focus is only trustworthy when enough owned words are
        // actually classified. Report the gap with counts instead of handing back
        // a set that silently omits unclassified words.
        if (request.PartOfSpeech is not null && matches.Count < VocabularyFocusRequest.MinCount)
        {
            var coverage = (double)classifiedCount / ownedWords.Count;
            if (coverage < MinimumClassifiedCoverage)
            {
                _logger.LogInformation(
                    "Vocabulary focus metadata unavailable: {Classified}/{Owned} owned words classified, {Matched} matched.",
                    classifiedCount, ownedWords.Count, matches.Count);
                return Counted(request, VocabularyFocusOutcome.MetadataUnavailable,
                    ownedWords.Count, classifiedCount, matches.Count);
            }
        }

        if (matches.Count == 0)
        {
            return Counted(request, VocabularyFocusOutcome.NoMatches,
                ownedWords.Count, classifiedCount, 0);
        }

        var progressById = await _db.VocabularyProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new ProgressRow(
                p.VocabularyWordId, p.MasteryScore, p.TotalAttempts, p.NextReviewDate, p.LastPracticedAt))
            .ToListAsync(ct);

        var progressLookup = progressById
            .GroupBy(p => p.VocabularyWordId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var ranked = matches
            .Select(w => Rank(w, progressLookup.GetValueOrDefault(w.Id), today))
            .OrderBy(r => (int)r.Tier)
            .ThenBy(r => r.SecondarySort)
            .ThenBy(r => r.Word.Id, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count < VocabularyFocusRequest.MinCount)
        {
            // No padding with unrelated words: an undersized honest set is
            // reported as such so the caller can ask for a different focus.
            _logger.LogInformation(
                "Vocabulary focus matched only {Matched} words, below the minimum bound.", ranked.Count);
            return Counted(request, VocabularyFocusOutcome.InsufficientMatches,
                ownedWords.Count, classifiedCount, ranked.Count);
        }

        var items = ranked
            .Take(request.RequestedCount)
            .Select(r => new VocabularyFocusItem
            {
                VocabularyWordId = r.Word.Id,
                TargetLanguageTerm = r.Word.TargetLanguageTerm,
                NativeLanguageTerm = r.Word.NativeLanguageTerm,
                PartOfSpeech = r.Word.PartOfSpeech,
                MatchReason = r.Reason
            })
            .ToList();

        _logger.LogInformation(
            "Vocabulary focus selected {Selected} of {Matched} matching words ({Classified}/{Owned} classified).",
            items.Count, ranked.Count, classifiedCount, ownedWords.Count);

        return new VocabularyFocusResult
        {
            Outcome = VocabularyFocusOutcome.Success,
            Items = items,
            DisplayDescription = request.DisplayDescription,
            RequestedCount = request.RequestedCount,
            OwnedCandidateCount = ownedWords.Count,
            ClassifiedCandidateCount = classifiedCount,
            MatchedCount = ranked.Count
        };
    }

    /// <summary>
    /// Canonical matching only: exact part of speech and/or whole-tag equality.
    /// Never a substring scan over terms or glosses.
    /// </summary>
    private static bool Matches(WordRow word, VocabularyFocusRequest request)
    {
        if (request.PartOfSpeech is { } pos && word.PartOfSpeech != pos)
        {
            return false;
        }

        var tags = request.NormalizedCategoryTags();
        if (tags.Count == 0)
        {
            return true;
        }

        var wordTags = SplitTags(word.Tags);
        return tags.Any(t => wordTags.Contains(t));
    }

    /// <summary>
    /// Splits the stored tag string into whole, normalized tags. Both separators
    /// in use are honored: consumers split on comma, while the extraction
    /// mapper joins its groups with a semicolon.
    /// </summary>
    private static HashSet<string> SplitTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return tags
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static RankedWord Rank(WordRow word, ProgressRow? progress, DateTime today)
    {
        if (progress is null || progress.TotalAttempts == 0)
        {
            // Owned but never practiced: high learning value, ranked right after
            // due/weak work. Stable secondary sort keeps the order reproducible.
            return new RankedWord(word, RankTier.NeverPracticed, 0d, VocabularyFocusMatchReason.NeverPracticed);
        }

        if (progress.NextReviewDate is { } due && due <= today)
        {
            return new RankedWord(word, RankTier.DueOrWeak, progress.MasteryScore, VocabularyFocusMatchReason.DueForReview);
        }

        if (progress.MasteryScore < 0.6f)
        {
            return new RankedWord(word, RankTier.DueOrWeak, progress.MasteryScore, VocabularyFocusMatchReason.WeakMastery);
        }

        return new RankedWord(
            word,
            RankTier.LeastRecentlyPracticed,
            progress.LastPracticedAt.Ticks,
            VocabularyFocusMatchReason.LeastRecentlyPracticed);
    }

    private static VocabularyFocusResult Empty(VocabularyFocusRequest request, VocabularyFocusOutcome outcome) =>
        Counted(request, outcome, 0, 0, 0);

    private static VocabularyFocusResult Counted(
        VocabularyFocusRequest request,
        VocabularyFocusOutcome outcome,
        int owned,
        int classified,
        int matched) =>
        new()
        {
            Outcome = outcome,
            DisplayDescription = request.DisplayDescription,
            RequestedCount = request.RequestedCount,
            OwnedCandidateCount = owned,
            ClassifiedCandidateCount = classified,
            MatchedCount = matched
        };

    private enum RankTier
    {
        DueOrWeak = 0,
        NeverPracticed = 1,
        LeastRecentlyPracticed = 2
    }

    private sealed record WordRow(
        string Id,
        string? TargetLanguageTerm,
        string? NativeLanguageTerm,
        VocabularyPartOfSpeech? PartOfSpeech,
        string? Tags);

    private sealed record ProgressRow(
        string VocabularyWordId,
        float MasteryScore,
        int TotalAttempts,
        DateTime? NextReviewDate,
        DateTime LastPracticedAt);

    private sealed record RankedWord(
        WordRow Word,
        RankTier Tier,
        double SecondarySort,
        VocabularyFocusMatchReason Reason);
}
