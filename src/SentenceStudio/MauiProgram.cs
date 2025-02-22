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
using SentenceStudio.Pages.Vocabulary;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Scene;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;
using OpenAI;

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

		return builder.Build();
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

		// services.AddSingleton<AppShellModel>();

		services.AddSingleton<StoryRepository>();
		services.AddSingleton<UserProfileRepository>();
		services.AddSingleton<UserActivityRepository>();
		services.AddSingleton<SkillProfileRepository>();

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
