using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using MauiReactor.Parameters;
using SentenceStudio.Pages.Account;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.Vocabulary;
using SentenceStudio.Pages.YouTube;

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
        private UserProfile _currentUserProfile;

        [Inject] UserProfileRepository _userProfileRepository;

        protected override void OnMounted()
        {
            base.OnMounted();

            if (!_initialized)
            {
                _initialized = true;
                Task.Run(async () =>
                {
                    state.Value.CurrentUserProfile = await _userProfileRepository.GetAsync();
                }).Wait();
            }

            // State.CurrentAppTheme = Application.Current.UserAppTheme;
        }

        public override VisualNode Render() {

            if (state.Value.CurrentUserProfile == null)
                return new OnboardingPage();

            return Shell(
                FlyoutItem("Dashboard",
                    ShellContent()
                        .Title("Dashboard")
                        .RenderContent(() => new DashboardPage())
                        .Route("dashboard")
                ),
                FlyoutItem("Vocabulary",
                    ShellContent()
                        .Title("Vocabulary")
                        .RenderContent(() => new ListVocabularyPage())
                        .Route(nameof(ListVocabularyPage))
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
        //         .Stroke(Theme.IsLightTheme ? ApplicationTheme.Black : ApplicationTheme.White)
        //         .StrokeThickness(1)
        //         .SelectedIndex(Theme.CurrentAppTheme == AppTheme.Light ? 0 : 1)
        //         .OnSelectionChanged((s, e) => Theme.UserTheme = e.NewIndex == 0 ? AppTheme.Light : AppTheme.Dark)
        //         .SegmentWidth(40)
        //         .SegmentHeight(40)

        //     )
        //     .Padding(15)
        // );


        public static async Task DisplayToastAsync(string message)
        {
            ToastDuration duration = ToastDuration.Long;
            double fontSize = 14;
            var toast = Toast.Make(message, duration, fontSize);
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            await toast.Show(cancellationTokenSource.Token);
        }
    }
