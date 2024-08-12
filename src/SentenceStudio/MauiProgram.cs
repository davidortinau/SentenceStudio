using CommunityToolkit.Maui;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Lesson;
using SentenceStudio.Pages.Vocabulary;
using Microsoft.Maui.Platform;
using MauiIcons.SegoeFluent;
using Microsoft.Maui.Handlers;
using SentenceStudio.Pages.Controls;
using Microsoft.Extensions.Configuration;
#if ANDROID || IOS || MACCATALYST
using Shiny;
#endif
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.SyntacticAnalysis;
using SentenceStudio.Pages.Account;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Translation;
using CommunityToolkit.Maui.Media;
using The49.Maui.BottomSheet;
using SentenceStudio.Data;
using SkiaSharp.Views.Maui.Controls.Hosting;
using OxyPlot.Maui.Skia;
using SentenceStudio.Pages.Clozure;
using CommunityToolkit.Maui.Markup;
using Common;
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
			.UseOxyPlotSkia()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Segoe-Ui-Bold.ttf", "SegoeBold");
				fonts.AddFont("Segoe-Ui-Regular.ttf", "SegoeRegular");
				fonts.AddFont("Segoe-Ui-Semibold.ttf", "SegoeSemibold");
				fonts.AddFont("Segoe-Ui-Semilight.ttf", "SegoeSemilight");
				fonts.AddFont("bm_yeonsung.ttf", "Yeonsung");
				fonts.AddFont("fa_solid.ttf", "FontAwesome");
				fonts.AddFont("FluentSystemIcons-Regular.ttf", "FluentUI");
			})
			.ConfigureMauiHandlers(handlers =>
			{
				ModifyEntry();
				//ModifyPicker();
			})
			.ConfigureFilePicker(100)
            ;

        //builder.Configuration.AddConfiguration(new ConfigurationBuilder().AddConfiguration("appsettings.json").Build());


		builder.Services.AddSingleton<TeacherService>();
		builder.Services.AddSingleton<VocabularyService>();
		builder.Services.AddSingleton<ConversationService>();
		builder.Services.AddSingleton<AiService>();
		builder.Services.AddSingleton<SceneImageService>();
		builder.Services.AddSingleton<UserProfileService>();
		builder.Services.AddSingleton<SyntacticAnalysisService>();
		builder.Services.AddSingleton<ClozureService>();
		builder.Services.AddSingleton<UserActivityRepository>();
		builder.Services.AddSingleton<SkillProfileRepository>();
		builder.Services.AddSingleton<AppShellModel>();

		builder.Services.AddTransient<FeedbackPanel>();
		builder.Services.AddTransient<FeedbackPanelModel>();
		
		builder.Services.AddTransientWithShellRoute<DashboardPage, DashboardPageModel>("dashboard");	
		builder.Services.AddTransientWithShellRoute<TranslationPage, TranslationPageModel>("translation");		
		builder.Services.AddTransientWithShellRoute<ListVocabularyPage, ListVocabularyPageModel>("vocabulary");
		builder.Services.AddTransientWithShellRoute<AddVocabularyPage, AddVocabularyPageModel>("addVocabulary");
		builder.Services.AddTransientWithShellRoute<EditVocabularyPage, EditVocabularyPageModel>("editVocabulary");
		builder.Services.AddTransientWithShellRoute<LessonStartPage, LessonStartPageModel>("playLesson");
		builder.Services.AddTransientWithShellRoute<WritingPage, WritingPageModel>("writingLesson");
		builder.Services.AddTransientWithShellRoute<WarmupPage, WarmupPageModel>("warmup");
		builder.Services.AddTransientWithShellRoute<DescribeAScenePage, DescribeAScenePageModel>("describeScene");
		builder.Services.AddTransientWithShellRoute<AnalysisPage, AnalysisPageModel>("syntacticAnalysis");
		builder.Services.AddTransientWithShellRoute<UserProfilePage, UserProfilePageModel>("userProfile");
		builder.Services.AddTransientWithShellRoute<OnboardingPage, OnboardingPageModel>("onboarding");
		builder.Services.AddTransientWithShellRoute<ClozurePage, ClozurePageModel>("clozures");
		builder.Services.AddTransientWithShellRoute<SkillProfilesPage, SkillProfilesPageModel>("skills");

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
		builder.UseDebugRibbon(Colors.Green);
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
#if MACCATALYST
        Microsoft.Maui.Handlers.PickerHandler.Mapper.ReplaceMapping<Picker, IPickerHandler>(nameof(Picker.Title), (handler, view) =>
		{
			// do nothing
			Debug.WriteLine("Do nothing");
			
		});
#endif
    }

    public static void ModifyEntry()
    {
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoMoreBorders", (handler, view) =>
        {
#if ANDROID
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
