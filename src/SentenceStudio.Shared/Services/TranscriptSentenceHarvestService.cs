using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public interface ITranscriptSentenceHarvestService
{
    Task<TranscriptSentenceHarvestSummary> HarvestForUserAsync(
        string userId,
        IProgress<TranscriptSentenceHarvestProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class TranscriptSentenceHarvestService : ITranscriptSentenceHarvestService
{
    private const int CapPerWordPerResource = 2;

    private readonly ApplicationDbContext _db;
    private readonly ExampleSentenceRepository _exampleSentenceRepository;
    private readonly IAiService _aiService;
    private readonly ILogger<TranscriptSentenceHarvestService> _logger;

    public TranscriptSentenceHarvestService(
        ApplicationDbContext db,
        ExampleSentenceRepository exampleSentenceRepository,
        IAiService aiService,
        ILogger<TranscriptSentenceHarvestService> logger)
    {
        _db = db;
        _exampleSentenceRepository = exampleSentenceRepository;
        _aiService = aiService;
        _logger = logger;
    }

    public async Task<TranscriptSentenceHarvestSummary> HarvestForUserAsync(
        string userId,
        IProgress<TranscriptSentenceHarvestProgress>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new TranscriptSentenceHarvestSummary();
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Transcript sentence harvest called with no active userId — returning empty summary");
            return summary;
        }

        var profile = await _db.UserProfiles.AsNoTracking()
            .FirstOrDefaultAsync(profile => profile.Id == userId, ct);
        var nativeLanguage = string.IsNullOrWhiteSpace(profile?.NativeLanguage)
            ? "English"
            : profile.NativeLanguage;
        var defaultTargetLanguage = string.IsNullOrWhiteSpace(profile?.TargetLanguage)
            ? "Korean"
            : profile.TargetLanguage;

        var resources = await _db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == userId && !string.IsNullOrEmpty(resource.Transcript))
            .Select(resource => new HarvestResource(
                resource.Id,
                resource.Title,
                resource.Language ?? defaultTargetLanguage,
                resource.Transcript!))
            .ToListAsync(ct);

        for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var resource = resources[resourceIndex];
            progress?.Report(new TranscriptSentenceHarvestProgress(
                resourceIndex + 1,
                resources.Count,
                resource.Id,
                resource.Title));

            var words = await (
                    from mapping in _db.ResourceVocabularyMappings.AsNoTracking()
                    join word in _db.VocabularyWords.AsNoTracking() on mapping.VocabularyWordId equals word.Id
                    where mapping.ResourceId == resource.Id
                    select word)
                .Distinct()
                .ToListAsync(ct);

            if (words.Count == 0)
            {
                summary.ResourcesScanned++;
                continue;
            }

            var sentences = TranscriptSentenceSegmenter.Split(resource.Transcript, splitOnNewlines: true);
            if (sentences.Count == 0)
            {
                summary.WordsExamined += words.Count;
                summary.SkippedNoMatch += words.Count;
                summary.ResourcesScanned++;
                continue;
            }

            var wordIds = words.Select(word => word.Id).ToList();
            var existingExamples = await _db.ExampleSentences
                .Where(example => wordIds.Contains(example.VocabularyWordId))
                .ToListAsync(ct);
            var existingExamplesByWordId = existingExamples
                .GroupBy(example => example.VocabularyWordId)
                .ToDictionary(group => group.Key, group => (IReadOnlyCollection<ExampleSentence>)group.ToList());

            var candidates = BuildCandidates(resource.Id, words, sentences, existingExamplesByWordId, summary);
            var translations = await TranslateCandidatesAsync(candidates, resource.Language, nativeLanguage, summary, ct);

            foreach (var candidate in candidates)
            {
                existingExamplesByWordId.TryGetValue(candidate.Word.Id, out var existingForWord);
                translations.TryGetValue(candidate.Sentence, out var translation);

                var created = await _exampleSentenceRepository.CreateFromReadingIfNewAsync(
                    candidate.Word,
                    resource.Id,
                    candidate.Sentence,
                    translation,
                    existingForWord ?? Array.Empty<ExampleSentence>(),
                    status: ExampleSentenceStatus.Curated,
                    capPerWordPerResource: CapPerWordPerResource,
                    ct: ct);

                if (created == null)
                    summary.SkippedDuplicate++;
                else
                    summary.SentencesAdded++;
            }

            summary.ResourcesScanned++;
        }

        return summary;
    }

    private static List<HarvestCandidate> BuildCandidates(
        string resourceId,
        List<VocabularyWord> words,
        List<string> sentences,
        Dictionary<string, IReadOnlyCollection<ExampleSentence>> existingExamplesByWordId,
        TranscriptSentenceHarvestSummary summary)
    {
        var candidates = new List<HarvestCandidate>();

        foreach (var word in words)
        {
            summary.WordsExamined++;
            var matches = FindMatchingSentences(word, sentences).Take(CapPerWordPerResource).ToList();
            if (matches.Count == 0)
            {
                summary.SkippedNoMatch++;
                continue;
            }

            existingExamplesByWordId.TryGetValue(word.Id, out var existingForWord);
            var existing = existingForWord ?? Array.Empty<ExampleSentence>();
            var existingNormalized = existing
                .Select(example => NormalizeForDedup(example.TargetSentence))
                .Where(normalized => !string.IsNullOrWhiteSpace(normalized))
                .ToHashSet(StringComparer.Ordinal);
            var existingFromReadingForResource = existing.Count(example =>
                example.Source == ExampleSentenceSource.FromReading &&
                example.LearningResourceId == resourceId);
            var plannedForWord = new HashSet<string>(StringComparer.Ordinal);

            foreach (var sentence in matches)
            {
                var normalized = NormalizeForDedup(sentence);
                if (string.IsNullOrWhiteSpace(normalized) || existingNormalized.Contains(normalized) || !plannedForWord.Add(normalized))
                {
                    summary.SkippedDuplicate++;
                    continue;
                }

                if (existingFromReadingForResource + plannedForWord.Count > CapPerWordPerResource)
                {
                    summary.SkippedDuplicate++;
                    continue;
                }

                candidates.Add(new HarvestCandidate(word, sentence));
            }
        }

        return candidates;
    }

    private async Task<Dictionary<string, string?>> TranslateCandidatesAsync(
        List<HarvestCandidate> candidates,
        string? targetLanguage,
        string nativeLanguage,
        TranscriptSentenceHarvestSummary summary,
        CancellationToken ct)
    {
        var sentences = candidates
            .Select(candidate => candidate.Sentence)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sentences.Count == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        try
        {
            var prompt = BuildSentenceTranslationPrompt(sentences, targetLanguage, nativeLanguage);
            var response = await _aiService.SendPrompt<BulkTranslationResponse>(prompt);
            if (response?.Translations == null || response.Translations.Count == 0)
            {
                summary.TranslationFailures += sentences.Count;
                return new Dictionary<string, string?>(StringComparer.Ordinal);
            }

            var translations = response.Translations
                .Where(pair => !string.IsNullOrWhiteSpace(pair.TargetLanguageTerm))
                .GroupBy(pair => pair.TargetLanguageTerm, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => NormalizeUnknownTranslation(group.First().NativeLanguageTerm),
                    StringComparer.Ordinal);

            summary.TranslationFailures += sentences.Count(sentence =>
                !translations.TryGetValue(sentence, out var translation) || string.IsNullOrWhiteSpace(translation));
            return translations;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcript sentence harvest translation failed for {Count} candidate sentences", sentences.Count);
            summary.TranslationFailures += sentences.Count;
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }

    private static string BuildSentenceTranslationPrompt(
        IReadOnlyList<string> sentences,
        string? targetLanguage,
        string nativeLanguage)
    {
        var sourceLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "target language" : targetLanguage;
        var prompt = new StringBuilder();
        prompt.AppendLine($"Translate these {sourceLanguage} sentences into {nativeLanguage} for language learners.");
        prompt.AppendLine("Return ONLY valid JSON in this shape:");
        prompt.AppendLine("{\"translations\":[{\"targetLanguageTerm\":\"exact input sentence\",\"nativeLanguageTerm\":\"natural sentence translation\"}]}");
        prompt.AppendLine("Preserve targetLanguageTerm exactly as provided. Use [unknown] only if translation is impossible.");
        prompt.AppendLine();
        prompt.AppendLine("Sentences:");
        foreach (var sentence in sentences)
            prompt.AppendLine($"- {sentence}");

        return prompt.ToString();
    }

    private static IEnumerable<string> FindMatchingSentences(VocabularyWord word, IEnumerable<string> sentences)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (SentenceContainsWordTerm(word, trimmed) && seen.Add(NormalizeForDedup(trimmed)))
                yield return trimmed;
        }
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
            value = value[1..^1].Trim();

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

    private static string? NormalizeUnknownTranslation(string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation) || translation.Trim() == "[unknown]")
            return null;

        return translation.Trim();
    }

    private sealed record HarvestResource(string Id, string? Title, string? Language, string Transcript);
    private sealed record HarvestCandidate(VocabularyWord Word, string Sentence);
}

public sealed class TranscriptSentenceHarvestSummary
{
    public int ResourcesScanned { get; set; }
    public int WordsExamined { get; set; }
    public int SentencesAdded { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedNoMatch { get; set; }
    public int TranslationFailures { get; set; }
}

public sealed record TranscriptSentenceHarvestProgress(
    int ResourceIndex,
    int TotalResources,
    string ResourceId,
    string? ResourceTitle);
