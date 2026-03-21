using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// Service for cleaning up and formatting transcripts using language-specific rules and optional AI polish
/// </summary>
public class TranscriptFormattingService
{
    private readonly AiService _aiService;
    private readonly IEnumerable<ILanguageSegmenter> _segmenters;
    private readonly Dictionary<string, string> _aiPolishCache = new();
    private readonly ILogger<TranscriptFormattingService> _logger;
    private readonly IFileSystemService _fileSystem;

    public TranscriptFormattingService(AiService aiService, IEnumerable<ILanguageSegmenter> segmenters, ILogger<TranscriptFormattingService> logger, IFileSystemService fileSystem)
    {
        _aiService = aiService;
        _segmenters = segmenters;
        _logger = logger;
        _fileSystem = fileSystem;

        _logger.LogDebug("🏴‍☠️ TranscriptFormattingService initialized with {SegmenterCount} segmenters", _segmenters.Count());
        foreach (var seg in _segmenters)
        {
            _logger.LogDebug("  - {LanguageName} ({LanguageCode})", seg.LanguageName, seg.LanguageCode);
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

        _logger.LogDebug("🏴‍☠️ SmartCleanup called for language: '{Language}'", language ?? "null");
        _logger.LogDebug("🏴‍☠️ Input length: {Length} chars", transcript.Length);

        // Get appropriate segmenter for the language
        var segmenter = GetSegmenterForLanguage(language);
        _logger.LogDebug("🏴‍☠️ Segmenter found: {SegmenterName}", segmenter?.LanguageName ?? "NULL");

        var lines = transcript.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        _logger.LogDebug("🏴‍☠️ Split into {LineCount} lines", lines.Length);

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

        _logger.LogDebug("🏴‍☠️ Processed {LinesProcessed} lines, merged {LinesMerged}, created {ParagraphsCreated} paragraphs", linesProcessed, linesMerged, paragraphsCreated);
        _logger.LogDebug("🏴‍☠️ Output length: {Length} chars", cleanedText.Length);

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

        _logger.LogInformation("🚀 PolishWithAiAsync started");
        _logger.LogDebug("📏 Input length: {Length} chars", transcript.Length);
        _logger.LogDebug("🌍 Language: {Language}", language ?? "null");

        // Check for speaker markers in input
        var speakerMarkerCount = transcript.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
            .Count(line => line.TrimStart().StartsWith(">> "));
        _logger.LogDebug("🎤 Found {SpeakerMarkerCount} speaker markers (>> ) in input", speakerMarkerCount);

        // Check cache first
        var hash = ComputeHash(transcript);
        if (_aiPolishCache.TryGetValue(hash, out var cachedResult))
        {
            _logger.LogDebug("✅ Using cached AI polish result");
            return cachedResult;
        }

        try
        {
            // Load and render the CleanTranscript Scriban template
            var prompt = string.Empty;
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("CleanTranscript.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new
                {
                    video_title = (string?)null,  // Optional context (not available here)
                    channel_name = (string?)null, // Optional context (not available here)
                    raw_transcript = transcript
                });
            }

            _logger.LogDebug("🤖 Sending to AI...");

            var polished = await _aiService.SendPrompt<string>(prompt);

            if (string.IsNullOrWhiteSpace(polished))
            {
                _logger.LogError("❌ AI returned empty/null result");
                throw new Exception("AI service returned empty result. Check your internet connection and API configuration.");
            }

            // Verify speaker markers are preserved
            var polishedSpeakerCount = polished.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .Count(line => line.TrimStart().StartsWith(">> "));
            _logger.LogDebug("🎤 Speaker markers in output: {PolishedSpeakerCount}", polishedSpeakerCount);

            if (speakerMarkerCount != polishedSpeakerCount)
            {
                _logger.LogWarning("⚠️ Speaker marker count mismatch! Input: {InputCount}, Output: {OutputCount}", speakerMarkerCount, polishedSpeakerCount);
            }

            // Cache the result
            _aiPolishCache[hash] = polished;
            _logger.LogInformation("✅ AI polish complete, cached result (length: {Length} chars)", polished.Length);
            return polished;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ AI polish failed");
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

        _logger.LogDebug("🏴‍☠️ Looking for segmenter with language: '{Language}'", language);

        // Extract the core language name by removing common suffixes/annotations
        var cleanLanguage = CleanLanguageString(language);
        _logger.LogDebug("🏴‍☠️ Cleaned language: '{CleanLanguage}'", cleanLanguage);

        // Try to find a matching segmenter using multiple strategies
        foreach (var segmenter in _segmenters)
        {
            // Strategy 1: Exact match on cleaned language name
            if (segmenter.LanguageName.Equals(cleanLanguage, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("🏴‍☠️ Found exact match: {LanguageName}", segmenter.LanguageName);
                return segmenter;
            }

            // Strategy 2: Check if cleaned input starts with segmenter's language name
            if (cleanLanguage.StartsWith(segmenter.LanguageName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("🏴‍☠️ Found by prefix match: {LanguageName}", segmenter.LanguageName);
                return segmenter;
            }

            // Strategy 3: Check if segmenter's language name is contained in cleaned input
            if (cleanLanguage.Contains(segmenter.LanguageName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("🏴‍☠️ Found by contains match: {LanguageName}", segmenter.LanguageName);
                return segmenter;
            }

            // Strategy 4: Try language code matching
            var inputLanguageCode = GetLanguageCode(cleanLanguage);
            if (segmenter.LanguageCode.Equals(inputLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("🏴‍☠️ Found by language code: {LanguageName} ({LanguageCode})", segmenter.LanguageName, segmenter.LanguageCode);
                return segmenter;
            }
        }

        _logger.LogWarning("⚠️ No segmenter found for language: '{Language}'", language);
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
        _logger.LogDebug("🏴‍☠️ AI polish cache cleared");
    }
}
