using ReactorCustomLayouts;
using MauiReactor.Shapes;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Skills;

class ListSkillProfilesPageState
{
    public List<SkillProfile> Profiles { get; set; } = [];
    public bool IsLoading { get; set; } = true;
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

        return ContentPage("Skill Profiles",
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text($"{_localize["Add"]}")
                .OnClicked(async () => await AddProfile()),
            State.IsLoading
                ? (VisualNode)ActivityIndicator().IsRunning(true).Center()
                : State.Profiles.Count == 0
                ? (VisualNode)VStack(spacing: 16,
                    Label("No skill profiles yet")
                        .Muted()
                        .HCenter(),
                    Button($"{_localize["Add"]}")
                        .Primary()
                        .HCenter()
                        .OnClicked(async () => await AddProfile())
                  )
                  .Padding(40)
                  .VCenter()
                : VScrollView(
                    VStack(
                        new HWrap()
                        {
                            State.Profiles.Select(profile =>
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
                                    .Padding(16)
                                )
                                .WidthRequest(300)
                                .MinimumHeightRequest(120)
                                .Class("card")
                                .OnTapped(() => EditProfile(profile))
                            )
                        }
                        .Spacing(12)
                    )
                    .Padding(16)
                    .Spacing(24)
                )
        ).BackgroundColor(BootstrapTheme.Current.GetBackground()).OnAppearing(LoadProfiles);
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