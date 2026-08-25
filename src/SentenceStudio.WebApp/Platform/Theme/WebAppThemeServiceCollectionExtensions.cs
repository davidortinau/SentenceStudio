using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services.Theme;

namespace SentenceStudio.WebApp.Platform.Theme;

/// <summary>
/// Registers browser-scoped appearance state for the web host.
/// </summary>
/// <remarks>
/// <para>
/// A named method rather than three loose lines in <c>Program.cs</c> so the lifetimes can be
/// asserted directly. The bug this replaces was entirely a lifetime bug — <c>ThemeService</c> was a
/// process-wide singleton shared by every learner on the server — and a regression would look like
/// one word changing in a startup file. A test that reads the real registration catches that; a
/// test that re-declares the registrations it expects does not.
/// </para>
/// </remarks>
public static class WebAppThemeServiceCollectionExtensions
{
    /// <summary>
    /// Adds the per-browser appearance substrate: the cookie channel, the cookie-backed store, and
    /// the presentation state — all scoped, so one Blazor circuit is one browser.
    /// </summary>
    public static IServiceCollection AddBrowserAppearance(this IServiceCollection services)
    {
        // Scoped, all three. A Blazor circuit is a DI scope, so this is what makes one learner's
        // theme, and one learner's ThemeChanged invocation list, belong to one browser.
        services.AddScoped<IAppearanceCookieChannel, JsAppearanceCookieChannel>();
        services.AddScoped<IThemePreferenceStore, BrowserAppearanceCookieStore>();
        services.AddBrowserThemePresentation();
        return services;
    }
}
