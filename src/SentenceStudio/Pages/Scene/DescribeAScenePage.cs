using MauiReactor.Shapes;
using System.Collections.Immutable;
using SentenceStudio.Services;
using SentenceStudio.Pages.Dashboard;
using Microsoft.Extensions.Logging;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.Scene;

class DescribeAScenePageState
{
    public int Id { get; set; }
    public string Description { get; set; }
    public Uri ImageUrl { get; set; } = new Uri("https://fdczvxmwwjwpwbeeqcth.supabase.co/storage/v1/object/public/images/239cddf0-4406-4bb7-9326-23511fe938cd/6ed5384c-8025-4395-837c-dd4a73c0a0c1.png");
    public string UserInput { get; set; }
    public bool IsBusy { get; set; }
    public string LoadingMessage { get; set; } = "Loading...";
    public ImmutableList<Sentence> Sentences { get; set; } = ImmutableList<Sentence>.Empty;
    public ImmutableList<SceneImage> Images { get; set; } = ImmutableList<SceneImage>.Empty;
    public ImmutableList<SceneImage> SelectedImages { get; set; } = ImmutableList<SceneImage>.Empty;
    public SelectionMode SelectionMode { get; set; }
    public bool IsDeleteVisible { get; set; }
    public bool IsSelecting { get; set; }
    public bool IsExplanationShown { get; set; }
    public string ExplanationText { get; set; }
    public bool IsGalleryVisible { get; set; }

    // Added: controls the new SfBottomSheet visibility for mobile
    public bool IsGalleryBottomSheetOpen { get; set; } = false;

    // Bug fix: Prevent reloading random image on every OnAppearing
    public bool SceneLoaded { get; set; } = false;
}

