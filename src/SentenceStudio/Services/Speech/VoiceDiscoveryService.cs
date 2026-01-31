using ElevenLabs;
using ElevenLabs.Voices;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services.Speech;

/// <summary>
/// Discovers available voices from ElevenLabs API, with language filtering and caching.
/// </summary>
public class VoiceDiscoveryService : IVoiceDiscoveryService
{
    private readonly ElevenLabsClient _client;
    private readonly ILogger<VoiceDiscoveryService> _logger;
    
    private readonly Dictionary<string, List<VoiceInfo>> _voiceCache = new();
    private readonly Dictionary<string, DateTime> _cacheTimestamps = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(24);
    
    /// <summary>
    /// Language name to ElevenLabs language code mapping.
    /// </summary>
    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "English", "en" },
        { "French", "fr" },
        { "German", "de" },
        { "Korean", "ko" },
        { "Spanish", "es" }
    };
    
    /// <summary>
    /// Fallback voices when API is unavailable or returns no results.
    /// These are well-known ElevenLabs voice IDs that work for each language.
    /// </summary>
    private static readonly Dictionary<string, List<VoiceInfo>> FallbackVoices = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Korean", new List<VoiceInfo>
            {
                new() { VoiceId = "AW5wrnG1jVizOYY7R1Oo", Name = "Ji-Young", Language = "ko", Gender = "Female", Description = "Warm and clear Korean female voice" },
                new() { VoiceId = "xi3rF0t7dg7uN2M0WUhr", Name = "Yuna", Language = "ko", Gender = "Female", Description = "Young Korean female voice, cheerful" },
                new() { VoiceId = "z6Kj0hecH20CdetSElRT", Name = "Jennie", Language = "ko", Gender = "Female", Description = "Informative and youthful" },
                new() { VoiceId = "sSoVF9lUgTGJz0Xz3J9y", Name = "Jina", Language = "ko", Gender = "Female", Description = "Mid-aged Korean female, news style" },
                new() { VoiceId = "s07IwTCOrCDCaETjUVjx", Name = "Hyun-Bin", Language = "ko", Gender = "Male", Description = "Professional Korean male voice" },
                new() { VoiceId = "FQ3MuLxZh0jHcZmA5vW1", Name = "Do-Hyeon", Language = "ko", Gender = "Male", Description = "Older, mature Korean male" },
                new() { VoiceId = "4JJwo477JUAx3HV0T7n7", Name = "Yohan Koo", Language = "ko", Gender = "Male", Description = "Confident, authoritative" }
            }
        },
        { "English", new List<VoiceInfo>
            {
                new() { VoiceId = "21m00Tcm4TlvDq8ikWAM", Name = "Rachel", Language = "en", Gender = "Female", Accent = "American", Description = "Clear American female voice" },
                new() { VoiceId = "ED0k6LqFEfpMua5GXpMG", Name = "Antoni", Language = "en", Gender = "Male", Accent = "American", Description = "American male voice" },
                new() { VoiceId = "5Q0t7uMcjvnagumLfvZi", Name = "Dorothy", Language = "en", Gender = "Female", Accent = "British", Description = "British female voice" }
            }
        },
        { "German", new List<VoiceInfo>
            {
                new() { VoiceId = "pNInz6obpgDQGcFmaJgB", Name = "Adam", Language = "de", Gender = "Male", Description = "German male voice" }
            }
        },
        { "French", new List<VoiceInfo>
            {
                new() { VoiceId = "pNInz6obpgDQGcFmaJgB", Name = "Adam", Language = "fr", Gender = "Male", Description = "French male voice" }
            }
        },
        { "Spanish", new List<VoiceInfo>
            {
                new() { VoiceId = "pNInz6obpgDQGcFmaJgB", Name = "Adam", Language = "es", Gender = "Male", Description = "Spanish male voice" }
            }
        }
    };

    public VoiceDiscoveryService(ElevenLabsClient client, ILogger<VoiceDiscoveryService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public IReadOnlyList<string> SupportedLanguages => LanguageCodes.Keys.ToList().AsReadOnly();

    public string? GetLanguageCode(string language)
    {
        return LanguageCodes.TryGetValue(language, out var code) ? code : null;
    }

    public async Task<List<VoiceInfo>> GetVoicesForLanguageAsync(string language, bool forceRefresh = false)
    {
        // Check cache first
        if (!forceRefresh && _voiceCache.TryGetValue(language, out var cachedVoices))
        {
            if (_cacheTimestamps.TryGetValue(language, out var timestamp) && 
                DateTime.UtcNow - timestamp < _cacheDuration)
            {
                _logger.LogDebug("🎙️ Using cached voices for {Language} ({Count} voices)", language, cachedVoices.Count);
                return cachedVoices;
            }
        }

        var languageCode = GetLanguageCode(language);
        if (languageCode == null)
        {
            _logger.LogWarning("⚠️ Unsupported language for voice discovery: {Language}", language);
            return FallbackVoices.TryGetValue(language, out var fallback) ? fallback : new List<VoiceInfo>();
        }

        try
        {
            _logger.LogInformation("🎙️ Fetching voices from ElevenLabs for language: {Language} ({Code})", language, languageCode);
            
            var query = new SharedVoiceQuery
            {
                Language = languageCode,
                PageSize = 50,
                Category = "professional" // Prefer professional quality voices
            };
            
            var results = await _client.SharedVoicesEndpoint.GetSharedVoicesAsync(query);
            
            var voices = results.Voices
                .Where(v => v.FreeUsersAllowed) // Only include voices available to free tier
                .Select(v => new VoiceInfo
                {
                    VoiceId = v.VoiceId,
                    Name = v.Name,
                    Language = v.Language ?? languageCode,
                    Gender = CapitalizeFirst(v.Gender),
                    Accent = CapitalizeFirst(v.Accent),
                    Description = v.Description ?? "",
                    PreviewUrl = v.PreviewUrl ?? "",
                    Category = v.Category ?? ""
                })
                .OrderBy(v => v.Name)
                .ToList();

            // If API returns no results, use fallback
            if (voices.Count == 0)
            {
                _logger.LogWarning("⚠️ No voices returned from API for {Language}, using fallback", language);
                voices = FallbackVoices.TryGetValue(language, out var fallbackList) 
                    ? fallbackList.ToList() 
                    : new List<VoiceInfo>();
            }

            // Update cache
            _voiceCache[language] = voices;
            _cacheTimestamps[language] = DateTime.UtcNow;

            _logger.LogInformation("🎙️ Found {Count} voices for {Language}", voices.Count, language);
            return voices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to fetch voices for {Language}, using fallback", language);
            return FallbackVoices.TryGetValue(language, out var fallback) ? fallback : new List<VoiceInfo>();
        }
    }

    public void ClearCache()
    {
        _voiceCache.Clear();
        _cacheTimestamps.Clear();
        _logger.LogInformation("🗑️ Voice cache cleared");
    }

    private static string CapitalizeFirst(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return char.ToUpper(value[0]) + value[1..].ToLower();
    }
}
