using System.Collections.Frozen;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Speech;

/// <summary>The voice a speech request is synthesized with.</summary>
/// <param name="VoiceId">The ElevenLabs voice ID used.</param>
/// <param name="Substituted">
/// True when the request named a voice and the API's default voice is used instead. Clients must not
/// keep such a clip under the voice they asked for (it is returned to them as <c>VoiceSubstituted</c>).
/// </param>
public sealed record ResolvedSpeechVoice(string VoiceId, bool Substituted);

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
/// The account voices come straight from <see cref="IAccountVoiceSource"/>, the voices endpoint's
/// <c>VoiceDiscoveryService</c>, which alone owns their lifetime: one ElevenLabs call per load,
/// shared by concurrent requests, the same list <c>/speech/voices</c> shows, and a retry back-off
/// after a failed load. This class keeps no copy. Only requests naming a voice outside the
/// built-in set ask for the list.
/// </para>
/// </remarks>
public sealed class SpeechVoiceResolver
{
    /// <summary>Rachel: the API's default voice when <c>AI:ElevenLabs:DefaultVoice</c> is unset.</summary>
    public const string BuiltInDefaultVoiceId = "21m00Tcm4TlvDq8ikWAM";

    // Friendly slugs used by Flutter and the MAUI/web pages. Matched exactly, as before.
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

    private readonly IAccountVoiceSource? _accountVoices;
    private readonly ILogger<SpeechVoiceResolver> _logger;
    private readonly string _fallback;
    private readonly FrozenSet<string> _builtIn;

    public SpeechVoiceResolver(
        IConfiguration configuration,
        ILogger<SpeechVoiceResolver> logger,
        IAccountVoiceSource? accountVoices = null)
    {
        _logger = logger;
        _accountVoices = accountVoices;
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

    /// <summary>The voice for <paramref name="requested"/>; the default voice when it is not one this API offers.</summary>
    public async Task<ResolvedSpeechVoice> ResolveAsync(string? requested, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return new(_fallback, Substituted: false);

        if (Slugs.TryGetValue(requested, out var slugVoice))
            return new(slugVoice, Substituted: false);

        if (_builtIn.Contains(requested))
            return new(requested, Substituted: false);

        var account = _accountVoices is null ? null : await AccountVoicesAsync(cancellationToken);
        if (account is { Loaded: true } && account.Voices.Any(voice =>
                string.Equals(voice.VoiceId, requested, StringComparison.Ordinal)))
            return new(requested, Substituted: false);

        // The voice field is not learner content, but a caller controls it: only its length is logged.
        if (account is { Loaded: false })
        {
            _logger.LogWarning(
                "Speech voice could not be checked (the account voice list is unavailable); using the default voice. RequestedVoiceLength={RequestedVoiceLength}",
                requested.Length);
        }
        else
        {
            _logger.LogInformation(
                "Speech voice is not one this API offers; using the default voice. RequestedVoiceLength={RequestedVoiceLength}",
                requested.Length);
        }

        return new(_fallback, Substituted: true);
    }

    /// <summary>The account's voices from the source; a source that throws counts as not loaded.</summary>
    private async Task<AccountVoiceSnapshot> AccountVoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _accountVoices!.GetAccountVoicesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Speech voice list could not be loaded.");
            return AccountVoiceSnapshot.Unavailable;
        }
    }
}
