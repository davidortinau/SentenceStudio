using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Owner-bound Minimal Pairs reads and writes: the source MinimalPairRepository
/// and MinimalPairSessionRepository rules (A/B normalized by ordinal order,
/// duplicate pair refused, attempt sequence numbers per session, UTC times),
/// but every call takes the owner explicitly, refuses an empty owner and never
/// touches another owner's rows. The source listed and recorded with a
/// hard-coded user "1" and created pairs with the active profile, so a real
/// learner's pairs never appeared; that is not copied.
/// <para>
/// Pair words must be the owner's own vocabulary: words recorded to them (the one
/// ownership rule, Captain 2026-11-06), the words the create page lists. A pair
/// whose word is another learner's, or no one's, is not listed, opened or
/// practised; the row stays on the device.
/// </para>
/// </summary>
public sealed class NativeMinimalPairStore(IServiceScopeFactory scopes, ILogger<NativeMinimalPairStore> logger)
{
    /// <summary>The owner's vocabulary (words recorded to them), ordered by target term.</summary>
    public async Task<List<VocabularyWord>> ListWordsAsync(string owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return [];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await OwnedWords(db, owner).AsNoTracking()
            .OrderBy(word => word.TargetLanguageTerm)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Source GetUserPairsAsync for the owner, oldest first, with both words.</summary>
    public async Task<List<MinimalPair>> ListPairsAsync(string owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return [];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await OwnedPairs(db, owner).AsNoTracking()
            .Include(pair => pair.VocabularyWordA)
            .Include(pair => pair.VocabularyWordB)
            .OrderBy(pair => pair.CreatedAt)
            .ThenBy(pair => pair.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Source GetPairByIdAsync per id, restricted to the owner's pairs; unknown ids are skipped.</summary>
    public async Task<List<MinimalPair>> GetPairsAsync(string owner, IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || ids.Count == 0)
            return [];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await OwnedPairs(db, owner).AsNoTracking()
            .Include(pair => pair.VocabularyWordA)
            .Include(pair => pair.VocabularyWordB)
            .Where(pair => ids.Contains(pair.Id))
            .ToListAsync(cancellationToken);
        // Keep the requested order and only pairs whose two words still exist.
        return [.. ids.Distinct()
            .Select(id => rows.FirstOrDefault(pair => pair.Id == id))
            .Where(pair => pair?.VocabularyWordA is not null && pair.VocabularyWordB is not null)
            .Select(pair => pair!)];
    }

    /// <summary>
    /// Source CreatePairAsync: normalizes A &lt; B and returns null for a duplicate.
    /// Also null when either word is not the owner's vocabulary or the two are the same.
    /// </summary>
    public async Task<MinimalPair?> CreatePairAsync(string owner, string wordAId, string wordBId,
        string? contrastLabel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (string.IsNullOrWhiteSpace(wordAId) || string.IsNullOrWhiteSpace(wordBId)
            || string.Equals(wordAId, wordBId, StringComparison.Ordinal))
            return null;
        var (a, b) = string.CompareOrdinal(wordAId, wordBId) < 0 ? (wordAId, wordBId) : (wordBId, wordAId);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owned = await OwnedWords(db, owner)
            .Where(word => word.Id == a || word.Id == b)
            .CountAsync(cancellationToken);
        if (owned != 2)
        {
            logger.LogWarning("Minimal Pairs: {Owned} of the pair's two words are the owner's.", owned);
            return null;
        }
        if (await db.MinimalPairs.AnyAsync(pair => pair.UserId == owner
                && pair.VocabularyWordAId == a && pair.VocabularyWordBId == b, cancellationToken))
        {
            logger.LogInformation("Minimal Pairs: the owner already has this pair.");
            return null;
        }
        var now = DateTime.UtcNow;
        var row = new MinimalPair
        {
            // One pair per learner and word pair on every device and the web.
            Id = SentenceStudio.Services.SyncIds.MinimalPair(owner, a, b),
            UserId = owner,
            VocabularyWordAId = a,
            VocabularyWordBId = b,
            ContrastLabel = string.IsNullOrWhiteSpace(contrastLabel) ? null : contrastLabel.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MinimalPairs.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Usually the unique (UserId, A, B) index catching a concurrent duplicate; logged
            // because any other refusal (a constraint, a trigger) reads the same to the learner.
            logger.LogWarning(ex, "Minimal Pairs: the database refused a new pair.");
            return null;
        }
        return row;
    }

    /// <summary>Source DeletePairAsync for the owner's pair; its attempts go with it (source cascade).</summary>
    public async Task<bool> DeletePairAsync(string owner, string pairId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pair = await db.MinimalPairs.FirstOrDefaultAsync(
            row => row.Id == pairId && row.UserId == owner, cancellationToken);
        if (pair is null)
            return false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.MinimalPairAttempts.RemoveRange(await db.MinimalPairAttempts
            .Where(attempt => attempt.PairId == pairId).ToListAsync(cancellationToken));
        db.MinimalPairs.Remove(pair);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Source StartSessionAsync for the owner.</summary>
    public async Task<MinimalPairSession> StartSessionAsync(string owner, string mode, int? plannedTrials,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var session = new MinimalPairSession
        {
            UserId = owner,
            Mode = mode,
            PlannedTrialCount = plannedTrials,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MinimalPairSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    /// <summary>
    /// Source RecordAttemptAsync: next sequence number in the session. Refused
    /// (null) unless the session and pair are the owner's and both words belong to the pair.
    /// </summary>
    public async Task<MinimalPairAttempt?> RecordAttemptAsync(string owner, string sessionId, string pairId,
        string promptWordId, string selectedWordId, bool isCorrect, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await db.MinimalPairSessions.AnyAsync(
                session => session.Id == sessionId && session.UserId == owner, cancellationToken))
            return null;
        var pair = await OwnedPairs(db, owner).AsNoTracking().FirstOrDefaultAsync(
            row => row.Id == pairId, cancellationToken);
        if (pair is null || !InPair(pair, promptWordId) || !InPair(pair, selectedWordId))
            return null;
        var sequence = await db.MinimalPairAttempts
            .Where(attempt => attempt.SessionId == sessionId)
            .MaxAsync(attempt => (int?)attempt.SequenceNumber, cancellationToken) ?? 0;
        var row = new MinimalPairAttempt
        {
            UserId = owner,
            SessionId = sessionId,
            PairId = pairId,
            PromptWordId = promptWordId,
            SelectedWordId = selectedWordId,
            IsCorrect = isCorrect,
            SequenceNumber = sequence + 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.MinimalPairAttempts.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    /// <summary>Source EndSessionAsync for the owner's session.</summary>
    public async Task<bool> EndSessionAsync(string owner, string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var session = await db.MinimalPairSessions.FirstOrDefaultAsync(
            row => row.Id == sessionId && row.UserId == owner, cancellationToken);
        if (session is null)
            return false;
        session.EndedAt = DateTime.UtcNow;
        session.UpdatedAt = session.EndedAt.Value;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool InPair(MinimalPair pair, string wordId) =>
        pair.VocabularyWordAId == wordId || pair.VocabularyWordBId == wordId;

    /// <summary>
    /// The owner's words: recorded to them, the rule
    /// LearningResourceRepository.GetAllVocabularyWordsWithResourcesAsync applies too.
    /// </summary>
    private static IQueryable<VocabularyWord> OwnedWords(ApplicationDbContext db, string owner) =>
        db.VocabularyWords.OwnedBy(owner);

    /// <summary>The owner's pairs whose two words are both the owner's own.</summary>
    internal static IQueryable<MinimalPair> OwnedPairs(ApplicationDbContext db, string owner)
    {
        var words = OwnedWords(db, owner).Select(word => word.Id);
        return db.MinimalPairs.Where(pair => pair.UserId == owner
            && words.Contains(pair.VocabularyWordAId)
            && words.Contains(pair.VocabularyWordBId));
    }
}
