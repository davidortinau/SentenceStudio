using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.LanguageSegmentation;
using SentenceStudio.Workers;
using SentenceStudio.Workers.Platform;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults("SentenceStudio.Workers");

// PostgreSQL requires UTC DateTimes — enable legacy mode for SQLite-era DateTime.Now values
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Database - Aspire-managed PostgreSQL
builder.AddNpgsqlDbContext<ApplicationDbContext>("sentencestudio", configureDbContextOptions: options =>
{
    options.ConfigureWarnings(w =>
        w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// Platform abstractions. In Linux containers LocalApplicationData resolves under
// /app, which is read-only for the ACA runtime user.
var appDataDirectory = Path.Combine(GetWritableAppDataRoot(), "sentencestudio", "worker");
builder.Services.AddSingleton<IConnectivityService, WorkerConnectivityService>();
builder.Services.AddSingleton<IFileSystemService>(_ => new WorkerFileSystemService(appDataDirectory));

// Language segmenters
builder.Services.AddSingleton<KoreanLanguageSegmenter>();
builder.Services.AddSingleton<GenericLatinSegmenter>();
builder.Services.AddSingleton<FrenchLanguageSegmenter>();
builder.Services.AddSingleton<GermanLanguageSegmenter>();
builder.Services.AddSingleton<SpanishLanguageSegmenter>();
builder.Services.AddSingleton<IEnumerable<ILanguageSegmenter>>(provider =>
    new List<ILanguageSegmenter>
    {
        provider.GetRequiredService<KoreanLanguageSegmenter>(),
        provider.GetRequiredService<GenericLatinSegmenter>(),
        provider.GetRequiredService<FrenchLanguageSegmenter>(),
        provider.GetRequiredService<GermanLanguageSegmenter>(),
        provider.GetRequiredService<SpanishLanguageSegmenter>()
    });

// YouTube channel monitoring services
builder.Services.AddSingleton<ChannelMonitorService>();
builder.Services.AddSingleton<VideoImportPipelineService>();
builder.Services.AddSingleton<YouTubeImportService>();
builder.Services.AddSingleton<AudioAnalyzer>();
builder.Services.AddSingleton<TranscriptFormattingService>();
builder.Services.AddSingleton<AiService>();

// OpenAI client. Chat → Foundry uses keyless Entra auth (DefaultAzureCredential).
// AiService still reads Settings:OpenAIKey for the OpenAI audio (TTS) fallback client, so
// bridge it from the Aspire env var when present.
var openAiApiKey = builder.Configuration["AI:OpenAI:ApiKey"];
if (!string.IsNullOrWhiteSpace(openAiApiKey))
{
    builder.Configuration["Settings:OpenAIKey"] = openAiApiKey;
}

var aiEndpoint = builder.Configuration["AI:OpenAI:Endpoint"];
if (!string.IsNullOrWhiteSpace(aiEndpoint))
{
    // Resilient HttpClient for OpenAI — server defaults (AddServiceDefaults) provide
    // Polly retry/circuit-breaker via ConfigureHttpClientDefaults.
    builder.Services.AddResilientOpenAIHttpClient();

    // Default (fast) + keyed fast/reasoning chat clients via AzureOpenAIClient + Entra.
    var azureEndpoint = AiClientRegistration.AzureResourceEndpoint(builder.Configuration);
    builder.Services.AddTieredChatClients(builder.Configuration, sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
        var options = new AzureOpenAIClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };
        return new AzureOpenAIClient(new Uri(azureEndpoint), new DefaultAzureCredential(), options);
    });
}

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

static string GetWritableAppDataRoot()
{
    var configuredRoot = Environment.GetEnvironmentVariable("SENTENCESTUDIO_APPDATA_ROOT");
    if (!string.IsNullOrWhiteSpace(configuredRoot))
    {
        return configuredRoot;
    }

    return string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        ? Path.GetTempPath()
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
