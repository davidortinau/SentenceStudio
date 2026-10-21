using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Evaluation.Tests.Import;

/// <summary>
/// Hosts the app's real import pipeline with only its platform edges replaced:
///
///   ImportContent.razor RunPreview()                      (UI, not hosted here)
///     -> ContentImportService.ParseContentAsync(request)  (real, SentenceStudio.Shared)
///        -> DetectFormat -> "FreeText"                    (real)
///        -> ParseFreeTextContentAsync                     (real)
///           -> FreeTextToVocab.scriban-txt                (the app's raw asset, copied to output)
///           -> AiService.SendPrompt&lt;FreeTextVocabularyExtractionResponse&gt;   (real, direct-client branch)
///              -> keyed IChatClient (AiTier.Fast).GetResponseAsync&lt;T&gt;(prompt, AiChatOptionsFactory.Create())
///                 -> eval pipeline: capture -> disk response cache -> output cap -> meter -> Azure OpenAI
///        -> TranslateMissingNativeTermsAsync (only when a row has no gloss; real)
///
/// Replaced edges: IFileSystemService (reads the copied raw assets), IConnectivityService (always online),
/// IChatClient (the metered, cached Azure client). No database is touched: ParseContentAsync does not use
/// the repository, and the UI's follow-up EnrichPreviewWithDuplicateInfoAsync (per-user duplicate flags)
/// is out of scope.
/// </summary>
public sealed class AppImportHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly RecordingLoggerProvider _logs = new();

    public AppImportHarness(IChatClient chatClient, string targetDeployment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // AiService binds Settings for its text-to-speech fallback. Chat never uses this value.
                ["Settings:OpenAIKey"] = "not-used-by-eval",
                ["AI:OpenAI:Models:Fast"] = targetDeployment,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.ClearProviders().AddProvider(_logs).SetMinimumLevel(LogLevel.Information));
        services.AddHttpClient();
        services.AddSingleton<IConnectivityService, AlwaysOnlineConnectivity>();
        services.AddSingleton<IFileSystemService>(new AppRawAssetFileSystem(
            Path.Combine(AppContext.BaseDirectory, "AppTemplates")));
        services.AddSingleton(chatClient);
        services.AddKeyedSingleton(AiTier.Fast.ToKey(), chatClient);
        services.AddSingleton<IAiService, AiService>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<ContentImportService>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public IReadOnlyList<string> WarningsAndErrors => _logs.Entries;

    /// <summary>
    /// Mirrors ImportContent.razor RunPreview() with the page's default state: content type
    /// "Vocabulary", delimiter "auto", no header row, Korean target, English native, and the
    /// Vocabulary harvest defaults (words only).
    /// </summary>
    public async Task<ContentImportPreview> ParseAsync(string rawText, string targetLanguage, string nativeLanguage)
    {
        await using var scope = _services.CreateAsyncScope();
        var importService = scope.ServiceProvider.GetRequiredService<ContentImportService>();
        var request = new ContentImportRequest
        {
            RawText = rawText,
            ContentType = ContentType.Vocabulary,
            DelimiterOverride = null,
            HasHeaderRow = false,
            TargetLanguage = targetLanguage,
            NativeLanguage = nativeLanguage,
            HarvestTranscript = false,
            HarvestSentences = false,
            HarvestPhrases = false,
            HarvestWords = true,
        };

        return await importService.ParseContentAsync(request);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private sealed class AlwaysOnlineConnectivity : IConnectivityService
    {
        public bool IsInternetAvailable => true;
    }

    /// <summary>Serves MAUI raw assets (Resources/Raw) from the test output folder.</summary>
    private sealed class AppRawAssetFileSystem(string rawAssetFolder) : IFileSystemService
    {
        public string AppDataDirectory { get; } = Path.Combine(Path.GetTempPath(), "sentencestudio-eval");

        public Task<Stream> OpenAppPackageFileAsync(string filename)
        {
            var path = Path.Combine(rawAssetFolder, filename);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The app requested raw asset '{filename}', which the eval project does not copy. Add it to the csproj AppTemplates items.", path);
            }

            return Task.FromResult<Stream>(File.OpenRead(path));
        }
    }

    /// <summary>Keeps the app's own warnings and errors so a swallowed failure shows up in the report.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyList<string> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var shortCategory = category[(category.LastIndexOf('.') + 1)..];
                var message = formatter(state, exception);
                if (exception is not null)
                {
                    message += $" [{exception.GetType().Name}: {exception.Message}]";
                }

                entries.Enqueue($"{logLevel} {shortCategory}: {message}");
            }
        }
    }
}
