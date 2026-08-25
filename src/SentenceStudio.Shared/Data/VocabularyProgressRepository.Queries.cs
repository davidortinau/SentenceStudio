using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Vocabulary;

namespace SentenceStudio.Data;

/// <summary>
/// The projection-only read side of a learner's vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// Every query starts from <c>VocabularyProgress</c> and joins outward to <c>VocabularyWord</c>,
/// because a word row is shared content and the progress row is what makes it this learner's.
/// Starting from the word and filtering afterwards would let a guessed identifier reach a word the
/// learner has never met.
/// </para>
/// <para>
/// <see cref="GetProgressFactsAsync"/> deliberately carries no terms. A caller that reports how
/// much work is due should not be holding the answers to it, and the way to guarantee that is for
/// the terms never to be selected.
/// </para>
/// </remarks>
public partial class VocabularyProgressRepository
{
    public async Task<int> CountTrackedWordsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasWordOwner(userProfileId, nameof(CountTrackedWordsAsync)))
        {
            return 0;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.VocabularyProgresses
            .AsNoTracking()
            .CountAsync(p => p.UserId == userProfileId, cancellationToken);
    }

    public async Task<IReadOnlyList<VocabularyProgressFacts>> GetProgressFactsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasWordOwner(userProfileId, nameof(GetProgressFactsAsync)))
        {
            return [];
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.VocabularyProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userProfileId)
            .Select(p => new VocabularyProgressFacts(
                p.MasteryScore,
                p.ProductionInStreak,
                p.TotalAttempts,
                p.CorrectAttempts,
                p.IsUserDeclared,
                p.VerificationState,
                p.NextReviewDate))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string?>> GetDueWordTagsAsync(
        string userProfileId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (!HasWordOwner(userProfileId, nameof(GetDueWordTagsAsync)))
        {
            return [];
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.VocabularyProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userProfileId
                        && p.NextReviewDate != null
                        && p.NextReviewDate <= asOfUtc)
            .Join(
                db.VocabularyWords.AsNoTracking(),
                progress => progress.VocabularyWordId,
                word => word.Id,
                (progress, word) => word.Tags)
            .ToListAsync(cancellationToken);
    }

    public async Task<VocabularyWordPage> SearchUndueWordsAsync(
        string userProfileId,
        string? query,
        int maxResults,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (!HasWordOwner(userProfileId, nameof(SearchUndueWordsAsync)) || maxResults <= 0)
        {
            return new VocabularyWordPage(0, 0, []);
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // The learner's tracked words, narrowed by the query but not yet by the review schedule.
        // Counting here is what lets a caller say "four of your matches are due" without the four
        // terms ever being selected.
        var matchedQuery = db.VocabularyProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userProfileId);

        // Deliberately `is not null` rather than a whitespace test: the caller has already
        // decided whether it has a query, and it signals "no query" with null. An empty string
        // that survived sanitising is still a query the caller asked for, and treating it as
        // absent would quietly widen the result set.
        if (query is not null)
        {
            matchedQuery = matchedQuery.Where(p =>
                db.VocabularyWords.Any(w =>
                    w.Id == p.VocabularyWordId &&
                    (EF.Functions.Like(w.TargetLanguageTerm!, $"%{query}%") ||
                     EF.Functions.Like(w.NativeLanguageTerm!, $"%{query}%") ||
                     EF.Functions.Like(w.Lemma!, $"%{query}%"))));
        }

        var matchedCount = await matchedQuery.CountAsync(cancellationToken);

        // Minus anything currently due for review. A row is due when it has a review date that has
        // already passed; rows with no schedule yet, or a schedule still in the future, are safe to
        // surface in bulk.
        var progressQuery = matchedQuery
            .Where(p => p.NextReviewDate == null || p.NextReviewDate > asOfUtc);

        var totalCount = await progressQuery.CountAsync(cancellationToken);

        // Ordered and paged on the joined rows before the record is built. EF can translate an
        // ORDER BY over an anonymous member; it cannot see through a record constructor, so
        // projecting first would silently push the whole undue set into memory to sort it.
        var rows = await progressQuery
            .Join(db.VocabularyWords.AsNoTracking(),
                p => p.VocabularyWordId,
                w => w.Id,
                (p, w) => new
                {
                    w.Id,
                    w.TargetLanguageTerm,
                    w.NativeLanguageTerm,
                    w.Lemma,
                    w.Language,
                    w.Tags,
                    p.MasteryScore,
                    p.LastPracticedAt,
                    p.IsUserDeclared
                })
            .OrderByDescending(x => x.MasteryScore)
            .Take(maxResults)
            .ToListAsync(cancellationToken);

        var words = rows
            .Select(r => new VocabularyWordSummary(
                r.Id,
                r.TargetLanguageTerm,
                r.NativeLanguageTerm,
                r.Lemma,
                r.Language,
                r.Tags,
                r.MasteryScore,
                r.LastPracticedAt,
                r.IsUserDeclared))
            .ToList();

        return new VocabularyWordPage(matchedCount, totalCount, words);
    }

    public async Task<VocabularyWordDetailFacts?> GetTrackedWordAsync(
        string userProfileId,
        string wordId,
        CancellationToken cancellationToken = default)
    {
        if (!HasWordOwner(userProfileId, nameof(GetTrackedWordAsync)) || string.IsNullOrWhiteSpace(wordId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.VocabularyProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userProfileId && p.VocabularyWordId == wordId)
            .Join(db.VocabularyWords.AsNoTracking(),
                p => p.VocabularyWordId,
                w => w.Id,
                (p, w) => new VocabularyWordDetailFacts(
                    w.Id,
                    w.TargetLanguageTerm,
                    w.NativeLanguageTerm,
                    w.Lemma,
                    w.Language,
                    w.Tags,
                    p.MasteryScore,
                    p.LastPracticedAt,
                    p.TotalAttempts,
                    p.CorrectAttempts,
                    p.IsUserDeclared))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private bool HasWordOwner(string userProfileId, string method)
    {
        if (!string.IsNullOrWhiteSpace(userProfileId))
        {
            return true;
        }

        _logger.LogWarning(
            "VocabularyProgressRepository.{Method} called without an owner — returning empty to prevent cross-tenant data leak.",
            method);
        return false;
    }
}
