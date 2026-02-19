using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Services;
using System.Text;
using Microsoft.Extensions.Logging;
using MauiReactor.Shapes;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;



namespace SentenceStudio.Pages.Writing;

class WritingPageState
{
    public bool IsBusy { get; set; }
    public bool ShowMore { get; set; }
    public string UserInput { get; set; }
    public string UserMeaning { get; set; }
    public List<Sentence> Sentences { get; set; } = [];
    public List<VocabularyWord> VocabBlocks { get; set; } = [];
    public string EmptyMessage { get; set; } = string.Empty;
}

partial class WritingPage : Component<WritingPageState, ActivityProps>
{
    [Inject] TeacherService _teacherService;
    [Inject] TranslationService _translationService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] LearningResourceRepository _learningResourceRepository;
    [Inject] VocabularyProgressService _vocabularyProgressService;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    [Inject] ILogger<WritingPage> _logger;
    [Inject] NativeThemeService _themeService;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        if (!string.IsNullOrEmpty(State.EmptyMessage))
        {
            return ContentPage($"{_localize["Writing"]}",
                VStack(
                    Label(State.EmptyMessage)
                        .HCenter(),
                    Button($"{_localize["GoBack"]}")
                        .Class("btn-outline-secondary")
                        .OnClicked(async () => await MauiControls.Shell.Current.GoToAsync(".."))
                        .HCenter()
                )
                .VCenter()
                .HCenter()
                .Spacing(16)
            ).BackgroundColor(BootstrapTheme.Current.GetBackground());
        }

        return ContentPage($"{_localize["Writing"]}",
            ToolbarItem($"{_localize["Refresh"]}")
                .IconImageSource(BootstrapIcons.Create(BootstrapIcons.ArrowRepeat, BootstrapTheme.Current.GetOnBackground(), 20))
                .OnClicked(LoadVocabulary),
            Grid("Auto,*,Auto", "",
                SentencesHeader(),
                SentencesScrollView(),
                InputUI(),
                LoadingOverlay()
            )
        )
        .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new Components.ActivityTimerBar() : null)
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(LoadVocabulary);
    }

    VisualNode SentencesHeader()
    {
        var theme = BootstrapTheme.Current;
        return Grid("", columns: "*,*,*,*",
            Label(_localize["Sentence"])
                .H5()
                .GridColumn(0),
            Label(_localize["Accuracy"])
                .H5()
                .Center()
                .GridColumn(1),
            Label(_localize["Fluency"])
                .H5()
                .Center()
                .GridColumn(2),
            Label(_localize["Actions"])
                .H5()
                .Center()
                .GridColumn(3)
        ).Margin(16);
    }

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

    VisualNode InputUI()
    {
        var theme = BootstrapTheme.Current;
        return Grid(rows: "Auto,Auto,Auto", columns: "*,Auto,Auto",
            ScrollView(
                VStack(spacing: 40,
                    Label(_localize["ChooseAVocabularyWord"])
                        .H5(),
                    HStack(spacing: 40,
                        State.VocabBlocks.Select(word =>
                            Button(word.TargetLanguageTerm)
                                .Class("btn-outline-secondary")
                                .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                                .OnClicked(() => UseVocab(word.TargetLanguageTerm))
                        )
                    )
                )
            ).GridColumnSpan(3),

            Entry()
                .Class("form-control")
                .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 16 : 32)
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .ReturnType(State.ShowMore ? ReturnType.Next : ReturnType.Go)
                .OnCompleted(GradeMe)
                .Placeholder($"{_localize["WhatDoYouWantToSay"]}")
                .GridRow(1)
                .GridColumn(0),

            ImageButton()
                .Source(BootstrapIcons.Create(BootstrapIcons.Translate, theme.GetOnBackground(), 20))
                .Background(Colors.Transparent)
                .OnClicked(async () => await TranslateInput())
                .WidthRequest(44)
                .HeightRequest(44)
                .GridRow(1)
                .GridColumn(1),

            Button("Grade")
                .Class("btn-primary")
                .OnClicked(GradeMe)
                .GridRow(1)
                .GridColumn(2)

        ).GridRow(2)
        .Padding(16)
        .RowSpacing(40);
    }

    VisualNode LoadingOverlay()
    {
        var theme = BootstrapTheme.Current;
        return Grid(
            VStack(spacing: 12,
                ActivityIndicator()
                    .IsRunning(true)
                    .Color(theme.Primary)
                    .HCenter(),
                Label($"{_localize["LoadingSentences"]}")
                    .Muted()
                    .HCenter()
            )
            .VCenter()
        )
        .Background(theme.GetBackground().WithAlpha(0.9f))
        .GridRowSpan(2)
        .IsVisible(State.IsBusy);
    }

    async Task LoadVocabulary()
    {
        // Start activity timer if launched from Today's Plan (only once)
        if (Props?.FromTodaysPlan == true && !_timerService.IsActive)
        {
            _logger.LogDebug("WritingPage: Starting activity timer for Writing, PlanItemId: {PlanItemId}", Props.PlanItemId);
            _timerService.StartSession("Writing", Props.PlanItemId);
        }

        SetState(s => s.IsBusy = true);
        try
        {
            var random = new Random();

            // First make sure we have the resource with all its vocabulary
            if (Props.Resource != null && Props.Resource.Id != 0)
            {
                // Fetch the complete resource with vocabulary
                var fullResource = await _learningResourceRepository.GetResourceAsync(Props.Resource.Id);

                // Update our props resource with the fetched vocabulary
                if (fullResource?.Vocabulary != null && fullResource.Vocabulary.Any())
                {
                    // Update the Props.Resource with the fetched vocabulary
                    Props.Resource.Vocabulary = fullResource.Vocabulary;

                    SetState(s => s.VocabBlocks = Props.Resource.Vocabulary
                        .OrderBy(t => random.Next())
                        .Take(4)
                        .ToList()
                    );
                }
                else
                {
                    // Fallback to empty list if no vocabulary available
                    SetState(s =>
                    {
                        s.VocabBlocks = new List<VocabularyWord>();
                        s.EmptyMessage = $"{_localize["NoVocabularyInResource"]}";
                    });
                }
            }
            else
            {
                // No resource selected or invalid ID
                SetState(s =>
                {
                    s.VocabBlocks = new List<VocabularyWord>();
                    s.EmptyMessage = $"{_localize["SelectValidLearningResource"]}";
                });
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

    async Task GradeMe()
    {
        if (State.ShowMore && string.IsNullOrWhiteSpace(State.UserMeaning))
            return;

        var sentence = new Sentence
        {
            Answer = State.UserInput,
            Problem = State.UserMeaning
        };

        SetState(s =>
        {
            s.Sentences.Add(sentence);
            s.UserInput = string.Empty;
            s.UserMeaning = string.Empty;
        });

        var grade = await _teacherService.GradeSentence(sentence.Answer, sentence.Problem);
        if (grade == null)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = $"{_localize["Something went wrong. Check the server."]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
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
            Activity = SentenceStudio.Shared.Models.Activity.Writer.ToString(),
            Input = $"{sentence.Answer} {sentence.Problem}",
            Accuracy = sentence.Accuracy,
            Fluency = sentence.Fluency,
            CreatedAt = DateTime.Now
        });

        // Process vocabulary from the writing activity
        try
        {
            await ProcessVocabularyFromWriting(sentence.Answer, grade);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WritingPage: Error processing vocabulary");
        }

        SetState(s => { }); // Force refresh
    }

    async Task TranslateInput()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput))
            return;

        var translation = await _translationService.TranslateAsync(State.UserInput);
        await AppShell.DisplayToastAsync(translation);
    }

    VisualNode RenderDesktopSentence(Sentence sentence)
    {
        var theme = BootstrapTheme.Current;
        return Grid("", columns: "*,*,*,*",
            Label(sentence.Answer).GridColumn(0),
            Label(sentence.Accuracy.ToString()).Center().GridColumn(1)
                .TextColor(ScoreColor(sentence.Accuracy, theme)),
            Label(sentence.Fluency.ToString()).Center().GridColumn(2)
                .TextColor(ScoreColor(sentence.Fluency, theme)),
            HStack(spacing: 4,
                Button()
                    .Background(Colors.Transparent)
                    .TextColor(theme.GetOnBackground())
                    .ImageSource(BootstrapIcons.Create(BootstrapIcons.Clipboard, theme.GetOnBackground(), 20))
                    .OnClicked(() => UseVocab(sentence.Answer)),
                Button()
                    .Background(Colors.Transparent)
                    .TextColor(theme.GetOnBackground())
                    .ImageSource(BootstrapIcons.Create(BootstrapIcons.InfoCircle, theme.GetOnBackground(), 20))
                    .OnClicked(() => ShowExplanation(sentence))
            ).Center().GridColumn(3)
        );
    }

    VisualNode RenderMobileSentence(Sentence sentence)
    {
        var theme = BootstrapTheme.Current;
        return SwipeView(
            SwipeItemView(
                Grid(
                    Image()
                        .Source(BootstrapIcons.Create(BootstrapIcons.Clipboard, theme.OnDanger, 24))
                        .Center()
                ).Background(theme.Danger).WidthRequest(60)
            ).OnInvoked(() => UseVocab(sentence.Answer)).HStart(),
            SwipeItemView(
                Grid(
                    Image()
                        .Source(BootstrapIcons.Create(BootstrapIcons.InfoCircle, theme.OnWarning, 24))
                        .Center()
                ).Background(theme.Warning).WidthRequest(60)
            ).OnInvoked(() => ShowExplanation(sentence)).HEnd(),
            Grid("", columns: "*,*",
                Label(sentence.Answer).VCenter().GridColumn(0),
                Label(sentence.Accuracy.ToString()).Center().GridColumn(1)
                    .TextColor(ScoreColor(sentence.Accuracy, theme))
            )
            .Background(new SolidColorBrush(theme.GetSurface()))
        );
    }

    static Color ScoreColor(double score, BootstrapTheme theme) =>
        score >= 80 ? theme.Success :
        score >= 50 ? theme.Warning :
        theme.Danger;

    async Task ShowExplanation(Sentence sentence)
    {
        var sections = new List<string>();

        sections.Add($"Your Sentence:\n{sentence.Answer}");

        if (!string.IsNullOrEmpty(sentence.RecommendedSentence))
            sections.Add($"{_localize["RecommendedSentence"]}:\n{sentence.RecommendedSentence}");

        if (!string.IsNullOrEmpty(sentence.AccuracyExplanation))
            sections.Add($"{_localize["AccuracyExplanation"]} ({(int)sentence.Accuracy}%):\n{sentence.AccuracyExplanation}");

        if (!string.IsNullOrEmpty(sentence.FluencyExplanation))
            sections.Add($"{_localize["FluencyExplanation"]} ({(int)sentence.Fluency}%):\n{sentence.FluencyExplanation}");

        if (!string.IsNullOrEmpty(sentence.GrammarNotes))
            sections.Add($"{_localize["GrammarNotes"]}:\n{sentence.GrammarNotes}");

        string explanation = string.Join("\n\n", sections);

        await IPopupService.Current.PushAsync(new SimpleActionPopup
        {
            Title = $"{_localize["Explanation"]}",
            Text = explanation,
            ActionButtonText = $"{_localize["OK"]}",
            ShowSecondaryActionButton = false
        });
        return;
    }

    /// <summary>
    /// Process vocabulary from writing activity for enhanced tracking
    /// </summary>
    async Task ProcessVocabularyFromWriting(string userInput, GradeResponse gradeResponse)
    {
        _logger.LogDebug("ProcessVocabularyFromWriting: Starting vocabulary analysis for WritingPage");

        if (gradeResponse?.VocabularyAnalysis != null && gradeResponse.VocabularyAnalysis.Any())
        {
            _logger.LogDebug("ProcessVocabularyFromWriting: Found {Count} vocabulary items from AI analysis",
                gradeResponse.VocabularyAnalysis.Count);

            foreach (var vocabItem in gradeResponse.VocabularyAnalysis)
            {
                // Use vocabulary from current state
                var matchedVocab = State.VocabBlocks.FirstOrDefault(v =>
                    v.TargetLanguageTerm.Equals(vocabItem.DictionaryForm, StringComparison.OrdinalIgnoreCase));

                if (matchedVocab != null)
                {
                    var attempt = new VocabularyAttempt
                    {
                        VocabularyWordId = matchedVocab.Id,
                        UserId = 1, // Default user
                        Activity = "Writing",
                        InputMode = InputMode.Text.ToString(), // Production activity
                        WasCorrect = vocabItem.UsageCorrect,
                        DifficultyWeight = 1.0f,
                        ContextType = "Application",
                        UserInput = vocabItem.UsedForm ?? vocabItem.DictionaryForm,
                        ExpectedAnswer = vocabItem.DictionaryForm,
                        ResponseTimeMs = 0,
                        UserConfidence = 0.5f
                    };

                    await _vocabularyProgressService.RecordAttemptAsync(attempt);
                    _logger.LogDebug("ProcessVocabularyFromWriting: Recorded attempt for '{UsedForm}' (correct: {UsageCorrect})",
                        vocabItem.UsedForm, vocabItem.UsageCorrect);
                }
            }
        }
        else
        {
            _logger.LogDebug("ProcessVocabularyFromWriting: No AI vocabulary analysis available");
        }
    }


    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _logger.LogDebug("WritingPage: Pausing activity timer");
            _timerService.Pause();
        }
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();
}