using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using MauiReactor.Parameters;
using SentenceStudio.Pages.Account;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.YouTube;
using SentenceStudio.Pages.LearningResources;
using Microsoft.Extensions.Logging;

namespace SentenceStudio;

interface IAppState
{
    AppTheme CurrentAppTheme { get; set; }
    UserProfile CurrentUserProfile { get; set; }
}

public class AppState : IAppState
{
    public AppTheme CurrentAppTheme { get; set; }

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
    // private UserProfile _currentUserProfile;

    [Inject] UserProfileRepository _userProfileRepository;

    protected override void OnMounted()
    {
        base.OnMounted();

        // if (!_initialized)
        // {
        //     _initialized = true;
        //     Task.Run(async () =>
        //     {
        //         state.Value.CurrentUserProfile = await _userProfileRepository.GetAsync();
        //     }).Wait();
        // }

        // State.CurrentAppTheme = Application.Current.UserAppTheme;
    }

    // Method to refresh the user profile state (called after reset)
    public async Task RefreshUserProfileAsync()
    {
        var currentProfile = await _userProfileRepository.GetAsync();
        state.Set(s => s.CurrentUserProfile = currentProfile);
    }

    public override VisualNode Render()
    {

        if (!_initialized)
        {
            _initialized = true;
            Task.Run(async () =>
            {
                if(state != null && _userProfileRepository != null)
                    state.Value.CurrentUserProfile = await _userProfileRepository.GetAsync();
            }).Wait();
        }

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
        
        public static Task DisplayToastAsync(string message)
        {
            ToastDuration duration = ToastDuration.Long;
            double fontSize = 14;
            var toast = Toast.Make(message, duration, fontSize);
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            return toast.Show(cancellationTokenSource.Token);
        }
    }
