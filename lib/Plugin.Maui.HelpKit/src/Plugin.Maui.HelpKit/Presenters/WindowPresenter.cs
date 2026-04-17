using Microsoft.Maui.Controls;

namespace Plugin.Maui.HelpKit.Presenters;

/// <summary>
/// Presents the HelpKit overlay in a plain <see cref="NavigationPage"/> host
/// by modal-pushing onto the active window's navigation stack.
/// </summary>
public sealed class WindowPresenter : IHelpKitPresenter
{
    public async Task PresentAsync(Page helpPage, CancellationToken ct = default)
    {
        var navigation = ResolveNavigation();
        ct.ThrowIfCancellationRequested();
        await navigation.PushModalAsync(helpPage, animated: true).ConfigureAwait(false);
    }

    public async Task DismissAsync(CancellationToken ct = default)
    {
        var navigation = TryResolveNavigation();
        if (navigation?.ModalStack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            await navigation.PopModalAsync(animated: true).ConfigureAwait(false);
        }
    }

    private static INavigation ResolveNavigation()
        => TryResolveNavigation()
           ?? throw new InvalidOperationException(
               "WindowPresenter could not locate an active window / root page. " +
               "Ensure the app window has been created before calling IHelpKit.ShowAsync().");

    private static INavigation? TryResolveNavigation()
    {
        var app = Application.Current;
        if (app is null) return null;

        // MAUI 9+/11 multi-window path.
        var windows = app.Windows;
        if (windows is null || windows.Count == 0) return null;

        var page = windows[0].Page;
        return page?.Navigation;
    }
}
