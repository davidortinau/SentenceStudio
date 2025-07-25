using Microsoft.Extensions.Configuration;
using MauiReactor.Parameters;
using Microsoft.Maui.Graphics;
using MauiReactor.Shapes;

namespace SentenceStudio.Pages.Onboarding;

public class OnboardingState
{
    public int CurrentPosition { get; set; }
    public bool LastPositionReached { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NativeLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string DisplayLanguage { get; set; } = string.Empty;
    public string OpenAI_APIKey { get; set; } = string.Empty;
    public bool NeedsApiKey { get; set; }
    public string[] SuggestedNames { get; set; } = Array.Empty<string>();
    public bool IsLoadingNames { get; set; } = false;
    
    // New properties for final step and starter content creation
    public bool IsCreatingStarterContent { get; set; } = false;
    public string CreationProgressMessage { get; set; } = string.Empty;
    public bool ShowCreateStarterOption { get; set; } = false;
}

public partial class OnboardingPage : Component<OnboardingState>
{
    [Inject] IServiceProvider _service;
    [Inject] UserProfileRepository _userProfileRepository;
    [Inject] IConfiguration _configuration;
    [Inject] NameGenerationService _nameGenerationService;
    [Inject] AiService _aiService;
    [Inject] LearningResourceRepository _learningResourceRepository;
    [Inject] SkillProfileRepository _skillProfileRepository;
    [Param] IParameter<AppState> _appState;

    LocalizationManager _localize => LocalizationManager.Instance;
    
    private CancellationTokenSource? _cancellationTokenSource;
    
    VisualNode[] GetScreens() => new[]
    {
        RenderWelcomeStep(),
        RenderLanguageStep(
            "What is your primary language?", 
            s => s.NativeLanguage,
            (lang) => SetState(s => s.NativeLanguage = lang)),
        RenderLanguageStep(
            "What language are you here to practice?", 
            s => s.TargetLanguage,
            (lang) => { 
                SetState(s => s.TargetLanguage = lang);
                // Generate names when target language changes
                Task.Run(async () => 
                {
                    await LoadSuggestedNames(lang);
                });
            }),
        RenderNameStep(),
        State.NeedsApiKey ? RenderApiKeyStep() : null,
        RenderFinalStep()
    }.Where(screen => screen != null).ToArray();

    protected override void OnMounted()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        
        var settings = _configuration.GetRequiredSection("Settings").Get<Settings>();
        SetState(s => s.NeedsApiKey = string.IsNullOrEmpty(settings?.OpenAIKey));
        
        // Don't load names here - wait until user selects target language
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        base.OnWillUnmount();
    }

