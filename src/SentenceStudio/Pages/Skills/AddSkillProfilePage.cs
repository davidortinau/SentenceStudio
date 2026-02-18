using MauiReactor.Shapes;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Skills;

class AddSkillProfilePageState
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

partial class AddSkillProfilePage : Component<AddSkillProfilePageState>
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

        return ContentPage("Add Skill Profile",
            ToolbarItem("Save").OnClicked(SaveProfile),
            VScrollView(
                VStack(spacing: 16,
                    Border(
                        VStack(spacing: 16,
                            VStack(spacing: 4,
                                Label("Title *")
                                    .Class("form-label"),
                                Entry()
                                    .Text(State.Title)
                                    .OnTextChanged(text => SetState(s => s.Title = text))
                                    .Placeholder("Skill profile title")
                                    .Class("form-control")
                            ),

                            VStack(spacing: 4,
                                Label("Skills Description")
                                    .Class("form-label"),
                                Editor()
                                    .Text(State.Description)
                                    .OnTextChanged(text => SetState(s => s.Description = text))
                                    .AutoSize(EditorAutoSizeOption.TextChanges)
                                    .MinimumHeightRequest(150)
                                    .Placeholder("Describe the skills for this profile")
                                    .Class("form-control")
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
        )
        .BackgroundColor(BootstrapTheme.Current.GetBackground());
    }

    async Task SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(State.Title))
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["ValidationError"]}",
                Text = $"{_localize["TitleRequired"]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
            return;
        }

        var profile = new SkillProfile
        {
            Title = State.Title,
            Description = State.Description
        };

        await _skillsRepository.SaveAsync(profile);
        await MauiControls.Shell.Current.GoToAsync("..");
    }
}
