using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Services;
using System.Text;
using Microsoft.Extensions.Logging;
using Fonts;
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
        )
        .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new Components.ActivityTimerBar() : null)
        .OnAppearing(LoadVocabulary);
    }

    VisualNode SentencesHeader() =>
        Grid("", columns: "*,*,*,*",
            Label(_localize["Sentence"])
                .ThemeKey(MyTheme.Title3)
                .GridColumn(0),
            Label(_localize["Accuracy"])
                .ThemeKey(MyTheme.Title3)
                .Center()
                .GridColumn(1),
            Label(_localize["Fluency"])
                .ThemeKey(MyTheme.Title3)
                .Center()
                .GridColumn(2),
            Label(_localize["Actions"])
                .ThemeKey(MyTheme.Title3)
                .Center()
                .GridColumn(3)
        ).Margin(MyTheme.Size160);

    VisualNode SentencesScrollView() =>
        ScrollView(
            VStack(spacing: 0,
                State.Sentences.Select(sentence =>
                    DeviceInfo.Idiom == DeviceIdiom.Desktop ?
                        RenderDesktopSentence(sentence) :
                        RenderMobileSentence(sentence)
                )
            ).Margin(MyTheme.LayoutSpacing, 0)
        ).GridRow(1);

    VisualNode InputUI() =>
        Grid(rows: "Auto,Auto,Auto", columns: "*,Auto",
            ScrollView(
                VStack(spacing: MyTheme.Size40,
                    Label(_localize["ChooseAVocabularyWord"])
                        .ThemeKey(MyTheme.Title3),
                    HStack(spacing: MyTheme.Size40,
                        State.VocabBlocks.Select(word =>
                            Button(word.TargetLanguageTerm)
                                .Background(MyTheme.Gray200)
                                .TextColor(MyTheme.Gray900)
                                .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                                .Padding(MyTheme.Size40)
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
                    .Background(Colors.Transparent)
                    .HEnd()
                    .GridColumn(1)
                    .ImageSource(MyTheme.IconDictionary)
                    .OnClicked(TranslateInput)
            )
            .Hint($"{_localize["WhatDoYouWantToSay"]}")
            .GridRow(1)
            .GridColumn(0)

        ).GridRow(2)
        .Padding(MyTheme.Size160)
        .RowSpacing(MyTheme.Size40);

    VisualNode LoadingOverlay() =>
        Grid(
            Label("Thinking...")
                .FontSize(64)
                .TextColor(Theme.IsLightTheme ?
                    MyTheme.LightOnDarkBackground :
                    MyTheme.DarkOnLightBackground)
                .Center()
        )
        .Background(Color.FromArgb("#80000000"))
        .GridRowSpan(2)
        .IsVisible(State.IsBusy);

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
                    SetState(s => s.VocabBlocks = new List<VocabularyWord>());

                    // Show message to user
                    await AppShell.DisplayToastAsync("No vocabulary available in the selected resource");
                }
            }
            else
            {
                // No resource selected or invalid ID
                SetState(s => s.VocabBlocks = new List<VocabularyWord>());
                await AppShell.DisplayToastAsync("Please select a valid learning resource");
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

    VisualNode RenderDesktopSentence(Sentence sentence) =>
        Grid("", columns: "*,*,*,*",
            Label(sentence.Answer).GridColumn(0),
            Label(sentence.Accuracy.ToString()).Center().GridColumn(1),
            Label(sentence.Fluency.ToString()).Center().GridColumn(2),
            HStack(spacing: 4,
                Button()
                    .Background(Colors.Transparent)
                    .TextColor(Theme.IsLightTheme ?
                        MyTheme.LightOnDarkBackground :
                        MyTheme.DarkOnLightBackground)
                    .ImageSource(MyTheme.IconCopy)
                    .OnClicked(() => UseVocab(sentence.Answer)),
                Button()
                    .Background(Colors.Transparent)
                    .TextColor(Theme.IsLightTheme ?
                        MyTheme.LightOnDarkBackground :
                        MyTheme.DarkOnLightBackground)
                    .ImageSource(MyTheme.IconInfo)
                    .OnClicked(() => ShowExplanation(sentence))
            ).Center().GridColumn(3)
        );

    VisualNode RenderMobileSentence(Sentence sentence) =>
        SwipeView(
            SwipeItemView(
                Grid(
                    Label().Text(FluentUI.copy_24_regular).FontSize(24).Center()
                ).Background(Colors.Red).WidthRequest(60)
            ).OnInvoked(() => UseVocab(sentence.Answer)).HStart(),
            SwipeItemView(
                Grid(
                    Label().Text(FluentUI.info_24_regular).FontSize(24).Center()
                ).Background(Colors.Orange).WidthRequest(60)
            ).OnInvoked(() => ShowExplanation(sentence)).HEnd(),
            Grid("", columns: "*,*",
                Label(sentence.Answer).VCenter().GridColumn(0),
                Label(sentence.Accuracy.ToString()).Center().GridColumn(1)
            )
            .Background(Theme.IsLightTheme ?
                MyTheme.LightCardBackgroundBrush :
                MyTheme.DarkCardBackgroundBrush)
        ); async Task ShowExplanation(Sentence sentence)
    {
        string explanation = $"Original: {sentence.Answer}\n\n" +
            $"Recommended: {sentence.RecommendedSentence}\n\n" +
            $"Accuracy: {sentence.AccuracyExplanation}\n\n" +
            $"Fluency: {sentence.FluencyExplanation}\n\n" +
            $"Additional Notes: {sentence.GrammarNotes}";

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

    protected override void OnWillUnmount()
    {
        base.OnWillUnmount();

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _logger.LogDebug("WritingPage: Pausing activity timer");
            _timerService.Pause();
        }
    }
}