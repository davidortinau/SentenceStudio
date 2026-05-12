using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api;

/// <summary>
/// DTO returned by GET/PUT profile endpoints. Camel-cased over the wire by the
/// app-wide System.Text.Json defaults — do not override naming policy here.
/// </summary>
public sealed record ProfileDto(
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
/// Body for PUT /api/v1/profile/{profileId}. Only DisplayName and
/// PreferredSessionMinutes are required; the remaining fields preserve the
/// existing profile values when omitted.
/// </summary>
public sealed record UpdateProfileRequest(
    string DisplayName,
    string? Email,
    string? NativeLanguage,
    string? TargetLanguage,
    string? DisplayLanguage,
    string? TargetCefrLevel,
    int PreferredSessionMinutes,
    string? OpenAiApiKey,
    string? ElevenLabsApiKey);

public static class ProfileEndpoints
{
    private const int MaxDisplayNameLength = 200;
    private const int MaxLanguageLength = 50;
    private const int MinPreferredSessionMinutes = 1;
    private const int MaxPreferredSessionMinutes = 480;

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
        [FromServices] UserProfileRepository repository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ProfileEndpoints");

        var ownership = ResolveOwnership(profileId, user);
        if (ownership is not null) return ownership;

        var profile = await repository.GetByIdAsync(profileId, cancellationToken);
        if (profile is null)
        {
            logger.LogWarning("Profile {ProfileId} not found", profileId);
            return Results.NotFound();
        }

        return Results.Ok(MapToDto(profile));
    }

    private static async Task<IResult> UpdateProfile(
        string profileId,
        [FromBody] UpdateProfileRequest request,
        ClaimsPrincipal user,
        [FromServices] UserProfileRepository repository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ProfileEndpoints");

        // Ownership first, then existence. Checking existence first would leak
        // whether arbitrary profileIds exist on the server (404 vs 403 split
        // against the same probed id) — the caller can never legitimately
        // target a profile other than their own, so 403 should win immediately.
        var ownership = ResolveOwnership(profileId, user);
        if (ownership is not null)
        {
            logger.LogWarning("UpdateProfile: ownership rejected for profile {ProfileId}", profileId);
            return ownership;
        }

        var profile = await repository.GetByIdAsync(profileId, cancellationToken);
        if (profile is null)
        {
            logger.LogWarning("UpdateProfile: profile {ProfileId} not found", profileId);
            return Results.NotFound();
        }

        var validationErrors = ValidateUpdateRequest(request);
        if (validationErrors.Count > 0)
        {
            logger.LogInformation(
                "UpdateProfile: validation failed for profile {ProfileId} ({ErrorCount} errors)",
                profileId, validationErrors.Count);
            return TypedResults.ValidationProblem(validationErrors);
        }

        profile.Name = request.DisplayName.Trim();
        profile.Email = request.Email?.Trim() ?? string.Empty;
        profile.NativeLanguage = request.NativeLanguage?.Trim() ?? profile.NativeLanguage;
        profile.TargetLanguage = request.TargetLanguage?.Trim() ?? profile.TargetLanguage;
        profile.DisplayLanguage = request.DisplayLanguage?.Trim() ?? profile.DisplayLanguage;
        profile.TargetCEFRLevel = request.TargetCefrLevel?.Trim() ?? profile.TargetCEFRLevel;
        profile.PreferredSessionMinutes = request.PreferredSessionMinutes;
        profile.OpenAI_APIKey = request.OpenAiApiKey;
        // ElevenLabsApiKey: accepted in the request body for forward-compatibility,
        // but the current UserProfile model has no column for it, so the value is
        // discarded here. The DTO returned to clients always reports null until a
        // future migration adds the column. The server-config ElevenLabsKey in
        // appsettings.json remains the authoritative key for now.

        var saved = await repository.SaveAsync(profile);
        if (saved < 0)
        {
            logger.LogError(
                "UpdateProfile: SaveAsync failed for profile {ProfileId}", profileId);
            return TypedResults.Problem(
                title: "Save failed",
                detail: "Unable to save profile changes. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // Reload after save so the response reflects whatever EF / DB defaults
        // resolved during persistence — defends against a future computed column.
        var reloaded = await repository.GetByIdAsync(profileId, cancellationToken) ?? profile;
        logger.LogInformation("UpdateProfile: profile {ProfileId} updated", profileId);
        return Results.Ok(MapToDto(reloaded));
    }

    private static IResult? ResolveOwnership(string profileId, ClaimsPrincipal user)
    {
        var claimProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(claimProfileId))
            return Results.Unauthorized();

        if (!string.Equals(claimProfileId, profileId, StringComparison.Ordinal))
            return Results.Forbid();

        return null;
    }

    private static Dictionary<string, string[]> ValidateUpdateRequest(UpdateProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors[nameof(request.DisplayName)] = new[] { "Display name is required." };
        }
        else if (request.DisplayName.Trim().Length > MaxDisplayNameLength)
        {
            errors[nameof(request.DisplayName)] = new[]
            {
                $"Display name must be {MaxDisplayNameLength} characters or fewer."
            };
        }

        if (!string.IsNullOrWhiteSpace(request.NativeLanguage)
            && request.NativeLanguage.Length > MaxLanguageLength)
        {
            errors[nameof(request.NativeLanguage)] = new[]
            {
                $"Native language must be {MaxLanguageLength} characters or fewer."
            };
        }

        if (!string.IsNullOrWhiteSpace(request.TargetLanguage)
            && request.TargetLanguage.Length > MaxLanguageLength)
        {
            errors[nameof(request.TargetLanguage)] = new[]
            {
                $"Target language must be {MaxLanguageLength} characters or fewer."
            };
        }

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var trimmed = request.Email.Trim();
            if (!IsStrictlyValidEmail(trimmed))
            {
                errors[nameof(request.Email)] = new[] { "Email is not a valid address." };
            }
        }

        if (request.PreferredSessionMinutes < MinPreferredSessionMinutes
            || request.PreferredSessionMinutes > MaxPreferredSessionMinutes)
        {
            errors[nameof(request.PreferredSessionMinutes)] = new[]
            {
                $"Preferred session minutes must be between {MinPreferredSessionMinutes} and {MaxPreferredSessionMinutes}."
            };
        }

        return errors;
    }

    private static ProfileDto MapToDto(UserProfile profile) => new(
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

    // Stricter email check than EmailAddressAttribute (which accepts e.g. "missing@tld"
    // and other inputs without a dotted domain). Requires a non-empty local part with
    // no whitespace, and a domain with at least one dot.
    private static bool IsStrictlyValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!new EmailAddressAttribute().IsValid(value)) return false;

        var atIndex = value.IndexOf('@');
        if (atIndex <= 0 || atIndex == value.Length - 1) return false;

        var local = value[..atIndex];
        var domain = value[(atIndex + 1)..];

        if (local.Length == 0 || domain.Length == 0) return false;
        if (local.Any(char.IsWhiteSpace) || domain.Any(char.IsWhiteSpace)) return false;
        if (!domain.Contains('.')) return false;

        var labels = domain.Split('.');
        if (labels.Any(string.IsNullOrEmpty)) return false;

        return true;
    }
}
