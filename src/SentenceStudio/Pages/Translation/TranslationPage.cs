using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using SentenceStudio.Pages.Dashboard;
using CommunityToolkit.Maui.Media;
using System.Globalization;
using SentenceStudio.Pages.Clozure;

namespace SentenceStudio.Pages.Translation;

class TranslationPageState
{
    public bool IsBusy { get; set; }
    public bool IsBuffering { get; set; }
    public string UserMode { get; set; } = InputMode.Text.ToString();
    public string CurrentSentence { get; set; }
    public string UserInput { get; set; }
    public string Progress { get; set; }
    public bool HasFeedback { get; set; }
    public string Feedback { get; set; }
    public bool CanListenExecute { get; set; } = true;
    public bool CanStartListenExecute { get; set; } = true;
    public bool CanStopListenExecute { get; set; }
    public List<string> VocabBlocks { get; set; } = [];
    public string RecommendedTranslation { get; set; }
    public List<Challenge> Sentences { get; set; } = [];
}

partial class TranslationPage : Component<TranslationPageState, ActivityProps>
{
    [Inject] TeacherService _teacherService;
    [Inject] VocabularyService _vocabularyService;
    [Inject] AiService _aiService;

    LocalizationManager _localize => LocalizationManager.Instance;

    private int _currentSentenceIndex = 0;

    public override VisualNode Render() 
		=> ContentPage(
            Grid(rows: "*,80", columns: "*",
                ScrollView(
                    Grid("30,*,auto", "*",
                        RenderSentenceContent(),
                        RenderInputUI(),
                        RenderProgress()
                    )
                ),

                RenderBottomNavigation(),

                RenderLoadingOverlay()
            )
		)
		.OnAppearing(LoadSentences);

    private VisualNode RenderLoadingOverlay() =>
		Grid(
			Label("Thinking.....")
				.FontSize(64)
				.TextColor(Theme.IsLightTheme ? 
					ApplicationTheme.DarkOnLightBackground : 
					ApplicationTheme.LightOnDarkBackground)
				.Center()
		)
			.Background(Color.FromArgb("#80000000"))
			.GridRowSpan(2)
			.IsVisible(State.IsBusy);

    private VisualNode RenderSentenceContent()
        => Grid("*", DeviceInfo.Idiom == DeviceIdiom.Phone ? "*" : "6*, 3*",
            Label()
                .Text(State.CurrentSentence)
                .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 32 : 64)
                .TextColor(Theme.IsLightTheme ?
                    ApplicationTheme.DarkOnLightBackground :
                    ApplicationTheme.LightOnDarkBackground)
                .IsVisible(!State.HasFeedback)
                .HorizontalOptions(LayoutOptions.Start),

