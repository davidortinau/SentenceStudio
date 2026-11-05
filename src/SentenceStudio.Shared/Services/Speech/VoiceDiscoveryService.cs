using ElevenLabs;
using ElevenLabs.Voices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services.Speech;

/// <summary>The account's voices, and whether ElevenLabs could actually be read.</summary>
/// <param name="Loaded">
/// True when the list came from ElevenLabs. False when it could not be read (an outage, an error, or
/// the retry back-off after one); <paramref name="Voices"/> is then empty.
/// </param>
public sealed record AccountVoiceSnapshot(bool Loaded, IReadOnlyList<VoiceInfo> Voices)
{
    public static AccountVoiceSnapshot Unavailable { get; } = new(false, []);
}

/// <summary>The voices available to the API's ElevenLabs account (the API's speech voice check uses it).</summary>
public interface IAccountVoiceSource
{
    /// <summary>The account's voices from one shared, cached load.</summary>
    Task<AccountVoiceSnapshot> GetAccountVoicesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Discovers available voices from ElevenLabs API, with language filtering and caching.
/// </summary>
/// <remarks>
/// Safe for concurrent use; the API holds one instance for every learner. The account's voice list
/// is read with one ElevenLabs call per load, shared by concurrent callers, kept for
/// <see cref="AccountVoicesLifetime"/> and used for every language. A failed load is not kept: callers
/// get the fallback voices, and ElevenLabs is asked again on a later request once
/// <see cref="FailureBackoff"/> has passed.
/// </remarks>
public class VoiceDiscoveryService : IVoiceDiscoveryService, IAccountVoiceSource
{
    /// <summary>How long a successfully read account voice list is used.</summary>
    public static TimeSpan AccountVoicesLifetime { get; } = TimeSpan.FromHours(24);

    /// <summary>How long after a failed read the account list is not asked for again.</summary>
    public static TimeSpan FailureBackoff { get; } = TimeSpan.FromSeconds(30);

    private readonly ElevenLabsClient _client;
    private readonly ILogger<VoiceDiscoveryService> _logger;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private IReadOnlyList<VoiceInfo>? _accountVoices;
    private DateTimeOffset _accountVoicesLoadedAt;
    private DateTimeOffset? _lastFailureAt;
    private Task<AccountVoiceSnapshot>? _load;

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
    /// Populated from config (AI:ElevenLabs:Voices) with hardcoded defaults as last resort.
    /// </summary>
    private readonly Dictionary<string, List<VoiceInfo>> _fallbackVoices;

    // Hardcoded defaults — config takes precedence (see constructor)
    private static readonly Dictionary<string, List<VoiceInfo>> DefaultFallbackVoices = new(StringComparer.OrdinalIgnoreCase)
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

    public VoiceDiscoveryService(
        ElevenLabsClient client,
        ILogger<VoiceDiscoveryService> logger,
        IConfiguration? configuration = null,
        TimeProvider? time = null)
    {
        _client = client;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _fallbackVoices = BuildFallbackVoices(configuration);
    }

    public IReadOnlyList<string> SupportedLanguages => LanguageCodes.Keys.ToList().AsReadOnly();

    public string? GetLanguageCode(string language)
    {
        return LanguageCodes.TryGetValue(language, out var code) ? code : null;
    }

    public async Task<List<VoiceInfo>> GetVoicesForLanguageAsync(string language, bool forceRefresh = false)
    {
        var languageCode = GetLanguageCode(language);
        if (languageCode == null)
        {
            _logger.LogWarning("Unsupported language for voice discovery: {Language}", language);
            return Fallback(language);
        }

        var account = await LoadAccountVoicesAsync(forceRefresh, CancellationToken.None);
        if (!account.Loaded)
        {
            _logger.LogWarning("Voice list unavailable; using the fallback voices for {Language}", language);
            return Fallback(language);
        }

        // The account's voices are offered for every language; most are multilingual.
        if (account.Voices.Count == 0)
        {
            _logger.LogWarning("No voices returned from ElevenLabs; using the fallback voices for {Language}", language);
            return Fallback(language);
        }

        return account.Voices
            .Select(v => new VoiceInfo
            {
                VoiceId = v.VoiceId,
                Name = v.Name,
                Language = languageCode,
                Gender = v.Gender,
                Accent = v.Accent,
                Description = v.Description,
                PreviewUrl = v.PreviewUrl,
                Category = v.Category
            })
            .ToList();
    }

    /// <inheritdoc />
    public Task<AccountVoiceSnapshot> GetAccountVoicesAsync(CancellationToken cancellationToken) =>
        LoadAccountVoicesAsync(forceRefresh: false, cancellationToken);

