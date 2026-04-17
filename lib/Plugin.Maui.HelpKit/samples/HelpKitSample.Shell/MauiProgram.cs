using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using HelpKitSample.SharedStubs;
using Plugin.Maui.HelpKit;

namespace HelpKitSample.Shell;

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

        // ---- HelpKit registration ----
        // ContentDirectories is populated lazily at app-ready time (after the
        // sample markdown has been copied out of MauiAssets into AppDataDirectory).
        // We add the target directory here; the installer runs on first launch.
        builder.AddHelpKit(opts =>
        {
            opts.ContentDirectories.Add(Path.Combine(FileSystem.AppDataDirectory, "help-content"));
            opts.Language = "en";
            opts.MaxQuestionsPerMinute = 30; // bumped for demo convenience
        });

        // Opt-in: inject a "Help" flyout entry into the Shell at startup.
        builder.AddHelpKitShellFlyout(title: "Help");

        // ---- BYO AI providers (stubs for the sample) ----
        // HelpKit resolves via keyed DI first (key = opts.HelpKitServiceKey,
        // default "helpkit"). Replace the two lines below with real providers
        // before shipping. Examples:
        //
        //   services.AddKeyedSingleton<IChatClient>("helpkit",
        //       new Azure.AI.OpenAI.AzureOpenAIClient(endpoint, credential)
        //           .AsChatClient("gpt-4o-mini"));
        //
        //   services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
        //       "helpkit",
        //       new Azure.AI.OpenAI.AzureOpenAIClient(endpoint, credential)
        //           .AsEmbeddingGenerator("text-embedding-3-small"));
        //
        // Other providers that implement Microsoft.Extensions.AI work the same
        // way: OpenAI, Azure AI Foundry (SentenceStudio ships Foundry), Ollama
        // for local inference, etc.
        builder.Services.AddKeyedSingleton<IChatClient>("helpkit", new StubChatClient());
        builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            "helpkit",
            new StubEmbeddingGenerator());

        return builder.Build();
    }
}
