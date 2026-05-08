using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api;

/// <summary>
/// DTO returned by GET/PUT profile endpoints. Camel-cased over the wire by the
/// app-wide System.Text.Json defaults — do not override naming policy here.
/// </summary>
public sealed record UserProfileDto(
    string Id,
    string DisplayName,
    string Email,
    string NativeLanguage,
    string TargetLanguage,
    string DisplayLanguage,
    string TargetCefrLevel,
    int PreferredSessionMinutes,
    string? OpenAiApiKey,
    string? ElevenLabsApiKey);

/// <summary>
/// Body for PUT /api/v1/profile/{profileId}.
/// </summary>
public sealed record UpdateUserProfileRequest(
    string DisplayName,
    string Email,
    string NativeLanguage,
    string TargetLanguage,
    string DisplayLanguage,
    string TargetCefrLevel,
    int PreferredSessionMinutes,
    string? OpenAiApiKey,
    string? ElevenLabsApiKey);

public static class ProfileEndpoints
{
    public static WebApplication MapProfileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/profile").RequireAuthorization();

        group.MapGet("/{profileId}", GetProfile);
        group.MapPut("/{profileId}", UpdateProfile);

        return app;
    }

    private static async Task<IResult> GetProfile(
        string profileId,
        ClaimsPrincipal user,
        [FromServices] UserProfileRepository repository)
    {
        var ownership = ResolveOwnership(profileId, user);
        if (ownership is not null) return ownership;

        var profile = (await repository.ListAsync()).FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return Results.NotFound();

        return Results.Ok(MapToDto(profile));
    }

    private static async Task<IResult> UpdateProfile(
        string profileId,
        [FromBody] UpdateUserProfileRequest request,
        ClaimsPrincipal user,
        [FromServices] UserProfileRepository repository)
    {
        var ownership = ResolveOwnership(profileId, user);
        if (ownership is not null) return ownership;

        var profile = (await repository.ListAsync()).FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return Results.NotFound();

        profile.Name = request.DisplayName;
        profile.Email = request.Email;
        profile.NativeLanguage = request.NativeLanguage;
        profile.TargetLanguage = request.TargetLanguage;
        profile.DisplayLanguage = request.DisplayLanguage;
        profile.TargetCEFRLevel = request.TargetCefrLevel;
        profile.PreferredSessionMinutes = request.PreferredSessionMinutes;
        profile.OpenAI_APIKey = request.OpenAiApiKey;
        // ElevenLabsApiKey is accepted in the request for forward-compatibility,
        // but the current UserProfile model does not persist it server-side.
        // It remains a server-config value (ElevenLabsKey in appsettings) for now.

        var saved = await repository.SaveAsync(profile);
        if (saved < 0) return Results.Problem("Failed to save profile.");

        return Results.Ok(MapToDto(profile));
    }

    private static IResult? ResolveOwnership(string profileId, ClaimsPrincipal user)
    {
        var claimProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(claimProfileId))
            return Results.Unauthorized();

        if (!string.Equals(claimProfileId, profileId, StringComparison.Ordinal))
            return Results.Forbid();

        return null;
    }

    private static UserProfileDto MapToDto(UserProfile profile) => new(
        Id: profile.Id,
        DisplayName: profile.Name ?? string.Empty,
        Email: profile.Email ?? string.Empty,
        NativeLanguage: profile.NativeLanguage,
        TargetLanguage: profile.TargetLanguage,
        DisplayLanguage: profile.DisplayLanguage ?? string.Empty,
        TargetCefrLevel: profile.TargetCEFRLevel ?? string.Empty,
        PreferredSessionMinutes: profile.PreferredSessionMinutes,
        OpenAiApiKey: profile.OpenAI_APIKey,
        ElevenLabsApiKey: null);
}
