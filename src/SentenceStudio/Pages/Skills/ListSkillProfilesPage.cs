using CustomLayouts;
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
        return ContentPage("Skill Profiles",
            VScrollView(
                VStack(
                    new HWrap()
                    {
                        State.Profiles.Select(profile =>
                            Border(
                                Grid(
                                    Label()
                                        .Center()
                                        .Text(profile.Title)
                                )
                                .WidthRequest(300)
                                .HeightRequest(120)
                                .OnTapped(() => EditProfile(profile))
                            )
                            .StrokeShape(new Rectangle())
                            .StrokeThickness(1)
                        )
                    }
                    .Spacing((Double)Application.Current.Resources["size320"]),

                    Border(
                        Grid(
                            Label("Add")
                                .Center()
                        )
                        .WidthRequest(300)
                        .HeightRequest(120)
                        .OnTapped(AddProfile)
                    )
                    .StrokeShape(new Rectangle())
                    .StrokeThickness(1)
                    .HStart()
                )
                .Padding((Double)Application.Current.Resources["size160"])
                .Spacing((Double)Application.Current.Resources["size240"])
            )
        ).OnAppearing(LoadProfiles);
    }

    private async Task LoadProfiles()
    {
        var profiles = await _skillsRepository.ListAsync();
        SetState(s => s.Profiles = profiles.ToList());
    }

    private async Task AddProfile()
    {
        await MauiControls.Shell.Current.GoToAsync(nameof(AddSkillProfilePage));
    }

    private async Task EditProfile(SkillProfile profile)
    {
        await MauiControls.Shell.Current.GoToAsync<EditSkillProfileProps>(
            nameof(EditSkillProfilePage),
            props => props.ProfileID = profile.ID);
    }
}