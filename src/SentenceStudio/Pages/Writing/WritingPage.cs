using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Fonts;
using Microsoft.Maui.Platform;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Plugin.Maui.DebugOverlay;
using MauiReactor;
using SentenceStudio.Pages.Dashboard;





#if IOS
using UIKit;
using Foundation;
#endif

namespace SentenceStudio.Pages.Writing;

class WritingPageState
{
    public bool IsBusy { get; set; }
    public bool ShowMore { get; set; }
    public string UserInput { get; set; }
    public string UserMeaning { get; set; }
    public List<Sentence> Sentences { get; set; } = [];
    public List<VocabularyWord> VocabBlocks { get; set; } = [];
}

partial class WritingPage : Component<WritingPageState, ActivityProps>
{
    [Inject] TeacherService _teacherService;
    [Inject] VocabularyService _vocabService;
    [Inject] UserActivityRepository _userActivityRepository;

    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["Writing"]}",
            Grid("Auto,*,Auto", "",
                SentencesHeader(),
                SentencesScrollView(),
                InputUI(),
                LoadingOverlay()
            )
        ).OnAppearing(LoadVocabulary);
    }

    private VisualNode SentencesHeader() =>
        Grid("", columns: "*,*,*,*",
            Label(_localize["Sentence"])
                .Style((Style)Application.Current.Resources["Title3"])
                .GridColumn(0),
            Label(_localize["Accuracy"])
                .Style((Style)Application.Current.Resources["Title3"])
                .Center()
                .GridColumn(1),
            Label(_localize["Fluency"])
                .Style((Style)Application.Current.Resources["Title3"])
                .Center()
                .GridColumn(2),
            Label(_localize["Actions"])
                .Style((Style)Application.Current.Resources["Title3"])
                .Center()
                .GridColumn(3)
        ).Margin((double)Application.Current.Resources["size160"]);

    private VisualNode SentencesScrollView() =>
        ScrollView(
            VStack(spacing: 0,
                State.Sentences.Select(sentence =>
                    DeviceInfo.Idiom == DeviceIdiom.Desktop ?
                        RenderDesktopSentence(sentence) :
                        RenderMobileSentence(sentence)
                )
            ).Margin(16, 0)
        ).GridRow(1);

    private VisualNode InputUI() =>
        Grid(rows: "Auto,Auto,Auto", columns: "*,Auto",
            ScrollView(
                VStack(spacing: (double)Application.Current.Resources["size40"],
                    Label(_localize["ChooseAVocabularyWord"])
                        .Style((Style)Application.Current.Resources["Title3"]),
                    HStack(spacing: (double)Application.Current.Resources["size40"],
                        State.VocabBlocks.Select(word =>
                            Button(word.TargetLanguageTerm)
                                .BackgroundColor((Color)Application.Current.Resources["Gray200"])
                                .TextColor((Color)Application.Current.Resources["Gray900"])
                                .FontSize(18)
                                .Padding((double)Application.Current.Resources["size40"])
                                .VStart()
                                .OnClicked(() => UseVocab(word.TargetLanguageTerm))
                        )
                    )
                )
            ).GridColumnSpan(2),

            new SfTextInputLayout{
                Entry()
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 16 : 32)
                    .Text(State.UserInput)
                    .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                    .ReturnType(State.ShowMore ? ReturnType.Next : ReturnType.Go)
                    .OnCompleted(GradeMe)
            }
            .TrailingView(
                Button()
                    .BackgroundColor(Colors.Transparent)
                    .HEnd()
                    .GridColumn(1)
                    .ImageSource(SegoeFluentIcons.Dictionary.ToImageSource())
                    .OnClicked(TranslateInput)
            )
            .Hint(_localize["WhatDoYouWantToSay"].ToString())
            .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
            .GridRow(1)
            .GridColumn(0)

        ).GridRow(2)
        .Padding((double)Application.Current.Resources["size160"])
        .RowSpacing((double)Application.Current.Resources["size40"]);

    private VisualNode LoadingOverlay() =>
        Grid(
            Label("Thinking...")
                .FontSize(64)
                .TextColor(Theme.IsLightTheme ? 
                    (Color)Application.Current.Resources["LightOnDarkBackground"] : 
                    (Color)Application.Current.Resources["DarkOnLightBackground"])
                .Center()
        )
        .BackgroundColor(Color.FromArgb("#80000000"))
        .GridRowSpan(2)
        .IsVisible(State.IsBusy);

    private async void LoadVocabulary()
    {
        SetState(s => s.IsBusy = true);
        try 
        {
            var random = new Random();
            var vocab = await _vocabService.GetListAsync(Props.Vocabulary.ID);
            if (vocab != null)
            {
                SetState(s => s.VocabBlocks = vocab.Words
                    .OrderBy(t => random.Next())
                    .Take(4)
                    .ToList()
                );
            }
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    private void UseVocab(string word)
    {
        SetState(s => s.UserInput = (s.UserInput ?? "") + word);
    }

    private async void GradeMe()
    {
        if (State.ShowMore && string.IsNullOrWhiteSpace(State.UserMeaning))
            return;

        var sentence = new Sentence
        {
            Answer = State.UserInput,
            Problem = State.UserMeaning
        };
        
        SetState(s => {
            s.Sentences.Add(sentence);
            s.UserInput = string.Empty;
            s.UserMeaning = string.Empty;
        });

        var grade = await _teacherService.GradeSentence(sentence.Answer, sentence.Problem);
        if (grade == null)
        {
            await Application.Current.MainPage.DisplayAlert(
                _localize["Error"].ToString(),
                _localize["Something went wrong. Check the server."].ToString(),
                _localize["OK"].ToString());
            return;
        }

        sentence.Accuracy = grade.Accuracy;
        sentence.Fluency = grade.Fluency;
        sentence.FluencyExplanation = grade.FluencyExplanation;
        sentence.AccuracyExplanation = grade.AccuracyExplanation;
        sentence.RecommendedSentence = grade.GrammarNotes.RecommendedTranslation;
        sentence.GrammarNotes = grade.GrammarNotes.Explanation;

        await _userActivityRepository.SaveAsync(new UserActivity
        {
            Activity = Models.Activity.Writer.ToString(),
            Input = $"{sentence.Answer} {sentence.Problem}",
            Accuracy = sentence.Accuracy,
            Fluency = sentence.Fluency,
            CreatedAt = DateTime.Now
        });

        SetState(s => { }); // Force refresh
    }

    private async void TranslateInput()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput))
            return;

        var translation = await _teacherService.Translate(State.UserInput);
        await Application.Current.MainPage.DisplayAlert(
            _localize["Translation"].ToString(),
            translation,
            "Okay");
    }

    private VisualNode RenderDesktopSentence(Sentence sentence) =>
        Grid("",columns: "*,*,*,*",
            Label(sentence.Answer).GridColumn(0),
            Label(sentence.Accuracy.ToString()).Center().GridColumn(1),
            Label(sentence.Fluency.ToString()).Center().GridColumn(2),
            HStack(spacing: 4,
                Button()
                    .BackgroundColor(Colors.Transparent)
                    .TextColor(Theme.IsLightTheme ? 
                        (Color)Application.Current.Resources["LightOnDarkBackground"] :
                        (Color)Application.Current.Resources["DarkOnLightBackground"])
                    .ImageSource(SegoeFluentIcons.Copy.ToImageSource())
                    .OnClicked(() => UseVocab(sentence.Answer)),
                Button()
                    .BackgroundColor(Colors.Transparent)
                    .TextColor(Theme.IsLightTheme ? 
                        (Color)Application.Current.Resources["LightOnDarkBackground"] :
                        (Color)Application.Current.Resources["DarkOnLightBackground"])
                    .ImageSource(SegoeFluentIcons.Info.ToImageSource())
                    .OnClicked(() => ShowExplanation(sentence))
            ).Center().GridColumn(3)
        );

    private VisualNode RenderMobileSentence(Sentence sentence) =>
        SwipeView(
            SwipeItemView(
                Grid(
                    Label().Text(SegoeFluentIcons.Copy.ToString()).FontSize(24).Center()
                ).BackgroundColor(Colors.Red).WidthRequest(60)
            ).OnInvoked(() => UseVocab(sentence.Answer)).HStart(),
            SwipeItemView(
                Grid(
                    Label().Text(SegoeFluentIcons.Info.ToString()).FontSize(24).Center()
                ).BackgroundColor(Colors.Orange).WidthRequest(60)
            ).OnInvoked(() => ShowExplanation(sentence)).HEnd(),
            Grid("",columns: "*,*",
                Label(sentence.Answer).VCenter().GridColumn(0),
                Label(sentence.Accuracy.ToString()).Center().GridColumn(1)
            )
            .Background(Theme.IsLightTheme ? 
                (Brush)Application.Current.Resources["LightCardBackground"] : 
                (Brush)Application.Current.Resources["DarkCardBackground"])
        );

    private async void ShowExplanation(Sentence sentence)
    {
        string explanation = $"Original: {sentence.Answer}\n\n" +
            $"Recommended: {sentence.RecommendedSentence}\n\n" +
            $"Accuracy: {sentence.AccuracyExplanation}\n\n" +
            $"Fluency: {sentence.FluencyExplanation}\n\n" +
            $"Additional Notes: {sentence.GrammarNotes}";

        await Application.Current.MainPage.DisplayAlert(
            _localize["Explanation"].ToString(),
            explanation,
            _localize["OK"].ToString());
    }
}