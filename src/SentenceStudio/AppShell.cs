using MauiReactor.Parameters;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Pages.Account;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.YouTube;
using SentenceStudio.Pages.LearningResources;
using SentenceStudio.Pages.VocabularyManagement;
using SentenceStudio.Pages.MinimalPairs;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace SentenceStudio;

interface IAppState
{
    Microsoft.Maui.ApplicationModel.AppTheme CurrentAppTheme { get; set; }
    UserProfile CurrentUserProfile { get; set; }
}

public class AppState : IAppState
{
    public Microsoft.Maui.ApplicationModel.AppTheme CurrentAppTheme { get; set; }

    public UserProfile CurrentUserProfile { get; set; }
}

public partial class AppShell : Component
{
    [Param]
    private readonly IParameter<AppState> state;

    [Inject] ILogger<AppShell> _logger;

    // public AppShell()
    // {
    //     MauiExceptions.UnhandledException += (sender, args) =>
    //     {
    //         Debug.WriteLine(args.ExceptionObject);
    //         throw (Exception)args.ExceptionObject;
    //     };
    // }

    // ~AppShell()
    // {
    //     MauiExceptions.UnhandledException -= (sender, args) =>
    //     {
    //         Debug.WriteLine(args.ExceptionObject);
    //         throw (Exception)args.ExceptionObject;
    //     };
    // }

    private bool _initialized = false;
    private bool _isLoadingProfile = false;

    [Inject] UserProfileRepository _userProfileRepository;

    protected override void OnMounted()
    {
        base.OnMounted();

        if (!_initialized && !_isLoadingProfile)
        {
            _initialized = true;
            LoadUserProfileAsync();
        }
    }

    private async void LoadUserProfileAsync()
    {
        if (_isLoadingProfile) return;
        _isLoadingProfile = true;

        try
        {
            if (_userProfileRepository != null)
            {
                var profile = await _userProfileRepository.GetAsync();
                state.Set(s => s.CurrentUserProfile = profile);
            }
        }
        finally
        {
            _isLoadingProfile = false;
        }
    }

    // Method to refresh the user profile state (called after reset)
    public async Task RefreshUserProfileAsync()
    {
        var currentProfile = await _userProfileRepository.GetAsync();
        state.Set(s => s.CurrentUserProfile = currentProfile);
    }

    public override VisualNode Render()
    {
        // Check if user needs onboarding - either no profile exists or onboarding was never completed
        var isOnboarded = Preferences.Default.Get("is_onboarded", false);
        var hasProfile = state.Value.CurrentUserProfile != null;

        // Debug information
        _logger.LogDebug("AppShell Render - isOnboarded: {IsOnboarded}, hasProfile: {HasProfile}", isOnboarded, hasProfile);
        if (hasProfile)
        {
            _logger.LogDebug("Profile exists - Name: '{ProfileName}', Id: {ProfileId}", state.Value.CurrentUserProfile.Name, state.Value.CurrentUserProfile.Id);
        }

        // Show onboarding if either condition is true:
        // 1. User hasn't completed onboarding flow yet
        // 2. No user profile exists in database
        if (!isOnboarded || !hasProfile)
        {
            _logger.LogDebug("Showing OnboardingPage");
            return new OnboardingPage();
        }

        _logger.LogDebug("Showing Main Shell");
        return Shell(
            FlyoutItem("Dashboard",
                ShellContent()
                    .Title("Dashboard")
                    .RenderContent(() => new DashboardPage())
                    .Route("dashboard")
            ),
            FlyoutItem("Learning Resources",
                ShellContent()
                    .Title("Learning Resources")
                    .RenderContent(() => new ListLearningResourcesPage())
                    .Route(nameof(ListLearningResourcesPage))
            ),
            FlyoutItem("Vocabulary",
                ShellContent()
                    .Title("Vocabulary")
                    .RenderContent(() => new VocabularyManagementPage())
                    .Route(nameof(VocabularyManagementPage))
            ),
            FlyoutItem("Minimal Pairs",
                ShellContent()
                    .Title("Minimal Pairs")
                    .RenderContent(() => new Pages.MinimalPairs.MinimalPairsPage())
                    .Route(nameof(Pages.MinimalPairs.MinimalPairsPage))
            ),
            FlyoutItem("Skills",
                ShellContent()
                    .Title("Skills")
                    .RenderContent(() => new ListSkillProfilesPage())
                    .Route(nameof(ListSkillProfilesPage))
            ),
            FlyoutItem("Import",
                ShellContent()
                    .Title("Import")
                    .RenderContent(() => new YouTubeImportPage())
                    .Route(nameof(YouTubeImportPage))
            ),
            FlyoutItem("Profile",
                ShellContent()
                    .Title("Profile")
                    .RenderContent(() => new UserProfilePage())
                    .Route(nameof(UserProfilePage))
            ),
            FlyoutItem("Settings",
                ShellContent()
                    .Title("Settings")
                    .RenderContent(() => new Pages.AppSettings.SettingsPage())
                    .Route(nameof(Pages.AppSettings.SettingsPage))
            )
        );
    }
    // .FlyoutFooter(
    //     Grid(            
    //         new SfSegmentedControl{
    //             new SfSegmentItem().ImageSource(ResourceHelper.GetResource<FontImageSource>("IconLight")),
    //             new SfSegmentItem().ImageSource(ResourceHelper.GetResource<FontImageSource>("IconDark"))
    //         }
    //         .Background(Microsoft.Maui.Graphics.Colors.Transparent)
    //         .ShowSeparator(true)
    //         .SegmentCornerRadius(0)
    //         .Stroke(Theme.IsLightTheme ? MyTheme.Black : MyTheme.White)
    //         .StrokeThickness(1)
    //         .SelectedIndex(Theme.CurrentAppTheme == AppTheme.Light ? 0 : 1)
    //         .OnSelectionChanged((s, e) => Theme.UserTheme = e.NewIndex == 0 ? AppTheme.Light : AppTheme.Dark)
    //         .SegmentWidth(40)
    //         .SegmentHeight(40)

    //     )
    //     .Padding(15)

    public static async Task DisplayToastAsync(string message)
    {
        var toast = new UXDivers.Popups.Maui.Controls.Toast { Title = message };
        await IPopupService.Current.PushAsync(toast);
        _ = Task.Delay(3000).ContinueWith(async _ =>
        {
            try { await IPopupService.Current.PopAsync(toast); } catch { }
        });
    }
}
