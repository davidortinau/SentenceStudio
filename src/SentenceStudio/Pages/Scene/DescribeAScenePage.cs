using MauiReactor.Shapes;
using The49.Maui.BottomSheet;
using System.Collections.Immutable;
using SentenceStudio.Services;
using System.Diagnostics;

namespace SentenceStudio.Pages.Scene;

class DescribeAScenePageState
{
    public int Id { get; set; }
    public string Description { get; set; }
    public Uri ImageUrl { get; set; } = new Uri("https://fdczvxmwwjwpwbeeqcth.supabase.co/storage/v1/object/public/images/239cddf0-4406-4bb7-9326-23511fe938cd/6ed5384c-8025-4395-837c-dd4a73c0a0c1.png");
    public string UserInput { get; set; }
    public bool IsBusy { get; set; }
    public ImmutableList<Sentence> Sentences { get; set; } = ImmutableList<Sentence>.Empty;
    public ImmutableList<SceneImage> Images { get; set; } = ImmutableList<SceneImage>.Empty;
    public ImmutableList<SceneImage> SelectedImages { get; set; } = ImmutableList<SceneImage>.Empty;
    public SelectionMode SelectionMode { get; set; }
    public bool IsDeleteVisible { get; set; }
    public bool IsSelecting { get; set; }
    public bool IsExplanationShown { get; set; }
    public string ExplanationText { get; set; }
    public bool IsGalleryVisible { get; set; }
    public string FeedbackMessage { get; set; }
    public string FeedbackType { get; set; } // "success", "info", "hint", "achievement"
    public bool ShowFeedback { get; set; }
}

