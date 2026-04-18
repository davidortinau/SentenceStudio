using Microsoft.Extensions.DependencyInjection;

namespace SentenceStudio.WebUI.Services;

public static class BlazorUIServiceExtensions
{
    /// <summary>
    /// Registers the shared Blazor UI services used by both MAUI Hybrid and server-side Blazor hosts.
    /// </summary>
    public static IServiceCollection AddBlazorUIServices(this IServiceCollection services)
    {
        services.AddSingleton<ToastService>();
        services.AddSingleton<ModalService>();
        services.AddScoped<BlazorLocalizationService>();
        services.AddSingleton<BlazorNavigationService>();
        services.AddScoped<NavigationMemoryService>();
        services.AddScoped<JsInteropService>();
        services.AddSingleton<SentenceStudio.Services.Timer.IActivityTimerService, SentenceStudio.Services.Timer.ActivityTimerService>();
        return services;
    }
}
