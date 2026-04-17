using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;

namespace Plugin.Maui.HelpKit.ShellIntegration;

/// <summary>
/// Runs once at app ready and, when the host opted in via
/// <c>AddHelpKitShellFlyout()</c>, appends a Help menu item to the current
/// Shell. If the host is not Shell-based, this is a logged no-op.
/// </summary>
internal sealed class HelpKitShellFlyoutInitializer : IMauiInitializeService
{
    private readonly IOptions<HelpKitShellFlyoutOptions> _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<HelpKitShellFlyoutInitializer>? _logger;

    public HelpKitShellFlyoutInitializer(
        IOptions<HelpKitShellFlyoutOptions> options,
        IServiceProvider services,
        ILogger<HelpKitShellFlyoutInitializer>? logger = null)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    public void Initialize(IServiceProvider services)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
            return;

        // Shell isn't present until after Application.Current fires
        // OnStart/MainPage-set, so defer to first frame via the dispatcher.
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => TryAttach(opts));
    }

    private void TryAttach(HelpKitShellFlyoutOptions opts)
    {
        var shell = Shell.Current;
        if (shell is null)
        {
            _logger?.LogWarning(
                "AddHelpKitShellFlyout() was called but the app is not Shell-based; skipping flyout injection. " +
                "Use IHelpKit.ShowAsync() from your own UI instead.");
            return;
        }

        var menuItem = new MenuItem
        {
            Text = opts.Title,
        };

        if (!string.IsNullOrWhiteSpace(opts.Icon))
            menuItem.IconImageSource = opts.Icon;

        menuItem.Clicked += async (_, _) =>
        {
            try
            {
                var help = _services.GetService(typeof(IHelpKit)) as IHelpKit;
                if (help is null)
                {
                    _logger?.LogError("IHelpKit not registered; cannot open help from flyout.");
                    return;
                }
                await help.ShowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Opening HelpKit from Shell flyout failed.");
            }
        };

        try
        {
            // MenuShellItem is the public wrapper MAUI uses when XAML parses
            // a MenuItem as a direct Shell child. Using it from code gives
            // us the same UX.
            shell.Items.Add(new MenuShellItem(menuItem));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to append HelpKit flyout item to Shell.Items from code. " +
                "As a workaround, add a MenuItem to your AppShell.xaml and call IHelpKit.ShowAsync() in its Clicked handler.");
        }
    }
}
