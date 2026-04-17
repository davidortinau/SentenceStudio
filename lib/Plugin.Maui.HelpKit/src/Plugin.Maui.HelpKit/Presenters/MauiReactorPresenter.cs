using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;

namespace Plugin.Maui.HelpKit.Presenters;

/// <summary>
/// Presenter used by MauiReactor hosts. Reactor apps may or may not use
/// <see cref="Shell"/>; this presenter tries Shell first, then falls back to
/// the plain <see cref="WindowPresenter"/> path.
/// </summary>
/// <remarks>
/// MauiReactor ultimately materialises plain MAUI pages, so the native
/// navigation stack is the same one a plain-MAUI app exposes. We therefore
/// delegate to either <see cref="ShellPresenter"/> (when a Shell is active)
/// or <see cref="WindowPresenter"/> (plain NavigationPage / multi-window),
/// with a runtime fall-through if Shell throws unexpectedly.
/// </remarks>
public sealed class MauiReactorPresenter : IHelpKitPresenter
{
    private readonly ShellPresenter _shell;
    private readonly WindowPresenter _window;
    private readonly ILogger<MauiReactorPresenter>? _logger;

    public MauiReactorPresenter(
        ShellPresenter shell,
        WindowPresenter window,
        ILogger<MauiReactorPresenter>? logger = null)
    {
        _shell = shell;
        _window = window;
        _logger = logger;
    }

    public async Task PresentAsync(Page helpPage, CancellationToken ct = default)
    {
        if (Shell.Current is not null)
        {
            try
            {
                _logger?.LogDebug("MauiReactorPresenter: routing through ShellPresenter.");
                await _shell.PresentAsync(helpPage, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ShellPresenter failed in a MauiReactor host; falling back to WindowPresenter.");
            }
        }

        _logger?.LogDebug("MauiReactorPresenter: routing through WindowPresenter.");
        await _window.PresentAsync(helpPage, ct).ConfigureAwait(false);
    }

    public async Task DismissAsync(CancellationToken ct = default)
    {
        if (Shell.Current is not null)
        {
            try
            {
                await _shell.DismissAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ShellPresenter dismiss failed; falling back to WindowPresenter.");
            }
        }

        await _window.DismissAsync(ct).ConfigureAwait(false);
    }
}
