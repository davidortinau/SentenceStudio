using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Service for filtering and sorting vocabulary words with optimized queries
/// </summary>
public class VocabularyFilterService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<VocabularyFilterService> _logger;

    public VocabularyFilterService(ApplicationDbContext context, ILogger<VocabularyFilterService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Filter vocabulary words by tag using optimized query
    /// Target: <50ms for 5000 words with index on Tags column
    /// </summary>
    public async Task<List<VocabularyWord>> FilterByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return new List<VocabularyWord>();
        }

        try
        {
            var words = await _context.VocabularyWords
                .Where(w => w.Tags != null && EF.Functions.Like(w.Tags, $"%{tag}%"))
                .OrderBy(w => w.TargetLanguageTerm ?? string.Empty)
                .ToListAsync(cancellationToken);

            _logger.LogDebug("🏷️ Filtered {Count} words by tag: {Tag}", words.Count, tag);
            return words;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to filter vocabulary by tag: {Tag}", tag);
            throw;
        }
    }
}
