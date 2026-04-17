using Microsoft.Maui.Controls;

namespace Plugin.Maui.HelpKit.Presenters;

/// <summary>
/// Presents the HelpKit overlay in a <see cref="Shell"/>-based host by
/// modal-pushing onto <c>Shell.Current.Navigation</c>.
/// </summary>
public sealed class ShellPresenter : IHelpKitPresenter
{
    public async Task PresentAsync(Page helpPage, CancellationToken ct = default)
    {
        var shell = Shell.Current
            ?? throw new InvalidOperationException(
                "ShellPresenter requires a Shell host. Use WindowPresenter for plain NavigationPage apps, or MauiReactorPresenter for MauiReactor.");

        ct.ThrowIfCancellationRequested();
        await shell.Navigation.PushModalAsync(helpPage, animated: true).ConfigureAwait(false);
    }

    public async Task DismissAsync(CancellationToken ct = default)
    {
        var shell = Shell.Current;
        if (shell?.Navigation.ModalStack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            await shell.Navigation.PopModalAsync(animated: true).ConfigureAwait(false);
        }
    }
}
