using HelpKitSample.SharedStubs;
using MauiReactor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Plugin.Maui.HelpKit;

namespace HelpKitSample.MauiReactor.Pages;

class MainPageState
{
    public bool IsInitializing { get; set; } = true;
    public string Status { get; set; } = "Preparing help content...";
    public bool HelpReady { get; set; }
}

public class MainPage : Component<MainPageState>
{
    protected override async void OnMounted()
    {
        base.OnMounted();
        await InitializeAsync();
    }

    public override VisualNode Render()
    {
        return ContentPage("HelpKit MauiReactor Sample",
            ScrollView(
                VStack(spacing: 16,
                    Label("MauiReactor host")
                        .FontSize(28)
                        .FontAttributes(FontAttributes.Bold),

                    Label("This sample demonstrates that Plugin.Maui.HelpKit drops cleanly into a MauiReactor (MVU / fluent UI) app. The library itself uses plain MAUI controls for the help page, but hosts cleanly inside a MauiReactor app.")
                        .LineBreakMode(LineBreakMode.WordWrap),

                    Label(State.Status)
                        .TextColor(State.HelpReady ? Colors.Green : Colors.Gray),

                    Button("Ask Help")
                        .HStart()
                        .IsEnabled(State.HelpReady)
                        .OnClicked(ShowHelp)
                )
                .Padding(24)
                .HStart()
                .VStart()
            )
        );
    }

    private async Task InitializeAsync()
    {
        try
        {
            await SampleHelpContentInstaller.EnsureInstalledAsync();

            var services = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
            if (services?.GetService<IHelpKit>() is { } helpKit)
            {
                try { await helpKit.IngestAsync(); }
                catch (NotImplementedException) { /* Wave 2 gap; ShowAsync still works */ }
            }

            SetState(s =>
            {
                s.IsInitializing = false;
                s.HelpReady = true;
                s.Status = "Help is ready. Tap Ask Help to open the pane.";
            });
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.IsInitializing = false;
                s.HelpReady = false;
                s.Status = $"Init failed: {ex.Message}";
            });
        }
    }

    private async void ShowHelp()
    {
        var services = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
        if (services?.GetService<IHelpKit>() is { } helpKit)
            await helpKit.ShowAsync();
    }
}
