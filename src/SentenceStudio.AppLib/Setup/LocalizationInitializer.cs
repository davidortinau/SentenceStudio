using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;

namespace SentenceStudio;

/// <summary>
/// MAUI startup service: reads the saved UserProfile.DisplayLanguage and applies it
/// to LocalizationManager + process-wide culture so the client launches in the user's
/// preferred language. Safe on MAUI only — do not register in the WebApp (multi-user).
/// 
/// ONLY applies locale if active_profile_id is already set in preferences (user has
/// logged in before). For fresh login flow, locale is applied in IdentityAuthService
/// after SignInAsync sets the preference.
/// </summary>
public sealed class LocalizationInitializer : IMauiInitializeService
{
    private static readonly string[] SupportedCultures = { "en", "ko" };

    public void Initialize(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<LocalizationInitializer>();
        try
        {
            var preferences = services.GetService<IPreferencesService>();
            var repo = services.GetService<UserProfileRepository>();
            
            if (repo is null)
            {
                logger?.LogDebug("LocalizationInitializer: UserProfileRepository not registered; skipping.");
                return;
            }

            if (preferences is null)
            {
                logger?.LogDebug("LocalizationInitializer: IPreferencesService not registered; skipping.");
                return;
            }

            // CRITICAL: Only apply locale if active_profile_id is already set.
            // If not set, we're in fresh-launch state before login and GetAsync would
            // return the first profile by DB order (wrong user). Let SignInAsync handle it.
            var activeId = preferences.Get("active_profile_id", string.Empty);
            if (string.IsNullOrEmpty(activeId))
            {
                logger?.LogDebug("LocalizationInitializer: no active_profile_id set; skipping boot-time locale (will apply after login).");
                return;
            }

            // GetAsync is async; block on startup — fast local DB read, acceptable for single-user MAUI boot.
            var profile = Task.Run(async () => await repo.GetAsync()).GetAwaiter().GetResult();
            
            if (profile is null)
            {
                logger?.LogWarning("LocalizationInitializer: active_profile_id {ProfileId} set but no profile found in DB.", activeId);
                return;
            }

            if (profile.Id != activeId)
            {
                logger?.LogWarning("LocalizationInitializer: expected active profile {ExpectedId} but GetAsync returned {ActualId}.", 
                    activeId, profile.Id);
            }

            ApplyLocaleFromProfile(profile, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "LocalizationInitializer: failed to apply saved culture; continuing with defaults.");
        }
    }

    /// <summary>
    /// Applies the saved locale from a UserProfile to the process-wide culture.
    /// Shared helper used by LocalizationInitializer (boot-time) and IdentityAuthService (post-login).
    /// </summary>
    public static void ApplyLocaleFromProfile(UserProfile? profile, ILogger? logger = null)
    {
        if (profile is null)
        {
            logger?.LogDebug("ApplyLocaleFromProfile: null profile, skipping.");
            return;
        }

        var culture = profile.DisplayCulture;
        if (string.IsNullOrWhiteSpace(culture))
        {
            logger?.LogDebug("ApplyLocaleFromProfile: profile {ProfileId} has no DisplayLanguage; using OS culture {Culture}", 
                profile.Id, CultureInfo.CurrentUICulture.Name);
            return;
        }

        // Validate against supported cultures (en, ko)
        var normalized = culture.Split('-')[0].ToLowerInvariant(); // "ko-KR" → "ko"
        if (!SupportedCultures.Contains(normalized))
        {
            logger?.LogWarning("ApplyLocaleFromProfile: unsupported culture {Culture} for profile {ProfileId}; using OS culture.", 
                culture, profile.Id);
            return;
        }

        try
        {
            LocalizationManager.Instance.SetCulture(new CultureInfo(normalized));
            logger?.LogInformation("ApplyLocaleFromProfile: applied culture {Culture} for profile {ProfileId}", 
                normalized, profile.Id);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "ApplyLocaleFromProfile: failed to set culture {Culture} for profile {ProfileId}", 
                culture, profile.Id);
        }
    }
}
