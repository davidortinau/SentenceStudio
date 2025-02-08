using CommunityToolkit.Maui;
using SentenceStudio.Pages.Dashboard;
using Microsoft.Maui.Platform;
using Microsoft.Extensions.Configuration;
#if ANDROID || IOS || MACCATALYST
using Shiny;
#endif
using CommunityToolkit.Maui.Media;
using The49.Maui.BottomSheet;
using SkiaSharp.Views.Maui.Controls.Hosting;
using Plugin.Maui.Audio;
using Fonts;
using Syncfusion.Maui.Toolkit.Hosting;
using MauiReactor.HotReload;
using SentenceStudio.Pages.Warmup;
using SentenceStudio.Pages.HowDoYouSay;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.Vocabulary;
using SentenceStudio.Pages.Skills;







#if WINDOWS
using System.Reflection;
#endif

#if DEBUG
#endif
using Plugin.Maui.DebugOverlay;

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
		// builder.EnableMauiReactorHotReload(); 
		// builder.OnMauiReactorUnhandledException(ex =>
        // {
        //     System.Diagnostics.Debug.WriteLine(ex);
        // });
		// builder.UseDebugRibbon(Colors.Black);
#endif

		RegisterRoutes();
		RegisterServices(builder.Services);

// 		services.AddLogging(logging =>
// 			{
// #if WINDOWS
// 				logging.AddDebug();
// #else
// 				logging.AddConsole();
// #endif

// 				// Enable maximum logging for BlazorWebView
// 				logging.AddFilter("Microsoft.AspNetCore.Components.WebView", LogLevel.Trace);
// 			});

	

		
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
		// MauiReactor.Routing.RegisterRoute<ProjectDetailsPage>(nameof(ProjectDetailsPage));
		// services.AddTransientWithShellRoute<TranslationPage, TranslationPageModel>("translation");		
		// services.AddTransientWithShellRoute<AddVocabularyPage, AddVocabularyPageModel>("addVocabulary");
		// services.AddTransientWithShellRoute<EditVocabularyPage, EditVocabularyPageModel>("editVocabulary");
		// services.AddTransientWithShellRoute<WritingPage, WritingPageModel>("writingLesson");
		// services.AddTransientWithShellRoute<WarmupPage, WarmupPageModel>("warmup");
		// services.AddTransientWithShellRoute<DescribeAScenePage, DescribeAScenePageModel>("describeScene");
		// services.AddTransientWithShellRoute<ClozurePage, ClozurePageModel>("clozures");
		// services.AddTransientWithShellRoute<EditSkillProfilePage, EditSkillProfilePageModel>("editSkillProfile");
		// services.AddTransientWithShellRoute<AddSkillProfilePage, AddSkillProfilePageModel>("addSkillProfile");
		// services.AddTransientWithShellRoute<StorytellerPage, StorytellerPageModel>("storyteller");
		// services.AddTransientWithShellRoute<HowDoYouSayPage, HowDoYouSayPageModel>("howDoYouSay");
	}

    [ComponentServices]
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

		services.AddSingleton<AppShellModel>();

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
