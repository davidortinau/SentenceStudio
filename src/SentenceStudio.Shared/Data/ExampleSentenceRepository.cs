using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using System.Text.RegularExpressions;

namespace SentenceStudio.Data;

/// <summary>
/// Repository for example sentence CRUD operations with performance optimizations.
/// Each sentence belongs to a learner (phase 3 of docs/sync-all-tables-plan.md): the learner
/// who created it, else its resource's or word's owner. Reads, updates and deletes for pages
/// are scoped to the active learner (<see cref="ActiveOwner"/>); sentences saved before they had
/// an owner, with no proven owner, stay visible only on the device that holds them (D3).
/// </summary>
public class ExampleSentenceRepository
{
    // Matches ExampleSentence.TargetSentence / NativeSentence [MaxLength(500)] — enforced by the
    // Postgres provider. Guard harvest inserts against it so a run-on segment can't abort a batch.
    private const int MaxSentenceLength = 500;
    private const int MaxQuizHintWordIds = 20;
    private const int MaxQuizHintsPerWord = 3;

    private readonly ApplicationDbContext _context;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExampleSentenceRepository> _logger;

    public ExampleSentenceRepository(
        ApplicationDbContext context,
        IServiceProvider serviceProvider,
        ILogger<ExampleSentenceRepository> logger)
    {
        _context = context;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// The active learner's sentences: recorded to them (the one ownership rule, on every store;
    /// a device's own legacy ownerless sentences take their word's owner on sync,
    /// <see cref="VocabularyWordOwnership.ClaimOwnSentencesOnDeviceAsync"/>). None when there is
    /// no active learner.
    /// </summary>
    private IQueryable<ExampleSentence> Visible(string operation)
    {
        if (!ActiveOwner.TryResolve(_serviceProvider, _logger, operation, out var owner))
            return _context.ExampleSentences.Where(_ => false);
        return _context.ExampleSentences.OwnedBy(owner);
    }

    /// <summary>
    /// The owner of a new sentence: the active learner who creates it; with none (an import
    /// running in the background), the owner of its resource, else of its word. Null when none
    /// is proven, and then nothing is written.
    /// </summary>
    private async Task<string?> OwnerForNewAsync(
        string vocabularyWordId, string? learningResourceId, CancellationToken ct)
    {
        if (ActiveOwner.TryResolve(_serviceProvider, NullLoggerForBackground, "ExampleSentence create", out var active))
            return active;
        // An import harvests many sentences per resource and word: look each owner up once.
        if (!string.IsNullOrWhiteSpace(learningResourceId))
        {
            if (!_resourceOwners.TryGetValue(learningResourceId, out var resourceOwner))
                _resourceOwners[learningResourceId] = resourceOwner = await _context.LearningResources
                    .AsNoTracking()
                    .Where(resource => resource.Id == learningResourceId)
                    .Select(resource => resource.UserProfileId)
                    .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(resourceOwner))
                return resourceOwner;
        }
        if (!_wordOwners.TryGetValue(vocabularyWordId, out var wordOwner))
            _wordOwners[vocabularyWordId] = wordOwner = await _context.VocabularyWords.AsNoTracking()
                .Where(word => word.Id == vocabularyWordId)
                .Select(word => word.UserProfileId)
                .FirstOrDefaultAsync(ct);
        return string.IsNullOrEmpty(wordOwner) ? null : wordOwner;
    }

    private readonly Dictionary<string, string?> _resourceOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _wordOwners = new(StringComparer.Ordinal);

