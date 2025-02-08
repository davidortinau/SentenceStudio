using System.ComponentModel;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.Vocabulary;
using SentenceStudio.Resources.Styles;
using Sharpnado.Tasks;

namespace SentenceStudio;

public class AppShellState
{

    public AppTheme CurrentAppTheme {get;set;}
}

public class AppShell : Component<AppShellState>
{
    public AppShell() 
    {
        MauiExceptions.UnhandledException += (sender, args) =>
        {
            Debug.WriteLine(args.ExceptionObject);
            throw (Exception)args.ExceptionObject;
        };
    }

    ~AppShell()
    {
        MauiExceptions.UnhandledException -= (sender, args) =>
        {
            Debug.WriteLine(args.ExceptionObject);
            throw (Exception)args.ExceptionObject;
        };
    }

    protected override void OnMounted()
    {
        base.OnMounted();

        State.CurrentAppTheme = Application.Current.UserAppTheme;
    }

    public override VisualNode Render()
        => Shell(
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
            )
        );
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
