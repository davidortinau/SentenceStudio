using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Repositories;

/// <summary>
/// Repository for managing MinimalPairSession and MinimalPairAttempt entities.
/// Provides operations for creating sessions, recording attempts, and retrieving practice history.
/// Every call takes the owner explicitly: an empty owner reads nothing and writes
/// nothing, and another owner's sessions, pairs and attempts are never touched.
/// </summary>
public class MinimalPairSessionRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<MinimalPairSessionRepository> _logger;

    public MinimalPairSessionRepository(ApplicationDbContext context, ILogger<MinimalPairSessionRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    #region Session Management

    /// <summary>
    /// Starts a new minimal pair practice session.
    /// </summary>
    /// <param name="userId">The owner</param>
    /// <param name="mode">Practice mode: "Focus" (one pair), "Mixed" (multiple pairs), or "Adaptive"</param>
    /// <param name="plannedTrialCount">Optional number of planned trials (null for open-ended)</param>
    /// <returns>The created session, or null when the owner is absent</returns>
    public async Task<MinimalPairSession?> StartSessionAsync(string userId, string mode, int? plannedTrialCount = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("MinimalPairSessionRepository.StartSessionAsync refused an absent owner.");
            return null;
        }

        var session = new MinimalPairSession
        {
            UserId = userId,
            Mode = mode,
            PlannedTrialCount = plannedTrialCount,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.MinimalPairSessions.Add(session);
        await _context.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// Ends the owner's active session by setting the EndedAt timestamp.
    /// </summary>
    public async Task<bool> EndSessionAsync(string userId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("MinimalPairSessionRepository.EndSessionAsync refused an absent owner.");
            return false;
        }

        var session = await _context.MinimalPairSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
        if (session == null)
        {
            return false;
        }

        session.EndedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Gets the owner's session by ID, including all attempts with related entities.
    /// </summary>
    public async Task<MinimalPairSession?> GetSessionByIdAsync(string userId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        return await _context.MinimalPairSessions
            .Include(s => s.Attempts)
                .ThenInclude(a => a.Pair)
                    .ThenInclude(p => p!.VocabularyWordA)
            .Include(s => s.Attempts)
                .ThenInclude(a => a.Pair)
                    .ThenInclude(p => p!.VocabularyWordB)
            .Include(s => s.Attempts)
                .ThenInclude(a => a.PromptWord)
            .Include(s => s.Attempts)
                .ThenInclude(a => a.SelectedWord)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
    }

    /// <summary>
    /// Gets all sessions for a user, ordered by most recent first.
    /// </summary>
    public async Task<List<MinimalPairSession>> GetUserSessionsAsync(string userId, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new List<MinimalPairSession>();

        return await _context.MinimalPairSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .ToListAsync();
    }

    #endregion

    #region Attempt Recording

    /// <summary>
    /// Records a single trial attempt within a session.
    /// </summary>
    /// <param name="userId">The owner</param>
    /// <param name="sessionId">The session ID</param>
    /// <param name="pairId">The minimal pair being practiced</param>
    /// <param name="promptWordId">The word that was played (prompt)</param>
    /// <param name="selectedWordId">The word the user selected</param>
    /// <param name="isCorrect">Whether the selection was correct</param>
    /// <returns>
    /// The recorded attempt, or null (nothing written) unless the owner is present, the
    /// session and pair are the owner's, and both words belong to the pair.
    /// </returns>
    public async Task<MinimalPairAttempt?> RecordAttemptAsync(
        string userId,
        string sessionId,
        string pairId,
        string promptWordId,
        string selectedWordId,
        bool isCorrect)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("MinimalPairSessionRepository.RecordAttemptAsync refused an absent owner.");
            return null;
        }

        if (!await _context.MinimalPairSessions.AnyAsync(s => s.Id == sessionId && s.UserId == userId))
        {
            _logger.LogWarning("MinimalPairSessionRepository.RecordAttemptAsync refused a session the owner does not own.");
            return null;
        }

        var pair = await _context.MinimalPairs.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pairId && p.UserId == userId);
        if (pair == null || !InPair(pair, promptWordId) || !InPair(pair, selectedWordId))
        {
            _logger.LogWarning("MinimalPairSessionRepository.RecordAttemptAsync refused a pair or word the owner does not own.");
            return null;
        }

        // Get the next sequence number for this session
        var maxSequence = await _context.MinimalPairAttempts
            .Where(a => a.SessionId == sessionId)
            .MaxAsync(a => (int?)a.SequenceNumber) ?? 0;

        var attempt = new MinimalPairAttempt
        {
            UserId = userId,
            SessionId = sessionId,
            PairId = pairId,
            PromptWordId = promptWordId,
            SelectedWordId = selectedWordId,
            IsCorrect = isCorrect,
            SequenceNumber = maxSequence + 1,
            CreatedAt = DateTime.UtcNow
        };

        _context.MinimalPairAttempts.Add(attempt);
        await _context.SaveChangesAsync();
        return attempt;
    }

    /// <summary>
    /// Gets all attempts for the owner's session, ordered by sequence number.
    /// </summary>
    public async Task<List<MinimalPairAttempt>> GetSessionAttemptsAsync(string userId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new List<MinimalPairAttempt>();

        return await _context.MinimalPairAttempts
            .Include(a => a.Pair)
                .ThenInclude(p => p!.VocabularyWordA)
            .Include(a => a.Pair)
                .ThenInclude(p => p!.VocabularyWordB)
            .Include(a => a.PromptWord)
            .Include(a => a.SelectedWord)
            .Where(a => a.SessionId == sessionId && a.UserId == userId)
            .OrderBy(a => a.SequenceNumber)
            .ToListAsync();
    }

    #endregion

    #region Analytics & History

    /// <summary>
    /// Gets practice history for a specific minimal pair.
    /// Returns attempts across all sessions, ordered by most recent.
    /// </summary>
    public async Task<List<MinimalPairAttempt>> GetPairHistoryAsync(string userId, string pairId, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new List<MinimalPairAttempt>();

        return await _context.MinimalPairAttempts
            .Include(a => a.PromptWord)
            .Include(a => a.SelectedWord)
            .Where(a => a.UserId == userId && a.PairId == pairId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Calculates accuracy statistics for a specific pair.
    /// </summary>
    public async Task<(int TotalAttempts, int Correct, double AccuracyPercent)> GetPairAccuracyAsync(string userId, string pairId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return (0, 0, 0);

        var attempts = await _context.MinimalPairAttempts
            .Where(a => a.UserId == userId && a.PairId == pairId)
            .ToListAsync();

        var total = attempts.Count;
        var correct = attempts.Count(a => a.IsCorrect);
        var accuracy = total > 0 ? (double)correct / total * 100 : 0;

        return (total, correct, accuracy);
    }

    /// <summary>
    /// Gets statistics (total attempts, correct, accuracy) for the owner's session.
    /// </summary>
    public async Task<(int TotalAttempts, int Correct, double AccuracyPercent)> GetSessionAccuracyAsync(string userId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return (0, 0, 0);

        var attempts = await _context.MinimalPairAttempts
            .Where(a => a.SessionId == sessionId && a.UserId == userId)
            .ToListAsync();

        var total = attempts.Count;
        var correct = attempts.Count(a => a.IsCorrect);
        var accuracy = total > 0 ? (double)correct / total * 100 : 0;

        return (total, correct, accuracy);
    }

    #endregion

    private static bool InPair(MinimalPair pair, string wordId) =>
        pair.VocabularyWordAId == wordId || pair.VocabularyWordBId == wordId;
}