    // A background import has no active learner by design; that is not worth a warning.
    private static readonly ILogger NullLoggerForBackground =
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Get all example sentences for a vocabulary word
    /// </summary>
    public Task<List<ExampleSentence>> GetByVocabularyWordIdAsync(string vocabularyWordId)
    {
        return Visible(nameof(GetByVocabularyWordIdAsync))
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
        return Visible(nameof(GetCoreExamplesAsync))
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

        return await Visible(nameof(GetCountsByVocabularyWordIdsAsync))
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
        return Visible(nameof(GetQuizEligibleAsync))
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
    /// Returns target-language-only sentence hints for a bounded vocabulary batch.
    /// Ownership is proven by an exact word/resource mapping to a learning resource
    /// owned by the explicit user. Unlinked or ambiguously owned examples are omitted.
    /// </summary>
    public async Task<IReadOnlyList<VocabQuizSentenceHint>> GetQuizHintsForWordsAsync(
        string userId,
        IEnumerable<string>? vocabularyWordIds,
        string? targetCefrLevel,
        int maxHintsPerWord = MaxQuizHintsPerWord,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning(
                "GetQuizHintsForWordsAsync called with no userId — returning empty result to prevent cross-tenant data leak.");
            return Array.Empty<VocabQuizSentenceHint>();
        }

        var requestedWordIds = vocabularyWordIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? new List<string>();

        if (requestedWordIds.Count == 0)
        {
            _logger.LogWarning(
                "GetQuizHintsForWordsAsync called with no vocabulary word IDs — returning empty result.");
            return Array.Empty<VocabQuizSentenceHint>();
        }

        if (requestedWordIds.Count > MaxQuizHintWordIds)
        {
            _logger.LogWarning(
                "GetQuizHintsForWordsAsync called with {WordCount} distinct vocabulary word IDs; maximum is {Maximum}. Returning empty result.",
                requestedWordIds.Count,
                MaxQuizHintWordIds);
            return Array.Empty<VocabQuizSentenceHint>();
        }

        if (maxHintsPerWord is < 1 or > MaxQuizHintsPerWord)
        {
            _logger.LogWarning(
                "GetQuizHintsForWordsAsync called with maxHintsPerWord {Maximum}; supported range is 1-{SupportedMaximum}. Returning empty result.",
                maxHintsPerWord,
                MaxQuizHintsPerWord);
            return Array.Empty<VocabQuizSentenceHint>();
        }

        var normalizedUserId = userId.Trim();
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ownedCandidates =
            from sentence in db.ExampleSentences.AsNoTracking()
            join mapping in db.ResourceVocabularyMappings.AsNoTracking()
                on new
                {
                    sentence.VocabularyWordId,
                    ResourceId = sentence.LearningResourceId
                }
                equals new
                {
                    mapping.VocabularyWordId,
                    ResourceId = (string?)mapping.ResourceId
                }
            join resource in db.LearningResources.AsNoTracking()
                on mapping.ResourceId equals resource.Id
            where requestedWordIds.Contains(sentence.VocabularyWordId)
                // The learner's own sentence on their own word (the one ownership rule).
                && sentence.UserProfileId == normalizedUserId
                && db.VocabularyWords.Any(word => word.Id == sentence.VocabularyWordId
                    && word.UserProfileId == normalizedUserId)
                && sentence.TargetSentence.Trim() != string.Empty
                && !sentence.IsFlagged
                && (sentence.Status == ExampleSentenceStatus.Curated
                    || sentence.Status == ExampleSentenceStatus.Verified)
            select new
            {
                resource.UserProfileId,
                ExampleSentenceId = sentence.Id,
                sentence.VocabularyWordId,
                sentence.TargetSentence,
                sentence.DifficultyLevel,
                sentence.IsCore,
                sentence.Status,
                sentence.CreatedAt
            };

        var rows = await (
            from user in db.UserProfiles.AsNoTracking()
            where user.Id == normalizedUserId
            join candidate in ownedCandidates
                on user.Id equals candidate.UserProfileId into candidateGroup
            from candidate in candidateGroup.DefaultIfEmpty()
            select new
            {
                ExistingUserId = user.Id,
                ExampleSentenceId = (string?)candidate.ExampleSentenceId,
                candidate.VocabularyWordId,
                candidate.TargetSentence,
                DifficultyLevel = (int?)candidate.DifficultyLevel,
                IsCore = (bool?)candidate.IsCore,
                Status = (ExampleSentenceStatus?)candidate.Status,
                CreatedAt = (DateTime?)candidate.CreatedAt
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            _logger.LogWarning(
                "GetQuizHintsForWordsAsync called with an unknown userId — returning empty result to prevent cross-tenant data leak.");
            return Array.Empty<VocabQuizSentenceHint>();
        }

        var candidates = rows
            .Where(row => row.ExampleSentenceId != null)
            .Select(row => new QuizHintCandidate(
                row.ExampleSentenceId!,
                row.VocabularyWordId!,
                row.TargetSentence!,
                row.DifficultyLevel,
                row.IsCore!.Value,
                row.Status!.Value,
                row.CreatedAt!.Value));
        var targetDifficulty = MapCefrToDifficulty(targetCefrLevel);
        var wordOrder = requestedWordIds
            .Select((id, index) => new { id, index })
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);

        return candidates
            .DistinctBy(candidate => candidate.ExampleSentenceId)
            .GroupBy(candidate => candidate.VocabularyWordId, StringComparer.Ordinal)
            .OrderBy(group => wordOrder[group.Key])
            .SelectMany(group => RankQuizHintCandidates(group, targetDifficulty)
                .Take(maxHintsPerWord)
                .Select(candidate => new VocabQuizSentenceHint(
                    candidate.ExampleSentenceId,
                    candidate.VocabularyWordId,
                    candidate.TargetSentence.Trim())))
            .ToList();
    }

