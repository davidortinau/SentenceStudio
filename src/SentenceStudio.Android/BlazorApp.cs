using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using SentenceStudio.Services;

namespace SentenceStudio;

public class BlazorApp : Application
{
    public BlazorApp()
    {
        MainPage = new BlazorHostPage();
    }

    protected override void OnResume()
    {
        base.OnResume();

        _ = Task.Run(async () =>
        {
            try
            {
                var authService = Handler?.MauiContext?.Services
                    ?.GetService<IAuthService>();

                if (authService is not null && !authService.IsSignedIn)
                {
                    var result = await authService.SignInAsync();
                    var logger = Handler?.MauiContext?.Services?.GetService<ILogger<BlazorApp>>();
                    if (result is not null)
                        logger?.LogInformation("OnResume: Token refresh succeeded");
                    else
                        logger?.LogWarning("OnResume: Token refresh returned null (will retry on next access)");
                }
            }
            catch (Exception ex)
            {
                var logger = Handler?.MauiContext?.Services?.GetService<ILogger<BlazorApp>>();
                logger?.LogWarning(ex, "OnResume: Failed to refresh auth tokens");
            }
        });
    }
}
