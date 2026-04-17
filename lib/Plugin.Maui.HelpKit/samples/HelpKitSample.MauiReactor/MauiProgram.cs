using HelpKitSample.MauiReactor.Pages;
using HelpKitSample.SharedStubs;
using MauiReactor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Plugin.Maui.HelpKit;

namespace HelpKitSample.MauiReactor;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiReactorApp<MainPage>(app =>
            {
                app.SetWindowsSpecificAssetsManager();
            })
#if DEBUG
            .EnableMauiReactorHotReload()
#endif
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // ---- HelpKit registration ----
        builder.AddHelpKit(opts =>
        {
            opts.ContentDirectories.Add(Path.Combine(FileSystem.AppDataDirectory, "help-content"));
            opts.Language = "en";
            opts.MaxQuestionsPerMinute = 30;
        });

        // MauiReactor renders through Microsoft.Maui.Controls.Shell internally
        // for Shell-style hosts. This sample uses the MauiReactor-native page
        // host (no Shell), so we do NOT call AddHelpKitShellFlyout here.
        // The MainPage component invokes IHelpKit.ShowAsync directly from a button.

        // ---- BYO AI providers (stubs) ----
        // Replace with real providers in production. SentenceStudio (the host
        // where HelpKit is incubating) uses Azure AI Foundry via
        // Microsoft.Extensions.AI; see its MauiProgram.cs for a real example.
        // Other options: OpenAI / Azure OpenAI / Ollama for local inference.
        builder.Services.AddKeyedSingleton<IChatClient>("helpkit", new StubChatClient());
        builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            "helpkit",
            new StubEmbeddingGenerator());

        return builder.Build();
    }
}