    public void ClearCache()
    {
        lock (_gate)
        {
            _accountVoices = null;
            _lastFailureAt = null;
        }
        _logger.LogInformation("Voice cache cleared");
    }

    private Task<AccountVoiceSnapshot> LoadAccountVoicesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Task<AccountVoiceSnapshot> load;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!forceRefresh && _accountVoices is { } cached && now - _accountVoicesLoadedAt < AccountVoicesLifetime)
                return Task.FromResult(new AccountVoiceSnapshot(true, cached));

            if (_load is { IsCompleted: false } running)
            {
                load = running;
            }
            else if (!forceRefresh && _lastFailureAt is { } failedAt && now - failedAt < FailureBackoff)
            {
                return Task.FromResult(AccountVoiceSnapshot.Unavailable);
            }
            else
            {
                load = _load = ReadAccountVoicesAsync();
            }
        }

        // One shared read for every caller; a caller's cancellation stops only its own wait.
        return load.WaitAsync(cancellationToken);
    }

    private async Task<AccountVoiceSnapshot> ReadAccountVoicesAsync()
    {
        // Run the read off the caller's lock-holding stack.
        await Task.Yield();
        try
        {
            _logger.LogInformation("Fetching the account's voices from ElevenLabs");

            // GetAllVoicesAsync returns the voices this account can use (not the shared library).
            // Without downloadSettings: false the SDK also fetches every voice's settings, one more
            // ElevenLabs call per voice; nothing here uses them.
            var allVoices = await _client.VoicesEndpoint.GetAllVoicesAsync(downloadSettings: false);
            var voices = allVoices
                .Select(v => new VoiceInfo
                {
                    VoiceId = v.Id,
                    Name = v.Name,
                    Gender = v.Labels?.TryGetValue("gender", out var gender) == true ? CapitalizeFirst(gender) : "",
                    Accent = v.Labels?.TryGetValue("accent", out var accent) == true ? CapitalizeFirst(accent) : "",
                    Description = v.Labels?.TryGetValue("description", out var desc) == true ? desc : "",
                    PreviewUrl = v.PreviewUrl ?? "",
                    Category = v.Category ?? ""
                })
                .OrderBy(v => v.Name)
                .ToList()
                .AsReadOnly();

            lock (_gate)
            {
                _accountVoices = voices;
                _accountVoicesLoadedAt = _time.GetUtcNow();
                _lastFailureAt = null;
            }

            _logger.LogInformation("Found {Count} voices for this account", voices.Count);
            return new AccountVoiceSnapshot(true, voices);
        }
        catch (Exception ex)
        {
            // Reported as not loaded, never as an empty account; see AccountVoiceSnapshot.
            lock (_gate)
            {
                _lastFailureAt = _time.GetUtcNow();
            }
            _logger.LogError(ex, "Failed to fetch the account's voices from ElevenLabs");
            return AccountVoiceSnapshot.Unavailable;
        }
    }

    private List<VoiceInfo> Fallback(string language) =>
        _fallbackVoices.TryGetValue(language, out var fallback) ? fallback.ToList() : new List<VoiceInfo>();

    private static string CapitalizeFirst(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return char.ToUpper(value[0]) + value[1..].ToLower();
    }

    /// <summary>
    /// Builds fallback voice dictionary from config (AI:ElevenLabs:Voices), falling back
    /// to hardcoded defaults for any language not present in config.
    /// </summary>
    private static Dictionary<string, List<VoiceInfo>> BuildFallbackVoices(IConfiguration? configuration)
    {
        var result = new Dictionary<string, List<VoiceInfo>>(DefaultFallbackVoices, StringComparer.OrdinalIgnoreCase);

        var voicesSection = configuration?.GetSection("AI:ElevenLabs:Voices");
        if (voicesSection == null || !voicesSection.Exists())
            return result;

        foreach (var langSection in voicesSection.GetChildren())
        {
            var language = langSection.Key; // e.g. "Korean"
            var langCode = LanguageCodes.TryGetValue(language, out var code) ? code : language.ToLowerInvariant();
            var voices = new List<VoiceInfo>();

            foreach (var voiceEntry in langSection.GetChildren())
            {
                var voiceId = voiceEntry.Value;
                if (!string.IsNullOrWhiteSpace(voiceId))
                {
                    voices.Add(new VoiceInfo
                    {
                        VoiceId = voiceId,
                        Name = voiceEntry.Key,
                        Language = langCode
                    });
                }
            }

            if (voices.Count > 0)
                result[language] = voices;
        }

        return result;
    }
}
