using Microsoft.Extensions.Configuration;
using MauiReactor.Parameters;

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
}

public partial class OnboardingPage : Component<OnboardingState>
{
    [Inject] IServiceProvider _service;
    [Inject] UserProfileRepository _userProfileRepository;
    [Inject] IConfiguration _configuration;
    [Inject] NameGenerationService _nameGenerationService;
    [Param] IParameter<AppState> _appState;

    LocalizationManager _localize => LocalizationManager.Instance;
    
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
        var settings = _configuration.GetRequiredSection("Settings").Get<Settings>();
        SetState(s => s.NeedsApiKey = string.IsNullOrEmpty(settings?.OpenAIKey));
        
        // Load default names for English initially
        Task.Run(async () => 
        {
            await LoadSuggestedNames("English");
        });
        
        base.OnMounted();
    }

    async Task LoadSuggestedNames(string targetLanguage)
    {
        if (string.IsNullOrEmpty(targetLanguage)) return;
        
        MainThread.BeginInvokeOnMainThread(() => SetState(s => s.IsLoadingNames = true));
        
        try
        {
            var names = await _nameGenerationService.GenerateNamesAsync(targetLanguage);
            MainThread.BeginInvokeOnMainThread(() => SetState(s => 
            {
                s.SuggestedNames = names;
                s.IsLoadingNames = false;
            }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load suggested names: {ex.Message}");
            MainThread.BeginInvokeOnMainThread(() => SetState(s => s.IsLoadingNames = false));
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
        return ContentPage($"{_localize["MyProfile"]}",
            
                Grid(rows: "*, Auto", "",
                    // Render the current screen directly
                    screens[State.CurrentPosition],

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
                        .RowSpacing(20)
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

    VisualNode RenderFinalStep() =>
        ContentView(
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