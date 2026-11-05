using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Services.Api;

/// <summary>
/// Voice lists for the shared Razor pages (WebApp and MAUI Settings, How Do You Say), read from the
/// API's <c>GET /api/v1/speech/voices</c> as the signed-in learner. The API holds the ElevenLabs key
/// and answers with its own fallback list when ElevenLabs is unavailable, so the hosts need no key.
/// </summary>
/// <remarks>
/// Same contract as the in-process <see cref="VoiceDiscoveryService"/> it replaces on these hosts:
/// the same five languages and codes, and a successful list kept per language for 24 hours. A
/// failure is thrown rather than turned into an empty list; no active learner throws
/// <see cref="UnauthorizedAccessException"/>.
/// </remarks>
public sealed class ApiVoiceDiscoveryService(
    ISpeechApiClient speechApi,
    IServiceProvider services,
    ILogger<ApiVoiceDiscoveryService> logger) : IVoiceDiscoveryService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "English", "en" },
        { "French", "fr" },
        { "German", "de" },
        { "Korean", "ko" },
        { "Spanish", "es" }
    };

    private readonly ConcurrentDictionary<string, (List<VoiceInfo> Voices, DateTime FetchedUtc)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> SupportedLanguages { get; } = LanguageCodes.Keys.ToList().AsReadOnly();

    public string? GetLanguageCode(string language) =>
        LanguageCodes.TryGetValue(language, out var code) ? code : null;

    public async Task<List<VoiceInfo>> GetVoicesForLanguageAsync(string language, bool forceRefresh = false)
    {
        if (GetLanguageCode(language) is null)
        {
            logger.LogWarning("Unsupported language for voice discovery: {Language}", language);
            return [];
        }

        // The voice list is not learner data, but the endpoint answers only a signed-in learner.
        using (var scope = services.CreateScope())
        {
            if (!ActiveOwner.TryResolve(scope.ServiceProvider, logger, "Voice discovery", out _))
                throw new UnauthorizedAccessException("Voice discovery needs a signed-in learner.");
        }

        if (!forceRefresh && _cache.TryGetValue(language, out var cached)
            && DateTime.UtcNow - cached.FetchedUtc < CacheDuration)
        {
            return cached.Voices;
        }

        var voices = (await speechApi.GetVoicesAsync(language))
            .Select(v => new VoiceInfo
            {
                VoiceId = v.Id,
                Name = v.Name,
                Language = v.Language,
                Gender = v.Gender ?? string.Empty,
                Accent = v.Accent ?? string.Empty
            })
            .ToList();

        _cache[language] = (voices, DateTime.UtcNow);
        logger.LogInformation("Found {Count} available voices for {Language}", voices.Count, language);
        return voices;
    }

    public void ClearCache()
    {
        _cache.Clear();
        logger.LogInformation("Voice cache cleared");
    }
}