    /// <summary>
    /// Create a new example sentence
    /// </summary>
    public async Task<ExampleSentence> CreateAsync(ExampleSentence sentence)
    {
        sentence.UserProfileId = await OwnerForNewAsync(
            sentence.VocabularyWordId, sentence.LearningResourceId, default)
            ?? throw new InvalidOperationException("An example sentence needs a proven owner.");
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
        if (ReadingSentence(word, targetSentence, requireTermInSentence: true) is not { } prepared)
            return null;
        var (trimmedTargetSentence, normalizedTargetSentence) = prepared;

        var existingSentences = existingForWord ?? Array.Empty<ExampleSentence>();
        var trackedSentences = _context.ExampleSentences.Local
            .Where(es => es.VocabularyWordId == word.Id && !existingSentences.Any(existing =>
                ReferenceEquals(existing, es) || existing.Id == es.Id))
            .ToList();
        var sentencesForChecks = existingSentences
            .Concat(trackedSentences)
            .Where(es => es.VocabularyWordId == word.Id)
            .ToList();

        if (HasOrIsCapped(sentencesForChecks, normalizedTargetSentence, learningResourceId, capPerWordPerResource))
            return null;

        if (await OwnerForNewAsync(word.Id, learningResourceId, ct) is not { } owner)
        {
            _logger.LogWarning("CreateFromReadingIfNewAsync skipped a sentence with no proven owner.");
            return null;
        }

        var sentence = NewReadingSentence(owner, word.Id, learningResourceId, trimmedTargetSentence,
            nativeSentence, status, register);

        _context.ExampleSentences.Add(sentence);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created FromReading example sentence {Id} for word {WordId}",
            sentence.Id, sentence.VocabularyWordId);