partial class DescribeAScenePage : Component<DescribeAScenePageState, ActivityProps>
{
    [Inject] AiService _aiService;
    [Inject] TeacherService _teacherService; // still used for grading
    [Inject] TranslationService _translationService; // added for translation
    [Inject] SceneImageService _sceneImageService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] VocabularyProgressService _progressService;
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    [Inject] ILogger<DescribeAScenePage> _logger;
    [Inject] NativeThemeService _themeService;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["DescribeAScene"]}",
            ToolbarItem()
                .IconImageSource(MyTheme.IconInfo)
                .OnClicked(ViewDescription),

            // Removed direct toolbar Add/Image button — image add is now inside the gallery bottom sheet

            ToolbarItem()
                .IconImageSource(MyTheme.IconSwitch)
                .OnClicked(ManageImages),

            // Main grid - only 2 content rows: main content (*) and input (Auto)
            // Overlays (popup, loading, gallery) span full grid without affecting layout
            Grid("*,Auto", "*",
                RenderMainContent(),
                RenderInput(),
                RenderLoadingOverlay(),
                RenderGalleryBottomSheet()
            )
        )
        .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new Components.ActivityTimerBar() : null)
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(LoadScene);
    }

    VisualNode RenderMainContent() => Grid("", "*,*",
            Grid(
                Image()
                    .Source(State.ImageUrl)
                    .Aspect(Aspect.AspectFit)
                    .HFill()
                    .VStart()
                    .Margin(MyTheme.Size160)
                    .AutomationId("SceneImage") // Appium: main scene image
            ).GridColumn(0),

            Grid(
                CollectionView()
                    .ItemsSource(State.Sentences, RenderSentence)
                    .AutomationId("SentencesList") // Appium: list of scored sentences
                    .Header(
                        ContentView(
                            Label($"{_localize["ISee"]}")
                                .Padding(MyTheme.Size160)
                        )
                    )
            )
            .GridColumn(1)
        )
        .GridRow(0);  // Changed from GridRow(1) to GridRow(0)

    VisualNode RenderSentence(Sentence sentence) => VStack(spacing: 2,
            Label(sentence.Answer)
                .FontSize(18),
            sentence.IsGrading
                ? Label("Grading...").FontSize(12).TextColor(MyTheme.Gray500)
                : Label($"Accuracy: {sentence.Accuracy}").FontSize(12)
        )
        .Padding(MyTheme.Size160)
        .OnTapped(() => { if (!sentence.IsGrading) ShowExplanation(sentence); });

    VisualNode RenderInput() => new SfTextInputLayout(
            Entry()
                .Class("form-control")
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .ReturnType(ReturnType.Next)
                .OnCompleted(GradeMyDescription)
                .GridColumn(0)
                .FontSize(18)
                .AutomationId("DescriptionEntry") // Appium: text input field
        )
        .Hint($"{_localize["WhatDoYouSee"]}")
        .TrailingView(
            HStack(
                Button()
                    .Background(Colors.Transparent)
                    .ImageSource(MyTheme.IconSend)
                    .OnClicked(GradeMyDescription)
                    .AutomationId("SubmitButton"),

                Button()
                    .Background(Colors.Transparent)
                    .ImageSource(MyTheme.IconTranslate)
                    .OnClicked(TranslateInput),

                Button()
                    .Background(Colors.Transparent)
                    .ImageSource(MyTheme.IconErase)
                    .OnClicked(ClearInput)
            ).Spacing(MyTheme.Size40).HStart()
        )
        .GridRow(1)  // Changed from GridRow(2) to GridRow(1)
        .Margin(MyTheme.Size160);

    /// <summary>
    /// Renders a mobile/desktop SfBottomSheet that contains the full gallery management UI.
    /// Top-aligned actions (Add, MultiSelect, Delete, Close) and a boxy card style.
    /// </summary>
    VisualNode RenderGalleryBottomSheet() =>
        new SfBottomSheet(

                Grid("Auto,Auto,*,Auto", "*",

                    Label("Gallery")
                        .FontSize(18)
                        .FontAttributes(FontAttributes.Bold),

                    HStack(
                        Button().ImageSource(MyTheme.IconImageExport).Background(Colors.Transparent).OnClicked(LoadImage),

                        Button().ImageSource(MyTheme.IconMultiSelect).Background(Colors.Transparent).OnClicked(ToggleSelection),

                        Button().ImageSource(MyTheme.IconDelete).Background(Colors.Transparent).OnClicked(DeleteImages).IsVisible(State.IsDeleteVisible),

                        Button().ImageSource(MyTheme.IconClose).Background(Colors.Transparent).OnClicked(() => SetState(s => s.IsGalleryBottomSheetOpen = false))
                    ).Spacing(MyTheme.Size40).HEnd().GridRow(1),


                    // Gallery content row
                    RenderGallery(),

                    // Small status row under the gallery (keeps hierarchy but is unobtrusive)
                    Label(State.IsSelecting ? $"Selected: {State.SelectedImages.Count}" : "Tap an image to select it")
                        .Padding(MyTheme.Size160).GridRow(3)
                )//grid

        )
        .GridRowSpan(2)  // Changed from 3 to 2 rows
        .IsOpen(State.IsGalleryBottomSheetOpen);

    VisualNode RenderGallery() => CollectionView()
            .ItemsSource(State.Images, RenderGalleryItem)
            .SelectionMode(State.SelectionMode)
            .SelectedItems(State.SelectedImages.Cast<object>().ToList())
            .ItemsLayout(
                // Bug fix: Use VerticalGridItemsLayout for 4 columns with vertical scroll
                new VerticalGridItemsLayout(4)
                    .VerticalItemSpacing(MyTheme.Size80)
                    .HorizontalItemSpacing(MyTheme.Size80)
            ).GridRow(2);


    // Bug fix: Add explicit sizing to gallery items to prevent overlap
    VisualNode RenderGalleryItem(SceneImage image) => Grid(
            Image()
                .Source(new Uri(image.Url))
                .Aspect(Aspect.AspectFill) // Use AspectFill for uniform card appearance
                .HeightRequest(100)
                .WidthRequest(100) // Explicit width for consistent sizing
                .OnTapped(() => OnImageSelected(image)),

            // Checkbox background to avoid overlapping text/artifacts
            Border(
                Image().Source(MyTheme.IconCheckbox).WidthRequest(24).HeightRequest(24)
            )
            .StrokeThickness(0)
            .Background(Color.FromArgb("#CCFFFFFF"))
            .StrokeShape(new RoundRectangle().CornerRadius(12))
            .Padding(4)
            .VEnd()
            .HEnd()
            .IsVisible(State.IsSelecting)
            .Margin(4),

            Border(
                Image().Source(MyTheme.IconCheckboxSelected).WidthRequest(24).HeightRequest(24)
            )
            .StrokeThickness(0)
            .Background(Color.FromArgb("#CCFFFFFF"))
            .StrokeShape(new RoundRectangle().CornerRadius(12))
            .Padding(4)
            .VEnd()
            .HEnd()
            .IsVisible(image.IsSelected)
            .Margin(4)
        );

    VisualNode RenderLoadingOverlay() => Grid(
            Label(State.LoadingMessage)
                .FontSize(32)
                .TextColor(Colors.White)
                .Center()
        )
        .Background(Color.FromArgb("#80000000"))
        .IsVisible(State.IsBusy)
        .GridRowSpan(2);  // Changed from 3 to 2 rows

    // Event handlers and other methods...
    async Task LoadScene()
    {
        // Bug fix: Skip if scene already loaded to prevent random image changes
        if (State.SceneLoaded)
        {
            _logger.LogDebug("DescribeAScenePage: Scene already loaded, skipping reload");
            return;
        }

        // Start activity timer if launched from Today's Plan (only once)
        if (Props?.FromTodaysPlan == true && !_timerService.IsActive)
        {
            _logger.LogDebug("DescribeAScenePage: Starting activity timer for SceneDescription, PlanItemId: {PlanItemId}", Props.PlanItemId);
            _timerService.StartSession("SceneDescription", Props.PlanItemId);
        }

        SetState(s =>
        {
            s.IsBusy = true;
            s.LoadingMessage = "Loading scene...";
        });

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
                    s.SceneLoaded = true; // Mark scene as loaded
                    s.IsBusy = false; // Hide loading once image is loaded
                });

                // Only analyze if we don't have a saved description
                if (string.IsNullOrWhiteSpace(State.Description))
                {
                    await GetDescription();
                }
            }
            else
            {
                SetState(s => s.IsBusy = false);
            }
        }
        catch
        {
            SetState(s => s.IsBusy = false);
            throw;
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
            _logger.LogDebug("DescribeAScenePage: Pausing activity timer");
            _timerService.Pause();
        }
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    async Task ManageImages()
    {
        var imgs = await _sceneImageService.ListAsync();
        SetState(s => s.Images = ImmutableList.CreateRange(imgs));

        _logger.LogDebug("DescribeAScenePage: ManageImages invoked, loaded {Count} images", imgs?.Count ?? 0);

        // Always use the SfBottomSheet for managing images on all platforms.
        SetState(s =>
        {
            s.IsGalleryBottomSheetOpen = true;
            s.IsGalleryVisible = false; // ensure popup is not shown
        });

        _logger.LogDebug("DescribeAScenePage: IsGalleryBottomSheetOpen={IsOpen}, IsGalleryVisible={IsVisible}", State.IsGalleryBottomSheetOpen, State.IsGalleryVisible);
    }

    async Task ViewDescription()
    {
        // Bug fix: Fetch description if empty before showing popup
        if (string.IsNullOrWhiteSpace(State.Description))
        {
            _logger.LogDebug("DescribeAScenePage: Description is empty, fetching before showing popup");
            await GetDescription();
            _logger.LogDebug("DescribeAScenePage: After GetDescription, Description is now: {DescriptionLength} chars", State.Description?.Length ?? 0);
        }
        else
        {
            _logger.LogDebug("DescribeAScenePage: Description already exists: {DescriptionLength} chars", State.Description?.Length ?? 0);
        }

        var text = !string.IsNullOrWhiteSpace(State.Description)
            ? State.Description
            : "No description available. Tap the info button after loading an image.";

        await IPopupService.Current.PushAsync(new SimpleActionPopup
        {
            Title = "Description",
            Text = text,
            ActionButtonText = "Close",
            ShowSecondaryActionButton = false
        });
    }

    async void ShowExplanation(Sentence sentence)
    {
        var explanationText = $"Original: {sentence.Answer}\n\n" +
                             $"Recommended: {sentence.RecommendedSentence}\n\n" +
                             $"Accuracy: {sentence.AccuracyExplanation}\n\n" +
                             $"Fluency: {sentence.FluencyExplanation}\n\n" +
                             $"Grammar Notes: {sentence.GrammarNotes}";

        await IPopupService.Current.PushAsync(new SimpleActionPopup
        {
            Title = "Explanation",
            Text = explanationText,
            ActionButtonText = "Close",
            ShowSecondaryActionButton = false
        });
    }

    async Task GradeMyDescription()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput)) return;

        // Capture input before clearing - grading happens asynchronously
        var userInput = State.UserInput;
        var description = State.Description;

        // Create pending sentence and add to list immediately
        var pendingSentence = new Sentence
        {
            Answer = userInput,
            IsGrading = true
        };

        // Clear input and add sentence immediately so user sees it and can keep typing
        SetState(s =>
        {
            s.UserInput = string.Empty;
            s.Sentences = s.Sentences.Insert(0, pendingSentence);
        });

        _logger.LogDebug("DescribeAScenePage: Starting grading for user input: {UserInput}", userInput);

        var stopwatch = Stopwatch.StartNew();
        // No loading overlay - grading happens in background

        try
        {
            var grade = await _teacherService.GradeDescription(userInput, description);
            stopwatch.Stop();

            _logger.LogDebug("DescribeAScenePage: Grading completed! Accuracy: {Accuracy:F2}, Fluency: {Fluency:F2}, ResponseTime: {ElapsedMs}ms", grade.Accuracy, grade.Fluency, stopwatch.ElapsedMilliseconds);
            _logger.LogDebug("DescribeAScenePage: AccuracyExpl: {AccuracyExpl}, FluencyExpl: {FluencyExpl}, Recommended: {Recommended}",
                grade.AccuracyExplanation ?? "(null)",
                grade.FluencyExplanation ?? "(null)",
                grade.GrammarNotes?.RecommendedTranslation ?? "(null)");

            var sentence = new Sentence
            {
                Answer = userInput,
                Accuracy = grade.Accuracy,
                Fluency = grade.Fluency,
                FluencyExplanation = grade.FluencyExplanation,
                AccuracyExplanation = grade.AccuracyExplanation,
                // RecommendedTranslation is inside GrammarNotes from the AI response
                RecommendedSentence = grade.GrammarNotes?.RecommendedTranslation ?? grade.RecommendedTranslation,
                GrammarNotes = grade.GrammarNotes?.Explanation
            };

            // Track user activity (legacy)
            await _userActivityRepository.SaveAsync(new UserActivity
            {
                Activity = SentenceStudio.Shared.Models.Activity.SceneDescription.ToString(),
                Input = userInput,
                Accuracy = grade.Accuracy,
                Fluency = grade.Fluency,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });

            _logger.LogDebug("DescribeAScenePage: Legacy activity tracking saved");

            // Enhanced vocabulary tracking - extract words from user input and process each
            _logger.LogDebug("DescribeAScenePage: Starting enhanced vocabulary tracking...");
            await ProcessVocabularyFromDescription(userInput, grade, (int)stopwatch.ElapsedMilliseconds);

            // Show feedback as toast
            await ShowEnhancedFeedback(grade, userInput);

            // Update the pending sentence with grading results
            SetState(s =>
            {
                var index = s.Sentences.IndexOf(pendingSentence);
                if (index >= 0)
                {
                    s.Sentences = s.Sentences.SetItem(index, sentence);
                }
            });

            _logger.LogDebug("DescribeAScenePage: Grading process completed successfully!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DescribeAScenePage: Error during grading");
        }
        // No finally block needed - no loading state to reset
    }

    async Task TranslateInput()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput)) return;

        SetState(s => s.IsBusy = true);
        try
        {
            var translation = await _translationService.TranslateAsync(State.UserInput);
            await AppShell.DisplayToastAsync(translation);
            SetState(s =>
            {
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
        SetState(s =>
        {
            s.IsBusy = true;
            s.LoadingMessage = "Analyzing the image...";
        });
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
        var formFields = new List<FormField> { new FormField { Placeholder = "https://example.com/something.jpg" } };
        var formPopup = new FormPopup
        {
            Title = "Enter Image URL",
            Text = "Please enter the URL of the image you would like to describe.",
            Items = formFields,
            ActionButtonText = "OK",
            SecondaryActionText = "Cancel"
        };
        List<string?>? formResult = await IPopupService.Current.PushAsync(formPopup);
        var result = formResult?.FirstOrDefault();

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
            SetState(s =>
            {
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
                // Close both sheet and popup flags to ensure UI is hidden
                s.IsGalleryBottomSheetOpen = false;
                s.IsGalleryVisible = false;
            });

            try
            {
                // Persist selection (ensure the image is saved/updated server-side if needed)
                await _sceneImageService.SaveAsync(image);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DescribeAScenePage: Error saving selected image");
            }

            if (string.IsNullOrWhiteSpace(image.Description))
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

            foreach (var img in s.SelectedImages)
            {
                img.IsSelected = false;
            }
            s.SelectedImages = ImmutableList<SceneImage>.Empty;
        });
    }

    async Task DeleteImages()
    {
        if (State.SelectedImages.Count == 0)
            return;

        foreach (var img in State.SelectedImages)
        {
            await _sceneImageService.DeleteAsync(img);
            SetState(s => s.Images = s.Images.Remove(img));
        }

        // Clear selections and exit selection mode
        SetState(s =>
        {
            s.SelectedImages = ImmutableList<SceneImage>.Empty;
            s.SelectionMode = SelectionMode.None;
            s.IsDeleteVisible = false;
            s.IsSelecting = false;
        });
    }

    Task ShowError()
    {
        return IPopupService.Current.PushAsync(new SimpleActionPopup
        {
            Title = "Error",
            Text = "Something went wrong. Check the server.",
            ActionButtonText = "OK",
            ShowSecondaryActionButton = false
        });
    }

    // Enhanced tracking helper methods
    async Task ProcessVocabularyFromDescription(string userInput, GradeResponse grade, int responseTimeMs)
    {
        try
        {
            _logger.LogDebug("DescribeAScenePage: Starting vocabulary processing for input: '{UserInput}'", userInput);
            _logger.LogDebug("DescribeAScenePage: Grade accuracy: {Accuracy:F2}", grade.Accuracy);

            // Extract vocabulary words from the user's description
            // For scene descriptions, we'll attempt to identify key vocabulary terms
            var words = await ExtractVocabularyFromUserInput(userInput, grade);

            _logger.LogDebug("DescribeAScenePage: Extracted {Count} vocabulary words to process", words.Count);

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
                    _logger.LogDebug("DescribeAScenePage: Using AI analysis for '{Term}' - used as '{UsedForm}', correct: {Correct}", vocabularyWord.TargetLanguageTerm, actualUsedForm, wasCorrect);
                }
                else
                {
                    wasCorrect = grade.Accuracy >= 0.7; // Consider 70%+ as correct usage
                    actualUsedForm = vocabularyWord.TargetLanguageTerm ?? "";
                    _logger.LogDebug("DescribeAScenePage: No specific AI analysis for '{Term}', using overall accuracy: {Correct}", vocabularyWord.TargetLanguageTerm, wasCorrect);
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

                _logger.LogDebug("DescribeAScenePage: Recording vocabulary attempt for Word ID: {WordId}, '{Term}' - Correct: {Correct}", vocabularyWord.Id, vocabularyWord.TargetLanguageTerm, wasCorrect);

                var updatedProgress = await _progressService.RecordAttemptAsync(attempt);

                _logger.LogDebug("DescribeAScenePage: Progress updated! Word ID: {WordId}, MasteryScore: {MasteryScore:F2}, Phase: {Phase}", vocabularyWord.Id, updatedProgress.MasteryScore, updatedProgress.CurrentPhase);
            }

            _logger.LogDebug("DescribeAScenePage: Completed processing {Count} vocabulary words", words.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DescribeAScenePage: Error in ProcessVocabularyFromDescription");
        }
    }

    async Task<List<VocabularyWord>> ExtractVocabularyFromUserInput(string userInput, GradeResponse grade)
    {
        try
        {
            _logger.LogDebug("DescribeAScenePage: Extracting vocabulary from input: '{UserInput}'", userInput);

            var matchedWords = new List<VocabularyWord>();

            // Check if we have AI-powered vocabulary analysis
            if (grade.VocabularyAnalysis != null && grade.VocabularyAnalysis.Any())
            {
                _logger.LogDebug("DescribeAScenePage: Using AI vocabulary analysis - found {Count} analyzed words", grade.VocabularyAnalysis.Count);

                // Get available vocabulary from learning resources
                var resources = await _resourceRepo.GetAllResourcesAsync();
                var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>()).ToList();
                _logger.LogDebug("DescribeAScenePage: Loaded {VocabCount} vocabulary words from {ResourceCount} resources", allVocabulary.Count, resources.Count);

                // Debug: Log details about each resource
                foreach (var resource in resources)
                {
                    var vocabCount = resource.Vocabulary?.Count ?? 0;
                    _logger.LogDebug("DescribeAScenePage: Resource '{Title}' has {VocabCount} vocabulary words", resource.Title, vocabCount);
                    if (vocabCount > 0 && resource.Vocabulary != null)
                    {
                        var sampleWords = resource.Vocabulary.Take(3).Select(v => $"'{v.TargetLanguageTerm}'").ToList();
                        _logger.LogDebug("DescribeAScenePage: Sample words from '{Title}': [{SampleWords}]", resource.Title, string.Join(", ", sampleWords));
                    }
                }

                foreach (var analysis in grade.VocabularyAnalysis)
                {
                    _logger.LogDebug("DescribeAScenePage: Looking for dictionary form '{DictionaryForm}' (used as '{UsedForm}')", analysis.DictionaryForm, analysis.UsedForm);

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
                            _logger.LogDebug("DescribeAScenePage: ✅ Matched Word ID: {WordId}, '{TargetTerm}' -> '{NativeTerm}', Usage correct: {UsageCorrect}", matchedWord.Id, matchedWord.TargetLanguageTerm, matchedWord.NativeLanguageTerm, analysis.UsageCorrect);
                        }
                        else
                        {
                            _logger.LogDebug("DescribeAScenePage: ⚠️ Duplicate word avoided: {Term}", matchedWord.TargetLanguageTerm);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("DescribeAScenePage: ❌ No match found for '{DictionaryForm}' (used as '{UsedForm}')", analysis.DictionaryForm, analysis.UsedForm);
                    }
                }
            }
            else
            {
                _logger.LogDebug("DescribeAScenePage: ⚠️ No AI vocabulary analysis available, falling back to simple matching");

                // Fallback to simple string matching (legacy behavior)
                var resources = await _resourceRepo.GetAllResourcesAsync();
                var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>()).ToList();
                _logger.LogDebug("DescribeAScenePage: Found {VocabCount} total vocabulary words in {ResourceCount} resources", allVocabulary.Count, resources.Count);

                var inputWords = userInput.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _logger.LogDebug("DescribeAScenePage: Input words: [{InputWords}]", string.Join(", ", inputWords));

                foreach (var vocab in allVocabulary)
                {
                    // Check if any form of the vocabulary word appears in the user input
                    if (!string.IsNullOrWhiteSpace(vocab.TargetLanguageTerm) &&
                        inputWords.Any(w => w.Contains(vocab.TargetLanguageTerm.ToLower()) ||
                                           vocab.TargetLanguageTerm.ToLower().Contains(w)))
                    {
                        matchedWords.Add(vocab);
                        _logger.LogDebug("DescribeAScenePage: MATCH found! '{Term}' matches in user input", vocab.TargetLanguageTerm);
                    }

                    // Also check native language terms (in case user mixed languages)
                    if (!string.IsNullOrWhiteSpace(vocab.NativeLanguageTerm) &&
                        inputWords.Any(w => w.Contains(vocab.NativeLanguageTerm.ToLower()) ||
                                           vocab.NativeLanguageTerm.ToLower().Contains(w)))
                    {
                        if (!matchedWords.Contains(vocab)) // Avoid duplicates
                        {
                            matchedWords.Add(vocab);
                            _logger.LogDebug("DescribeAScenePage: MATCH found! '{NativeTerm}' (native) matches in user input", vocab.NativeLanguageTerm);
                        }
                    }
                }

                // Limit to avoid overwhelming
                matchedWords = matchedWords.Take(5).ToList();
            }

            _logger.LogDebug("DescribeAScenePage: Extraction completed - found {Count} vocabulary words", matchedWords.Count);
            foreach (var word in matchedWords)
            {
                _logger.LogDebug("DescribeAScenePage: Final matched word - Target: '{TargetTerm}', Native: '{NativeTerm}'", word.TargetLanguageTerm, word.NativeLanguageTerm);
            }

            return matchedWords;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DescribeAScenePage: Error in ExtractVocabularyFromUserInput");
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

    async Task ShowEnhancedFeedback(GradeResponse grade, string userInput)
    {
        try
        {
            string message;
            // Determine feedback based on accuracy and fluency
            if (grade.Accuracy >= 0.8 && grade.Fluency >= 0.8)
            {
                message = "🌟 Excellent description! Your Korean is very natural!";
            }
            else if (grade.Accuracy >= 0.7)
            {
                message = $"✅ Good work! Accuracy: {(int)(grade.Accuracy * 100)}%";
            }
            else if (grade.Accuracy >= 0.5)
            {
                message = $"📝 Keep practicing! {(int)(grade.Accuracy * 100)}% accuracy";
            }
            else
            {
                message = "🔍 Try focusing on simpler sentences first";
            }

            await AppShell.DisplayToastAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DescribeAScenePage: Error showing feedback");
        }
    }

    int GetCurrentUserId()
    {
        // For now, return a default user ID
        // This should be replaced with actual user management
        return 1;
    }
}