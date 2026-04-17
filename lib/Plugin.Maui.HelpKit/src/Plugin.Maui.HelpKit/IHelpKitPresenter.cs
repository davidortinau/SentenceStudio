using Microsoft.Maui.Controls;

namespace Plugin.Maui.HelpKit;

/// <summary>
/// Abstraction over how the HelpKit overlay gets on screen. Three concrete
/// implementations ship in Alpha:
/// <list type="bullet">
///   <item><see cref="Presenters.ShellPresenter"/> — for Shell apps.</item>
///   <item><see cref="Presenters.WindowPresenter"/> — for plain NavigationPage hosts.</item>
///   <item><see cref="Presenters.MauiReactorPresenter"/> — for MauiReactor apps.</item>
/// </list>
/// Consumers can register their own implementation to override resolution.
/// </summary>
public interface IHelpKitPresenter
{
    /// <summary>Presents <paramref name="helpPage"/> modally.</summary>
    Task PresentAsync(Page helpPage, CancellationToken ct = default);

    /// <summary>Dismisses the currently presented help page, if any.</summary>
    Task DismissAsync(CancellationToken ct = default);
}
