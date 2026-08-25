using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SentenceStudio.Services.Theme;

/// <summary>
/// Registration for the appearance presentation state. Deliberately <b>not</b> part of
/// <c>AddSentenceStudioCoreServices</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ThemeService"/> used to be registered by the shared core extension as a singleton,
/// which was correct for exactly one of the two hosts. On MAUI a singleton is the device. On the
/// web a singleton is the whole server: every learner shared one tuple and one event invocation
/// list, so a theme change made by one person moved and repainted everyone else's browser.
/// </para>
/// <para>
/// A shared extension method cannot make that choice on the caller's behalf, so it no longer tries.
/// Each host names its own semantics at registration time, and the name says which one it means.
/// </para>
/// </remarks>
public static class ThemePresentationServiceCollectionExtensions
{
    /// <summary>
    /// Registers device-scoped appearance state for the MAUI heads: one tuple per installation,
    /// persisted in platform preferences. Singleton is correct here because the process is the
    /// device.
    /// </summary>
    public static IServiceCollection AddDeviceThemePresentation(this IServiceCollection services)
    {
        services.TryAddSingleton<IThemePreferenceStore, DevicePreferencesThemeStore>();
        services.TryAddSingleton<ThemeService>();
        return services;
    }

    /// <summary>
    /// Registers browser-scoped appearance state for the web host: one tuple per Blazor circuit /
    /// per request scope, persisted in a per-browser cookie.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <remarks>
    /// The caller must also register an <see cref="IThemePreferenceStore"/> with a scoped lifetime —
    /// the web host registers its cookie-backed store. This overload only fixes the lifetime of the
    /// presentation state itself, which is the part that must never be shared between learners.
    /// </remarks>
    public static IServiceCollection AddBrowserThemePresentation(this IServiceCollection services)
    {
        services.TryAddScoped<ThemeService>();
        return services;
    }
}
