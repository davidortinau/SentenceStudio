using System.Security.Cryptography;
using System.Text;

namespace SentenceStudio.Services;

/// <summary>
/// Service for cleaning up and formatting transcripts using language-specific rules and optional AI polish
/// </summary>
public class TranscriptFormattingService
{
    private readonly AiService _aiService;
    private readonly IEnumerable<ILanguageSegmenter> _segmenters;
    private readonly Dictionary<string, string> _aiPolishCache = new();

    public TranscriptFormattingService(AiService aiService, IEnumerable<ILanguageSegmenter> segmenters)
    {
        _aiService = aiService;
        _segmenters = segmenters;

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ TranscriptFormattingService initialized with {_segmenters.Count()} segmenters");
        foreach (var seg in _segmenters)
        {
            System.Diagnostics.Debug.WriteLine($"  - {seg.LanguageName} ({seg.LanguageCode})");
        }
    }

    /// <summary>
    /// Smart cleanup of transcript text using language-specific rules (Plan 2)
    /// </summary>
    /// <param name="transcript">Raw transcript text with excessive line breaks</param>
    /// <param name="language">Language of the transcript (e.g., "Korean", "English")</param>
    /// <returns>Cleaned up transcript text</returns>
    public string SmartCleanup(string transcript, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return transcript;

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SmartCleanup called for language: '{language ?? "null"}'");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Input length: {transcript.Length} chars");

        // Get appropriate segmenter for the language
        var segmenter = GetSegmenterForLanguage(language);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Segmenter found: {segmenter?.LanguageName ?? "NULL"}");

        var lines = transcript.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Split into {lines.Length} lines");

        var result = new StringBuilder();
        var currentParagraph = new StringBuilder();
        int linesProcessed = 0;
        int linesMerged = 0;
        int paragraphsCreated = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var currentLine = lines[i].Trim();
            var nextLine = i < lines.Length - 1 ? lines[i + 1].Trim() : string.Empty;

            linesProcessed++;

            // Skip completely empty lines
            if (string.IsNullOrWhiteSpace(currentLine))
            {
                // If we have accumulated paragraph text, flush it
                if (currentParagraph.Length > 0)
                {
                    result.AppendLine(currentParagraph.ToString().Trim());
                    result.AppendLine(); // Add paragraph break
                    currentParagraph.Clear();
                    paragraphsCreated++;
                }
                continue;
            }

            // Check if we should preserve the line break (make new paragraph)
            bool shouldPreserve = segmenter?.ShouldPreserveLineBreak(currentLine, nextLine) ?? true;