partial class DescribeAScenePage : Component<DescribeAScenePageState>
{
    [Inject] AiService _aiService;
    [Inject] TeacherService _teacherService; // still used for grading
    [Inject] TranslationService _translationService; // added for translation
    [Inject] SceneImageService _sceneImageService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] VocabularyProgressService _progressService;
    [Inject] LearningResourceRepository _resourceRepo;
    LocalizationManager _localize => LocalizationManager.Instance;
    CommunityToolkit.Maui.Views.Popup? _popup;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["DescribeAScene"]}",
            ToolbarItem()
                .IconImageSource(ApplicationTheme.IconInfo)
                .OnClicked(ViewDescription),

            ToolbarItem()
                .IconImageSource(ApplicationTheme.IconImageExport)
                .OnClicked(LoadImage),

            ToolbarItem()
                .IconImageSource(ApplicationTheme.IconSwitch)
                .OnClicked(ManageImages),

            Grid("Auto,*,Auto", "*",
                RenderMainContent(),
                RenderInput(),
                RenderExplanationPopup(),
                RenderGalleryPopup(),
                RenderLoadingOverlay()
            )
        ).OnAppearing(LoadScene);
    }

    VisualNode RenderMainContent() => Grid("","*,*",
            Grid(
                Image()
                    .Source(State.ImageUrl)
                    .Aspect(Aspect.AspectFit)
                    .HorizontalOptions(LayoutOptions.Fill)
                    .VerticalOptions(LayoutOptions.Start)
                    .Margin(ApplicationTheme.Size160)
            ).GridColumn(0),

            VStack(spacing: 8,
                CollectionView()
                    .ItemsSource(State.Sentences, RenderSentence)
                    .Header(
                        ContentView(
                            Label($"{_localize["ISee"]}")
                                .Padding(ApplicationTheme.Size160)
                        )
                    ),
                
                // Enhanced feedback display
                State.ShowFeedback ? 
                    Border(
                        Label(State.FeedbackMessage)
                            .FontSize(16)
                            .Padding(12)
                            .Center()
                    )
                    .BackgroundColor(GetFeedbackBackgroundColor(State.FeedbackType))
                    .StrokeShape(new RoundRectangle().CornerRadius(8))
                    .StrokeThickness(0)
                    .Margin(ApplicationTheme.Size160, 8) 
                    : null
            )
            .GridColumn(1)
        )
        .GridRow(1);

    VisualNode RenderSentence(Sentence sentence) => VStack(spacing: 2,
            Label(sentence.Answer)
                .FontSize(18),
            Label($"Accuracy: {sentence.Accuracy}")
                .FontSize(12)
        )
        .Padding(ApplicationTheme.Size160)
        .OnTapped(() => ShowExplanation(sentence));

    VisualNode RenderInput() => new SfTextInputLayout(
            Entry()
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .ReturnType(ReturnType.Next)
                .OnCompleted(GradeMyDescription)
                .GridColumn(0)
                .FontSize(18)
        )
        .Hint($"{_localize["WhatDoYouSee"]}")
        .TrailingView(
            HStack(
                Button()
                    .Background(Colors.Transparent)
                    .ImageSource(ApplicationTheme.IconTranslate)
                    .OnClicked(TranslateInput),

                Button()
                    .Background(Colors.Transparent)
                    .ImageSource(ApplicationTheme.IconErase)
                    .OnClicked(ClearInput)
            ).Spacing(ApplicationTheme.Size40).HStart()
        )
        .GridRow(2)
        .Margin(ApplicationTheme.Size160);

    VisualNode RenderExplanationPopup() => new PopupHost(r => _popup = r)
        {
            VStack(spacing: 10,
                Label()
                    .Text(State.Description),
                Button("Close", () => {
                    SetState(s => s.IsExplanationShown = false);
                    _ = _popup?.CloseAsync();
                })
            ).Padding(20)
            .BackgroundColor(ApplicationTheme.LightBackground)
        }
        .IsShown(State.IsExplanationShown);

    VisualNode RenderGalleryPopup() => new PopupHost(r => _popup = r)
        {
            Grid("Auto,*,Auto", "",
                RenderHeader(),
                RenderGallery(),
                Button("Close")
                    .OnClicked(() => _ = _popup.CloseAsync())
                    .GridRow(2)
            )
                .Padding(ApplicationTheme.Size240)
                .RowSpacing(ApplicationTheme.Size120)
                .Margin(ApplicationTheme.Size240),
        }
        .IsShown(State.IsGalleryVisible && DeviceInfo.Idiom != DeviceIdiom.Phone);

    VisualNode RenderHeader() => Grid(
            Label("Choose an image")
                .ThemeKey(ApplicationTheme.Title1)
                .HStart(),

            HStack(spacing: ApplicationTheme.Size60,
                Button()
                    .ImageSource(ApplicationTheme.IconImageExport)
                    .Background(Colors.Transparent)
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(!State.IsDeleteVisible),

                Button()
                    .ImageSource(ApplicationTheme.IconCheckbox)
                    .Background(Colors.Transparent)
                    .Padding(0)
                    .Margin(0)
                    .VCenter(),

                Button()
                    .ImageSource(ApplicationTheme.IconDelete)
                    .Background(Colors.Transparent)
                    .TextColor(Colors.Black)
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(State.IsDeleteVisible)
            )
            .HEnd()
        );

    VisualNode RenderGallery() => CollectionView()
            .ItemsSource(State.Images, RenderGalleryItem)
            .SelectionMode(State.SelectionMode)
            .SelectedItems(State.SelectedImages.Cast<object>().ToList())
            .ItemsLayout(
                new HorizontalGridItemsLayout(4)
                    .VerticalItemSpacing(ApplicationTheme.Size240)
                    .HorizontalItemSpacing(ApplicationTheme.Size240)
            )
            .GridRow(1);
    

    VisualNode RenderGalleryItem(SceneImage image) => Grid(
            Image()
                .Source(new Uri(image.Url))
                .Aspect(Aspect.AspectFit)
                .HeightRequest(100)
                .OnTapped(() => OnImageSelected(image)),

            Image()
                .Source(ApplicationTheme.IconCheckbox)
                .VEnd()
                .HEnd()
                .IsVisible(State.IsSelecting)
                .Margin(4),

            Image()
                .Source(ApplicationTheme.IconCheckboxSelected)
                .VEnd()
                .HEnd()
                .IsVisible(image.IsSelected)
                .Margin(4)
        );

    VisualNode RenderLoadingOverlay() => Grid(
            Label("Analyzing the image...")
                .FontSize(64)
                .TextColor(ApplicationTheme.DarkOnLightBackground)
                .Center()
        )
        .BackgroundColor(Color.FromArgb("#80000000"))
        .IsVisible(State.IsBusy)
        .GridRowSpan(3);

    // Event handlers and other methods...
    async Task LoadScene()
    {
        SetState(s => s.IsBusy = true);

        try
        {
            var image = await _sceneImageService.GetRandomAsync();
            if (image != null)
            {
                SetState(s =>
                {
                    s.Id = image.Id;
                    s.ImageUrl = new Uri(image.Url);
                    s.Description = image.Description;
                });

                if (string.IsNullOrWhiteSpace(State.Description))
                {
                    await GetDescription();
                }
            }
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    async Task ManageImages()
    {
        var imgs = await _sceneImageService.ListAsync();
        SetState(s => s.Images = ImmutableList.CreateRange(imgs));

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            await BottomSheetManager.ShowAsync(
                () => new ImageGalleryBottomSheet()
                    .State(State),
                sheet =>
                {
                    sheet.HasBackdrop = true;
                    sheet.HasHandle = true;
                    sheet.CornerRadius = ApplicationTheme.Size120;
                    sheet.Detents = new Detent[]
                    {
                        new FullscreenDetent(),
                        new MediumDetent()
                    };
                }
            );
        }
        else
        {
            SetState(s => s.IsGalleryVisible = true);
        }
    }

    async Task ViewDescription()
    {
        SetState(s => s.IsExplanationShown = true);
    }

    void ShowExplanation(Sentence sentence)
    {
        SetState(s => 
        {
            s.ExplanationText = $"Original: {sentence.Answer}\n\n" +
                               $"Recommended: {sentence.RecommendedSentence}\n\n" +
                               $"Accuracy: {sentence.AccuracyExplanation}\n\n" +
                               $"Fluency: {sentence.FluencyExplanation}\n\n" +
                               $"Grammar Notes: {sentence.GrammarNotes}";
            s.IsExplanationShown = true;
        });
    }

    async Task GradeMyDescription()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput)) return;

        Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Starting grading for user input: '{State.UserInput}'");
        
        var stopwatch = Stopwatch.StartNew();
        SetState(s => s.IsBusy = true);
        
        try
        {
            var grade = await _teacherService.GradeDescription(State.UserInput, State.Description);
            stopwatch.Stop();
            
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Grading completed! Accuracy: {grade.Accuracy:F2}, Fluency: {grade.Fluency:F2}, Response time: {stopwatch.ElapsedMilliseconds}ms");
            
            var sentence = new Sentence 
            { 
                Answer = State.UserInput,
                Accuracy = grade.Accuracy,
                Fluency = grade.Fluency,
                FluencyExplanation = grade.FluencyExplanation,
                AccuracyExplanation = grade.AccuracyExplanation,
                RecommendedSentence = grade.RecommendedTranslation,
                GrammarNotes = grade.GrammarNotes?.Explanation
            };
            
            // Track user activity (legacy)
            await _userActivityRepository.SaveAsync(new UserActivity
            {
                Activity = SentenceStudio.Shared.Models.Activity.SceneDescription.ToString(),
                Input = State.UserInput,
                Accuracy = grade.Accuracy,
                Fluency = grade.Fluency,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
            
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Legacy activity tracking saved");
            
            // Enhanced vocabulary tracking - extract words from user input and process each
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Starting enhanced vocabulary tracking...");
            await ProcessVocabularyFromDescription(State.UserInput, grade, (int)stopwatch.ElapsedMilliseconds);
            
            // Show enhanced feedback
            ShowEnhancedFeedback(grade, State.UserInput);
            
            SetState(s =>
            {
                s.UserInput = string.Empty;
                s.Sentences = s.Sentences.Insert(0, sentence);
            });
            
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Grading process completed successfully!");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Error during grading: {ex.Message}");
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Stack trace: {ex.StackTrace}");
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    async Task TranslateInput()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput)) return;

        SetState(s => s.IsBusy = true);
        try
        {
            var translation = await _translationService.TranslateAsync(State.UserInput);
            await AppShell.DisplayToastAsync(translation);
            SetState(s => {
                s.Sentences = s.Sentences.Insert(0, new Sentence { Answer = translation, Accuracy = 100 });
                s.UserInput = string.Empty;
            });
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    void ClearInput()
    {
        SetState(s => s.UserInput = string.Empty);
    }

    async Task GetDescription()
    {
        SetState(s => s.IsBusy = true);
        try
        {
            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("DescribeThisImage.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync();
            }

            var description = await _aiService.SendImage(State.ImageUrl.AbsoluteUri, prompt);
            SetState(s => s.Description = description);
            
            await _sceneImageService.SaveAsync(new SceneImage
            {
                Id = State.Id,
                Url = State.ImageUrl.AbsoluteUri,
                Description = State.Description
            });
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    [RelayCommand]
    async Task LoadImage()
    {
        var result = await Application.Current.MainPage.DisplayPromptAsync(
            "Enter Image URL", 
            "Please enter the URL of the image you would like to describe.", 
            "OK", 
            "Cancel", 
            "https://example.com/something.jpg"
        );
        
        if (result != null)
        {
            SetState(s => 
            {
                s.ImageUrl = new Uri(result);
                s.Sentences = ImmutableList<Sentence>.Empty;
            });
            var sceneImage = new SceneImage { Url = result };
            SetState(s => s.Images = s.Images.Add(sceneImage));
            await _sceneImageService.SaveAsync(sceneImage);
            await GetDescription();
        }
    }

    async Task OnImageSelected(SceneImage image)
    {
        if (State.SelectionMode != SelectionMode.None)
        {             
            SetState(s => {
                if (s.SelectedImages.Contains(image))
                {
                    s.SelectedImages = s.SelectedImages.Remove(image);
                    image.IsSelected = false;
                }
                else
                {
                    s.SelectedImages = s.SelectedImages.Add(image);
                    image.IsSelected = true;
                }
            });
        }
        else
        {
            SetState(s => 
            {
                s.ImageUrl = new Uri(image.Url);
                s.Description = image.Description;
                s.Sentences = ImmutableList<Sentence>.Empty;
            });
            
            if(string.IsNullOrWhiteSpace(image.Description))
                await GetDescription();
        }
    }

    void ToggleSelection()
    {
        SetState(s => 
        {
            s.SelectionMode = s.SelectionMode == SelectionMode.None ? 
                SelectionMode.Multiple : SelectionMode.None;
            s.IsDeleteVisible = s.SelectionMode != SelectionMode.None;
            s.IsSelecting = s.SelectionMode != SelectionMode.None;
            
            foreach(var img in s.SelectedImages)
            {
                img.IsSelected = false;
            }
            s.SelectedImages = ImmutableList<SceneImage>.Empty;
        });
    }

    async Task DeleteImages()
    {
        if(State.SelectedImages.Count == 0)
            return;

        foreach(var img in State.SelectedImages)
        {
            await _sceneImageService.DeleteAsync(img);
            SetState(s => s.Images = s.Images.Remove(img));
        }
        SetState(s => s.SelectedImages = ImmutableList<SceneImage>.Empty);
    }
    
    Task ShowError()
    {
        return Application.Current.MainPage.DisplayAlert(
            "Error",
            "Something went wrong. Check the server.",
            "OK"
        );
    }
    
    // Enhanced tracking helper methods
    async Task ProcessVocabularyFromDescription(string userInput, GradeResponse grade, int responseTimeMs)
    {
        try
        {
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Starting vocabulary processing for input: '{userInput}'");
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Grade accuracy: {grade.Accuracy:F2}");
            
            // Extract vocabulary words from the user's description
            // For scene descriptions, we'll attempt to identify key vocabulary terms
            var words = await ExtractVocabularyFromUserInput(userInput, grade);
            
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Extracted {words.Count} vocabulary words to process");
            
            foreach (var vocabularyWord in words)
            {
                // Try to find specific usage information from AI analysis
                var vocabularyAnalysis = grade.VocabularyAnalysis?.FirstOrDefault(va => 
                    string.Equals(va.DictionaryForm, vocabularyWord.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(va.UsedForm, vocabularyWord.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase));
                
                // Determine if the usage was correct - use AI analysis if available, otherwise use overall accuracy
                bool wasCorrect;
                string actualUsedForm;
                
                if (vocabularyAnalysis != null)
                {
                    wasCorrect = vocabularyAnalysis.UsageCorrect;
                    actualUsedForm = vocabularyAnalysis.UsedForm;
                    Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Using AI analysis for '{vocabularyWord.TargetLanguageTerm}' - used as '{actualUsedForm}', correct: {wasCorrect}");
                }
                else
                {
                    wasCorrect = grade.Accuracy >= 0.7; // Consider 70%+ as correct usage
                    actualUsedForm = vocabularyWord.TargetLanguageTerm ?? "";
                    Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: No specific AI analysis for '{vocabularyWord.TargetLanguageTerm}', using overall accuracy: {wasCorrect}");
                }
                
                var attempt = new VocabularyAttempt
                {
                    VocabularyWordId = vocabularyWord.Id,
                    UserId = GetCurrentUserId(),
                    Activity = "SceneDescription",
                    InputMode = "TextEntry",
                    WasCorrect = wasCorrect,
                    DifficultyWeight = CalculateDescriptionDifficulty(userInput, vocabularyWord),
                    ContextType = "Sentence", // Scene descriptions are sentence-level usage
                    UserInput = userInput,
                    ExpectedAnswer = vocabularyWord.TargetLanguageTerm,
                    ResponseTimeMs = responseTimeMs,
                    UserConfidence = null // Could be added later with user self-assessment
                };
                
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Recording vocabulary attempt for Word ID: {vocabularyWord.Id}, '{vocabularyWord.TargetLanguageTerm}' - Correct: {wasCorrect}");
                
                var updatedProgress = await _progressService.RecordAttemptAsync(attempt);
                
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Progress updated! Word ID: {vocabularyWord.Id}, MasteryScore: {updatedProgress.MasteryScore:F2}, Phase: {updatedProgress.CurrentPhase}");
            }
            
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Completed processing {words.Count} vocabulary words");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Error in ProcessVocabularyFromDescription: {ex.Message}");
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Stack trace: {ex.StackTrace}");
        }
    }
    
    async Task<List<VocabularyWord>> ExtractVocabularyFromUserInput(string userInput, GradeResponse grade)
    {
        try
        {
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Extracting vocabulary from input: '{userInput}'");
            
            var matchedWords = new List<VocabularyWord>();
            
            // Check if we have AI-powered vocabulary analysis
            if (grade.VocabularyAnalysis != null && grade.VocabularyAnalysis.Any())
            {
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Using AI vocabulary analysis - found {grade.VocabularyAnalysis.Count} analyzed words");
                
                // Get available vocabulary from learning resources
                var resources = await _resourceRepo.GetAllResourcesAsync();
                var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>()).ToList();
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Loaded {allVocabulary.Count} vocabulary words from {resources.Count} resources");
                
                // Debug: Log details about each resource
                foreach (var resource in resources)
                {
                    var vocabCount = resource.Vocabulary?.Count ?? 0;
                    Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Resource '{resource.Title}' has {vocabCount} vocabulary words");
                    if (vocabCount > 0 && resource.Vocabulary != null)
                    {
                        var sampleWords = resource.Vocabulary.Take(3).Select(v => $"'{v.TargetLanguageTerm}'").ToList();
                        Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Sample words from '{resource.Title}': [{string.Join(", ", sampleWords)}]");
                    }
                }
                
                foreach (var analysis in grade.VocabularyAnalysis)
                {
                    Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Looking for dictionary form '{analysis.DictionaryForm}' (used as '{analysis.UsedForm}')");
                    
                    // Try to match by dictionary form (target language)
                    var matchedWord = allVocabulary.FirstOrDefault(v => 
                        string.Equals(v.TargetLanguageTerm?.Trim(), analysis.DictionaryForm.Trim(), StringComparison.OrdinalIgnoreCase));
                    
                    if (matchedWord == null)
                    {
                        // Try to match by used form in case the dictionary form wasn't identified correctly
                        matchedWord = allVocabulary.FirstOrDefault(v => 
                            string.Equals(v.TargetLanguageTerm?.Trim(), analysis.UsedForm.Trim(), StringComparison.OrdinalIgnoreCase));
                    }
                    
                    if (matchedWord == null && !string.IsNullOrEmpty(analysis.Meaning))
                    {
                        // Try to match by English meaning
                        matchedWord = allVocabulary.FirstOrDefault(v => 
                            !string.IsNullOrEmpty(v.NativeLanguageTerm) && 
                            v.NativeLanguageTerm.Contains(analysis.Meaning, StringComparison.OrdinalIgnoreCase));
                    }
                    
                    if (matchedWord != null)
                    {
                        if (!matchedWords.Any(w => w.Id == matchedWord.Id))
                        {
                            matchedWords.Add(matchedWord);
                            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: ✅ Matched Word ID: {matchedWord.Id}, '{matchedWord.TargetLanguageTerm}' -> '{matchedWord.NativeLanguageTerm}', Usage correct: {analysis.UsageCorrect}");
                        }
                        else
                        {
                            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: ⚠️ Duplicate word avoided: {matchedWord.TargetLanguageTerm}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: ❌ No match found for '{analysis.DictionaryForm}' (used as '{analysis.UsedForm}')");
                    }
                }
            }
            else
            {
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: ⚠️ No AI vocabulary analysis available, falling back to simple matching");
                
                // Fallback to simple string matching (legacy behavior)
                var resources = await _resourceRepo.GetAllResourcesAsync();
                var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>()).ToList();
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Found {allVocabulary.Count} total vocabulary words in {resources.Count} resources");
                
                var inputWords = userInput.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Input words: [{string.Join(", ", inputWords)}]");
                
                foreach (var vocab in allVocabulary)
                {
                    // Check if any form of the vocabulary word appears in the user input
                    if (!string.IsNullOrWhiteSpace(vocab.TargetLanguageTerm) && 
                        inputWords.Any(w => w.Contains(vocab.TargetLanguageTerm.ToLower()) || 
                                           vocab.TargetLanguageTerm.ToLower().Contains(w)))
                    {
                        matchedWords.Add(vocab);
                        Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: MATCH found! '{vocab.TargetLanguageTerm}' matches in user input");
                    }
                    
                    // Also check native language terms (in case user mixed languages)
                    if (!string.IsNullOrWhiteSpace(vocab.NativeLanguageTerm) && 
                        inputWords.Any(w => w.Contains(vocab.NativeLanguageTerm.ToLower()) || 
                                           vocab.NativeLanguageTerm.ToLower().Contains(w)))
                    {
                        if (!matchedWords.Contains(vocab)) // Avoid duplicates
                        {
                            matchedWords.Add(vocab);
                            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: MATCH found! '{vocab.NativeLanguageTerm}' (native) matches in user input");
                        }
                    }
                }
                
                // Limit to avoid overwhelming
                matchedWords = matchedWords.Take(5).ToList();
            }
            
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Extraction completed - found {matchedWords.Count} vocabulary words");
            foreach (var word in matchedWords)
            {
                Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Final matched word - Target: '{word.TargetLanguageTerm}', Native: '{word.NativeLanguageTerm}'");
            }
            
            return matchedWords;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Error in ExtractVocabularyFromUserInput: {ex.Message}");
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Stack trace: {ex.StackTrace}");
            return new List<VocabularyWord>();
        }
    }
    
    float CalculateDescriptionDifficulty(string userInput, VocabularyWord vocabularyWord)
    {
        float difficulty = 1.0f;
        
        // Longer descriptions are generally more challenging
        int wordCount = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 10) difficulty += 0.3f;
        if (wordCount > 20) difficulty += 0.3f;
        
        // Complex sentence structures increase difficulty
        if (userInput.Contains(",") || userInput.Contains("and") || userInput.Contains("but"))
            difficulty += 0.2f;
            
        // Note: VocabularyWord doesn't have Notes property, so we'll skip that check
        // Could add difficulty based on word length or other factors
        if (!string.IsNullOrWhiteSpace(vocabularyWord.TargetLanguageTerm) && vocabularyWord.TargetLanguageTerm.Length > 6)
            difficulty += 0.2f;
            
        return Math.Min(difficulty, 2.0f); // Cap at 2.0
    }
    
    void ShowEnhancedFeedback(GradeResponse grade, string userInput)
    {
        try
        {
            // Determine feedback based on accuracy and fluency
            if (grade.Accuracy >= 0.8 && grade.Fluency >= 0.8)
            {
                ShowFeedback("🌟 Excellent description! Your Korean is very natural!", "achievement");
            }
            else if (grade.Accuracy >= 0.7)
            {
                ShowFeedback($"✅ Good work! Accuracy: {(int)(grade.Accuracy * 100)}%", "success");
            }
            else if (grade.Accuracy >= 0.5)
            {
                ShowFeedback($"📝 Keep practicing! Some improvements needed - {(int)(grade.Accuracy * 100)}% accuracy", "info");
            }
            else
            {
                ShowFeedback("🔍 Try focusing on simpler sentences first", "hint");
            }
            
            // Additional context-specific feedback
            if (userInput.Length > 100)
            {
                ShowFeedback("💪 Great job with a detailed description!", "info");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ DescribeAScenePage: Error showing feedback: {ex.Message}");
        }
    }
    
    void ShowFeedback(string message, string type)
    {
        SetState(s => 
        {
            s.FeedbackMessage = message;
            s.FeedbackType = type;
            s.ShowFeedback = true;
        });
        
        // Auto-hide feedback after 4 seconds
        Task.Run(async () =>
        {
            await Task.Delay(4000);
            SetState(s => s.ShowFeedback = false);
        });
    }
    
    int GetCurrentUserId()
    {
        // For now, return a default user ID
        // This should be replaced with actual user management
        return 1;
    }
    
    Color GetFeedbackBackgroundColor(string feedbackType)
    {
        return feedbackType switch
        {
            "success" => ApplicationTheme.Primary,
            "achievement" => Colors.Gold,
            "info" => ApplicationTheme.Secondary,
            "hint" => ApplicationTheme.Gray300,
            _ => ApplicationTheme.Gray200
        };
    }
}