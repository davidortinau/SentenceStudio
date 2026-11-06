using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Repositories;

/// <summary>
/// Repository for managing MinimalPair entities.
/// Provides CRUD operations and ensures pairs are normalized (A < B).
/// Every call takes the owner explicitly: an empty owner reads nothing and
/// writes nothing, and another owner's pair is never read, changed or deleted.
/// </summary>
public class MinimalPairRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<MinimalPairRepository> _logger;

    public MinimalPairRepository(ApplicationDbContext context, ILogger<MinimalPairRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new minimal pair. Automatically normalizes word IDs so that A < B.
    /// </summary>
    /// <param name="userId">The owner</param>
    /// <param name="wordAId">First vocabulary word ID</param>
    /// <param name="wordBId">Second vocabulary word ID</param>
    /// <param name="contrastLabel">Optional label describing the contrast (e.g., "ㅂ vs ㅃ")</param>
    /// <returns>
    /// The created MinimalPair, or null if the owner is absent, the words are the same,
    /// either word is not the owner's vocabulary, or the pair already exists.
    /// </returns>
    public async Task<MinimalPair?> CreatePairAsync(string userId, string wordAId, string wordBId, string? contrastLabel = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("MinimalPairRepository.CreatePairAsync refused an absent owner.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(wordAId) || string.IsNullOrWhiteSpace(wordBId)
            || string.Equals(wordAId, wordBId, StringComparison.Ordinal))
            return null;

        // Normalize: ensure A < B to prevent duplicates
        var (normalizedA, normalizedB) = string.Compare(wordAId, wordBId, StringComparison.Ordinal) < 0 ? (wordAId, wordBId) : (wordBId, wordAId);

        // Both words must be the owner's vocabulary (the words the create page lists).
        var ownedCount = await OwnedWords(userId)
            .Where(word => word.Id == normalizedA || word.Id == normalizedB)
            .CountAsync();
        if (ownedCount != 2)
        {
            _logger.LogWarning("MinimalPairRepository.CreatePairAsync refused words that are not the owner's vocabulary.");
            return null;
        }

        // Check for existing pair
        var existingPair = await _context.MinimalPairs
            .FirstOrDefaultAsync(p =>
                p.UserId == userId &&
                p.VocabularyWordAId == normalizedA &&
                p.VocabularyWordBId == normalizedB);

        if (existingPair != null)
        {
            return null; // Duplicate pair
        }

        var pair = new MinimalPair
        {
            Id = SentenceStudio.Services.SyncIds.MinimalPair(userId, normalizedA, normalizedB),
            UserId = userId,
            VocabularyWordAId = normalizedA,
            VocabularyWordBId = normalizedB,
            ContrastLabel = contrastLabel,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.MinimalPairs.Add(pair);
        await _context.SaveChangesAsync();
        return pair;
    }

    /// <summary>
    /// Gets all minimal pairs for a user, including related vocabulary words.
    /// </summary>
    public async Task<List<MinimalPair>> GetUserPairsAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("MinimalPairRepository.GetUserPairsAsync called without an owner; returning no pairs.");
            return new List<MinimalPair>();
        }

        return await _context.MinimalPairs
            .Include(p => p.VocabularyWordA)
            .Include(p => p.VocabularyWordB)
            .Where(p => p.UserId == userId)
            .Where(BothWordsOwned(userId))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Gets the owner's minimal pair by ID, including related vocabulary words.
    /// Null for an absent owner or a pair owned by anyone else.
    /// </summary>
    public async Task<MinimalPair?> GetPairByIdAsync(string userId, string pairId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(pairId))
            return null;

        return await _context.MinimalPairs
            .Include(p => p.VocabularyWordA)
            .Include(p => p.VocabularyWordB)
            .Where(BothWordsOwned(userId))
            .FirstOrDefaultAsync(p => p.Id == pairId && p.UserId == userId);
    }

    /// <summary>
    /// Checks if a minimal pair exists for the given word IDs (order-independent).
    /// </summary>
    public async Task<bool> PairExistsAsync(string userId, string wordAId, string wordBId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var (normalizedA, normalizedB) = string.Compare(wordAId, wordBId, StringComparison.Ordinal) < 0 ? (wordAId, wordBId) : (wordBId, wordAId);

        return await _context.MinimalPairs
            .AnyAsync(p =>
                p.UserId == userId &&
                p.VocabularyWordAId == normalizedA &&
                p.VocabularyWordBId == normalizedB);
    }

    /// <summary>
    /// Deletes the owner's minimal pair by ID.
    /// Also deletes all related attempts via cascade delete.
    /// False for an absent owner or a pair owned by anyone else.
    /// </summary>
    public async Task<bool> DeletePairAsync(string userId, string pairId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("MinimalPairRepository.DeletePairAsync refused an absent owner.");
            return false;
        }

        var pair = await _context.MinimalPairs
            .FirstOrDefaultAsync(p => p.Id == pairId && p.UserId == userId);
        if (pair == null)
        {
            return false;
        }

        _context.MinimalPairs.Remove(pair);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Updates the contrast label for the owner's minimal pair.
    /// </summary>
    public async Task<bool> UpdateContrastLabelAsync(string userId, string pairId, string? contrastLabel)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("MinimalPairRepository.UpdateContrastLabelAsync refused an absent owner.");
            return false;
        }

        var pair = await _context.MinimalPairs
            .FirstOrDefaultAsync(p => p.Id == pairId && p.UserId == userId);
        if (pair == null)
        {
            return false;
        }

        pair.ContrastLabel = contrastLabel;
        pair.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Gets all pairs that include a specific vocabulary word.
    /// Useful for finding related pairs when browsing vocabulary.
    /// </summary>
    public async Task<List<MinimalPair>> GetPairsByWordIdAsync(string userId, string wordId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new List<MinimalPair>();

        return await _context.MinimalPairs
            .Include(p => p.VocabularyWordA)
            .Include(p => p.VocabularyWordB)
            .Where(p => p.UserId == userId &&
                       (p.VocabularyWordAId == wordId || p.VocabularyWordBId == wordId))
            .Where(BothWordsOwned(userId))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// The owner's vocabulary: words recorded to them (the one ownership rule), the same set
    /// LearningResourceRepository.GetAllVocabularyWordsAsync lists on the create page.
    /// </summary>
    private IQueryable<VocabularyWord> OwnedWords(string userId) =>
        _context.VocabularyWords.OwnedBy(userId);

    /// <summary>
    /// A pair is shown only with both of its words the owner's: a pair follows its words, so a
    /// word another learner owns, or no one owns, never appears through one.
    /// </summary>
    private System.Linq.Expressions.Expression<Func<MinimalPair, bool>> BothWordsOwned(string userId) =>
        pair => _context.VocabularyWords.Any(word => word.Id == pair.VocabularyWordAId && word.UserProfileId == userId)
            && _context.VocabularyWords.Any(word => word.Id == pair.VocabularyWordBId && word.UserProfileId == userId);
}