            if (shouldPreserve)
            {
                // Add current line to paragraph and flush
                if (currentParagraph.Length > 0)
                {
                    currentParagraph.Append(' ');
                }
                currentParagraph.Append(currentLine);

                result.AppendLine(currentParagraph.ToString().Trim());
                result.AppendLine(); // Add paragraph break
                currentParagraph.Clear();
                paragraphsCreated++;
            }
            else
            {
                // Merge with current paragraph (incomplete sentence)
                if (currentParagraph.Length > 0)
                {
                    currentParagraph.Append(' ');
                }
                currentParagraph.Append(currentLine);
                linesMerged++;
            }
        }

        // Flush any remaining paragraph
        if (currentParagraph.Length > 0)
        {
            result.AppendLine(currentParagraph.ToString().Trim());
            paragraphsCreated++;
        }

        // Clean up excessive blank lines (max 2 consecutive newlines = 1 blank line)
        var cleanedText = result.ToString();
        while (cleanedText.Contains("\n\n\n"))
        {
            cleanedText = cleanedText.Replace("\n\n\n", "\n\n");
        }

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Processed {linesProcessed} lines, merged {linesMerged}, created {paragraphsCreated} paragraphs");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Output length: {cleanedText.Length} chars");

        return cleanedText.Trim();
    }

    /// <summary>
    /// Polish transcript text using AI for better readability
    /// </summary>
    /// <param name="transcript">Transcript text to polish</param>
    /// <param name="language">Language of the transcript</param>
    /// <returns>AI-polished transcript text</returns>
    public async Task<string> PolishWithAiAsync(string transcript, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return transcript;

        // Check cache first
        var hash = ComputeHash(transcript);
        if (_aiPolishCache.TryGetValue(hash, out var cachedResult))
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Using cached AI polish result");
            return cachedResult;
        }

        try
        {
            var languageText = !string.IsNullOrWhiteSpace(language) ? language : "the original language";

            var prompt = $@"You are a text formatting assistant. Your job is to fix excessive line breaks in this {languageText} transcript.

CRITICAL RULES:
1. NEVER change, translate, or modify ANY words - keep ALL text exactly as written
2. NEVER remove or add any words
3. ONLY fix line breaks by merging lines that were incorrectly split
4. Join incomplete sentences that were broken mid-sentence
5. Group complete sentences into natural paragraphs (2-4 sentences per paragraph)
6. Keep standalone lines separate (greetings like ""안녕하세요"", closings like ""감사합니다"", music notations like ""[음악]"")

EXAMPLES:

BAD (excessive line breaks):
안녕하세요. 한국어 한조각의 정


선생님입니다.


한국어 한조각의 전문

GOOD (properly formatted):
안녕하세요. 한국어 한조각의 정 선생님입니다.

한국어 한조각의 전문

---

Return ONLY the formatted text with no explanations, markdown, or additional commentary.

Transcript to format:
{transcript}";

            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Sending transcript to AI for polishing (length: {transcript.Length} chars)");

            var polished = await _aiService.SendPrompt<string>(prompt);

            if (!string.IsNullOrWhiteSpace(polished))
            {
                // Cache the result
                _aiPolishCache[hash] = polished;
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ AI polish complete, cached result");
                return polished;
            }

            System.Diagnostics.Debug.WriteLine($"⚠️ AI returned empty result, returning original");
            return transcript;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ AI polish failed: {ex.Message}");
            throw new Exception($"Failed to polish transcript with AI: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets the appropriate language segmenter for the given language
    /// </summary>
    private ILanguageSegmenter? GetSegmenterForLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Looking for segmenter with language: '{language}'");

        // Extract the core language name by removing common suffixes/annotations
        var cleanLanguage = CleanLanguageString(language);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Cleaned language: '{cleanLanguage}'");

        // Try to find a matching segmenter using multiple strategies
        foreach (var segmenter in _segmenters)
        {
            // Strategy 1: Exact match on cleaned language name
            if (segmenter.LanguageName.Equals(cleanLanguage, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Found exact match: {segmenter.LanguageName}");
                return segmenter;
            }

            // Strategy 2: Check if cleaned input starts with segmenter's language name
            if (cleanLanguage.StartsWith(segmenter.LanguageName, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Found by prefix match: {segmenter.LanguageName}");
                return segmenter;
            }

            // Strategy 3: Check if segmenter's language name is contained in cleaned input
            if (cleanLanguage.Contains(segmenter.LanguageName, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Found by contains match: {segmenter.LanguageName}");
                return segmenter;
            }

            // Strategy 4: Try language code matching
            var inputLanguageCode = GetLanguageCode(cleanLanguage);
            if (segmenter.LanguageCode.Equals(inputLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Found by language code: {segmenter.LanguageName} ({segmenter.LanguageCode})");
                return segmenter;
            }
        }

        System.Diagnostics.Debug.WriteLine($"⚠️ No segmenter found for language: '{language}'");
        return null;
    }

    /// <summary>
    /// Cleans up language strings by removing common annotations and normalizing format
    /// </summary>
    private string CleanLanguageString(string language)
    {
        // Remove parenthetical notes: "Korean (auto-generated)" -> "Korean"
        var parenIndex = language.IndexOf('(');
        if (parenIndex > 0)
        {
            language = language.Substring(0, parenIndex);
        }

        // Remove square bracket notes: "Korean [CC]" -> "Korean"
        var bracketIndex = language.IndexOf('[');
        if (bracketIndex > 0)
        {
            language = language.Substring(0, bracketIndex);
        }

        // Remove common suffixes
        language = language
            .Replace(" - auto", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" - manual", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" auto-generated", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" (generated)", "", StringComparison.OrdinalIgnoreCase);

        return language.Trim();
    }

    /// <summary>
    /// Maps language names to ISO 639-1 language codes
    /// </summary>
    private string GetLanguageCode(string language) => language switch
    {
        "Korean" => "ko",
        "English" => "en",
        "Spanish" => "es",
        "Japanese" => "ja",
        "Chinese" => "zh",
        "French" => "fr",
        "German" => "de",
        "Italian" => "it",
        "Portuguese" => "pt",
        "Russian" => "ru",
        _ => "en"
    };

    /// <summary>
    /// Computes SHA256 hash of text for caching purposes
    /// </summary>
    private string ComputeHash(string text)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(text);
        var hashBytes = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Clears the AI polish cache (useful for testing or memory management)
    /// </summary>
    public void ClearCache()
    {
        _aiPolishCache.Clear();
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ AI polish cache cleared");
    }
}
