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
    public async Task<List<ExampleSentence>> GetByVocabularyWordIdAsync(int vocabularyWordId)
    {
        return await _context.ExampleSentences
            .Where(es => es.VocabularyWordId == vocabularyWordId)
            .OrderBy(es => es.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get core example sentences for a vocabulary word (IsCore = true)
    /// Uses composite index (VocabularyWordId, IsCore) for performance
    /// </summary>
    public async Task<List<ExampleSentence>> GetCoreExamplesAsync(int vocabularyWordId)
    {
        return await _context.ExampleSentences
            .Where(es => es.VocabularyWordId == vocabularyWordId && es.IsCore)
            .OrderBy(es => es.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get example sentence counts for multiple vocabulary words (batch query to avoid N+1)
    /// </summary>
    public async Task<Dictionary<int, int>> GetCountsByVocabularyWordIdsAsync(List<int> vocabularyWordIds)
    {
        if (!vocabularyWordIds.Any())
            return new Dictionary<int, int>();

        return await _context.ExampleSentences
            .Where(es => vocabularyWordIds.Contains(es.VocabularyWordId))
            .GroupBy(es => es.VocabularyWordId)
            .Select(g => new { VocabularyWordId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VocabularyWordId, x => x.Count);
    }

    /// <summary>
    /// Create a new example sentence
    /// </summary>
    public async Task<ExampleSentence> CreateAsync(ExampleSentence sentence)
    {
        sentence.CreatedAt = DateTime.UtcNow;
        sentence.UpdatedAt = DateTime.UtcNow;

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
    /// Toggle the IsCore flag on an example sentence
    /// </summary>
    public async Task<ExampleSentence> SetCoreAsync(int id, bool isCore)
    {
        var sentence = await _context.ExampleSentences.FindAsync(id);
        if (sentence == null)
            throw new InvalidOperationException($"Example sentence {id} not found");

        sentence.IsCore = isCore;
        sentence.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("⭐ Set example sentence {Id} IsCore = {IsCore}", id, isCore);

        return sentence;
    }
}
