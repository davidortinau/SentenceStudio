using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Lesson;
using DotNet.Meteor.HotReload.Plugin;
using SentenceStudio.Services;
using SentenceStudio.Pages.Vocabulary;
using Microsoft.Maui.Platform;
using MauiIcons.SegoeFluent;
using Microsoft.Maui.Handlers;
using System.Diagnostics;
using SentenceStudio.Pages.Controls;
using Microsoft.Extensions.Configuration;
using Shiny;
using CommunityToolkit.Maui.ApplicationModel;

namespace SentenceStudio;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseShiny()
			.UseMauiCommunityToolkit()
			.UseSegoeFluentMauiIcons()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Segoe-Ui-Bold.ttf", "SegoeBold");
				fonts.AddFont("Segoe-Ui-Regular.ttf", "SegoeRegular");
				fonts.AddFont("Segoe-Ui-Semibold.ttf", "SegoeSemibold");
				fonts.AddFont("Segoe-Ui-Semilight.ttf", "SegoeSemilight");
				// fonts.AddFont("Segoe-Fluent-Icons.ttf", "SegoeFluentIcons");
			})
			.ConfigureMauiHandlers(handlers =>
			{
				ModifyEntry();
				//ModifyPicker();
			})
			.ConfigureFilePicker(100)
			;

#if DEBUG
		builder.Logging.AddDebug();
		builder.EnableHotReload();
#endif

		builder.Services.AddSingleton<TeacherService>();
		builder.Services.AddSingleton<VocabularyService>();
		builder.Services.AddSingleton<ConversationService>();
		builder.Services.AddSingleton<AiService>();
		builder.Services.AddTransient<FeedbackPanel>();
		builder.Services.AddTransient<FeedbackPanelModel>();
		builder.Services.AddTransientWithShellRoute<DashboardPage, DashboardPageModel>("dashboard");	
		builder.Services.AddTransientWithShellRoute<LessonPage, LessonPageModel>("lesson");		
		builder.Services.AddTransientWithShellRoute<ListVocabularyPage, ListVocabularyPageModel>("vocabulary");
		builder.Services.AddTransientWithShellRoute<AddVocabularyPage, AddVocabularyPageModel>("addVocabulary");
		builder.Services.AddTransientWithShellRoute<EditVocabularyPage, EditVocabularyPageModel>("editVocabulary");
		builder.Services.AddTransientWithShellRoute<LessonStartPage, LessonStartPageModel>("playLesson");
		builder.Services.AddTransientWithShellRoute<WritingPage, WritingPageModel>("writingLesson");
		builder.Services.AddTransientWithShellRoute<WarmupPage, WarmupPageModel>("warmup");

		builder.Configuration.AddJsonPlatformBundle();
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
#endif
        });
    }
}
