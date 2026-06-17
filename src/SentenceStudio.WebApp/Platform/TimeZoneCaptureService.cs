using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.WebApp.Platform;

/// <summary>
/// Service to capture and persist the user's browser timezone (IANA id) to
/// their <c>UserProfile.IanaTimeZoneId</c>. Called from the Blazor circuit
/// after JS interop retrieves <c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>.
///
/// Multi-tenant safe: requires an explicit <c>userProfileId</c> parameter.
/// Empty/null userId is a no-op (returns false, never writes).
/// </summary>
public sealed class TimeZoneCaptureService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TimeZoneCaptureService> _logger;

    public TimeZoneCaptureService(
        IServiceProvider serviceProvider,
        ILogger<TimeZoneCaptureService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Persists the browser-reported IANA timezone to the user's profile.
    /// Returns <c>true</c> if the value was written (new or changed).
    /// Returns <c>false</c> if the value was already current or the input was invalid.
    /// </summary>
    /// <param name="userProfileId">The authenticated user's profile id (multi-tenant scoping).</param>
    /// <param name="ianaTimeZoneId">IANA timezone id from browser (e.g. "America/Chicago").</param>
    public async Task<bool> CaptureAsync(string? userProfileId, string? ianaTimeZoneId)
    {
        if (string.IsNullOrEmpty(userProfileId))
        {
            _logger.LogWarning("TimeZoneCaptureService.CaptureAsync called with no userProfileId — refusing to write");
            return false;
        }

        if (string.IsNullOrWhiteSpace(ianaTimeZoneId))
        {
            _logger.LogDebug("TimeZoneCaptureService.CaptureAsync: empty ianaTimeZoneId — no-op");
            return false;
        }

        // Validate the timezone is recognized
        if (!SentenceStudio.Services.Plans.TimeZoneResolver.TryResolve(ianaTimeZoneId, out _))
        {
            _logger.LogWarning(
                "TimeZoneCaptureService.CaptureAsync: unrecognized timezone '{TzId}' for user '{UserId}' — not persisting",
                ianaTimeZoneId, userProfileId);
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == userProfileId);
        if (profile is null)
        {
            _logger.LogWarning(
                "TimeZoneCaptureService.CaptureAsync: UserProfile '{UserId}' not found — cannot persist timezone",
                userProfileId);
            return false;
        }

        if (string.Equals(profile.IanaTimeZoneId, ianaTimeZoneId, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "TimeZoneCaptureService.CaptureAsync: timezone unchanged for user '{UserId}' — no-op",
                userProfileId);
            return false;
        }

        var previous = profile.IanaTimeZoneId;
        profile.IanaTimeZoneId = ianaTimeZoneId;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "TimeZoneCaptureService: persisted timezone '{NewTz}' for user '{UserId}' (was '{OldTz}')",
            ianaTimeZoneId, userProfileId, previous ?? "(null)");
        return true;
    }
}
