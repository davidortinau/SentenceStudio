using MauiReactor.Shapes;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Skills;

class ListSkillProfilesPageState
{
    public List<SkillProfile> Profiles { get; set; } = [];
    public bool IsLoading { get; set; } = true;
    public double Width { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Width;
    public double Density { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Density;
}

partial class ListSkillProfilesPage : Component<ListSkillProfilesPageState>
{
    [Inject] SkillProfileRepository _skillsRepository;
    [Inject] NativeThemeService _themeService;
    LocalizationManager _localize => LocalizationManager.Instance;


    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
    }


    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage($"{_localize["SkillProfiles"]}",
            ToolbarItem().Text($"{_localize["Add"]}")
                .IconImageSource(BootstrapIcons.Create(BootstrapIcons.PlusLg, theme.GetOnBackground(), 20))
                .OnClicked(async () => await AddProfile()),
            State.IsLoading
                ? (VisualNode)ActivityIndicator().IsRunning(true).Center()
                : State.Profiles.Count == 0
                ? (VisualNode)VStack(spacing: 16,
                    Label("No skill profiles yet")
                        .Muted()
                        .HCenter(),
                    Button($"{_localize["Add"]}")
                        .Class("btn-primary")
                        .HCenter()
                        .OnClicked(async () => await AddProfile())
                  )
                  .Padding(40)
                  .VCenter()
                : VScrollView(
                    RenderProfileGrid(theme)
                )
        ).BackgroundColor(BootstrapTheme.Current.GetBackground())
         .OnAppearing(LoadProfiles);
    }

    VisualNode RenderProfileGrid(BootstrapTheme theme)
    {
        double screenWidth = State.Width > 0 ? State.Width : DeviceDisplay.Current.MainDisplayInfo.Width / State.Density;
        int columns = screenWidth >= 900 ? 3 : (screenWidth >= 500 ? 2 : 1);
        int rows = (int)Math.Ceiling((double)State.Profiles.Count / columns);
        string rowDefs = string.Join(",", Enumerable.Repeat("Auto", rows));
        string colDefs = string.Join(",", Enumerable.Repeat("*", columns));

        return Grid(rowDefs, colDefs,
            State.Profiles.Select((profile, index) =>
                Border(
                    VStack(spacing: 4,
                        Label(profile.Title)
                            .H6()
                            .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                        string.IsNullOrEmpty(profile.Description)
                            ? null
                            : Label(profile.Description)
                                .Small()
                                .Muted()
                                .LineBreakMode(LineBreakMode.TailTruncation)
                                .MaxLines(2)
                    )
                    .VCenter()
                )
                .Class("card")
                .MinimumHeightRequest(100)
                .OnTapped(() => EditProfile(profile))
                .GridRow(index / columns)
                .GridColumn(index % columns)
            ).ToArray()
        )
        .ColumnSpacing(12)
        .RowSpacing(12)
        .Padding(16);
    }

    async Task LoadProfiles()
    {
        SetState(s => s.IsLoading = true);
        var profiles = await _skillsRepository.ListAsync();
        SetState(s =>
        {
            s.Profiles = profiles.ToList();
            s.IsLoading = false;
        });
    }

    Task AddProfile()
    {
        return MauiControls.Shell.Current.GoToAsync(nameof(AddSkillProfilePage));
    }

    Task EditProfile(SkillProfile profile)
    {
        return MauiControls.Shell.Current.GoToAsync<EditSkillProfileProps>(
            nameof(EditSkillProfilePage),
            props => props.ProfileID = profile.Id);
    }
}