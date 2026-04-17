using HelpKitSample.SharedStubs;
using Microsoft.Maui.Controls;
using Plugin.Maui.HelpKit;

namespace HelpKitSample.Plain;

public class MainPage : ContentPage
{
    private readonly IHelpKit _helpKit;
    private bool _initialized;

    public MainPage(IHelpKit helpKit)
    {
        _helpKit = helpKit;

        Title = "HelpKit Plain Sample";
        Padding = 24;

        Content = new VerticalStackLayout
        {
            Spacing = 16,
            Children =
            {
                new Label { Text = "Plain NavigationPage host", FontSize = 28, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = "This sample shows how to invoke HelpKit directly from a host that does not use Shell. Tap the Help toolbar item (top right) to open the help pane — it is backed by the WindowPresenter.",
                    LineBreakMode = LineBreakMode.WordWrap,
                },
                new Button
                {
                    Text = "Ask for Help",
                    HorizontalOptions = LayoutOptions.Start,
                    Command = new Command(async () => await _helpKit.ShowAsync()),
                },
            },
        };

        ToolbarItems.Add(new ToolbarItem
        {
            Text = "Help",
            Order = ToolbarItemOrder.Primary,
            Priority = 0,
            Command = new Command(async () => await _helpKit.ShowAsync()),
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized) return;
        _initialized = true;

        try
        {
            await SampleHelpContentInstaller.EnsureInstalledAsync();
            await _helpKit.IngestAsync();
        }
        catch (NotImplementedException)
        {
            // Wave 2 gap — IngestAsync still stubbed in the library. Sample
            // still launches; Help button still opens the pane.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HelpKitSample.Plain] init failed: {ex}");
        }
    }
}
