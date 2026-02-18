using ReactorCustomLayouts;
using MauiReactor.Shapes;

namespace SentenceStudio.Pages.Skills;

class ListSkillProfilesPageState
{
    public List<SkillProfile> Profiles { get; set; } = [];
}

partial class ListSkillProfilesPage : Component<ListSkillProfilesPageState>
{
    [Inject] SkillProfileRepository _skillsRepository;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage("Skill Profiles",
            ToolbarItem().Order(ToolbarItemOrder.Secondary).Text($"{_localize["Add"]}")
                .OnClicked(async () => await AddProfile()),
            State.Profiles.Count == 0
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
                                .BackgroundColor(theme.GetSurface())
                                .Stroke(theme.GetOutline())
                                .StrokeThickness(1)
                                .StrokeShape(new RoundRectangle().CornerRadius(12))
                                .OnTapped(() => EditProfile(profile))
                            )
                        }
                        .Spacing(12)
                    )
                    .Padding(16)
                    .Spacing(24)
                )
        ).OnAppearing(LoadProfiles);
    }

    async Task LoadProfiles()
    {
        var profiles = await _skillsRepository.ListAsync();
        SetState(s => s.Profiles = profiles.ToList());
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