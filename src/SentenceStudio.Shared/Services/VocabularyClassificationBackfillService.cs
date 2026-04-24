using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// One-time backfill service that classifies existing VocabularyWord rows where LexicalUnitType == Unknown
/// and populates PhraseConstituent rows for phrases/sentences by matching against user's vocabulary.
/// Runs at startup after MigrateAsync(). Idempotent — safe to call multiple times.
/// </summary>
public class VocabularyClassificationBackfillService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VocabularyClassificationBackfillService> _logger;

    /// <summary>
    /// Length threshold above which a term is considered a phrase (even without whitespace).
    /// Korean compounds like "공부하다" (4 chars) stay as Word; longer chunks become Phrase.
    /// </summary>
    private const int PhraseLengthThreshold = 12;

    /// <summary>
    /// Korean particles stripped from tokens before lemma lookup. Best-effort for backfill.
    /// </summary>
    private static readonly string[] KoreanParticles = 
    {
        "이", "가", "을", "를", "은", "는", "에", "의", "로", "으로", 
        "와", "과", "에서", "에게", "도", "만", "부터", "까지"
    };

    public VocabularyClassificationBackfillService(
        IServiceProvider serviceProvider,
        ILogger<VocabularyClassificationBackfillService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Backfills LexicalUnitType for existing VocabularyWord rows where LexicalUnitType == Unknown.
    /// Uses heuristics: tags, terminal punctuation, whitespace, length threshold, and CJK single-char guard.
    /// </summary>
    public async Task BackfillLexicalUnitTypesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Starting vocabulary classification backfill...");

        // Load all rows where LexicalUnitType == Unknown
        var unknownWords = await db.VocabularyWords
            .Where(w => w.LexicalUnitType == LexicalUnitType.Unknown)
            .ToListAsync(cancellationToken);

        if (unknownWords.Count == 0)
        {
            _logger.LogInformation("No vocabulary words with Unknown classification found. Backfill complete.");
            return;
        }

        var counts = new Dictionary<LexicalUnitType, int>
        {
            { LexicalUnitType.Word, 0 },
            { LexicalUnitType.Phrase, 0 },
            { LexicalUnitType.Sentence, 0 },
            { LexicalUnitType.Unknown, 0 }
        };

        foreach (var word in unknownWords)
        {
            var classification = ClassifyHeuristic(word.TargetLanguageTerm ?? string.Empty, word.Tags);
            word.LexicalUnitType = classification;
            counts[classification]++;
        }

        await db.SaveChangesAsync(cancellationToken);

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        _logger.LogInformation(
            "Vocabulary classification backfill complete. " +
            "Total: {Total}, Word: {Word}, Phrase: {Phrase}, Sentence: {Sentence}, Unknown: {StillUnknown}. " +
            "Elapsed: {ElapsedMs}ms",
            unknownWords.Count,
            counts[LexicalUnitType.Word],
            counts[LexicalUnitType.Phrase],
            counts[LexicalUnitType.Sentence],
            counts[LexicalUnitType.Unknown],
            elapsed);
    }

    /// <summary>
    /// Pure static classification heuristic. Exposed for unit testing without DB dependency.
    /// Classification priority:
    /// 1. Tags check: if Tags contains "phrase" → Phrase; if contains "sentence" → Sentence.
    /// 2. Terminal punctuation: if term ends with . ? ! 。 ？ ！ → Sentence.
    /// 3. Whitespace OR length threshold: if term contains whitespace OR length > 12 → Phrase.
    /// 4. Default: Word.
    /// 5. Conservative guard: if term is single non-ASCII char (length 1, CJK range), leave Unknown.
    /// </summary>
    public static LexicalUnitType ClassifyHeuristic(string term, string? tags)
    {
        if (string.IsNullOrWhiteSpace(term))
            return LexicalUnitType.Unknown;

        var trimmed = term.Trim();

        // Guard: single non-ASCII character (CJK ambiguous single chars) → Unknown
        if (trimmed.Length == 1 && trimmed[0] > 127)
            return LexicalUnitType.Unknown;

        // 1. Tags check (case-insensitive)
        if (!string.IsNullOrWhiteSpace(tags))
        {
            var tagsLower = tags.ToLowerInvariant();
            if (tagsLower.Contains("sentence"))
                return LexicalUnitType.Sentence;
            if (tagsLower.Contains("phrase"))
                return LexicalUnitType.Phrase;
        }

        // 2. Terminal punctuation → Sentence
        var lastChar = trimmed[trimmed.Length - 1];
        if (lastChar is '.' or '?' or '!' or '。' or '？' or '！')
            return LexicalUnitType.Sentence;

        // 3. Whitespace OR length threshold → Phrase
        // CJK ideographic space U+3000 is also considered whitespace
        if (trimmed.Any(c => char.IsWhiteSpace(c) || c == '\u3000') || trimmed.Length > PhraseLengthThreshold)
            return LexicalUnitType.Phrase;

        // 4. Default: Word
        return LexicalUnitType.Word;
    }

    /// <summary>
    /// Backfills PhraseConstituent rows for existing Phrase/Sentence VocabularyWords
    /// by tokenizing and matching against that user's Word vocabulary.
    /// Idempotent — skips phrases that already have constituents.
    /// </summary>
    public async Task BackfillPhraseConstituentsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Starting phrase constituent backfill...");

        // Get all distinct user IDs from VocabularyProgress (vocabulary is tracked per-user via progress)
        var userIds = await db.VocabularyProgresses
            .Select(vp => vp.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (userIds.Count == 0)
        {
            _logger.LogInformation("No user vocabulary progress found. Phrase constituent backfill complete.");
            return;
        }

        int totalPhrasesProcessed = 0;
        int totalConstituentsInserted = 0;
        int phrasesSkipped = 0;

        foreach (var userId in userIds)
        {
            _logger.LogDebug("Processing phrase constituents for user {UserId}", userId);

            // Pre-build lemma dictionary ONCE per user to avoid N+1 queries
            // Query through VocabularyProgress to get user-specific vocabulary
            var userProgress = await db.VocabularyProgresses
                .Where(vp => vp.UserId == userId)
                .Include(vp => vp.VocabularyWord)
                .ToListAsync(cancellationToken);

            var userWords = userProgress
                .Where(vp => vp.VocabularyWord != null)
                .Select(vp => vp.VocabularyWord!)
                .ToList();

            var wordLookup = userWords
                .Where(w => w.LexicalUnitType == LexicalUnitType.Word)
                .GroupBy(w => NormalizeLookupKey(w.Lemma ?? w.TargetLanguageTerm))
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(g => g.Key!, g => g.First());

            _logger.LogDebug("Built lemma dictionary with {Count} entries for user {UserId}", 
                wordLookup.Count, userId);

            // Get phrases/sentences for this user
            var phrases = userWords
                .Where(w => w.LexicalUnitType == LexicalUnitType.Phrase 
                         || w.LexicalUnitType == LexicalUnitType.Sentence)
                .ToList();

            foreach (var phrase in phrases)
            {
                // Idempotency guard: skip if constituents already exist
                var hasConstituents = await db.PhraseConstituents
                    .AnyAsync(pc => pc.PhraseWordId == phrase.Id, cancellationToken);

                if (hasConstituents)
                {
                    phrasesSkipped++;
                    continue;
                }

                // Skip garbage data
                if (string.IsNullOrWhiteSpace(phrase.TargetLanguageTerm) 
                    || phrase.TargetLanguageTerm.Trim().Length < 2)
                {
                    continue;
                }

                var constituentsToInsert = new List<PhraseConstituent>();
                var matchedConstituentIds = new HashSet<string>(); // Dedupe within phrase

                // Tokenize and match constituents
                var tokens = TokenizePhrase(phrase.TargetLanguageTerm, phrase.Language ?? "ko");
                int tokenMatchCount = 0;

                foreach (var token in tokens)
                {
                    if (string.IsNullOrWhiteSpace(token) || token.Length < 1)
                        continue;

                    var normalizedToken = NormalizeLookupKey(token);
                    
                    // Try exact lemma lookup first
                    if (wordLookup.TryGetValue(normalizedToken, out var matchedWord))
                    {
                        if (!matchedConstituentIds.Contains(matchedWord.Id))
                        {
                            constituentsToInsert.Add(new PhraseConstituent
                            {
                                Id = Guid.NewGuid().ToString(),
                                PhraseWordId = phrase.Id,
                                ConstituentWordId = matchedWord.Id,
                                CreatedAt = DateTime.UtcNow
                            });
                            matchedConstituentIds.Add(matchedWord.Id);
                            tokenMatchCount++;
                        }
                        continue;
                    }

                    // Fallback: substring match for conjugated forms (2+ chars only)
                    if (token.Length >= 2)
                    {
                        var substringMatch = userWords
                            .Where(w => w.LexicalUnitType == LexicalUnitType.Word)
                            .FirstOrDefault(w => 
                                !string.IsNullOrWhiteSpace(w.TargetLanguageTerm) 
                                && w.TargetLanguageTerm.Contains(token, StringComparison.OrdinalIgnoreCase));

                        if (substringMatch != null && !matchedConstituentIds.Contains(substringMatch.Id))
                        {
                            constituentsToInsert.Add(new PhraseConstituent
                            {
                                Id = Guid.NewGuid().ToString(),
                                PhraseWordId = phrase.Id,
                                ConstituentWordId = substringMatch.Id,
                                CreatedAt = DateTime.UtcNow
                            });
                            matchedConstituentIds.Add(substringMatch.Id);
                            tokenMatchCount++;
                        }
                    }
                }

                if (constituentsToInsert.Count > 0)
                {
                    db.PhraseConstituents.AddRange(constituentsToInsert);
                    totalConstituentsInserted += constituentsToInsert.Count;
                }

                totalPhrasesProcessed++;

                _logger.LogTrace(
                    "Phrase {PhraseId} ({Term}): {TokenCount} tokens, {MatchCount} constituents matched",
                    phrase.Id, phrase.TargetLanguageTerm, tokens.Count, tokenMatchCount);
            }

            // Save per-user to keep transaction size reasonable
            await db.SaveChangesAsync(cancellationToken);
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        _logger.LogInformation(
            "Phrase constituent backfill complete. " +
            "Phrases processed: {Processed}, Constituents inserted: {Inserted}, Skipped: {Skipped}. " +
            "Elapsed: {ElapsedMs}ms",
            totalPhrasesProcessed,
            totalConstituentsInserted,
            phrasesSkipped,
            elapsed);
    }

    /// <summary>
    /// Tokenizes a phrase into constituent tokens for matching.
    /// Exposed as public static for unit testing without DB dependency.
    /// </summary>
    /// <param name="term">The phrase or sentence to tokenize</param>
    /// <param name="languageCode">Language code (e.g., "ko" for Korean) to enable language-specific rules</param>
    /// <returns>List of normalized tokens</returns>
    public static IReadOnlyList<string> TokenizePhrase(string term, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Array.Empty<string>();

        var tokens = new List<string>();
        
        // Split on whitespace (including CJK ideographic space U+3000)
        var rawTokens = term.Split(
            new[] { ' ', '\t', '\n', '\r', '\u3000' }, 
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawToken in rawTokens)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
                continue;

            var token = rawToken.Trim();

            // Strip terminal punctuation
            token = token.TrimEnd('.', '?', '!', '。', '？', '！', ',', '、');

            // Korean-specific: strip trailing particles
            if (languageCode?.Equals("ko", StringComparison.OrdinalIgnoreCase) == true)
            {
                token = StripKoreanParticles(token);
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static string StripKoreanParticles(string token)
    {
        foreach (var particle in KoreanParticles)
        {
            if (token.EndsWith(particle) && token.Length > particle.Length)
            {
                // Only strip if it leaves at least 1 character
                var stripped = token.Substring(0, token.Length - particle.Length);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    return stripped;
                }
            }
        }
        return token;
    }

    private static string NormalizeLookupKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        
        return text.Trim().ToLowerInvariant();
    }
}