        return sentence;
    }

    /// <summary>One sentence an import keeps as a word's example (see the batch <c>CreateFromReadingIfNewAsync</c>).</summary>
    /// <param name="RequireTermInSentence">
    /// False only for an import row whose term the learner edited in the preview: its sentence was
    /// checked against the source text instead (<see cref="SentenceStudio.Services.ImportExampleSentencePolicy"/>).
    /// </param>
    public sealed record ReadingExample(
        VocabularyWord Word, string TargetSentence, string? NativeSentence, bool RequireTermInSentence = true);

    /// <summary>
    /// The batch form for an import (inside its transaction): each sentence gets the same checks as
    /// the single form, against the word's sentences in <paramref name="existingByWordId"/> (read
    /// from the database after the import's own saves) and the ones added earlier in this batch;
    /// then all are added and saved once. Returns how many were added.
    /// </summary>
    public async Task<int> CreateFromReadingIfNewAsync(
        IReadOnlyList<ReadingExample> examples,
        string? learningResourceId,
        IReadOnlyDictionary<string, IReadOnlyCollection<ExampleSentence>> existingByWordId,
        ExampleSentenceStatus status = ExampleSentenceStatus.Curated,
        SpeechRegister register = SpeechRegister.Unspecified,
        int capPerWordPerResource = 2,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(examples);
        ArgumentNullException.ThrowIfNull(existingByWordId);
        var addedByWordId = new Dictionary<string, List<ExampleSentence>>(StringComparer.Ordinal);
        var added = new List<ExampleSentence>();
        foreach (var example in examples)
        {
            ct.ThrowIfCancellationRequested();
            var word = example.Word;
            if (ReadingSentence(word, example.TargetSentence, example.RequireTermInSentence) is not { } prepared)
                continue;
            var (trimmedTargetSentence, normalizedTargetSentence) = prepared;

            existingByWordId.TryGetValue(word.Id, out var existingForWord);
            addedByWordId.TryGetValue(word.Id, out var addedForWord);
            var sentencesForChecks = (existingForWord ?? Array.Empty<ExampleSentence>())
                .Concat(addedForWord ?? Enumerable.Empty<ExampleSentence>());
            if (HasOrIsCapped(sentencesForChecks, normalizedTargetSentence, learningResourceId, capPerWordPerResource))
                continue;

            if (await OwnerForNewAsync(word.Id, learningResourceId, ct) is not { } owner)
            {
                _logger.LogWarning("CreateFromReadingIfNewAsync skipped a sentence with no proven owner.");
                continue;
            }

            var sentence = NewReadingSentence(owner, word.Id, learningResourceId, trimmedTargetSentence,
                example.NativeSentence, status, register);
            if (addedForWord is null)
                addedByWordId[word.Id] = addedForWord = new List<ExampleSentence>();
            addedForWord.Add(sentence);
            added.Add(sentence);
        }

        if (added.Count == 0)
            return 0;
        _context.ExampleSentences.AddRange(added);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Created {Count} FromReading example sentences for resource {ResourceId}",
            added.Count, learningResourceId);
        return added.Count;
    }

    /// <summary>
    /// The trimmed and normalized form of a harvested sentence, or null when it is blank, too long,
    /// or (when required) does not contain the word.
    /// </summary>
    private static (string Trimmed, string Normalized)? ReadingSentence(
        VocabularyWord? word, string? targetSentence, bool requireTermInSentence)
    {
        if (word == null || string.IsNullOrWhiteSpace(word.Id) || string.IsNullOrWhiteSpace(targetSentence))
            return null;

        var trimmedTargetSentence = targetSentence.Trim();
        if (requireTermInSentence && !SentenceContainsWordTerm(word, trimmedTargetSentence))
            return null;

        // ExampleSentence.TargetSentence is [MaxLength(500)] — enforced as varchar(500) on the
        // Postgres (WebApp/API) provider. An overlong segment (e.g. a long newline-delimited
        // transcript line with no sentence-final punctuation) would throw on insert, aborting a
        // whole harvest run. Such a run-on is a poor example anyway, so skip it rather than truncate.
        if (trimmedTargetSentence.Length > MaxSentenceLength)
            return null;

        var normalizedTargetSentence = NormalizeForDedup(trimmedTargetSentence);
        return string.IsNullOrWhiteSpace(normalizedTargetSentence)
            ? null
            : (trimmedTargetSentence, normalizedTargetSentence);
    }

    /// <summary>The word already has the sentence, or the resource already gave it its cap of reading sentences.</summary>
    private static bool HasOrIsCapped(IEnumerable<ExampleSentence> sentencesForWord,
        string normalizedTargetSentence, string? learningResourceId, int capPerWordPerResource)
    {
        var fromReadingForResource = 0;
        foreach (var existing in sentencesForWord)
        {
            if (NormalizeForDedup(existing.TargetSentence) == normalizedTargetSentence)
                return true;
            if (learningResourceId != null && existing.Source == ExampleSentenceSource.FromReading
                && existing.LearningResourceId == learningResourceId)
                fromReadingForResource++;
        }
        return learningResourceId != null && fromReadingForResource >= capPerWordPerResource;
    }

    private static ExampleSentence NewReadingSentence(string owner, string wordId, string? learningResourceId,
        string trimmedTargetSentence, string? nativeSentence, ExampleSentenceStatus status, SpeechRegister register)
    {
        var now = DateTime.UtcNow;
        return new ExampleSentence
        {
            UserProfileId = owner,
            VocabularyWordId = wordId,
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
    }

    /// <summary>
    /// Update an existing example sentence
    /// </summary>
    public async Task<ExampleSentence> UpdateAsync(ExampleSentence sentence)
    {
        var stored = await Visible(nameof(UpdateAsync)).AsNoTracking()
            .Where(es => es.Id == sentence.Id)
            .Select(es => new { es.UserProfileId })
            .SingleOrDefaultAsync()
            ?? throw new InvalidOperationException($"Example sentence {sentence.Id} not found");
        // The stored owner is kept; a caller cannot move a sentence to another learner.
        sentence.UserProfileId = stored.UserProfileId;
        sentence.UpdatedAt = DateTime.UtcNow;

        _context.ExampleSentences.Update(sentence);
        await _context.SaveChangesAsync();

        _logger.LogInformation("✅ Updated example sentence {Id}", sentence.Id);

        return sentence;
    }

    /// <summary>
    /// Delete an example sentence
    /// </summary>
    public async Task DeleteAsync(string id)
    {
        var sentence = await Visible(nameof(DeleteAsync)).SingleOrDefaultAsync(es => es.Id == id);
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
    public async Task<ExampleSentence> SetCoreAsync(string id, bool isCore)
    {
        var sentence = await Visible(nameof(SetCoreAsync)).SingleOrDefaultAsync(es => es.Id == id);
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
        // A conjugated Korean verb or adjective is found from its dictionary form (먹다 in
        // 먹었어요, 가다 in 갔어요, 모르다 in 몰라요; KoreanPredicateMatcher). A word with no lemma
        // (an imported word) is its own lemma, as a transcript word is.
        return ContainsTerm(sentence, word.TargetLanguageTerm) ||
            ContainsTerm(sentence, word.Lemma) ||
            KoreanPredicateMatcher.Contains(sentence,
                string.IsNullOrWhiteSpace(word.Lemma) ? word.TargetLanguageTerm : word.Lemma);
    }

    private static bool ContainsTerm(string sentence, string? term)
    {
        return !string.IsNullOrWhiteSpace(term) &&
            sentence.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int? MapCefrToDifficulty(string? targetCefrLevel)
    {
        if (string.IsNullOrWhiteSpace(targetCefrLevel))
            return null;

        return targetCefrLevel.Trim().ToUpperInvariant() switch
        {
            "A1" => 1,
            "A2" => 2,
            "B1" => 3,
            "B2" => 4,
            "C1" or "C2" => 5,
            _ => null
        };
    }

    private static IOrderedEnumerable<QuizHintCandidate> RankQuizHintCandidates(
        IEnumerable<QuizHintCandidate> candidates,
        int? targetDifficulty)
    {
        IOrderedEnumerable<QuizHintCandidate> ranked;
        if (targetDifficulty.HasValue)
        {
            ranked = candidates
                .OrderBy(candidate => IsKnownDifficulty(candidate.DifficultyLevel) ? 0 : 1)
                .ThenBy(candidate => IsKnownDifficulty(candidate.DifficultyLevel)
                    ? Math.Abs(candidate.DifficultyLevel!.Value - targetDifficulty.Value)
                    : int.MaxValue);
        }
        else
        {
            ranked = candidates.OrderBy(_ => 0);
        }

        return ranked
            .ThenByDescending(candidate => candidate.IsCore)
            .ThenByDescending(candidate => candidate.Status == ExampleSentenceStatus.Verified)
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.ExampleSentenceId, StringComparer.Ordinal);
    }

    private static bool IsKnownDifficulty(int? difficultyLevel) =>
        difficultyLevel is >= 1 and <= 5;

    private sealed record QuizHintCandidate(
        string ExampleSentenceId,
        string VocabularyWordId,
        string TargetSentence,
        int? DifficultyLevel,
        bool IsCore,
        ExampleSentenceStatus Status,
        DateTime CreatedAt);

    /// <summary>
    /// Clear IsCore on all other example sentences for a word so at most one stays core.
    /// </summary>
    private async Task DemoteOtherCoresAsync(string vocabularyWordId, string? exceptId)
    {
        var existingCores = await Visible(nameof(DemoteOtherCoresAsync))
            .Where(es => es.VocabularyWordId == vocabularyWordId && es.IsCore
                && (exceptId == null || es.Id != exceptId))
            .ToListAsync();

        foreach (var core in existingCores)
        {
            core.IsCore = false;
            core.UpdatedAt = DateTime.UtcNow;
        }
    }
}
