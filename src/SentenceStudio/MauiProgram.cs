using CommunityToolkit.Maui;
using Microsoft.Maui.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
#if ANDROID || IOS || MACCATALYST
using Shiny;
#endif
using CommunityToolkit.Maui.Media;
using The49.Maui.BottomSheet;
using SkiaSharp.Views.Maui.Controls.Hosting;
using Plugin.Maui.Audio;
using Fonts;
using Syncfusion.Maui.Toolkit.Hosting;
using SentenceStudio.Pages.Warmup;
using SentenceStudio.Pages.HowDoYouSay;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Services;
using SentenceStudio.Data;
using SentenceStudio.Common;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;
using OpenAI;
using ElevenLabs;
using CommunityToolkit.Maui.Storage;
using CoreSync;
using CoreSync.Http.Client;
using Microsoft.Extensions.Hosting;

#if WINDOWS
using System.Reflection;
#endif

#if DEBUG
#endif

namespace SentenceStudio;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.AddServiceDefaults()
			#if ANDROID || IOS || MACCATALYST
			.UseShiny()
			#endif
			.UseMauiCommunityToolkit()
			.UseSegoeFluentMauiIcons()
			.UseBottomSheet()
			.UseSkiaSharp()
			.ConfigureSyncfusionToolkit()
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
			})
			.ConfigureMauiHandlers(handlers =>
			{
				ModifyEntry();
				ModifyPicker();
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
		

#if DEBUG
		builder.Logging.AddConsole().AddDebug().SetMinimumLevel(LogLevel.Trace);
#endif

		RegisterRoutes();
		RegisterServices(builder.Services);		

		builder.Services.AddOpenTelemetry()
			.WithTracing(tracerProviderBuilder =>
			{
				tracerProviderBuilder
					.AddHttpClientInstrumentation() // Capture HttpClient requests
					// .AddSource("IChatClient") // Custom source for OpenAI API calls
					.AddConsoleExporter(); // Export traces to console for debugging
			});


		var openAiApiKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
			? Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey")!
			: builder.Configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

		builder.Services
			// .AddChatClient(new OllamaChatClient("http://localhost:11434", "deepseek-r1"))
			.AddChatClient(new OpenAIClient(openAiApiKey).AsChatClient(modelId: "gpt-4o-mini"))
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

        var app = builder.Build();

		// Initialize database and sync on startup using proper initialization pattern
		Task.Run(async () =>
		{
			
			var syncService = app.Services.GetRequiredService<SentenceStudio.Services.ISyncService>();
			await syncService.InitializeDatabaseAsync();
			await syncService.TriggerSyncAsync();
			System.Diagnostics.Debug.WriteLine($"[CoreSync] Startup initialization and sync completed successfully");
		
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
						System.Diagnostics.Debug.WriteLine($"[CoreSync] Connectivity sync completed successfully");
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"[CoreSync] Sync on connectivity: {ex.Message}");
					}
				});
			}
		};

		return app;
	}

	

	private static void RegisterRoutes()
	{
		MauiReactor.Routing.RegisterRoute<WarmupPage>("warmup");
		MauiReactor.Routing.RegisterRoute<HowDoYouSayPage>("howdoyousay");
		MauiReactor.Routing.RegisterRoute<ClozurePage>(nameof(ClozurePage));
		MauiReactor.Routing.RegisterRoute<TranslationPage>(nameof(TranslationPage));
		MauiReactor.Routing.RegisterRoute<EditSkillProfilePage>(nameof(EditSkillProfilePage));
		MauiReactor.Routing.RegisterRoute<AddSkillProfilePage>(nameof(AddSkillProfilePage));

		MauiReactor.Routing.RegisterRoute<WritingPage>(nameof(WritingPage));
		MauiReactor.Routing.RegisterRoute<DescribeAScenePage>(nameof(DescribeAScenePage));
		MauiReactor.Routing.RegisterRoute<VocabularyMatchingPage>(nameof(VocabularyMatchingPage));
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.Shadowing.ShadowingPage>("shadowing");
		// MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.YouTube.YouTubeImportPage>(nameof(YouTubeImportPage));
		
		// Register Learning Resources pages
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.LearningResources.ListLearningResourcesPage>(nameof(SentenceStudio.Pages.LearningResources.ListLearningResourcesPage));
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.LearningResources.AddLearningResourcePage>(nameof(SentenceStudio.Pages.LearningResources.AddLearningResourcePage));
		MauiReactor.Routing.RegisterRoute<SentenceStudio.Pages.LearningResources.EditLearningResourcePage>(nameof(SentenceStudio.Pages.LearningResources.EditLearningResourcePage));
	}

	
	static void RegisterServices(IServiceCollection services)
	{
		// #if DEBUG
		//         services.AddLogging(configure => configure.AddDebug());
		// #endif


		services.AddSingleton<TeacherService>();
		services.AddSingleton<VocabularyService>();
		services.AddSingleton<ConversationService>();
		services.AddSingleton<AiService>();
		services.AddSingleton<SceneImageService>();
		services.AddSingleton<ClozureService>();
		services.AddSingleton<StorytellerService>();
		services.AddSingleton<TranslationService>();
		services.AddSingleton<ShadowingService>();
		services.AddSingleton<AudioAnalyzer>();
		services.AddSingleton<YouTubeImportService>();
		services.AddSingleton<ElevenLabsSpeechService>();

		// services.AddSingleton<AppShellModel>();
		services.AddSingleton<StoryRepository>();
		services.AddSingleton<UserProfileRepository>();
		services.AddSingleton<UserActivityRepository>();
		services.AddSingleton<SkillProfileRepository>();
		services.AddSingleton<LearningResourceRepository>();
		services.AddSingleton<StreamHistoryRepository>();

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
}
