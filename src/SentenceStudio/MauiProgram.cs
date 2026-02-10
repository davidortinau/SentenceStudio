using Microsoft.AspNetCore.Components.WebView.Maui;
using CommunityToolkit.Maui;
using Microsoft.Maui.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
#if ANDROID || IOS || MACCATALYST
using Shiny;
#endif
using CommunityToolkit.Maui.Media;
using SkiaSharp.Views.Maui.Controls.Hosting;
using Plugin.Maui.Audio;
using Syncfusion.Maui.Toolkit.Hosting;
using SentenceStudio.Pages.Conversation;
using SentenceStudio.Pages.HowDoYouSay;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Pages.VocabularyQuiz;
using SentenceStudio.Pages.Reading;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;
using OpenAI;
using ElevenLabs;
using CommunityToolkit.Maui.Storage;
using Syncfusion.Maui.Core.Hosting;
using SentenceStudio.Pages.LearningResources;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Pages.MinimalPairs;
using SentenceStudio.Services.LanguageSegmentation;
using Microsoft.Maui.Controls.Hosting;
using MauiReactor.HotReload;
using UXDivers.Popups.Maui;
#if DEBUG
using MauiDevFlow.Agent;
#endif
#if ANDROID || IOS || MACCATALYST || WINDOWS
using System.Globalization;
#endif

#if WINDOWS
using System.Reflection;
#endif

namespace SentenceStudio;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{

		var builder = MauiApp.CreateBuilder();
		builder
			// .UseMauiApp<App>(app =>
			// {

			// })
			.UseMauiReactorApp<SentenceStudioApp>(app =>
			{
				app.UseTheme<MyTheme>();
				app.SetWindowsSpecificAssetsDirectory("Assets");
				app.Resources.MergedDictionaries.Add(new UXDivers.Popups.Maui.Controls.DarkTheme());
				app.Resources.MergedDictionaries.Add(new UXDivers.Popups.Maui.Controls.PopupStyles());

				// // Add custom resources
				// var customResources = new ResourceDictionary
				// {
				// 	// Font Families
				// 	{ "IconsFontFamily", MaterialSymbolsFont.FontFamily },
				// 	{ "AppFontFamily", "Manrope" },
				// 	{ "AppSemiBoldFamily", "ManropeSemibold" },

				// 	// UXDivers Popups Icon Overrides
				// 	{ "UXDPopupsCloseIconButton", MaterialSymbolsFont.Close },
				// 	{ "UXDPopupsCheckCircleIconButton", MaterialSymbolsFont.Check_circle },

				// 	// Icon Colors
				// 	// { "IconOrange", Color.FromArgb("#FF7134") },
				// 	// { "IconMagenta", Color.FromArgb("#FF1AD9") },
				// 	// { "IconCyan", Color.FromArgb("#05D9FF") },
				// 	// { "IconGreen", Color.FromArgb("#2FFF74") },
				// 	// { "IconPurple", Color.FromArgb("#BD3BFF") },
				// 	// { "IconBlue", Color.FromArgb("#1C7BFF") },
				// 	// { "IconLime", Color.FromArgb("#C8FF01") },
				// 	// { "IconRed", Color.FromArgb("#FF0000") },
				// 	// { "IconDarkBlue", Color.FromArgb("#6422FF") },
				// 	{ "BackgroundColor", MyTheme.DarkBackground },
				// 	{ "BackgroundSecondaryColor", MyTheme.DarkSecondaryBackground },
				// 	{ "BackgroundTertiaryColor", Colors.Purple },
				// 	{ "PrimaryColor", MyTheme.PrimaryDark},
				// 	{ "TextColor", MyTheme.PrimaryDarkText },
				// 	{ "PopupBorderColor", MyTheme.DarkSecondaryBackground }
				// };
				// app.Resources.MergedDictionaries.Add(customResources);

				// InitializeUserCulture();
				// InitializeSmartResources();

			})
			.UseUXDiversPopups()

