using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace Plugin.Maui.HelpKit.Presenters;

/// <summary>
/// Runtime presenter resolver. Invoked by DI when no explicit
/// <see cref="IHelpKitPresenter"/> has been registered. The selection is
/// made lazily (per resolution) so apps that construct their Shell after
/// DI build still work.
/// </summary>
internal static class DefaultPresenterSelector
{
    public static IHelpKitPresenter Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // MauiReactor hosts and most Shell apps are indistinguishable to the
        // library at this level — they all ride on top of the same MAUI
        // primitives. Prefer Shell when one exists; otherwise fall through
        // to the window-level navigation path.
        if (Shell.Current is not null)
            return services.GetRequiredService<ShellPresenter>();

        var app = Application.Current;
        if (app?.Windows is { Count: > 0 })
            return services.GetRequiredService<WindowPresenter>();

        throw new InvalidOperationException(
            "HelpKit could not select a presenter automatically: no Shell is active and no window exists yet. " +
            "Call IHelpKit.ShowAsync() after the app window is created, or register your own IHelpKitPresenter via AddHelpKit().");
    }
}
