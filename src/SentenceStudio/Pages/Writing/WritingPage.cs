using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Services;
using System.Text;
using System.Diagnostics;
using Fonts;



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
                .ThemeKey(ApplicationTheme.Title3)
                .GridColumn(0),
            Label(_localize["Accuracy"])
                .ThemeKey(ApplicationTheme.Title3)
                .Center()
                .GridColumn(1),
            Label(_localize["Fluency"])
                .ThemeKey(ApplicationTheme.Title3)
                .Center()
                .GridColumn(2),
            Label(_localize["Actions"])
                .ThemeKey(ApplicationTheme.Title3)
                .Center()
                .GridColumn(3)
        ).Margin(ApplicationTheme.Size160);

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
                        .ThemeKey(ApplicationTheme.Title3),
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
                    .ImageSource(ApplicationTheme.IconDictionary)
                    .OnClicked(TranslateInput)
            )
            .Hint(_localize["WhatDoYouWantToSay"].ToString())
            .GridRow(1)
            .GridColumn(0)

        ).GridRow(2)
        .Padding(ApplicationTheme.Size160)
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
    
    async Task LoadVocabulary()
    {
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
            Debug.WriteLine($"🏴‍☠️ WritingPage: Error processing vocabulary: {ex.Message}");
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
                    .ImageSource(ApplicationTheme.IconCopy)
                    .OnClicked(() => UseVocab(sentence.Answer)),
                Button()
                    .BackgroundColor(Colors.Transparent)
                    .TextColor(Theme.IsLightTheme ? 
                        ApplicationTheme.LightOnDarkBackground :
                        ApplicationTheme.DarkOnLightBackground)
                    .ImageSource(ApplicationTheme.IconInfo)
                    .OnClicked(() => ShowExplanation(sentence))
            ).Center().GridColumn(3)
        );

    VisualNode RenderMobileSentence(Sentence sentence) =>
        SwipeView(
            SwipeItemView(
                Grid(
                    Label().Text(FluentUI.copy_24_regular).FontSize(24).Center()
                ).BackgroundColor(Colors.Red).WidthRequest(60)
            ).OnInvoked(() => UseVocab(sentence.Answer)).HStart(),
            SwipeItemView(
                Grid(
                    Label().Text(FluentUI.info_24_regular).FontSize(24).Center()
                ).BackgroundColor(Colors.Orange).WidthRequest(60)
            ).OnInvoked(() => ShowExplanation(sentence)).HEnd(),
            Grid("",columns: "*,*",
                Label(sentence.Answer).VCenter().GridColumn(0),
                Label(sentence.Accuracy.ToString()).Center().GridColumn(1)
            )
            .Background(Theme.IsLightTheme ? 
                ApplicationTheme.LightCardBackgroundBrush : 
                ApplicationTheme.DarkCardBackgroundBrush)
        );    Task ShowExplanation(Sentence sentence)
    {
        string explanation = $"Original: {sentence.Answer}\n\n" +
            $"Recommended: {sentence.RecommendedSentence}\n\n" +
            $"Accuracy: {sentence.AccuracyExplanation}\n\n" +
            $"Fluency: {sentence.FluencyExplanation}\n\n" +
            $"Additional Notes: {sentence.GrammarNotes}";

        return Application.Current.MainPage.DisplayAlert(
            _localize["Explanation"].ToString(),
            explanation,
            _localize["OK"].ToString());
    }

    /// <summary>
    /// Process vocabulary from writing activity for enhanced tracking
    /// </summary>
    async Task ProcessVocabularyFromWriting(string userInput, GradeResponse gradeResponse)
    {
        Debug.WriteLine($"🏴‍☠️ ProcessVocabularyFromWriting: Starting vocabulary analysis for WritingPage");

        if (gradeResponse?.VocabularyAnalysis != null && gradeResponse.VocabularyAnalysis.Any())
        {
            Debug.WriteLine($"🏴‍☠️ ProcessVocabularyFromWriting: Found {gradeResponse.VocabularyAnalysis.Count} vocabulary items from AI analysis");

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
                        InputMode = "TextEntry",
                        WasCorrect = vocabItem.UsageCorrect,
                        DifficultyWeight = 1.0f,
                        ContextType = "Application",
                        UserInput = vocabItem.UsedForm ?? vocabItem.DictionaryForm,
                        ExpectedAnswer = vocabItem.DictionaryForm,
                        ResponseTimeMs = 0,
                        UserConfidence = 0.5f
                    };
                    
                    await _vocabularyProgressService.RecordAttemptAsync(attempt);
                    Debug.WriteLine($"🏴‍☠️ ProcessVocabularyFromWriting: Recorded attempt for '{vocabItem.UsedForm}' (correct: {vocabItem.UsageCorrect})");
                }
            }
        }
        else
        {
            Debug.WriteLine($"🏴‍☠️ ProcessVocabularyFromWriting: No AI vocabulary analysis available");
        }
    }
}