			// .AddServiceDefaults()
#if ANDROID || IOS || MACCATALYST
			.UseShiny()
#endif
			.UseMauiCommunityToolkit()
			.UseSkiaSharp()
			.ConfigureSyncfusionToolkit()
			.ConfigureSyncfusionCore()
			.AddAudio(
				playbackOptions =>
				{
#if IOS || MACCATALYST
					playbackOptions.Category = AVFoundation.AVAudioSessionCategory.Playback;
#endif
				},
				recordingOptions =>
				{
#if IOS || MACCATALYST
					recordingOptions.Category = AVFoundation.AVAudioSessionCategory.Record;
					recordingOptions.Mode = AVFoundation.AVAudioSessionMode.Default;
					recordingOptions.CategoryOptions = AVFoundation.AVAudioSessionCategoryOptions.MixWithOthers;
#endif
				})
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Segoe-Ui-Bold.ttf", "SegoeBold");
				fonts.AddFont("Segoe-Ui-Regular.ttf", "SegoeRegular");
				fonts.AddFont("Segoe-Ui-Semibold.ttf", "SegoeSemibold");
				fonts.AddFont("Segoe-Ui-Semilight.ttf", "SegoeSemilight");
				fonts.AddFont("bm_yeonsung.ttf", "Yeonsung");
				fonts.AddFont("fa_solid.ttf", FontAwesome.FontFamily);
				fonts.AddFont("FluentSystemIcons-Regular.ttf", FluentUI.FontFamily);
				fonts.AddFont("Manrope-Regular.ttf", "Manrope");
				fonts.AddFont("Manrope-SemiBold.ttf", "ManropeSemibold");
				fonts.AddFont("MaterialSymbols.ttf", MaterialSymbolsFont.FontFamily);
			})
			.ConfigureMauiHandlers(handlers =>
			{
				ModifyEntry();
				ModifyPicker();
				ConfigureWebView();
			})
			.ConfigureFilePicker(100)
			;



#if ANDROID || IOS || MACCATALYST
		builder.Configuration.AddJsonPlatformBundle();
#else
		var a = Assembly.GetExecutingAssembly();
		using var stream = a.GetManifestResourceStream("SentenceStudio.appsettings.json");

		var config = new ConfigurationBuilder()
			.AddJsonStream(stream)
			.Build();

		builder.Configuration.AddConfiguration(config);
#endif


		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Logging
			.AddDebug()
			.AddConsole()
			.SetMinimumLevel(LogLevel.Debug);
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.AddMauiDevFlowAgent(options => { options.Port = 9223; });
		// builder.UseDebugRibbon();
