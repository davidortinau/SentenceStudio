using HelpKitSample.Shell.Pages;
using HelpKitSample.SharedStubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Plugin.Maui.HelpKit;
using MauiControlsShell = Microsoft.Maui.Controls.Shell;

namespace HelpKitSample.Shell;

public partial class AppShell : MauiControlsShell
{
    public AppShell()
    {
        FlyoutBehavior = FlyoutBehavior.Flyout;
        Title = "HelpKit Shell Sample";

        Items.Add(new FlyoutItem
        {
            Title = "Dashboard",
            Route = "dashboard",
            Items =
            {
                new ShellContent
                {
                    Title = "Dashboard",
                    ContentTemplate = new DataTemplate(typeof(DashboardPage)),
                },
            },
        });

        Items.Add(new FlyoutItem
        {
            Title = "Profile",
            Route = "profile",
            Items =
            {
                new ShellContent
                {
                    Title = "Profile",
                    ContentTemplate = new DataTemplate(typeof(ProfilePage)),
                },
            },
        });

        Items.Add(new FlyoutItem
        {
            Title = "About",
            Route = "about",
            Items =
            {
                new ShellContent
                {
                    Title = "About",
                    ContentTemplate = new DataTemplate(typeof(AboutPage)),
                },
            },
        });

        // Kick off sample-content install + ingestion once the first page appears.
        Loaded += async (_, _) => await InitializeHelpKitAsync();
    }

    private static async Task InitializeHelpKitAsync()
    {
        try
        {
            await SampleHelpContentInstaller.EnsureInstalledAsync();

            var services = Application.Current?.Handler?.MauiContext?.Services;
            if (services?.GetService<IHelpKit>() is { } helpKit)
                await helpKit.IngestAsync();
        }
        catch (NotImplementedException)
        {
            // Wave 2 gap: IngestAsync is still stubbed in the library. The
            // sample launches fine without it; the help pane still opens.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HelpKitSample.Shell] init failed: {ex}");
        }
    }
}
