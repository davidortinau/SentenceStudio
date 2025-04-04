using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Services;

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
    [Inject] TranslationService _translationService;
    [Inject] VocabularyService _vocabService;
    [Inject] UserActivityRepository _userActivityRepository;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["Writing"]}",
            ToolbarItem($"{_localize["Refresh"]}").OnClicked(LoadVocabulary),
            Grid("Auto,*,Auto", "",
                SentencesHeader(),
                SentencesScrollView(),
                InputUI(),
                LoadingOverlay()
            )
        ).OnAppearing(LoadVocabulary);
    }

    VisualNode SentencesHeader() =>
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

    VisualNode SentencesScrollView() =>
        ScrollView(
            VStack(spacing: 0,
                State.Sentences.Select(sentence =>
                    DeviceInfo.Idiom == DeviceIdiom.Desktop ?
                        RenderDesktopSentence(sentence) :
                        RenderMobileSentence(sentence)
                )
            ).Margin(16, 0)
        ).GridRow(1);

    VisualNode InputUI() =>
        Grid(rows: "Auto,Auto,Auto", columns: "*,Auto",
            ScrollView(
                VStack(spacing: ApplicationTheme.Size40,
                    Label(_localize["ChooseAVocabularyWord"])
                        .Style((Style)Application.Current.Resources["Title3"]),
                    HStack(spacing: ApplicationTheme.Size40,
                        State.VocabBlocks.Select(word =>
                            Button(word.TargetLanguageTerm)
                                .BackgroundColor(ApplicationTheme.Gray200)
                                .TextColor(ApplicationTheme.Gray900)
                                .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                                .Padding(ApplicationTheme.Size40)
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
            .GridRow(1)
            .GridColumn(0)

        ).GridRow(2)
        .Padding((double)Application.Current.Resources["size160"])
        .RowSpacing(ApplicationTheme.Size40);

    VisualNode LoadingOverlay() =>
        Grid(
            Label("Thinking...")
                .FontSize(64)
                .TextColor(Theme.IsLightTheme ? 
                    ApplicationTheme.LightOnDarkBackground : 
                    ApplicationTheme.DarkOnLightBackground)
                .Center()
        )
        .BackgroundColor(Color.FromArgb("#80000000"))
        .GridRowSpan(2)
        .IsVisible(State.IsBusy);

    async void LoadVocabulary()
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

    void UseVocab(string word)
    {
        SetState(s => s.UserInput = (s.UserInput ?? "") + word);
    }

    async void GradeMe()
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

    async void TranslateInput()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput))
            return;

        var translation = await _translationService.TranslateAsync(State.UserInput);
        await AppShell.DisplayToastAsync(translation);
    }

    VisualNode RenderDesktopSentence(Sentence sentence) =>
        Grid("",columns: "*,*,*,*",
            Label(sentence.Answer).GridColumn(0),
            Label(sentence.Accuracy.ToString()).Center().GridColumn(1),
            Label(sentence.Fluency.ToString()).Center().GridColumn(2),
            HStack(spacing: 4,
                Button()
                    .BackgroundColor(Colors.Transparent)
                    .TextColor(Theme.IsLightTheme ? 
                        ApplicationTheme.LightOnDarkBackground :
                        ApplicationTheme.DarkOnLightBackground)
                    .ImageSource(SegoeFluentIcons.Copy.ToImageSource())
                    .OnClicked(() => UseVocab(sentence.Answer)),
                Button()
                    .BackgroundColor(Colors.Transparent)
                    .TextColor(Theme.IsLightTheme ? 
                        ApplicationTheme.LightOnDarkBackground :
                        ApplicationTheme.DarkOnLightBackground)
                    .ImageSource(SegoeFluentIcons.Info.ToImageSource())
                    .OnClicked(() => ShowExplanation(sentence))
            ).Center().GridColumn(3)
        );

    VisualNode RenderMobileSentence(Sentence sentence) =>
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

    async void ShowExplanation(Sentence sentence)
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