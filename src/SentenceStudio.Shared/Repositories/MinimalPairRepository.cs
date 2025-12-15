using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Repositories;

/// <summary>
/// Repository for managing MinimalPair entities.
/// Provides CRUD operations and ensures pairs are normalized (A < B).
/// </summary>
public class MinimalPairRepository
{
    private readonly ApplicationDbContext _context;

    public MinimalPairRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Creates a new minimal pair. Automatically normalizes word IDs so that A < B.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="wordAId">First vocabulary word ID</param>
    /// <param name="wordBId">Second vocabulary word ID</param>
    /// <param name="contrastLabel">Optional label describing the contrast (e.g., "ㅂ vs ㅃ")</param>
    /// <returns>The created MinimalPair, or null if a duplicate already exists</returns>
    public async Task<MinimalPair?> CreatePairAsync(int userId, int wordAId, int wordBId, string? contrastLabel = null)
    {
        // Normalize: ensure A < B to prevent duplicates
        var (normalizedA, normalizedB) = wordAId < wordBId ? (wordAId, wordBId) : (wordBId, wordAId);

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
    public async Task<List<MinimalPair>> GetUserPairsAsync(int userId)
    {
        return await _context.MinimalPairs
            .Include(p => p.VocabularyWordA)
            .Include(p => p.VocabularyWordB)
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Gets a specific minimal pair by ID, including related vocabulary words.
    /// </summary>
    public async Task<MinimalPair?> GetPairByIdAsync(int pairId)
    {
        return await _context.MinimalPairs
            .Include(p => p.VocabularyWordA)
            .Include(p => p.VocabularyWordB)
            .FirstOrDefaultAsync(p => p.Id == pairId);
    }

    /// <summary>
    /// Checks if a minimal pair exists for the given word IDs (order-independent).
    /// </summary>
    public async Task<bool> PairExistsAsync(int userId, int wordAId, int wordBId)
    {
        var (normalizedA, normalizedB) = wordAId < wordBId ? (wordAId, wordBId) : (wordBId, wordAId);

        return await _context.MinimalPairs
            .AnyAsync(p =>
                p.UserId == userId &&
                p.VocabularyWordAId == normalizedA &&
                p.VocabularyWordBId == normalizedB);
    }

    /// <summary>
    /// Deletes a minimal pair by ID.
    /// Also deletes all related attempts via cascade delete.
    /// </summary>
    public async Task<bool> DeletePairAsync(int pairId)
    {
        var pair = await _context.MinimalPairs.FindAsync(pairId);
        if (pair == null)
        {
            return false;
        }

        _context.MinimalPairs.Remove(pair);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Updates the contrast label for a minimal pair.
    /// </summary>
    public async Task<bool> UpdateContrastLabelAsync(int pairId, string? contrastLabel)
    {
        var pair = await _context.MinimalPairs.FindAsync(pairId);
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
    public async Task<List<MinimalPair>> GetPairsByWordIdAsync(int userId, int wordId)
    {
        return await _context.MinimalPairs
            .Include(p => p.VocabularyWordA)
            .Include(p => p.VocabularyWordB)
            .Where(p => p.UserId == userId &&
                       (p.VocabularyWordAId == wordId || p.VocabularyWordBId == wordId))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
    }
}
