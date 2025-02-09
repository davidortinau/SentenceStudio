using MauiReactor;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Resources.Styles;

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
}

public partial class OnboardingPage : Component<OnboardingState>
{
    [Inject] IServiceProvider _service;
    [Inject] UserProfileRepository _userProfileRepository;
    [Inject] VocabularyService _vocabularyService;
    [Inject] IConfiguration _configuration;

    LocalizationManager _localize => LocalizationManager.Instance;
    private int _screens = 4;

    protected override void OnMounted()
    {
        var settings = _configuration.GetRequiredSection("Settings").Get<Settings>();
        SetState(s => s.NeedsApiKey = string.IsNullOrEmpty(settings?.OpenAIKey));
        base.OnMounted();
    }

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["MyProfile"]}",
            
                Grid(rows: "*, Auto", "",
                    CarouselView()
                        .HorizontalScrollBarVisibility(ScrollBarVisibility.Never)
                        .IsSwipeEnabled(false)
                        .Loop(false)
                        .Position(State.CurrentPosition)
                        .OnPositionChanged((pos) => SetState(s =>
                        {
                            s.CurrentPosition = pos.CurrentPosition;
                            s.LastPositionReached = pos.CurrentPosition == _screens;
                        }))
                        .ItemsSource(new[]
                        {
                            RenderWelcomeStep(),
                            RenderNameStep(),
                            // RenderLanguageStep("What is your primary language?", s => s.NativeLanguage),
                            // RenderLanguageStep("What language are you here to practice?", s => s.TargetLanguage),
                            RenderApiKeyStep(),
                            RenderFinalStep()
                        }, RenderItemTemplate),

                    Grid(rows: "Auto, Auto", "",
                        Button("Next")
                            .IsVisible(!State.LastPositionReached)
                            .OnClicked(() => SetState(s =>
                            {
                                if (s.CurrentPosition >= _screens) return;
                                s.CurrentPosition++;
                                s.LastPositionReached = s.CurrentPosition == _screens;
                            })),

                        Button("Continue")
                            .IsVisible(State.LastPositionReached)
                            .OnClicked(End),

                        IndicatorView()
                            .GridRow(1)
                            .HCenter()
                            .IndicatorColor(ApplicationTheme.Gray200)
                            .SelectedIndicatorColor(ApplicationTheme.Primary)
                            .IndicatorSize(DeviceInfo.Platform == DevicePlatform.iOS ? 6 : 8)
                    )
                    .GridRow(1)
                    .RowSpacing(20)
                )
                .Padding(ApplicationTheme.Size160)
            );
    }

    private VisualNode RenderItemTemplate(VisualNode node)
    {
        return node;
    }

    private VisualNode RenderWelcomeStep() =>
        ContentView(
            Grid("Auto, Auto","",
                Label("Welcome to Sentence Studio!")
                    .Style((Style)Application.Current.Resources["Title1"])
                    .HCenter(),

                Label("Strengthen your language skills with our fun and interactive sentence building activities.")
                    .Style((Style)Application.Current.Resources["Title3"])
                    .HCenter()
                    .GridRow(1)
            )
            .RowSpacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        );

    private VisualNode RenderNameStep() =>
        ContentView(
            Grid("Auto, Auto","",
                Label("What should I call you?")
                    .Style((Style)Application.Current.Resources["Title1"])
                    .HCenter(),

                new SfTextInputLayout
                {
                    Entry()
                        .Text(State.Name)
                        .OnTextChanged(text => SetState(s => s.Name = text))
                }
                .GridRow(1)
                .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                .ContainerBackground(Colors.White)
                .Hint("Enter your name")
            )
            .RowSpacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        );

    private VisualNode RenderLanguageStep(string title, Action<OnboardingState> setter) =>
        ContentView(
            Grid("Auto, Auto","",
                Label(title)
                    .Style((Style)Application.Current.Resources["Title1"])
                    .HCenter(),

                new SfTextInputLayout
                {
                    Picker()
                        .ItemsSource(Languages)
                        // .OnSelectedIndexChanged(index => SetState(s => setter(s)))
                }
                .GridRow(1)
                .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                .ContainerBackground(Colors.White)
                .Hint("Select language")
            )
            .RowSpacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        );

    private VisualNode RenderApiKeyStep() =>
        ContentView(
            VStack(
                Label("Sentence Studio needs an API key from OpenAI to use the AI features.")
                    .Style((Style)Application.Current.Resources["Title1"])
                    .HCenter(),

                new SfTextInputLayout
                {
                    Entry()
                        .IsPassword(true)
                        .Text(State.OpenAI_APIKey)
                        .OnTextChanged(text => SetState(s => s.OpenAI_APIKey = text))
                }
                .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                .ContainerBackground(Colors.White)
                .Hint("Enter your OpenAI API key"),

                Label("Get an API key from OpenAI.com")
                    .TextDecorations(TextDecorations.Underline)
                    .OnTapped(() => Browser.OpenAsync("https://platform.openai.com/account/api-keys"))
            )
            .Spacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        )
        .IsVisible(State.NeedsApiKey);

    private VisualNode RenderFinalStep() =>
        ContentView(
            Grid("Auto, Auto","",
                Label("Let's begin!")
                    .Style((Style)Application.Current.Resources["Title1"])
                    .HCenter(),

                Label("On the next screen, you will be able to choose from a variety of activities to practice your language skills. Along the way Sentence Studio will keep track of your progress and report your growth.")
                    .Style((Style)Application.Current.Resources["Title3"])
                    .HCenter()
                    .GridRow(1)
            )
            .RowSpacing(ApplicationTheme.Size160)
            .Margin(ApplicationTheme.Size160)
        );

    private async Task End()
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
        await AppShell.DisplayToastAsync($"{_localize["Saved"]}");

        Preferences.Default.Set("is_onboarded", true);
        // App.Current.Windows[0].Page = new AppShell(_service.GetService<AppShellModel>());
    }

    private readonly string[] Languages = new[]
    {
        "English", "Spanish", "French", "German", "Italian", "Portuguese",
        "Chinese", "Japanese", "Korean", "Arabic", "Russian", "Other"
    };
}