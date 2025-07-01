using CommunityToolkit.Maui;
using Microsoft.Maui.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
using SentenceStudio.Pages.Vocabulary;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.VocabularyMatching;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;
using OpenAI;
using ElevenLabs;
using CommunityToolkit.Maui.Storage;
using CoreSync;

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

		builder.AddServiceDefaults();

        // TODO replace with the service that you use to communicate with the web app
        // TODO then inject the HttpClient into your service through the constructor
        builder.Services.AddHttpClient<AiService>(client =>
		{
            // This URL uses "https+http://" to indicate HTTPS is preferred over HTTP.
            // Learn more about service discovery scheme resolution at https://aka.ms/dotnet/sdschemes.
            client.BaseAddress = new("https+http://webapp");
        });

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
		

#if DEBUG && !WINDOWS
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
		var dbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sentencestudio", "mobile");
		Directory.CreateDirectory(dbFolder);
		var dbPath = Path.Combine(dbFolder, "sentencestudio.db");

		// Register CoreSync data and sync services
		builder.Services.AddDataServices(dbPath);
		builder.Services.AddSyncServices(dbPath, new Uri($"http://{(DeviceInfo.Current.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost")}:5065"));

		var app = builder.Build();

		// Trigger sync on startup
		var syncProvider = app.Services.GetService<ISyncProvider>();
		if (syncProvider != null)
		{
			Task.Run(async () =>
			{
				try
				{
					// TODO: Fix sync method call - await syncProvider.SynchronizeAsync();
					System.Diagnostics.Debug.WriteLine($"[CoreSync] Sync provider available");
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[CoreSync] Sync on startup failed: {ex.Message}");
				}
			});
		}

		// Listen for connectivity changes to trigger sync when online
		Connectivity.Current.ConnectivityChanged += async (s, e) =>
		{
			if (e.NetworkAccess == NetworkAccess.Internet && syncProvider != null)
			{
				try
				{
					// TODO: Fix sync method call - await syncProvider.SynchronizeAsync();
					System.Diagnostics.Debug.WriteLine($"[CoreSync] Connectivity changed, sync available");
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[CoreSync] Sync on connectivity: {ex.Message}");
				}
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

		MauiReactor.Routing.RegisterRoute<AddVocabularyPage>(nameof(AddVocabularyPage));
		MauiReactor.Routing.RegisterRoute<EditVocabularyPage>(nameof(EditVocabularyPage));
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
