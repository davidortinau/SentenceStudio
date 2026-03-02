using ElevenLabs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using Plugin.Maui.Audio;
using SentenceStudio.WebApp.Platform;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Repositories;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.LanguageSegmentation;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.WebApp.Auth;
using SentenceStudio.WebApp.Components;
using SentenceStudio.WebApp.Platform;
using SentenceStudio.WebUI.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var appDataRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "sentencestudio",
    "webapp");
Directory.CreateDirectory(appDataRoot);

var databasePath = Path.Combine(appDataRoot, "sstudio-webapp.db3");
var preferencesPath = Path.Combine(appDataRoot, "preferences.json");

var appLibRawAssets = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, "..", "SentenceStudio.AppLib", "Resources", "Raw"));
if (!Directory.Exists(appLibRawAssets))
{
    appLibRawAssets = Path.Combine(builder.Environment.ContentRootPath, "Resources", "Raw");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IPreferencesService>(_ => new WebPreferencesService(preferencesPath));
builder.Services.AddSingleton<ISecureStorageService, WebSecureStorageService>();
builder.Services.AddSingleton<IConnectivityService, WebConnectivityService>();
builder.Services.AddSingleton<IFilePickerService, WebFilePickerService>();
builder.Services.AddSingleton<IAudioPlaybackService, WebAudioPlaybackService>();
builder.Services.AddSingleton<IFileSystemService>(_ => new WebFileSystemService(appDataRoot, appLibRawAssets));
builder.Services.AddSingleton(WebAudioManagerProxy.Create());

builder.Services.AddDataServices(databasePath);

var apiBaseUrl = builder.Configuration.GetValue<string>("ApiBaseUrl") ?? "https+http://api";
builder.Services.AddApiClients(new Uri(apiBaseUrl));
builder.Services.AddConversationAgentServices();

var openAiApiKey = builder.Configuration["Settings:OpenAIKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey");
}
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = "not-configured";
}
builder.Configuration["Settings:OpenAIKey"] = openAiApiKey;
builder.Services
    .AddChatClient(new OpenAIClient(openAiApiKey).GetChatClient("gpt-4o-mini").AsIChatClient())
    .UseLogging();

var elevenLabsKey = builder.Configuration["Settings:ElevenLabsKey"];
if (string.IsNullOrWhiteSpace(elevenLabsKey))
{
    elevenLabsKey = Environment.GetEnvironmentVariable("ElevenLabsKey");
}
if (string.IsNullOrWhiteSpace(elevenLabsKey))
{
    elevenLabsKey = "not-configured";
}
builder.Configuration["Settings:ElevenLabsKey"] = elevenLabsKey;
builder.Services.AddSingleton(new ElevenLabsClient(elevenLabsKey));

RegisterSentenceStudioServices(builder.Services);
RegisterBlazorServices(builder.Services);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(typeof(SentenceStudio.WebUI.Routes).Assembly)
    .AddInteractiveServerRenderMode();

app.Run();

static void RegisterBlazorServices(IServiceCollection services)
{
    services.AddSingleton<ToastService>();
    services.AddSingleton<ModalService>();
    services.AddSingleton<BlazorLocalizationService>();
    services.AddSingleton<BlazorNavigationService>();
    services.AddScoped<NavigationMemoryService>();
    services.AddScoped<JsInteropService>();
}

static void RegisterSentenceStudioServices(IServiceCollection services)
{
    services.AddSingleton<ThemeService>();

    services.AddSingleton<TeacherService>();
    services.AddSingleton<ConversationService>();
    services.AddSingleton<AiService>();
    services.AddSingleton<SceneImageService>();
    services.AddSingleton<ClozureService>();
    services.AddSingleton<StorytellerService>();
    services.AddSingleton<TranslationService>();
    services.AddSingleton<ShadowingService>();
    services.AddSingleton<VideoWatchingService>();
    services.AddSingleton<AudioAnalyzer>();
    services.AddSingleton<YouTubeImportService>();
    services.AddSingleton<ElevenLabsSpeechService>();
    services.AddSingleton<DataExportService>();
    services.AddSingleton<NameGenerationService>();
    services.AddSingleton<VocabularyQuizPreferences>();
    services.AddSingleton<SpeechVoicePreferences>();
    services.AddSingleton<SentenceStudio.Services.Speech.IVoiceDiscoveryService, SentenceStudio.Services.Speech.VoiceDiscoveryService>();

    services.AddSingleton<KoreanLanguageSegmenter>();
    services.AddSingleton<GenericLatinSegmenter>();
    services.AddSingleton<FrenchLanguageSegmenter>();
    services.AddSingleton<GermanLanguageSegmenter>();
    services.AddSingleton<SpanishLanguageSegmenter>();

    services.AddSingleton<IEnumerable<ILanguageSegmenter>>(provider =>
        new List<ILanguageSegmenter>
        {
            provider.GetRequiredService<KoreanLanguageSegmenter>(),
            provider.GetRequiredService<GenericLatinSegmenter>(),
            provider.GetRequiredService<FrenchLanguageSegmenter>(),
            provider.GetRequiredService<GermanLanguageSegmenter>(),
            provider.GetRequiredService<SpanishLanguageSegmenter>()
        });

    services.AddSingleton<LanguageSegmenterFactory>();
    services.AddSingleton<TranscriptFormattingService>();
    services.AddSingleton<TranscriptSentenceExtractor>();

    services.AddSingleton<StoryRepository>();
    services.AddSingleton<UserProfileRepository>();
    services.AddSingleton<UserActivityRepository>();
    services.AddSingleton<SkillProfileRepository>();
    services.AddSingleton<LearningResourceRepository>();
    services.AddSingleton<StreamHistoryRepository>();
    services.AddSingleton<VocabularyProgressRepository>();
    services.AddSingleton<VocabularyLearningContextRepository>();
    services.AddSingleton<VocabularyProgressService>();
    services.AddSingleton<IVocabularyProgressService>(provider => provider.GetRequiredService<VocabularyProgressService>());
    services.AddSingleton<SmartResourceService>();

    services.AddScoped<MinimalPairRepository>();
    services.AddScoped<MinimalPairSessionRepository>();

    services.AddSingleton<ScenarioRepository>();
    services.AddSingleton<IScenarioService, ScenarioService>();

    services.AddSingleton<EncodingStrengthCalculator>();
    services.AddScoped<ExampleSentenceRepository>();
    services.AddScoped<VocabularyEncodingRepository>();
    services.AddScoped<VocabularyFilterService>();

    services.AddSingleton<ISearchQueryParser, SearchQueryParser>();

    services.AddSingleton<ProgressCacheService>();
    services.AddSingleton<IProgressService, ProgressService>();
    services.AddSingleton<SentenceStudio.Services.Timer.IActivityTimerService, SentenceStudio.Services.Timer.ActivityTimerService>();

    services.AddSingleton<DeterministicPlanBuilder>();
    services.AddSingleton<ILlmPlanGenerationService, ApiPlanGenerationService>();
    services.AddSingleton<VocabularyExampleGenerationService>();

    services.AddSingleton<IAppState, AppState>();
}
