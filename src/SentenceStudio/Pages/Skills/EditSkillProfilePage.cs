namespace SentenceStudio.Pages.Skills;

class EditSkillProfilePageState
{
    public SkillProfile Profile { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

class EditSkillProfileProps
{
    public int ProfileID { get; set; }
}

partial class EditSkillProfilePage : Component<EditSkillProfilePageState, EditSkillProfileProps>
{
    [Inject] SkillProfileRepository _skillsRepository;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage("Edit Skill Profile",
            ToolbarItem("Save").OnClicked(Save),
            ToolbarItem("Delete").OnClicked(Delete),
            VScrollView(
                VStack(
                    VStack(
                        Label("Title").HStart(),
                        Border(
                            Entry()
                                .Text(State.Title)
                                .OnTextChanged(text => SetState(s => s.Title = text))
                        )
                        .ThemeKey(MyTheme.InputWrapper)
                    )
                    .Spacing(MyTheme.Size120),

                    VStack(
                        Label("Skills Description").HStart(),
                        Border(
							Editor()
								.Text(State.Description)
								.MinimumHeightRequest(300)
                                .AutoSize(EditorAutoSizeOption.TextChanges)
                                .OnTextChanged(text => SetState(s => s.Description = text))
                        )
                        .ThemeKey(MyTheme.InputWrapper)
                    )
                    .Spacing(MyTheme.Size120),

                    Label($"Created: {State.Profile.CreatedAt:MM/dd/yyyy}"),
                    Label($"Updated: {State.Profile.UpdatedAt:MM/dd/yyyy}")
                )
                .Spacing(MyTheme.Size160)
                .Padding(MyTheme.Size160)
            )
        ).OnAppearing(LoadProfile);
    }

    async Task LoadProfile()
    {
        if (Props.ProfileID > 0)
        {
            var profile = await _skillsRepository.GetSkillProfileAsync(Props.ProfileID);
            SetState(s => 
            {
                s.Profile = profile;
                s.Title = profile.Title;
                s.Description = profile.Description;
            });
        }
    }

    async Task Save()
    {
        State.Profile.Title = State.Title;
        State.Profile.Description = State.Description;
        
        var result = await _skillsRepository.SaveAsync(State.Profile);
        if (result > 0)
            await AppShell.DisplayToastAsync(_localize["Saved"].ToString());
            
        await MauiControls.Shell.Current.GoToAsync("..");
    }

    async Task Delete()
    {
        var result = await _skillsRepository.DeleteAsync(State.Profile);
        if (result > 0)
            await AppShell.DisplayToastAsync(_localize["Deleted"].ToString());
            
        await MauiControls.Shell.Current.GoToAsync("..");
    }
}