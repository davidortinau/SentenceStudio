using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api;

public sealed record VoiceDto(
    string Id,
    string Name,
    string Language,
    string? Gender,
    string? Accent);

public sealed record VoiceListResponse(IReadOnlyList<VoiceDto> Voices);

public static class SpeechEndpoints
{
    /// <summary>
    /// Maps BCP-47 language tags to the human-readable labels expected by
    /// <see cref="IVoiceDiscoveryService"/>. The mapping lives at the endpoint
    /// boundary so the service remains unchanged.
    /// </summary>
    private static readonly Dictionary<string, string> Bcp47ToLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        { "en", "English" },
        { "fr", "French" },
        { "de", "German" },
        { "ko", "Korean" },
        { "es", "Spanish" }
    };

    public static WebApplication MapSpeechEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/speech").RequireAuthorization();

        group.MapGet("/voices", GetVoices);

        return app;
    }

    /// <summary>
    /// GET /api/v1/speech/voices?language={bcp47}.
    /// Returns voices for the requested BCP-47 language. When no language is
    /// supplied, falls back to the authenticated user's <c>TargetLanguage</c>.
    /// If neither a query string language nor a profile target language can
    /// be resolved, returns 400 so the client must explicitly specify one.
    /// </summary>
    private static async Task<IResult> GetVoices(
        [FromQuery] string? language,
        ClaimsPrincipal user,
        [FromServices] IVoiceDiscoveryService voiceService,
        [FromServices] UserProfileRepository profileRepository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SpeechEndpoints");

        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var label = await ResolveLanguageLabelAsync(
            language, userProfileId, profileRepository, cancellationToken);

        if (label is null)
        {
            logger.LogWarning(
                "GetVoices: unable to resolve language for user {UserProfileId} (no query, no profile target)",
                userProfileId);
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["language"] = new[]
                {
                    "Unable to determine target language. Pass ?language={bcp47} or set a target language on the user profile."
                }
            });
        }

        logger.LogInformation(
            "GetVoices: fetching voices for user {UserProfileId} with language label {LanguageLabel}",
            userProfileId, label);

        var voices = await voiceService.GetVoicesForLanguageAsync(label);

        var dtos = voices
            .Select(v => new VoiceDto(
                Id: v.VoiceId,
                Name: v.Name,
                Language: v.Language,
                Gender: string.IsNullOrWhiteSpace(v.Gender) ? null : v.Gender.Trim(),
                Accent: string.IsNullOrWhiteSpace(v.Accent) ? null : v.Accent.Trim()))
            .ToList();

        return Results.Ok(new VoiceListResponse(dtos));
    }

    /// <summary>
    /// Resolves the language label used by <see cref="IVoiceDiscoveryService"/>.
    /// Priority: explicit query string -> user profile target language -> null
    /// (caller returns 400). Returning null is the signal that the request lacks
    /// enough information to answer.
    /// </summary>
    private static async Task<string?> ResolveLanguageLabelAsync(
        string? bcp47,
        string userProfileId,
        UserProfileRepository profileRepository,
        CancellationToken cancellationToken)
    {
        var fromQuery = MapLabel(bcp47);
        if (fromQuery is not null) return fromQuery;

        var profile = await profileRepository.GetByIdAsync(userProfileId, cancellationToken);
        var fromProfile = MapLabel(profile?.TargetLanguage);
        return fromProfile;
    }

    /// <summary>
    /// Maps either a BCP-47 tag (e.g. "ko" or "ko-KR") or a human-readable
    /// label (e.g. "Korean") to the label expected by the discovery service.
    /// Returns null for null/empty input.
    /// </summary>
    private static string? MapLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();

        // If the caller already supplied a known label, pass it through.
        if (Bcp47ToLabel.Values.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            return trimmed;

        // Otherwise treat as BCP-47 (accept full tags like "ko-KR").
        var primary = trimmed.Split('-')[0];
        return Bcp47ToLabel.TryGetValue(primary, out var label) ? label : trimmed;
    }
}