#endif

		RegisterRoutes();
		RegisterServices(builder.Services);

		// TODO: Is this still necessary or move to ServiceDefaults?
		// builder.Services.AddOpenTelemetry()
		// 	.WithTracing(tracerProviderBuilder =>
		// 	{
		// 		tracerProviderBuilder
		// 			.AddHttpClientInstrumentation() // Capture HttpClient requests
		// 											// .AddSource("IChatClient") // Custom source for OpenAI API calls
		// 			.AddConsoleExporter(); // Export traces to console for debugging
		// 	});

		var sfKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
			? Environment.GetEnvironmentVariable("SyncfusionKey")!
			: builder.Configuration.GetRequiredSection("Settings").Get<Settings>().SyncfusionKey;

		Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(sfKey);

		var openAiApiKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
			? Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey")!
			: builder.Configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

		builder.Services
			// .AddChatClient(new OllamaChatClient("http://localhost:11434", "deepseek-r1"))
			.AddChatClient(new OpenAIClient(openAiApiKey).GetChatClient("gpt-4o-mini").AsIChatClient())
			//.AsChatClient(modelId: "gpt-4o-mini"))
			// .UseFunctionInvocation()
			.UseLogging();
		// .UseOpenTelemetry();

		// Debug.WriteLine($"ElevenLabs from Env: {Environment.GetEnvironmentVariable("ElevenLabsKey")}");
		// Debug.WriteLine($"ElevenLabs from Config: {builder.Configuration.GetRequiredSection("Settings").Get<Settings>().ElevenLabsKey}");

		var elevenLabsKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
			? Environment.GetEnvironmentVariable("ElevenLabsKey")!
			: builder.Configuration.GetRequiredSection("Settings").Get<Settings>().ElevenLabsKey;

		builder.Services.AddSingleton<ElevenLabsClient>(new ElevenLabsClient(elevenLabsKey));


		// --- CoreSync setup ---
		// Use the existing database path that already contains data
		var dbPath = Constants.DatabasePath; // This points to the existing sstudio.db3

		// Register CoreSync data and sync services
		builder.Services.AddDataServices(dbPath);
		// #if DEBUG
		// 		// Around line 151 - change the server URI to HTTP
		// 		builder.Services.AddSyncServices(dbPath, new Uri($"http://{(DeviceInfo.Current.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost")}:5240"));
		// #else
		// 		builder.Services.AddSyncServices(dbPath, new Uri($"https://{(DeviceInfo.Current.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost")}:5240"));
		// #endif
		builder.Services.AddSyncServices(dbPath, new Uri($"http://{(DeviceInfo.Current.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost")}:5240"));

		// Register ISyncService for use in repositories
		builder.Services.AddSingleton<SentenceStudio.Services.ISyncService, SentenceStudio.Services.SyncService>();

		// Register Multi-Agent Conversation Services
		builder.Services.AddConversationAgentServices();

		// Register Minimal Pair repositories
		builder.Services.AddScoped<SentenceStudio.Repositories.MinimalPairRepository>();
		builder.Services.AddScoped<SentenceStudio.Repositories.MinimalPairSessionRepository>();

		var app = builder.Build();
		var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MauiProgram");
		logger.LogDebug("✅ MauiApp built successfully");

		// CRITICAL: Initialize database schema SYNCHRONOUSLY before app starts
		// This ensures MinutesSpent column exists before any queries attempt to use it
		logger.LogDebug("🚀 CHECKPOINT 1: About to get ISyncService");

		SentenceStudio.Services.ISyncService syncService;
		try
		{
			syncService = app.Services.GetRequiredService<SentenceStudio.Services.ISyncService>();
			logger.LogDebug("✅ CHECKPOINT 2: Got ISyncService successfully");

			// BLOCKING call - wait for schema to be ready
			logger.LogDebug("🚀 CHECKPOINT 3: Starting InitializeDatabaseAsync with Wait()");
			Task.Run(async () => await syncService.InitializeDatabaseAsync()).Wait();
			logger.LogDebug("✅ CHECKPOINT 4: Database initialization complete");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "❌ FATAL ERROR in database initialization");
			throw; // Re-throw to prevent app from starting with broken database
		}

		// Background sync (non-blocking)
		Task.Run(async () =>
		{
			try
			{
				logger.LogDebug("🚀 Starting async database initialization");
				var syncService = app.Services.GetRequiredService<SentenceStudio.Services.ISyncService>();

				await syncService.InitializeDatabaseAsync();
				logger.LogDebug("✅ Database initialization complete");

				// Seed predefined conversation scenarios
				var scenarioService = app.Services.GetRequiredService<SentenceStudio.Services.IScenarioService>();
				await scenarioService.SeedPredefinedScenariosAsync();
				logger.LogDebug("✅ Conversation scenarios seeded");

				// Trigger background sync after initialization
				await syncService.TriggerSyncAsync();
				logger.LogInformation("[CoreSync] Background sync completed successfully");
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "[CoreSync] Background sync failed");
			}
		});

		// Listen for connectivity changes to trigger sync when online
		Connectivity.Current.ConnectivityChanged += (s, e) =>
		{
			if (e.NetworkAccess == NetworkAccess.Internet)
			{
				Task.Run(async () =>
				{
					try
					{
						var syncService = app.Services.GetRequiredService<SentenceStudio.Services.ISyncService>();
						await syncService.TriggerSyncAsync();
						logger.LogInformation("[CoreSync] Connectivity sync completed successfully");
					}
					catch (Exception ex)
					{
						logger.LogWarning(ex, "[CoreSync] Sync on connectivity failed");
					}
				});
			}
		};

		return app;
	}



	private static void RegisterRoutes()
	{
		MauiReactor.Routing.RegisterRoute<ConversationPage>("conversation");
		MauiReactor.Routing.RegisterRoute<HowDoYouSayPage>("howdoyousay");
		MauiReactor.Routing.RegisterRoute<ClozurePage>(nameof(ClozurePage));
		MauiReactor.Routing.RegisterRoute<VocabularyQuizPage>(nameof(VocabularyQuizPage));
		MauiReactor.Routing.RegisterRoute<TranslationPage>(nameof(TranslationPage));
		MauiReactor.Routing.RegisterRoute<CreateMinimalPairPage>(nameof(CreateMinimalPairPage));
		MauiReactor.Routing.RegisterRoute<MinimalPairSessionPage>(nameof(MinimalPairSessionPage));
		MauiReactor.Routing.RegisterRoute<EditSkillProfilePage>(nameof(EditSkillProfilePage));
		MauiReactor.Routing.RegisterRoute<AddSkillProfilePage>(nameof(AddSkillProfilePage));

		MauiReactor.Routing.RegisterRoute<WritingPage>(nameof(WritingPage));
		MauiReactor.Routing.RegisterRoute<DescribeAScenePage>(nameof(DescribeAScenePage));
		MauiReactor.Routing.RegisterRoute<VocabularyMatchingPage>(nameof(VocabularyMatchingPage));
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.Shadowing.ShadowingPage>("shadowing");
		MauiReactor.Routing.RegisterRoute<ReadingPage>("reading");
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.VideoWatching.VideoWatchingPage>(nameof(SentenceStudio.Pages.VideoWatching.VideoWatchingPage));
		// MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.YouTube.YouTubeImportPage>(nameof(YouTubeImportPage));

		// Register Learning Resources pages
		MauiReactor.Routing.RegisterRoute<AddLearningResourcePage>(nameof(AddLearningResourcePage));
		MauiReactor.Routing.RegisterRoute<EditLearningResourcePage>(nameof(EditLearningResourcePage));

		// Register Vocabulary Progress pages
		MauiReactor.Routing.RegisterRoute<VocabularyLearningProgressPage>(nameof(VocabularyLearningProgressPage));

		// Register Vocabulary Management pages
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.VocabularyManagement.VocabularyManagementPage>(nameof(SentenceStudio.Pages.VocabularyManagement.VocabularyManagementPage));
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.VocabularyManagement.EditVocabularyWordPage>(nameof(SentenceStudio.Pages.VocabularyManagement.EditVocabularyWordPage));
	}


	static void RegisterServices(IServiceCollection services)
	{
		// #if DEBUG
		//         services.AddLogging(configure => configure.AddDebug());
		// #endif


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
		services.AddSingleton<SpeechVoicePreferences>(); // Per-language voice preference for learning activities
		services.AddSingleton<Services.Speech.IVoiceDiscoveryService, Services.Speech.VoiceDiscoveryService>();

		// Language segmenters - register all supported languages
		services.AddSingleton<KoreanLanguageSegmenter>();
		services.AddSingleton<GenericLatinSegmenter>();
		services.AddSingleton<FrenchLanguageSegmenter>();
		services.AddSingleton<GermanLanguageSegmenter>();
		services.AddSingleton<SpanishLanguageSegmenter>();
		
		// Register all segmenters as IEnumerable for services that need all of them
		services.AddSingleton<IEnumerable<ILanguageSegmenter>>(provider =>
			new List<ILanguageSegmenter>
			{
				provider.GetRequiredService<KoreanLanguageSegmenter>(),
				provider.GetRequiredService<GenericLatinSegmenter>(),
				provider.GetRequiredService<FrenchLanguageSegmenter>(),
				provider.GetRequiredService<GermanLanguageSegmenter>(),
				provider.GetRequiredService<SpanishLanguageSegmenter>()
			});
		
		// Language segmenter factory for resolving segmenters by language name
		services.AddSingleton<LanguageSegmenterFactory>();
		
		services.AddSingleton<TranscriptFormattingService>();
		services.AddSingleton<TranscriptSentenceExtractor>();

#if DEBUG
		// Debug services - only available in debug builds
		// services.AddSingleton<VisualTreeDumpService>();
#endif

		// services.AddSingleton<AppShellModel>();
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

		// Scenario service for Conversation scenarios
		services.AddSingleton<ScenarioRepository>();
		services.AddSingleton<IScenarioService, ScenarioService>();

		// Vocabulary encoding repositories and services
		services.AddSingleton<EncodingStrengthCalculator>();
		services.AddSingleton<ExampleSentenceRepository>();
		services.AddSingleton<VocabularyEncodingRepository>();
		services.AddSingleton<VocabularyFilterService>(); // [US3-T048]

		// Vocabulary search syntax parser (GitHub-style search: tag:value, resource:value, etc.)
		services.AddSingleton<ISearchQueryParser, SearchQueryParser>();

		// PHASE 2 OPTIMIZATION: Progress cache service for faster dashboard loading
		services.AddSingleton<SentenceStudio.Services.Progress.ProgressCacheService>();

		// Progress aggregation service for dashboard visuals
		services.AddSingleton<SentenceStudio.Services.Progress.IProgressService, SentenceStudio.Services.Progress.ProgressService>();

		// Activity timer service for Today's Plan tracking
		services.AddSingleton<SentenceStudio.Services.Timer.IActivityTimerService, SentenceStudio.Services.Timer.ActivityTimerService>();

		// Deterministic plan builder - research-based pedagogical algorithms
		services.AddSingleton<SentenceStudio.Services.PlanGeneration.DeterministicPlanBuilder>();

		// LLM-based plan generation (uses deterministic builder)
		services.AddSingleton<SentenceStudio.Services.PlanGeneration.ILlmPlanGenerationService, SentenceStudio.Services.PlanGeneration.LlmPlanGenerationService>();

		// Vocabulary example generation service (AI)
		services.AddSingleton<VocabularyExampleGenerationService>();

		// services.AddTransient<FeedbackPanel,FeedbackPanelModel>();

		// services.AddSingleton<DesktopTitleBar,DesktopTitleBarViewModel>();

		// services.AddSingleton<OnboardingPageModel>();
		// services.AddSingleton<DashboardPageModel>();
		// services.AddSingleton<ListVocabularyPageModel>();
		// services.AddSingleton<LessonStartPageModel>();
		// services.AddSingleton<UserProfilePageModel>();
		// services.AddSingleton<ListSkillProfilesPageModel>();

		services.AddSingleton<ISpeechToText>(SpeechToText.Default);
		services.AddFilePicker();
		services.AddSingleton<IFileSaver>(FileSaver.Default);

		// services.AddTransientPopup<PhraseClipboardPopup, PhraseClipboardViewModel>();
		// services.AddTransientPopup<ExplanationPopup, ExplanationViewModel>();

		services.AddSingleton<IAppState, AppState>();
	}

	private static void ModifyPicker()
	{


		Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("GoodByePickerUnderline", (handler, view) =>
		{
#if ANDROID
			handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#elif IOS || MACCATALYST
			handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
		});
	}

	private static void ConfigureWebView()
	{
#if IOS || MACCATALYST
		Microsoft.Maui.Handlers.WebViewHandler.PlatformViewFactory = (handler) =>
		{
			var config = Microsoft.Maui.Platform.MauiWKWebView.CreateConfiguration();

			// Enable inline media playback for YouTube videos
			config.AllowsInlineMediaPlayback = true;

			// Enable AirPlay for video
			config.AllowsAirPlayForMediaPlayback = true;

			// Enable Picture in Picture
			config.AllowsPictureInPictureMediaPlayback = true;

			// Allow media to play without user action (enables inline play button)
			config.MediaTypesRequiringUserActionForPlayback = WebKit.WKAudiovisualMediaTypes.None;

			return new Microsoft.Maui.Platform.MauiWKWebView(CoreGraphics.CGRect.Empty, (Microsoft.Maui.Handlers.WebViewHandler)handler, config);
		};
#endif
	}

	public static void ModifyEntry()
	{
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoMoreBorders", (handler, view) =>
		{
#if ANDROID
			handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
			handler.PlatformView.SetBackgroundColor(Colors.Transparent.ToPlatform());
#elif IOS || MACCATALYST
			handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
			// (handler.PlatformView as UITextField).InlinePredictionType = UITextInlinePredictionType.Yes;
#elif WINDOWS
			handler.PlatformView.FontWeight = Microsoft.UI.Text.FontWeights.Thin;
			handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
#endif
		});
	}

	// private async Task InitializeUserCulture()
	// {
	// 	try
	// 	{
	// 		var profile = await _userProfileRepository.GetAsync();
	// 		if (profile != null && !string.IsNullOrEmpty(profile.DisplayLanguage))
	// 		{
	// 			// Convert display language name to culture code
	// 			string cultureCode = profile.DisplayLanguage == "Korean" ? "ko-KR" : "en-US";
	// 			var culture = new CultureInfo(cultureCode);

	// 			// Set the culture using the LocalizationManager
	// 			LocalizationManager.Instance.SetCulture(culture);

	// 			_logger.LogInformation($"App culture set to {culture.Name} from user profile");
	// 			//Console.Writeline($"App culture set to {culture.Name} from user profile");
	// 		}
	// 	}
	// 	catch (Exception ex)
	// 	{
	// 		_logger.LogError(ex, "Failed to initialize user culture");
	// 		//Console.Writeline($"Failed to initialize user culture: {ex}");
	// 		_logger.LogError($"Stack trace at culture init failure: {Environment.StackTrace}");
	// 		//Console.Writeline($"Stack trace at culture init failure: {Environment.StackTrace}");
	// 	}
	// }

	// private async Task InitializeSmartResources()
	// {
	// 	try
	// 	{
	// 		// Get user's target language from profile (default to Korean)
	// 		var profile = await _userProfileRepository.GetAsync();
	// 		string targetLanguage = profile?.TargetLanguage ?? "Korean";

	// 		// Initialize smart resources (creates them if they don't exist)
	// 		await _smartResourceService.InitializeSmartResourcesAsync(targetLanguage);

	// 		_logger.LogInformation("Smart resources initialized successfully");
	// 	}
	// 	catch (Exception ex)
	// 	{
	// 		_logger.LogError(ex, "Failed to initialize smart resources");
	// 	}
	// }
}
