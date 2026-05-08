using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
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
    /// Returns voices for the requested BCP-47 language. Unknown or missing
    /// language tags currently fall through to the service default (Korean) —
    /// callers should always supply a tag for predictable results.
    /// </summary>
    private static async Task<IResult> GetVoices(
        [FromQuery] string? language,
        ClaimsPrincipal user,
        [FromServices] IVoiceDiscoveryService voiceService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var label = ResolveLanguageLabel(language);
        var voices = await voiceService.GetVoicesForLanguageAsync(label);

        var dtos = voices
            .Select(v => new VoiceDto(
                Id: v.VoiceId,
                Name: v.Name,
                Language: v.Language,
                Gender: string.IsNullOrWhiteSpace(v.Gender) ? null : v.Gender,
                Accent: string.IsNullOrWhiteSpace(v.Accent) ? null : v.Accent))
            .ToList();

        return Results.Ok(new VoiceListResponse(dtos));
    }

    private static string ResolveLanguageLabel(string? bcp47)
    {
        if (string.IsNullOrWhiteSpace(bcp47))
            return "Korean";

        // Accept full tags like "ko-KR" by reducing to the primary subtag.
        var primary = bcp47.Split('-')[0];
        return Bcp47ToLabel.TryGetValue(primary, out var label) ? label : bcp47;
    }
}
