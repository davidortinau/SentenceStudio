using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Repositories;

/// <summary>
/// Repository for managing MinimalPairSession and MinimalPairAttempt entities.
/// Provides operations for creating sessions, recording attempts, and retrieving practice history.
/// </summary>
public class MinimalPairSessionRepository
{
    private readonly ApplicationDbContext _context;

    public MinimalPairSessionRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    #region Session Management

    /// <summary>
    /// Starts a new minimal pair practice session.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="mode">Practice mode: "Focus" (one pair), "Mixed" (multiple pairs), or "Adaptive"</param>
    /// <param name="plannedTrialCount">Optional number of planned trials (null for open-ended)</param>
    /// <returns>The created session</returns>
    public async Task<MinimalPairSession> StartSessionAsync(int userId, string mode, int? plannedTrialCount = null)
    {
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
    /// Ends an active session by setting the EndedAt timestamp.
    /// </summary>
    public async Task<bool> EndSessionAsync(int sessionId)
    {
        var session = await _context.MinimalPairSessions.FindAsync(sessionId);
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
    /// Gets a session by ID, including all attempts with related entities.
    /// </summary>
    public async Task<MinimalPairSession?> GetSessionByIdAsync(int sessionId)
    {
        return await _context.MinimalPairSessions
            .Include(s => s.Attempts)
                .ThenInclude(a => a.Pair)
                    .ThenInclude(p => p.VocabularyWordA)
            .Include(s => s.Attempts)
                .ThenInclude(a => a.Pair)
                    .ThenInclude(p => p.VocabularyWordB)
            .Include(s => s.Attempts)
                .ThenInclude(a => a.PromptWord)
            .Include(s => s.Attempts)
                .ThenInclude(a => a.SelectedWord)
            .FirstOrDefaultAsync(s => s.Id == sessionId);
    }

    /// <summary>
    /// Gets all sessions for a user, ordered by most recent first.
    /// </summary>
    public async Task<List<MinimalPairSession>> GetUserSessionsAsync(int userId, int limit = 20)
    {
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
    /// <param name="userId">The user ID</param>
    /// <param name="sessionId">The session ID</param>
    /// <param name="pairId">The minimal pair being practiced</param>
    /// <param name="promptWordId">The word that was played (prompt)</param>
    /// <param name="selectedWordId">The word the user selected</param>
    /// <param name="isCorrect">Whether the selection was correct</param>
    /// <returns>The recorded attempt</returns>
    public async Task<MinimalPairAttempt> RecordAttemptAsync(
        int userId,
        int sessionId,
        int pairId,
        int promptWordId,
        int selectedWordId,
        bool isCorrect)
    {
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
    /// Gets all attempts for a specific session, ordered by sequence number.
    /// </summary>
    public async Task<List<MinimalPairAttempt>> GetSessionAttemptsAsync(int sessionId)
    {
        return await _context.MinimalPairAttempts
            .Include(a => a.Pair)
                .ThenInclude(p => p.VocabularyWordA)
            .Include(a => a.Pair)
                .ThenInclude(p => p.VocabularyWordB)
            .Include(a => a.PromptWord)
            .Include(a => a.SelectedWord)
            .Where(a => a.SessionId == sessionId)
            .OrderBy(a => a.SequenceNumber)
            .ToListAsync();
    }

    #endregion

    #region Analytics & History

    /// <summary>
    /// Gets practice history for a specific minimal pair.
    /// Returns attempts across all sessions, ordered by most recent.
    /// </summary>
    public async Task<List<MinimalPairAttempt>> GetPairHistoryAsync(int userId, int pairId, int limit = 50)
    {
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
    public async Task<(int TotalAttempts, int Correct, double AccuracyPercent)> GetPairAccuracyAsync(int userId, int pairId)
    {
        var attempts = await _context.MinimalPairAttempts
            .Where(a => a.UserId == userId && a.PairId == pairId)
            .ToListAsync();

        var total = attempts.Count;
        var correct = attempts.Count(a => a.IsCorrect);
        var accuracy = total > 0 ? (double)correct / total * 100 : 0;

        return (total, correct, accuracy);
    }

    /// <summary>
    /// Gets session statistics (total attempts, correct, accuracy).
    /// </summary>
    public async Task<(int TotalAttempts, int Correct, double AccuracyPercent)> GetSessionAccuracyAsync(int sessionId)
    {
        var attempts = await _context.MinimalPairAttempts
            .Where(a => a.SessionId == sessionId)
            .ToListAsync();

        var total = attempts.Count;
        var correct = attempts.Count(a => a.IsCorrect);
        var accuracy = total > 0 ? (double)correct / total * 100 : 0;

        return (total, correct, accuracy);
    }

    #endregion
}
