using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// Pair words must be vocabulary linked to one of the owner's resources (the
/// words the create page lists). MinimalPair, MinimalPairSession and
/// MinimalPairAttempt are not in the CoreSync set (SharedSyncRegistration), so
/// these rows stay on this device and no sync is triggered.
/// </para>
/// </summary>
public sealed class NativeMinimalPairStore(IServiceScopeFactory scopes)
{
    /// <summary>The owner's vocabulary (linked to an owned resource), ordered by target term.</summary>
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
        return await db.MinimalPairs.AsNoTracking()
            .Include(pair => pair.VocabularyWordA)
            .Include(pair => pair.VocabularyWordB)
            .Where(pair => pair.UserId == owner)
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
        var rows = await db.MinimalPairs.AsNoTracking()
            .Include(pair => pair.VocabularyWordA)
            .Include(pair => pair.VocabularyWordB)
            .Where(pair => pair.UserId == owner && ids.Contains(pair.Id))
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
            return null;
        if (await db.MinimalPairs.AnyAsync(pair => pair.UserId == owner
                && pair.VocabularyWordAId == a && pair.VocabularyWordBId == b, cancellationToken))
            return null;
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
        catch (DbUpdateException)
        {
            // The unique (UserId, A, B) index caught a concurrent duplicate.
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
        var pair = await db.MinimalPairs.AsNoTracking().FirstOrDefaultAsync(
            row => row.Id == pairId && row.UserId == owner, cancellationToken);
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

    /// <summary>Same owner link as LearningResourceRepository.GetAllVocabularyWordsWithResourcesAsync.</summary>
    private static IQueryable<VocabularyWord> OwnedWords(ApplicationDbContext db, string owner)
    {
        var ids = db.ResourceVocabularyMappings
            .Join(db.LearningResources.Where(resource => resource.UserProfileId == owner),
                mapping => mapping.ResourceId, resource => resource.Id,
                (mapping, _) => mapping.VocabularyWordId);
        return db.VocabularyWords.Where(word => ids.Contains(word.Id));
    }
}
