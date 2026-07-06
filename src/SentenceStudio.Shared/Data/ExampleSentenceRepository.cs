using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using System.Text.RegularExpressions;

namespace SentenceStudio.Data;

/// <summary>
/// Repository for example sentence CRUD operations with performance optimizations
/// </summary>
public class ExampleSentenceRepository
{
    // Matches ExampleSentence.TargetSentence / NativeSentence [MaxLength(500)] — enforced by the
    // Postgres provider. Guard harvest inserts against it so a run-on segment can't abort a batch.
    private const int MaxSentenceLength = 500;

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

    public async Task<ExampleSentence?> CreateFromReadingIfNewAsync(
        string vocabularyWordId,
        string? learningResourceId,
        string targetSentence,
        string? nativeSentence,
        ExampleSentenceStatus status = ExampleSentenceStatus.Curated,
        SpeechRegister register = SpeechRegister.Unspecified,
        int capPerWordPerResource = 2,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vocabularyWordId) || string.IsNullOrWhiteSpace(targetSentence))
            return null;

        var word = await _context.VocabularyWords
            .FirstOrDefaultAsync(w => w.Id == vocabularyWordId, ct);

        if (word == null)
            return null;

        var existingForWord = await _context.ExampleSentences
            .Where(es => es.VocabularyWordId == vocabularyWordId)
            .ToListAsync(ct);

        return await CreateFromReadingIfNewAsync(
            word,
            learningResourceId,
            targetSentence,
            nativeSentence,
            existingForWord,
            status,
            register,
            capPerWordPerResource,
            ct);
    }

    public async Task<ExampleSentence?> CreateFromReadingIfNewAsync(
        VocabularyWord word,
        string? learningResourceId,
        string targetSentence,
        string? nativeSentence,
        IReadOnlyCollection<ExampleSentence> existingForWord,
        ExampleSentenceStatus status = ExampleSentenceStatus.Curated,
        SpeechRegister register = SpeechRegister.Unspecified,
        int capPerWordPerResource = 2,
        CancellationToken ct = default)
    {
        if (word == null || string.IsNullOrWhiteSpace(word.Id) || string.IsNullOrWhiteSpace(targetSentence))
            return null;

        var trimmedTargetSentence = targetSentence.Trim();
        if (!SentenceContainsWordTerm(word, trimmedTargetSentence))
            return null;

        // ExampleSentence.TargetSentence is [MaxLength(500)] — enforced as varchar(500) on the
        // Postgres (WebApp/API) provider. An overlong segment (e.g. a long newline-delimited
        // transcript line with no sentence-final punctuation) would throw on insert, aborting a
        // whole harvest run. Such a run-on is a poor example anyway, so skip it rather than truncate.
        if (trimmedTargetSentence.Length > MaxSentenceLength)
            return null;

        var normalizedTargetSentence = NormalizeForDedup(trimmedTargetSentence);
        if (string.IsNullOrWhiteSpace(normalizedTargetSentence))
            return null;

        var existingSentences = existingForWord ?? Array.Empty<ExampleSentence>();
        var trackedSentences = _context.ExampleSentences.Local
            .Where(es => es.VocabularyWordId == word.Id && !existingSentences.Any(existing =>
                ReferenceEquals(existing, es) || (existing.Id != 0 && existing.Id == es.Id)))
            .ToList();
        var sentencesForChecks = existingSentences
            .Concat(trackedSentences)
            .Where(es => es.VocabularyWordId == word.Id)
            .ToList();

        if (sentencesForChecks
            .Any(es => NormalizeForDedup(es.TargetSentence) == normalizedTargetSentence))
        {
            return null;
        }

        if (learningResourceId != null)
        {
            var existingFromReadingCount = sentencesForChecks.Count(es =>
                es.Source == ExampleSentenceSource.FromReading &&
                es.LearningResourceId == learningResourceId);

            if (existingFromReadingCount >= capPerWordPerResource)
                return null;
        }

        var now = DateTime.UtcNow;
        var sentence = new ExampleSentence
        {
            VocabularyWordId = word.Id,
            LearningResourceId = learningResourceId,
            TargetSentence = trimmedTargetSentence,
            NativeSentence = ClampNativeSentence(nativeSentence),
            IsCore = false,
            Source = ExampleSentenceSource.FromReading,
            Status = status,
            Register = register,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.ExampleSentences.Add(sentence);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created FromReading example sentence {Id} for word {WordId}",
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

    private static string? ClampNativeSentence(string? nativeSentence)
    {
        if (string.IsNullOrWhiteSpace(nativeSentence))
            return null;

        var trimmed = nativeSentence.Trim();
        // Drop an over-long translation rather than throw on the varchar(500) column; the target
        // sentence is still worth keeping even without its native gloss.
        return trimmed.Length > MaxSentenceLength ? null : trimmed;
    }

    private static string NormalizeForDedup(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return string.Empty;

        var normalized = Regex.Replace(sentence.Trim(), @"\s+", " ");
        normalized = StripWrappingQuotes(normalized);

        return normalized.ToUpperInvariant();
    }

    private static string StripWrappingQuotes(string value)
    {
        while (value.Length >= 2 && IsWrappingQuotePair(value[0], value[^1]))
        {
            value = value[1..^1].Trim();
        }

        return value;
    }

    private static bool IsWrappingQuotePair(char first, char last)
    {
        return (first == '"' && last == '"') ||
            (first == '\'' && last == '\'') ||
            (first == '“' && last == '”') ||
            (first == '‘' && last == '’') ||
            (first == '「' && last == '」') ||
            (first == '『' && last == '』');
    }

    private static bool SentenceContainsWordTerm(VocabularyWord word, string sentence)
    {
        return ContainsTerm(sentence, word.TargetLanguageTerm) ||
            ContainsTerm(sentence, word.Lemma) ||
            ContainsKoreanDictionaryStem(sentence, word.Lemma);
    }

    private static bool ContainsTerm(string sentence, string? term)
    {
        return !string.IsNullOrWhiteSpace(term) &&
            sentence.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsKoreanDictionaryStem(string sentence, string? lemma)
    {
        if (string.IsNullOrWhiteSpace(lemma))
            return false;

        var trimmed = lemma.Trim();
        if (!trimmed.EndsWith('다') || trimmed.Length <= 1)
            return false;

        return sentence.Contains(trimmed[..^1], StringComparison.OrdinalIgnoreCase);
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
