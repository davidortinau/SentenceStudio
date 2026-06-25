using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Repository for example sentence CRUD operations with performance optimizations
/// </summary>
public class ExampleSentenceRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ExampleSentenceRepository> _logger;

    public ExampleSentenceRepository(ApplicationDbContext context, ILogger<ExampleSentenceRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get all example sentences for a vocabulary word
    /// </summary>
    public Task<List<ExampleSentence>> GetByVocabularyWordIdAsync(string vocabularyWordId)
    {
        return _context.ExampleSentences
            .Where(es => es.VocabularyWordId == vocabularyWordId)
            .OrderBy(es => es.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get core example sentences for a vocabulary word (IsCore = true)
    /// Uses composite index (VocabularyWordId, IsCore) for performance
    /// </summary>
    public Task<List<ExampleSentence>> GetCoreExamplesAsync(string vocabularyWordId)
    {
        return _context.ExampleSentences
            .Where(es => es.VocabularyWordId == vocabularyWordId && es.IsCore)
            .OrderBy(es => es.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get example sentence counts for multiple vocabulary words (batch query to avoid N+1)
    /// </summary>
    public async Task<Dictionary<string, int>> GetCountsByVocabularyWordIdsAsync(List<string> vocabularyWordIds)
    {
        if (!vocabularyWordIds.Any())
            return new Dictionary<string, int>();

        return await _context.ExampleSentences
            .Where(es => vocabularyWordIds.Contains(es.VocabularyWordId))
            .GroupBy(es => es.VocabularyWordId)
            .Select(g => new { VocabularyWordId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VocabularyWordId, x => x.Count);
    }

    /// <summary>
    /// Get quiz/review-eligible example sentences for a vocabulary word
    /// (Curated or Verified, not flagged). Core first, then by creation order.
    /// </summary>
    public Task<List<ExampleSentence>> GetQuizEligibleAsync(string vocabularyWordId)
    {
        return _context.ExampleSentences
            .Where(es => es.VocabularyWordId == vocabularyWordId
                && !es.IsFlagged
                && (es.Status == ExampleSentenceStatus.Curated || es.Status == ExampleSentenceStatus.Verified))
            .OrderByDescending(es => es.IsCore)
            .ThenBy(es => es.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get the single best example to surface for a word on study/review cards:
    /// the core example if one exists, otherwise the earliest quiz-eligible sentence.
    /// Returns null when the word has no eligible example.
    /// </summary>
    public async Task<ExampleSentence?> GetCoreOrFirstEligibleAsync(string vocabularyWordId)
    {
        var eligible = await GetQuizEligibleAsync(vocabularyWordId);
        return eligible.FirstOrDefault();
    }

    /// <summary>
    /// Create a new example sentence
    /// </summary>
    public async Task<ExampleSentence> CreateAsync(ExampleSentence sentence)
    {
        sentence.CreatedAt = DateTime.UtcNow;
        sentence.UpdatedAt = DateTime.UtcNow;

        // Enforce a single core example per word.
        if (sentence.IsCore)
            await DemoteOtherCoresAsync(sentence.VocabularyWordId, exceptId: null);

        _context.ExampleSentences.Add(sentence);
        await _context.SaveChangesAsync();

        _logger.LogInformation("✅ Created example sentence {Id} for word {WordId}",
            sentence.Id, sentence.VocabularyWordId);

        return sentence;
    }

    /// <summary>
    /// Update an existing example sentence
    /// </summary>
    public async Task<ExampleSentence> UpdateAsync(ExampleSentence sentence)
    {
        sentence.UpdatedAt = DateTime.UtcNow;

        _context.ExampleSentences.Update(sentence);
        await _context.SaveChangesAsync();

        _logger.LogInformation("✅ Updated example sentence {Id}", sentence.Id);

        return sentence;
    }

    /// <summary>
    /// Delete an example sentence
    /// </summary>
    public async Task DeleteAsync(int id)
    {
        var sentence = await _context.ExampleSentences.FindAsync(id);
        if (sentence != null)
        {
            _context.ExampleSentences.Remove(sentence);
            await _context.SaveChangesAsync();

            _logger.LogInformation("🗑️ Deleted example sentence {Id}", id);
        }
    }

    /// <summary>
    /// Toggle the IsCore flag on an example sentence. Setting a sentence as core
    /// demotes any other core example for the same word (single-core invariant).
    /// </summary>
    public async Task<ExampleSentence> SetCoreAsync(int id, bool isCore)
    {
        var sentence = await _context.ExampleSentences.FindAsync(id);
        if (sentence == null)
            throw new InvalidOperationException($"Example sentence {id} not found");

        if (isCore)
            await DemoteOtherCoresAsync(sentence.VocabularyWordId, exceptId: id);

        sentence.IsCore = isCore;
        sentence.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("⭐ Set example sentence {Id} IsCore = {IsCore}", id, isCore);

        return sentence;
    }

    /// <summary>
    /// Clear IsCore on all other example sentences for a word so at most one stays core.
    /// </summary>
    private async Task DemoteOtherCoresAsync(string vocabularyWordId, int? exceptId)
    {
        var existingCores = await _context.ExampleSentences
            .Where(es => es.VocabularyWordId == vocabularyWordId && es.IsCore
                && (exceptId == null || es.Id != exceptId.Value))
            .ToListAsync();

        foreach (var core in existingCores)
        {
            core.IsCore = false;
            core.UpdatedAt = DateTime.UtcNow;
        }
    }
}