    async Task LoadSuggestedNames(string targetLanguage)
    {
        if (string.IsNullOrEmpty(targetLanguage) || _cancellationTokenSource?.Token.IsCancellationRequested == true) 
            return;
        
        // Defensive check to prevent calling SetState on unmounted component
        if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
        
        MainThread.BeginInvokeOnMainThread(() => 
        {
            if (_cancellationTokenSource?.Token.IsCancellationRequested == false)
                SetState(s => s.IsLoadingNames = true);
        });
        
        try
        {
            var names = await _nameGenerationService.GenerateNamesAsync(targetLanguage);
            
            // Check cancellation again before updating state
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            MainThread.BeginInvokeOnMainThread(() => 
            {
                if (_cancellationTokenSource?.Token.IsCancellationRequested == false)
                {
                    SetState(s => 
                    {
                        s.SuggestedNames = names;
                        s.IsLoadingNames = false;
                    });
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load suggested names: {ex.Message}");
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            MainThread.BeginInvokeOnMainThread(() => 
            {
                if (_cancellationTokenSource?.Token.IsCancellationRequested == false)
                    SetState(s => s.IsLoadingNames = false);
            });
        }
    }

    void NavigateToPosition(int newPosition)
    {
        var screens = GetScreens();
        var maxScreens = screens.Length - 1;
        if (newPosition < 0 || newPosition > maxScreens) return;
        
        SetState(s => 
        {
            s.CurrentPosition = newPosition;
            s.LastPositionReached = newPosition == maxScreens;
            
            // If navigating to the name step (position 3) and we have a target language,
            // load suggested names
            if (newPosition == 3 && !string.IsNullOrEmpty(s.TargetLanguage) && 
                s.SuggestedNames.Length == 0 && !s.IsLoadingNames)
            {
                Task.Run(async () => await LoadSuggestedNames(s.TargetLanguage));
            }
            
            // If navigating to final step (last screen), show starter content options
            if (newPosition == maxScreens) // Final step (dynamic position)
            {
                s.ShowCreateStarterOption = true;
            }
        });
    }

    bool CanProceedToNext()
    {
        return State.CurrentPosition switch
        {
            0 => true, // Welcome step - always can proceed
            1 => !string.IsNullOrEmpty(State.NativeLanguage), // Native language step
            2 => !string.IsNullOrEmpty(State.TargetLanguage), // Target language step  
            3 => !string.IsNullOrEmpty(State.Name), // Name step
            4 => !State.NeedsApiKey || !string.IsNullOrEmpty(State.OpenAI_APIKey), // API key step
            _ => true
        };
    }

    public override VisualNode Render()
    {
        var screens = GetScreens();
        var maxScreens = screens.Length - 1;
        return ContentPage($"{_localize["MyProfile"]}",
            
                Grid(rows: "*, Auto", "",
                    // Render the current screen directly
                    screens[State.CurrentPosition],

                    // Only show navigation buttons if NOT on final step with starter content options
                    !(State.CurrentPosition == maxScreens && State.ShowCreateStarterOption) ?
                        Grid(rows: "Auto", columns: "1*, 3*",
                            Button("Back")
                                .IsVisible(State.CurrentPosition > 0)
                                .IsEnabled(State.CurrentPosition > 0)
                                .OnClicked(() => NavigateToPosition(State.CurrentPosition - 1)),

                            Button("Next")
                                .GridColumn(1)
                                .IsVisible(!State.LastPositionReached)
                                .IsEnabled(CanProceedToNext())
                                .OnClicked(() => NavigateToPosition(State.CurrentPosition + 1)),

                            Button("Continue")
                                .GridColumn(1)
                                .IsVisible(State.LastPositionReached)
                                .IsEnabled(CanProceedToNext())
                                .OnClicked(End)
                        )
                        .ColumnSpacing(8)
                        .GridRow(1)
                        .RowSpacing(20) :
                    null
                )
                .Padding(ApplicationTheme.Size160)
            );
    }

    VisualNode RenderWelcomeStep() =>
        ContentView(
            Grid("Auto, Auto","",
                Label("Welcome to Sentence Studio!")
                    .ThemeKey(ApplicationTheme.Title1)
                    .HCenter(),

                Label("Strengthen your language skills with our fun and interactive sentence building activities.")
                    .ThemeKey(ApplicationTheme.Title3)
                    .HCenter()
                    .GridRow(1)
            )
            .RowSpacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        );

    VisualNode RenderNameStep() =>
        ContentView(
            VStack(
                Label("What should I call you?")
                    .ThemeKey(ApplicationTheme.Title1)
                    .HCenter(),

                new SfTextInputLayout
                {
                    Entry()
                        .Text(State.Name)
                        .OnTextChanged(text => SetState(s => s.Name = text))
                }
                .Hint("Enter your name or tap a suggestion below"),

                // Show loading indicator when generating names
                State.IsLoadingNames ? 
                    Label("Generating name suggestions...")
                        .HCenter()
                        .FontSize(14)
                        .TextColor(ApplicationTheme.Gray400)
                    : null,

                // Show suggested names if available
                State.SuggestedNames.Length > 0 && !State.IsLoadingNames ?
                    VStack(
                        Label($"Suggestions in {(!string.IsNullOrEmpty(State.TargetLanguage) ? State.TargetLanguage : "English")}:")
                            .FontSize(14)
                            .TextColor(ApplicationTheme.Gray600)
                            .HCenter(),
                        
                        // First row - masculine names
                        Grid(rows: "auto",columns: "*, *, *, *",
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(0), 0),
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(1), 1),
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(2), 2),
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(3), 3)
                        )
                        .ColumnSpacing(8),
                        
                        // Second row - feminine names  
                        Grid(rows:"auto",columns: "*, *, *, *",
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(4), 0),
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(5), 1),
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(6), 2),
                            RenderNameButton(State.SuggestedNames.ElementAtOrDefault(7), 3)
                        )
                        .ColumnSpacing(8)
                    )
                    .Spacing(8)
                    : null
            )
            .Spacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        );

    VisualNode RenderNameButton(string name, int column)
    {
        if (string.IsNullOrEmpty(name)) return ContentView();
        
        return Button(name)
            .GridColumn(column)
            .BackgroundColor(ApplicationTheme.Gray100)
            .TextColor(ApplicationTheme.Gray900)
            .FontSize(14)
            .CornerRadius(8)
            .Padding(8, 6)
            .OnClicked(() => SetState(s => s.Name = name));
    }

    VisualNode RenderLanguageStep(string title, Func<OnboardingState, string> getter, Action<string> setter) =>
        ContentView(
            Grid("Auto, Auto", "",
                Label(title)
                    .ThemeKey(ApplicationTheme.Title1)
                    .HCenter(),

                new SfTextInputLayout
                {
                    Picker()
                        .ItemsSource(Constants.Languages)
                        .SelectedIndex(Array.IndexOf(Constants.Languages, getter(State)))
                        .OnSelectedIndexChanged((index) =>
                        {
                            if (index >= 0 && index < Constants.Languages.Length)
                            {
                                var selectedLanguage = Constants.Languages[index];
                                setter(selectedLanguage);
                            }
                        })
                }
                .GridRow(1)
                .Hint("Select language")
            )
            .RowSpacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        );

    VisualNode RenderApiKeyStep() =>
        ContentView(
            VStack(
                Label("Sentence Studio needs an API key from OpenAI to use the AI features.")
                    .ThemeKey(ApplicationTheme.Title1)
                    .HCenter(),

                new SfTextInputLayout
                {
                    Entry()
                        .IsPassword(true)
                        .Text(State.OpenAI_APIKey)
                        .OnTextChanged(text => SetState(s => s.OpenAI_APIKey = text))
                }
                .Hint("Enter your OpenAI API key"),

                Label("Get an API key from OpenAI.com")
                    .TextDecorations(TextDecorations.Underline)
                    .OnTapped(() => Browser.OpenAsync("https://platform.openai.com/account/api-keys"))
            )
            .Spacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        )
        .IsVisible(State.NeedsApiKey);

    VisualNode RenderFinalStep()
    {
        // Ensure ShowCreateStarterOption is set when we reach the final step
        if (!State.ShowCreateStarterOption && !State.IsCreatingStarterContent)
        {
            SetState(s => s.ShowCreateStarterOption = true);
        }
        
        System.Diagnostics.Debug.WriteLine($"RenderFinalStep: ShowCreateStarterOption={State.ShowCreateStarterOption}, IsCreatingStarterContent={State.IsCreatingStarterContent}");
        
        if (State.ShowCreateStarterOption && !State.IsCreatingStarterContent)
        {
            return ContentView(
                VStack(
                    Label("You're all set!")
                        .ThemeKey(ApplicationTheme.Title1)
                        .HCenter(),

                    Label("Choose how you'd like to begin your language learning journey:")
                        .ThemeKey(ApplicationTheme.Title3)
                        .HCenter()
                        .Margin(0, 0, 0, ApplicationTheme.Size160),

                    // Create starter content option
                    Border(
                        VStack(
                            Label("🎯 Create Starter Content")
                                .FontSize(18)
                                .FontAttributes(FontAttributes.Bold)
                                .HCenter()
                                .TextColor(ApplicationTheme.LightOnDarkBackground),

                            Label($"Let me create a beginner vocabulary list and skill profile for {State.TargetLanguage} to get you started!")
                                .FontSize(14)
                                .HCenter()
                                .TextColor(ApplicationTheme.LightOnDarkBackground)
                                .Margin(0, 8, 0, 0)
                        )
                        .Spacing(4)
                        .Padding(ApplicationTheme.Size160)
                    )
                    .BackgroundColor(ApplicationTheme.Primary)
                    .StrokeShape(new RoundRectangle().CornerRadius(12))
                    .Padding(4)
                    .OnTapped(CreateStarterContent),

                    // Skip option
                    Border(
                        VStack(
                            Label("⚡ Skip Setup")
                                .FontSize(18)
                                .FontAttributes(FontAttributes.Bold)
                                .HCenter()
                                .TextColor(ApplicationTheme.DarkOnLightBackground),

                            Label("I'll set up my learning materials later. Take me to the dashboard!")
                                .FontSize(14)
                                .HCenter()
                                .TextColor(ApplicationTheme.DarkOnLightBackground)
                                .Margin(0, 8, 0, 0)
                        )
                        .Spacing(4)
                        .Padding(ApplicationTheme.Size160)
                    )
                    .BackgroundColor(ApplicationTheme.Gray100)
                    .StrokeShape(new RoundRectangle().CornerRadius(12))
                    .Stroke(ApplicationTheme.Gray300)
                    .Padding(4)
                    .OnTapped(SkipAndStart)
                )
                .Spacing(ApplicationTheme.Size160)
                .Margin(ApplicationTheme.Size160)
            );
        }
        else if (State.IsCreatingStarterContent)
        {
            return ContentView(
                VStack(
                    Label("Creating Your Learning Materials...")
                        .ThemeKey(ApplicationTheme.Title1)
                        .HCenter(),

                    Label(State.CreationProgressMessage)
                        .ThemeKey(ApplicationTheme.Title3)
                        .HCenter()
                        .Margin(0, ApplicationTheme.Size160, 0, 0),

                    ActivityIndicator()
                        .IsRunning(true)
                        .HCenter()
                )
                .Spacing(ApplicationTheme.Size160)
                .Margin(ApplicationTheme.Size160)
            );
        }
        else
        {
            // Fallback to simple completion message
            return ContentView(
                Grid("Auto, Auto","",
                    Label("Let's begin!")
                        .ThemeKey(ApplicationTheme.Title1)
                        .HCenter(),

                    Label("On the next screen, you will be able to choose from a variety of activities to practice your language skills. Along the way Sentence Studio will keep track of your progress and report your growth.")
                        .ThemeKey(ApplicationTheme.Title3)
                        .HCenter()
                        .GridRow(1)
                )
                .RowSpacing(ApplicationTheme.Size160)
                .Margin(ApplicationTheme.Size160)
            );
        }
    }

    async Task CreateStarterContent()
    {
        if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
        
        SetState(s => 
        {
            s.IsCreatingStarterContent = true;
            s.CreationProgressMessage = "Creating your personalized vocabulary list...";
        });

        try
        {
            // Create the starter vocabulary resource
            await CreateStarterVocabularyResource();
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            SetState(s => s.CreationProgressMessage = "Setting up your beginner skill profile...");
            
            // Create the beginner skill profile
            await CreateBeginnerSkillProfile();
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            SetState(s => s.CreationProgressMessage = "Finishing setup...");
            
            // Complete the onboarding process
            await End();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error creating starter content: {ex}");
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            await Application.Current.MainPage.DisplayAlert("Error", 
                "Failed to create starter content. You can set up learning materials later from the dashboard.", "OK");
            
            // Still complete onboarding even if starter content creation fails
            await End();
        }
    }

    async Task CreateStarterVocabularyResource()
    {
        if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
        
        try
        {
            // Load the Scriban template
            using var stream = await FileSystem.OpenAppPackageFileAsync("GetStarterVocabulary.scriban-txt");
            using var reader = new StreamReader(stream);
            var template = await reader.ReadToEndAsync();
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            // Replace template variables
            var prompt = template
                .Replace("{{native_language}}", State.NativeLanguage)
                .Replace("{{target_language}}", State.TargetLanguage);
            
            // Generate vocabulary using AI
            var vocabularyCsv = await _aiService.SendPrompt<string>(prompt);
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            if (!string.IsNullOrEmpty(vocabularyCsv))
            {
                // Create and save the learning resource
                var resource = new LearningResource
                {
                    Title = $"Starter Vocabulary - {State.TargetLanguage}",
                    Description = $"AI-generated beginner vocabulary for {State.TargetLanguage} learners",
                    Language = State.TargetLanguage,
                    MediaType = "Vocabulary List",
                    Tags = "starter,vocabulary",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                // Parse vocabulary words from the CSV response
                var vocabularyWords = VocabularyWord.ParseVocabularyWords(vocabularyCsv);
                
                // Save the resource first
                await _learningResourceRepository.SaveResourceAsync(resource);
                
                if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
                
                // Now save each vocabulary word and associate with the resource
                foreach (var word in vocabularyWords)
                {
                    if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
                    
                    if (word.CreatedAt == default)
                        word.CreatedAt = DateTime.UtcNow;
                    word.UpdatedAt = DateTime.UtcNow;
                    
                    // Save the word
                    await _learningResourceRepository.SaveWordAsync(word);
                    
                    // Associate the word with the resource
                    await _learningResourceRepository.AddVocabularyToResourceAsync(resource.Id, word.Id);
                }
                
                System.Diagnostics.Debug.WriteLine($"Created starter vocabulary resource with {vocabularyWords.Count} vocabulary words");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error creating vocabulary resource: {ex}");
            throw; // Re-throw to be handled by the calling method
        }
    }

    async Task CreateBeginnerSkillProfile()
    {
        if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
        
        try
        {
            var prompt = $@"Create a skill description for a beginner {State.TargetLanguage} learner. The description should:
1. Be encouraging and supportive
2. Mention they're just starting their {State.TargetLanguage} learning journey
3. Include 2-3 specific beginner skills they should focus on
4. Be exactly 2-3 sentences long
5. Be written in {State.NativeLanguage}

Example skills to mention: basic vocabulary, simple sentence structure, pronunciation, greetings, numbers, common phrases.";

            var description = await _aiService.SendPrompt<string>(prompt);
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) return;
            
            if (!string.IsNullOrEmpty(description))
            {
                var skillProfile = new SkillProfile
                {
                    Title = $"Beginner {State.TargetLanguage}",
                    Description = description.Trim(),
                    Language = State.TargetLanguage,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                await _skillProfileRepository.SaveAsync(skillProfile);
                System.Diagnostics.Debug.WriteLine($"Created beginner skill profile: {skillProfile.Title}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error creating skill profile: {ex}");
            throw; // Re-throw to be handled by the calling method
        }
    }

    Task SkipAndStart()
    {
        // Just complete the onboarding without creating starter content
        return End();
    }

    async Task NavigateToDashboard()
    {
        try
        {
            await MauiControls.Shell.Current.GoToAsync("//main");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error navigating to dashboard: {ex}");
        }
    }

    async Task End()
    {
        try
        {
            var profile = new UserProfile
            {
                Name = State.Name,
                Email = State.Email,
                NativeLanguage = State.NativeLanguage,
                TargetLanguage = State.TargetLanguage,
                DisplayLanguage = State.DisplayLanguage,
                OpenAI_APIKey = State.OpenAI_APIKey
            };

            await _userProfileRepository.SaveAsync(profile);
            
            // Update the app state with the new profile
            _appState.Set(s => s.CurrentUserProfile = profile);
            
            // Set the onboarding preference to true
            Preferences.Default.Set("is_onboarded", true);
            
            await AppShell.DisplayToastAsync($"{_localize["Saved"]}");
            
            // The AppShell will automatically re-render and show the main Shell now
            // No need to navigate manually
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnboardingPage.End(): {ex}");
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to complete onboarding: {ex.Message}", "OK");
        }
    }

    
}