using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Lesson;
using SentenceStudio.Services;
using SentenceStudio.Pages.Vocabulary;
using Microsoft.Maui.Platform;
using MauiIcons.SegoeFluent;
using Microsoft.Maui.Handlers;
using System.Diagnostics;
using SentenceStudio.Pages.Controls;
using Microsoft.Extensions.Configuration;
#if ANDROID || IOS || MACCATALYST
using Shiny;
#endif
using CommunityToolkit.Maui.ApplicationModel;
using SentenceStudio.Pages.Scene;
using System.Reflection;
using System.Reactive;
using SentenceStudio.Pages.SyntacticAnalysis;
using SentenceStudio.Pages.Account;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Translation;

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
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Segoe-Ui-Bold.ttf", "SegoeBold");
				fonts.AddFont("Segoe-Ui-Regular.ttf", "SegoeRegular");
				fonts.AddFont("Segoe-Ui-Semibold.ttf", "SegoeSemibold");
				fonts.AddFont("Segoe-Ui-Semilight.ttf", "SegoeSemilight");
				fonts.AddFont("bm_yeonsung.ttf", "Yeonsung");
			})
			.ConfigureMauiHandlers(handlers =>
			{
				ModifyEntry();
				//ModifyPicker();
			})
			.ConfigureFilePicker(100)
            ;

        //builder.Configuration.AddConfiguration(new ConfigurationBuilder().AddConfiguration("appsettings.json").Build());

#if DEBUG
        builder.Logging.AddDebug();
#endif

		builder.Services.AddSingleton<TeacherService>();
		builder.Services.AddSingleton<VocabularyService>();
		builder.Services.AddSingleton<ConversationService>();
		builder.Services.AddSingleton<AiService>();
		builder.Services.AddSingleton<SceneImageService>();
		builder.Services.AddSingleton<UserProfileService>();
		builder.Services.AddSingleton<SyntacticAnalysisService>();
		builder.Services.AddSingleton<UserActivityService>();
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


        builder.Services.AddFilePicker();

		builder.Services.AddTransientPopup<PhraseClipboardPopup, PhraseClipboardViewModel>();
		builder.Services.AddTransientPopup<ExplanationPopup, ExplanationViewModel>();
		
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
#elif WINDOWS
            handler.PlatformView.FontWeight = Microsoft.UI.Text.FontWeights.Thin;
            handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
#endif
        });
    }
}
