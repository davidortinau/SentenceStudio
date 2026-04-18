using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio;

/// <summary>
/// MAUI startup service: reads the saved UserProfile.DisplayLanguage and applies it
/// to LocalizationManager + process-wide culture so the client launches in the user's
/// preferred language. Safe on MAUI only — do not register in the WebApp (multi-user).
/// </summary>
public sealed class LocalizationInitializer : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<LocalizationInitializer>();
        try
        {
            var repo = services.GetService<UserProfileRepository>();
            if (repo is null)
            {
                logger?.LogDebug("LocalizationInitializer: UserProfileRepository not registered; skipping.");
                return;
            }

            // GetAsync is async; block on startup — fast local DB read, acceptable for single-user MAUI boot.
            var profile = Task.Run(async () => await repo.GetAsync()).GetAwaiter().GetResult();
            var culture = profile?.DisplayCulture;
            if (string.IsNullOrWhiteSpace(culture))
            {
                logger?.LogDebug("LocalizationInitializer: no saved DisplayLanguage; using OS culture {Culture}", CultureInfo.CurrentUICulture.Name);
                return;
            }

            LocalizationManager.Instance.SetCulture(new CultureInfo(culture));
            logger?.LogInformation("LocalizationInitializer: applied saved culture {Culture}", culture);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "LocalizationInitializer: failed to apply saved culture; continuing with defaults.");
        }
    }
}
