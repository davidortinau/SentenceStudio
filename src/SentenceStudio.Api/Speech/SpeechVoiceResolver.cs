using System.Collections.Frozen;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Speech;

/// <summary>
/// Turns the voice a speech request names into the ElevenLabs voice ID the API synthesizes with.
/// </summary>
/// <remarks>
/// <para>
/// Only voices this API already offers are used: the slugs below, the built-in default voices the
/// clients fall back to (<c>SpeechVoicePreferences</c>, <c>VoiceDiscoveryService</c>), the
/// configured <c>AI:ElevenLabs:Voices</c>, and the account voices that
/// <c>GET /api/v1/speech/voices</c> lists. Anything else falls back to the default voice, so a caller
/// cannot send an arbitrary voice ID through the API.
/// </para>
/// <para>
/// The account voices come from <see cref="IVoiceDiscoveryService"/>, which caches each language's
/// list for a day. This resolver keeps its own snapshot for <see cref="AccountVoicesLifetime"/>, so
/// even while that list fails to load the ElevenLabs voice list is asked at most once per lifetime,
/// and only when a request names a voice outside the built-in set.
/// </para>
/// </remarks>
public sealed class SpeechVoiceResolver
{
    /// <summary>Rachel: the API's default voice when <c>AI:ElevenLabs:DefaultVoice</c> is unset.</summary>
    public const string BuiltInDefaultVoiceId = "21m00Tcm4TlvDq8ikWAM";

    /// <summary>How long one snapshot of the account's voice IDs is used.</summary>
    public static TimeSpan AccountVoicesLifetime { get; } = TimeSpan.FromMinutes(10);

    // Friendly slugs used by Flutter and the MAUI/web pages. Mirrors
    // ElevenLabsSpeechService.VoiceOptions in AppLib. Matched exactly, as before.
    private static readonly FrozenDictionary<string, string> Slugs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Korean
        ["yuna"] = "xi3rF0t7dg7uN2M0WUhr",
        ["jiyoung"] = "AW5wrnG1jVizOYY7R1Oo",
        ["hyunbin"] = "s07IwTCOrCDCaETjUVjx",
        ["jennie"] = "z6Kj0hecH20CdetSElRT",
        ["jina"] = "sSoVF9lUgTGJz0Xz3J9y",
        ["dohyeon"] = "FQ3MuLxZh0jHcZmA5vW1",
        ["yohankoo"] = "4JJwo477JUAx3HV0T7n7",
        // English
        ["echo"] = "21m00Tcm4TlvDq8ikWAM",
        ["rachel"] = "21m00Tcm4TlvDq8ikWAM",
        ["onyx"] = "ED0k6LqFEfpMua5GXpMG",
        ["antoni"] = "ED0k6LqFEfpMua5GXpMG",
        ["nova"] = "jsCqWAovK2LkecY7zXl4",
        ["elli"] = "jsCqWAovK2LkecY7zXl4",
        ["shimmer"] = "kgG8YXSrynzpPIncHKrx",
        ["adam"] = "kgG8YXSrynzpPIncHKrx",
        ["fable"] = "5Q0t7uMcjvnagumLfvZi",
        ["dorothy"] = "5Q0t7uMcjvnagumLfvZi",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The multilingual Adam voice: the German, French and Spanish default in
    /// <c>SpeechVoicePreferences</c> and in the voice discovery fallback list.
    /// </summary>
    private const string MultilingualAdam = "pNInz6obpgDQGcFmaJgB";

    private readonly IVoiceDiscoveryService? _discovery;
    private readonly ILogger<SpeechVoiceResolver> _logger;
    private readonly TimeProvider _time;
    private readonly string _fallback;
    private readonly FrozenSet<string> _builtIn;
    private readonly object _gate = new();
    private Task<FrozenSet<string>>? _accountVoices;
    private DateTimeOffset _accountVoicesExpire;

    public SpeechVoiceResolver(
        IConfiguration configuration,
        ILogger<SpeechVoiceResolver> logger,
        IVoiceDiscoveryService? discovery = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _discovery = discovery;
        _time = time ?? TimeProvider.System;
        _fallback = configuration["AI:ElevenLabs:DefaultVoice"] ?? BuiltInDefaultVoiceId;

        var builtIn = new HashSet<string>(Slugs.Values, StringComparer.Ordinal) { MultilingualAdam, _fallback };
        foreach (var language in configuration.GetSection("AI:ElevenLabs:Voices").GetChildren())
        {
            foreach (var voice in language.GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(voice.Value))
                    builtIn.Add(voice.Value.Trim());
            }
        }

        _builtIn = builtIn.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>The ElevenLabs voice ID for <paramref name="requested"/>; the default voice when it is not one this API offers.</summary>
    public async Task<string> ResolveAsync(string? requested, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return _fallback;

        if (Slugs.TryGetValue(requested, out var slugVoice))
            return slugVoice;

        if (_builtIn.Contains(requested))
            return requested;

        if (_discovery is not null
            && (await AccountVoiceIdsAsync().WaitAsync(cancellationToken)).Contains(requested))
            return requested;

        // The voice field is not learner content, but a caller controls it: only its length is logged.
        _logger.LogInformation(
            "Speech voice is not one this API offers; using the default voice. RequestedVoiceLength={RequestedVoiceLength}",
            requested.Length);
        return _fallback;
    }

    /// <summary>One shared load per lifetime: concurrent requests wait on the same task.</summary>
    private Task<FrozenSet<string>> AccountVoiceIdsAsync()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_accountVoices is null || now >= _accountVoicesExpire)
            {
                _accountVoices = LoadAccountVoiceIdsAsync();
                _accountVoicesExpire = now + AccountVoicesLifetime;
            }

            return _accountVoices;
        }
    }

    private async Task<FrozenSet<string>> LoadAccountVoiceIdsAsync()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // VoiceDiscoveryService is not safe for concurrent use, so the languages are read one
            // at a time. Each list is cached by the service, the same cache the voices endpoint uses.
            foreach (var language in _discovery!.SupportedLanguages)
            {
                foreach (var voice in await _discovery.GetVoicesForLanguageAsync(language))
                {
                    if (!string.IsNullOrWhiteSpace(voice.VoiceId))
                        ids.Add(voice.VoiceId);
                }
            }
        }
        catch (Exception ex)
        {
            // The request still synthesizes: a voice that cannot be checked gets the default voice,
            // exactly as an unknown voice does. The snapshot expires and is loaded again.
            _logger.LogWarning(ex, "Speech voice list could not be loaded; only built-in voices are accepted until it is retried.");
        }

        return ids.ToFrozenSet(StringComparer.Ordinal);
    }
}
