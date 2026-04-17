using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using HelpKitSample.SharedStubs;
using Plugin.Maui.HelpKit;

namespace HelpKitSample.Plain;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // MainPage is a plain NavigationPage. HelpKit's default presenter
        // selector will pick WindowPresenter because Shell.Current is null —
        // exactly the flow this sample exercises.
        builder.Services.AddTransient<MainPage>();

        builder.AddHelpKit(opts =>
        {
            opts.ContentDirectories.Add(Path.Combine(FileSystem.AppDataDirectory, "help-content"));
            opts.Language = "en";
            opts.MaxQuestionsPerMinute = 30;
        });

        // Deliberately NOT calling AddHelpKitShellFlyout() — this sample has no Shell.
        // The developer invokes IHelpKit.ShowAsync() directly from a ToolbarItem.

        // ---- BYO AI providers (stubs for the sample) ----
        // To wire real providers, replace with e.g.:
        //
        //   services.AddKeyedSingleton<IChatClient>("helpkit",
        //       new OpenAIClient(apiKey).AsChatClient("gpt-4o-mini"));
        //   services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
        //       "helpkit",
        //       new OpenAIClient(apiKey).AsEmbeddingGenerator("text-embedding-3-small"));
        //
        // Or for local inference:
        //
        //   services.AddKeyedSingleton<IChatClient>("helpkit",
        //       new OllamaChatClient(new Uri("http://localhost:11434"), "llama3.1"));
        //
        // SentenceStudio's production config uses Azure AI Foundry via
        // Microsoft.Extensions.AI; see its MauiProgram.cs for a real example.
        builder.Services.AddKeyedSingleton<IChatClient>("helpkit", new StubChatClient());
        builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            "helpkit",
            new StubEmbeddingGenerator());

        return builder.Build();
    }
}
