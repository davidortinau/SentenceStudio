using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio;

public static class LocaleProfileApplication
{
    private static readonly string[] SupportedCultures = { "en", "ko" };

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

        var normalized = culture.Split('-')[0].ToLowerInvariant();
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
