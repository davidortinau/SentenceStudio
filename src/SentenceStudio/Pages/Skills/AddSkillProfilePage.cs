using MauiReactor.Shapes;

namespace SentenceStudio.Pages.Skills;

class AddSkillProfilePageState
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

partial class AddSkillProfilePage : Component<AddSkillProfilePageState>
{
    [Inject] SkillProfileRepository _skillsRepository;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage("Add Skill Profile",
            ToolbarItem("Save").OnClicked(SaveProfile),
            VScrollView(
                VStack(spacing: 16,
                    Border(
                        VStack(spacing: 16,
                            VStack(spacing: 4,
                                Label("Title *")
                                    .FontSize(14)
                                    .Muted(),
                                Border(
                                    Entry()
                                        .Text(State.Title)
                                        .OnTextChanged(text => SetState(s => s.Title = text))
                                        .Placeholder("Skill profile title")
                                )
                                .Stroke(theme.GetOutline())
                                .StrokeThickness(1)
                                .StrokeShape(new RoundRectangle().CornerRadius(8))
                                .Padding(4, 0)
                            ),

                            VStack(spacing: 4,
                                Label("Skills Description")
                                    .FontSize(14)
                                    .Muted(),
                                Border(
                                    Editor()
                                        .Text(State.Description)
                                        .OnTextChanged(text => SetState(s => s.Description = text))
                                        .AutoSize(EditorAutoSizeOption.TextChanges)
                                        .MinimumHeightRequest(150)
                                        .Placeholder("Describe the skills for this profile")
                                )
                                .Stroke(theme.GetOutline())
                                .StrokeThickness(1)
                                .StrokeShape(new RoundRectangle().CornerRadius(8))
                                .Padding(4, 0)
                            )
                        )
                    )
                    .BackgroundColor(theme.GetSurface())
                    .Stroke(theme.GetOutline())
                    .StrokeThickness(1)
                    .StrokeShape(new RoundRectangle().CornerRadius(12))
                    .Padding(16),

                    Button("Save Skill Profile")
                        .Primary()
                        .HeightRequest(44)
                        .HFill()
                        .OnClicked(SaveProfile)
                )
                .Margin(16)
            )
        );
    }

    async Task SaveProfile()
    {
        var profile = new SkillProfile
        {
            Title = State.Title,
            Description = State.Description
        };

        await _skillsRepository.SaveAsync(profile);
        await MauiControls.Shell.Current.GoToAsync("..");
    }
}