            new FeedbackPanel()
            {
                IsVisible = State.HasFeedback,
                Feedback = State.Feedback
            }
                .GridColumn(DeviceInfo.Idiom == DeviceIdiom.Phone ? 0 : 1)
        )
        .GridRow(1)
        .Margin(30);

    private VisualNode RenderInputUI() => Grid("*,*","*,auto,auto,auto",
                State.UserMode == InputMode.MultipleChoice.ToString() ? 
                    RenderVocabBlocks() : 
                    RenderUserInput()
            ).RowSpacing(0)
            .Padding(30)
            .ColumnSpacing(15)
            .GridRow(2);

    private VisualNode RenderUserInput() =>
        Border(
            Entry()
                .Placeholder("그건 한국어로 어떻게 말해요?")
                .FontSize(32)
                .ReturnType(ReturnType.Go)
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .OnCompleted(GradeMe)
		)
        .Background(Colors.Transparent)
        .Stroke(ApplicationTheme.Gray300)
        .StrokeShape(new RoundRectangle().CornerRadius(6))
        .Padding(new Thickness(15, 0))
        .StrokeThickness(1)
        .GridRow(1)
        .GridColumnSpan(4);

    private VisualNode RenderVocabBlocks() =>
        HStack(
            State.VocabBlocks.Select(word =>
                Button()
                    .Text(word)
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                    .Padding(ApplicationTheme.Size40)
                    .BackgroundColor(ApplicationTheme.Gray200)
                    .TextColor(ApplicationTheme.Gray900)
                    .OnClicked(() => UseVocab(word))
            )
		).Background(Colors.Blue)
		.Spacing(4)
        .GridRow(0)
        .GridColumnSpan(4);

    

    private VisualNode RenderProgress() =>
        HStack(
        
            ActivityIndicator()
                .IsRunning(State.IsBuffering)
                .IsVisible(State.IsBuffering)
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .VCenter(),

            Label()
                .Text(State.Progress)
                .VCenter()
                .TextColor(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
		)
		.Spacing(8)
        .Padding(30)
        .HorizontalOptions(LayoutOptions.End)
        .VerticalOptions(LayoutOptions.Start)
        .GridRowSpan(2);

    private VisualNode RenderBottomNavigation() =>
        Grid("1,*", "60,1,*,1,60,1,60",
            Button("GO")
                .TextColor(Theme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground)
                .Background(Colors.Transparent)
                .GridRow(1).GridColumn(4)
                .OnClicked(GradeMe),

            new ModeSelector()
                .SelectedMode(State.UserMode)
                .OnSelectedModeChanged(mode => SetState(s => s.UserMode = mode))
                .GridRow(1).GridColumn(2),

            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(SegoeFluentIcons.Previous.ToImageSource())
                .GridRow(1).GridColumn(0)
                .OnClicked(PreviousSentence),

            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(SegoeFluentIcons.Next.ToImageSource())
                .GridRow(1).GridColumn(6)
                .OnClicked(NextSentence),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .HeightRequest(1)
                .GridColumnSpan(7),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .WidthRequest(1)
                .GridRow(1).GridColumn(1),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .WidthRequest(1)
                .GridRow(1).GridColumn(3),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .WidthRequest(1)
                .GridRow(1).GridColumn(5)
        ).GridRow(1);

    // this is the label that should float over the screen near the cursor when over a text block
    private VisualNode RenderPopOverLabel() =>
        Label()
            .Padding(8)
            .LineHeight(1)
            .IsVisible(false)
            .ZIndex(10)
            .FontSize(64)
            .HStart()
            .VStart()
            .BackgroundColor(Theme.IsLightTheme ?
                ApplicationTheme.LightBackground :
                ApplicationTheme.DarkBackground)
            .TextColor(Theme.IsLightTheme ?
                ApplicationTheme.DarkOnLightBackground :
                ApplicationTheme.LightOnDarkBackground);

    // Event handlers and methods
    private async Task LoadSentences()
    {
        await Task.Delay(100);
        SetState(s => s.IsBusy = true);
        
        var sentences = await _teacherService.GetChallenges(Props.Vocabulary.ID, 2, Props.Skill.ID);
        await Task.Delay(100);
        
        SetState(s => {
            foreach(var sentence in sentences)
            {
                s.Sentences.Add(sentence);
            }
        });
        
        SetState(s => s.IsBusy = false);
        
        SetCurrentSentence();

        if(State.Sentences.Count < 10)
        {
            SetState(s => s.IsBuffering = true);
            var moreSentences = await _teacherService.GetChallenges(Props.Vocabulary.ID, 8, Props.Skill.ID);
            SetState(s => {
                foreach(var sentence in moreSentences)
                {
                    s.Sentences.Add(sentence);
                }
                s.IsBuffering = false;
            });
        }
    }

    private void SetCurrentSentence()
    {
        if (State.Sentences != null && State.Sentences.Count > 0 && _currentSentenceIndex < State.Sentences.Count)
        {
            SetState(s => {
                s.UserMode = InputMode.Text.ToString();
                s.HasFeedback = false;
                s.Feedback = string.Empty;
                s.CurrentSentence = State.Sentences[_currentSentenceIndex].RecommendedTranslation;
                s.UserInput = string.Empty;
                s.RecommendedTranslation = State.Sentences[_currentSentenceIndex].SentenceText;
                s.Progress = $"{_currentSentenceIndex + 1} / {State.Sentences.Count}";
                s.VocabBlocks = State.Sentences[_currentSentenceIndex].Vocabulary?
                    .Select(v => v.TargetLanguageTerm)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .OrderBy(_ => Random.Shared.Next())
                    .ToList() ?? [];
            });
        }
    }

    private async void GradeMe()
    {
        SetState(s => {
            s.Feedback = string.Empty;
            s.IsBusy = true;
        });

        var prompt = await BuildGradePrompt();
        if (string.IsNullOrEmpty(prompt)) return;

        try 
        {
            var feedback = await _aiService.SendPrompt<string>(prompt);
            SetState(s => {
                s.HasFeedback = true;
                s.Feedback = feedback;
                s.IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            SetState(s => s.IsBusy = false);
        }
    }

    private async Task<string> BuildGradePrompt()
    {
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
        using StreamReader reader = new StreamReader(templateStream);
        var template = Template.Parse(reader.ReadToEnd());
        return await template.RenderAsync(new { 
            original_sentence = State.CurrentSentence, 
            recommended_translation = State.RecommendedTranslation, 
            user_input = State.UserInput
        });
    }

    private void NextSentence()
    {
        if (_currentSentenceIndex < State.Sentences.Count - 1)
        {
            _currentSentenceIndex++;
            SetCurrentSentence();
        }
    }

    private void PreviousSentence()
    {
        if (_currentSentenceIndex > 0)
        {
            _currentSentenceIndex--;
            SetCurrentSentence();
        }
    }

    private void UseVocab(string word)
    {
        SetState(s => s.UserInput += word);
    }    

    protected override void OnMounted()
    {
        base.OnMounted();
        LoadSentences();
    }
}

partial class FeedbackPanel : Component
{
    public bool IsVisible { get; set; }
    public string Feedback { get; set; }

    public override VisualNode Render()
    {
        return Border(
        
            Label()
                .Text(Feedback)
                .TextColor(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .FontSize(24)
		)
        .Background(Theme.IsLightTheme ? 
            ApplicationTheme.LightBackground : 
            ApplicationTheme.DarkBackground)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Padding(20)
        .IsVisible(IsVisible);
    }
}