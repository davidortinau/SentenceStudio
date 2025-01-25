using CommunityToolkit.Maui;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Lesson;
using SentenceStudio.Pages.Vocabulary;
using Microsoft.Maui.Platform;
using Microsoft.Extensions.Configuration;
#if ANDROID || IOS || MACCATALYST
using Shiny;
#endif
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Account;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Translation;
using CommunityToolkit.Maui.Media;
using The49.Maui.BottomSheet;
using SkiaSharp.Views.Maui.Controls.Hosting;
using SentenceStudio.Pages.Clozure;
using Plugin.Maui.Audio;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.Storyteller;
using SentenceStudio.Pages.HowDoYouSay;
using Fonts;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Pages.Warmup;
using Syncfusion.Maui.Toolkit.Hosting;



#if WINDOWS
using System.Reflection;
#endif



#if DEBUG
using Common;
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
			.UseMauiCommunityToolkitMarkup()
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

		builder.Services.AddSingleton<TeacherService>();
		builder.Services.AddSingleton<VocabularyService>();
		builder.Services.AddSingleton<ConversationService>();
		builder.Services.AddSingleton<AiService>();
		builder.Services.AddSingleton<SceneImageService>();
		builder.Services.AddSingleton<ClozureService>();
		builder.Services.AddSingleton<StorytellerService>();

		builder.Services.AddSingleton<AppShellModel>();

		builder.Services.AddSingleton<StoryRepository>();
		builder.Services.AddSingleton<UserProfileRepository>();
		builder.Services.AddSingleton<UserActivityRepository>();
		builder.Services.AddSingleton<SkillProfileRepository>();

		builder.Services.AddTransient<FeedbackPanel,FeedbackPanelModel>();

		builder.Services.AddSingleton<DesktopTitleBar,DesktopTitleBarViewModel>();

		builder.Services.AddSingleton<OnboardingPageModel>();
		builder.Services.AddSingleton<DashboardPageModel>();
		builder.Services.AddSingleton<ListVocabularyPageModel>();
		builder.Services.AddSingleton<LessonStartPageModel>();
		builder.Services.AddSingleton<UserProfilePageModel>();
		builder.Services.AddSingleton<ListSkillProfilesPageModel>();
		
		builder.Services.AddTransientWithShellRoute<TranslationPage, TranslationPageModel>("translation");		
		builder.Services.AddTransientWithShellRoute<AddVocabularyPage, AddVocabularyPageModel>("addVocabulary");
		builder.Services.AddTransientWithShellRoute<EditVocabularyPage, EditVocabularyPageModel>("editVocabulary");
		builder.Services.AddTransientWithShellRoute<WritingPage, WritingPageModel>("writingLesson");
		builder.Services.AddTransientWithShellRoute<WarmupPage, WarmupPageModel>("warmup");
		builder.Services.AddTransientWithShellRoute<DescribeAScenePage, DescribeAScenePageModel>("describeScene");
		builder.Services.AddTransientWithShellRoute<ClozurePage, ClozurePageModel>("clozures");
		builder.Services.AddTransientWithShellRoute<EditSkillProfilePage, EditSkillProfilePageModel>("editSkillProfile");
		builder.Services.AddTransientWithShellRoute<AddSkillProfilePage, AddSkillProfilePageModel>("addSkillProfile");
		builder.Services.AddTransientWithShellRoute<StorytellerPage, StorytellerPageModel>("storyteller");
		builder.Services.AddTransientWithShellRoute<HowDoYouSayPage, HowDoYouSayPageModel>("howDoYouSay");

        
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

		builder.Services.AddSingleton<ISpeechToText>(SpeechToText.Default);
        builder.Services.AddFilePicker();

		builder.Services.AddTransientPopup<PhraseClipboardPopup, PhraseClipboardViewModel>();
		builder.Services.AddTransientPopup<ExplanationPopup, ExplanationViewModel>();

#if DEBUG
		builder.UseDebugRibbon(Colors.Black);
		builder.Services.AddSingleton<ICommunityToolkitHotReloadHandler, HotReloadHandler>();
#endif

// 		builder.Services.AddLogging(logging =>
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
