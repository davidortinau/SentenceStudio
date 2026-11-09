using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Shared.Models;

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

        // Progress follows its word: only the learner's own words (the one ownership rule).
        return await db.VocabularyProgresses
            .AsNoTracking()
            .CountAsync(p => p.UserId == userProfileId && db.VocabularyWords.Any(w => w.Id == p.VocabularyWordId && w.UserProfileId == userProfileId), cancellationToken);
    }

    public async Task<VocabularyCountFacts> GetVocabularyCountsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        // Checked here, not left to GetVocabSummaryCountsAsync: that method falls back to the
        // active user when it is handed no owner, and a coach read must never guess one.
        if (!HasWordOwner(userProfileId, nameof(GetVocabularyCountsAsync)))
        {
            return VocabularyCountFacts.Empty;
        }

        // The dashboard's own query, so the two numbers cannot drift apart.
        var (newCount, learning, familiar, review, known) = await GetVocabSummaryCountsAsync(userProfileId);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var allOwned = await db.VocabularyWords
            .AsNoTracking()
            .OwnedBy(userProfileId)
            .CountAsync(cancellationToken);

        return new VocabularyCountFacts(newCount, learning, familiar, review, known, allOwned);
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
            .Where(p => p.UserId == userProfileId && db.VocabularyWords.Any(w => w.Id == p.VocabularyWordId && w.UserProfileId == userProfileId))
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
                (progress, word) => new { word.UserProfileId, word.Tags })
            .Where(row => row.UserProfileId == userProfileId)
            .Select(row => row.Tags)
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
            .Where(p => p.UserId == userProfileId && db.VocabularyWords.Any(w => w.Id == p.VocabularyWordId && w.UserProfileId == userProfileId));

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

    public async Task<VocabularyWordPage> SearchOwnedWordsAsync(
        string userProfileId,
        VocabularySearchFilter filter,
        int maxResults,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!HasWordOwner(userProfileId, nameof(SearchOwnedWordsAsync)) || maxResults <= 0)
        {
            return new VocabularyWordPage(0, 0, []);
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var words = db.VocabularyWords.AsNoTracking().OwnedBy(userProfileId);

        if (filter.Query is { } query)
        {
            words = words.Where(w =>
                EF.Functions.Like(w.TargetLanguageTerm!, $"%{query}%") ||
                EF.Functions.Like(w.NativeLanguageTerm!, $"%{query}%") ||
                EF.Functions.Like(w.Lemma!, $"%{query}%"));
        }

        if (filter.Tag is { } tag)
        {
            // Tags are stored comma separated, sometimes with spaces. Matched whole, so "food"
            // does not find "seafood", and without case, so "Food" finds "food".
            var needle = "%," + tag.Replace(" ", string.Empty).ToLowerInvariant() + ",%";
            words = words.Where(w => w.Tags != null
                && EF.Functions.Like("," + w.Tags.Replace(" ", "").ToLower() + ",", needle));
        }

        if (filter.ResourceId is { } resourceId)
        {
            // Only the learner's own resource counts; another learner's identifier matches nothing.
            words = words.Where(w => db.ResourceVocabularyMappings.Any(m =>
                m.VocabularyWordId == w.Id
                && m.ResourceId == resourceId
                && db.LearningResources.Any(r => r.Id == m.ResourceId && r.UserProfileId == userProfileId)));
        }

        // Scheduling facts only, no terms: every narrowing that needs the status rule runs on
        // these rows, and terms are read for the returned page alone.
        var rows = await (
                from w in words
                join p in db.VocabularyProgresses.AsNoTracking().Where(p => p.UserId == userProfileId)
                    on w.Id equals p.VocabularyWordId into progress
                from p in progress.DefaultIfEmpty()
                select new
                {
                    w.Id,
                    HasProgress = p != null,
                    MasteryScore = p != null ? p.MasteryScore : 0f,
                    ProductionInStreak = p != null ? p.ProductionInStreak : 0,
                    TotalAttempts = p != null ? p.TotalAttempts : 0,
                    IsUserDeclared = p != null && p.IsUserDeclared,
                    VerificationState = p != null ? p.VerificationState : VerificationStatus.None,
                    NextReviewDate = p != null ? p.NextReviewDate : null,
                    LastPracticedAt = p != null ? (DateTime?)p.LastPracticedAt : null,
                    w.CreatedAt
                })
            .ToListAsync(cancellationToken);

        var classified = rows
            .Where(r => filter.IncludeNeverPracticed || r.HasProgress)
            .Select(r => new
            {
                Row = r,
                IsDue = r.NextReviewDate != null && r.NextReviewDate <= asOfUtc,
                Status = StatusOf(r.HasProgress, r.MasteryScore, r.ProductionInStreak, r.TotalAttempts,
                    r.IsUserDeclared, r.VerificationState, r.NextReviewDate, asOfUtc)
            })
            .Where(r => filter.Status is null || r.Status == filter.Status)
            .ToList();

        var eligible = classified.Where(r => !r.IsDue).ToList();
        var page = eligible
            .OrderByDescending(r => r.Row.HasProgress)
            .ThenByDescending(r => r.Row.MasteryScore)
            .ThenBy(r => r.Row.CreatedAt)
            .Take(maxResults)
            .ToList();

        var ids = page.Select(r => r.Row.Id).ToList();
        var terms = await db.VocabularyWords
            .AsNoTracking()
            .OwnedBy(userProfileId)
            .Where(w => ids.Contains(w.Id))
            .Select(w => new { w.Id, w.TargetLanguageTerm, w.NativeLanguageTerm, w.Lemma, w.Language, w.Tags })
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        var summaries = page
            .Where(r => terms.ContainsKey(r.Row.Id))
            .Select(r =>
            {
                var t = terms[r.Row.Id];
                return new VocabularyWordSummary(
                    t.Id, t.TargetLanguageTerm, t.NativeLanguageTerm, t.Lemma, t.Language, t.Tags,
                    r.Row.MasteryScore, r.Row.LastPracticedAt, r.Row.IsUserDeclared, r.Row.HasProgress);
            })
            .ToList();

        return new VocabularyWordPage(classified.Count, eligible.Count, summaries);
    }

    /// <summary>
    /// The dashboard's status for one word (GetVocabSummaryCountsAsync), with "known" taken from
    /// <see cref="VocabularyProgress.IsKnown"/> itself so the rule is not restated here.
    /// </summary>
    private static VocabularySearchStatus StatusOf(
        bool hasProgress, float mastery, int productionInStreak, int totalAttempts,
        bool isUserDeclared, VerificationStatus verification, DateTime? nextReview, DateTime asOfUtc)
    {
        if (!hasProgress)
        {
            return VocabularySearchStatus.New;
        }

        if (isUserDeclared && verification == VerificationStatus.Pending)
        {
            return VocabularySearchStatus.Familiar;
        }

        var isKnown = new VocabularyProgress
        {
            MasteryScore = mastery,
            ProductionInStreak = productionInStreak,
            TotalAttempts = totalAttempts
        }.IsKnown;

        if (isKnown)
        {
            return VocabularySearchStatus.Known;
        }

        if (nextReview != null && nextReview <= asOfUtc)
        {
            return VocabularySearchStatus.Review;
        }

        return totalAttempts > 0 ? VocabularySearchStatus.Learning : VocabularySearchStatus.New;
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
            .Join(db.VocabularyWords.AsNoTracking().OwnedBy(userProfileId),
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

    public async Task<VocabularyExampleSentencePage?> GetWordExampleSentencesAsync(
        string userProfileId,
        string wordId,
        int maxResults,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (!HasWordOwner(userProfileId, nameof(GetWordExampleSentencesAsync))
            || string.IsNullOrWhiteSpace(wordId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var word = await db.VocabularyWords
            .AsNoTracking()
            .OwnedBy(userProfileId)
            .Where(w => w.Id == wordId)
            .Select(w => new { w.Id, w.TargetLanguageTerm })
            .FirstOrDefaultAsync(cancellationToken);
        if (word is null)
        {
            return null;
        }

        // The same "due" the coach's leak check uses (CoachValidationDataSource): this learner's
        // progress row says the review is due now.
        var isDue = await db.VocabularyProgresses
            .AsNoTracking()
            .AnyAsync(p => p.UserId == userProfileId
                           && p.VocabularyWordId == wordId
                           && p.NextReviewDate != null
                           && p.NextReviewDate <= asOfUtc,
                cancellationToken);

        var owned = db.ExampleSentences
            .AsNoTracking()
            .OwnedBy(userProfileId)
            .Where(e => e.VocabularyWordId == wordId);

        var total = await owned.CountAsync(cancellationToken);
        if (isDue || total == 0 || maxResults <= 0)
        {
            // Not selected, so a due word's sentences cannot be returned by mistake.
            return new VocabularyExampleSentencePage(word.Id, isDue ? null : word.TargetLanguageTerm, isDue, total, []);
        }

        var sentences = await owned
            .OrderByDescending(e => e.IsCore)
            .ThenBy(e => e.CreatedAt)
            .Take(maxResults)
            .Select(e => new VocabularyExampleSentenceFacts(e.Id, e.TargetSentence, e.NativeSentence, e.IsCore))
            .ToListAsync(cancellationToken);

        return new VocabularyExampleSentencePage(word.Id, word.TargetLanguageTerm, false, total, sentences);
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
