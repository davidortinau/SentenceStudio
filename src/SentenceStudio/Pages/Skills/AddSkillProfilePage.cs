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
        return ContentPage("Add Skill Profile",
			ToolbarItem("Save").OnClicked(SaveProfile),
            VScrollView(
                VStack(
                    new SfTextInputLayout
                    {
                        Entry()
                            .Text(State.Title)
                            .OnTextChanged(text => SetState(s => s.Title = text))
                    }
                    .Hint("Title"),

                    new SfTextInputLayout
                    {
                        Editor()
                            .Text(State.Description)
                            .OnTextChanged(text => SetState(s => s.Description = text))
                            .AutoSize(EditorAutoSizeOption.TextChanges)
                    }
                    .Hint("Skills Description")
                )
                .Spacing(ApplicationTheme.Size320)
                .Margin(ApplicationTheme.Size160)
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