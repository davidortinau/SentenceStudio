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
                    .Spacing(ApplicationTheme.Size320),

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
                .Padding(ApplicationTheme.Size160)
                .Spacing(ApplicationTheme.Size240)
            )
        ).OnAppearing(LoadProfiles);
    }

    async Task LoadProfiles()
    {
        var profiles = await _skillsRepository.ListAsync();
        SetState(s => s.Profiles = profiles.ToList());
    }    Task AddProfile()
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