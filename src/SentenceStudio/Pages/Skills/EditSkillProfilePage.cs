using MauiReactor.Shapes;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Skills;

class EditSkillProfilePageState
{
    public SkillProfile Profile { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsLoading { get; set; } = true;
}

class EditSkillProfileProps
{
    public int ProfileID { get; set; }
}

partial class EditSkillProfilePage : Component<EditSkillProfilePageState, EditSkillProfileProps>
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

        return ContentPage("Edit Skill Profile",
            ToolbarItem("Save").OnClicked(Save),
            ToolbarItem("Delete").OnClicked(Delete),
            State.IsLoading ?
                (VisualNode)ActivityIndicator().IsRunning(true).Center() :
            VScrollView(
                VStack(spacing: 16,
                    Border(
                        VStack(spacing: 16,
                            VStack(spacing: 4,
                                Label("Title")
                                    .Class("form-label").Muted(),
                                Entry()
                                    .Text(State.Title)
                                    .OnTextChanged(text => SetState(s => s.Title = text))
                                    .Class("form-control")
                            ),

                            VStack(spacing: 4,
                                Label("Skills Description")
                                    .Class("form-label").Muted(),
                                Editor()
                                    .Text(State.Description)
                                    .MinimumHeightRequest(300)
                                    .AutoSize(EditorAutoSizeOption.TextChanges)
                                    .OnTextChanged(text => SetState(s => s.Description = text))
                                    .Class("form-control")
                            )
                        )
                    )
                    .Class("card")
                    .PaddingLevel(4),

                    Border(
                        VStack(spacing: 8,
                            Label("Details")
                                .H5()
                                .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                            Grid(rows: "Auto,Auto", columns: "Auto,*",
                                Label("Created")
                                    .FontSize(14)
                                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                                    .GridRow(0).GridColumn(0)
                                    .Margin(0, 0, 16, 0),
                                Label($"{State.Profile.CreatedAt:g}")
                                    .FontSize(14)
                                    .GridRow(0).GridColumn(1),
                                Label("Updated")
                                    .FontSize(14)
                                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                                    .GridRow(1).GridColumn(0)
                                    .Margin(0, 0, 16, 0),
                                Label($"{State.Profile.UpdatedAt:g}")
                                    .FontSize(14)
                                    .GridRow(1).GridColumn(1)
                            )
                        )
                    )
                    .Class("card")
                    .PaddingLevel(4)
                )
                .Padding(16)
            )
        ).BackgroundColor(BootstrapTheme.Current.GetBackground()).OnAppearing(LoadProfile);
    }

    async Task LoadProfile()
    {
        SetState(s => s.IsLoading = true);

        if (Props.ProfileID > 0)
        {
            var profile = await _skillsRepository.GetSkillProfileAsync(Props.ProfileID);
            if (profile == null)
            {
                SetState(s => s.IsLoading = false);
                await IPopupService.Current.PushAsync(new SimpleActionPopup
                {
                    Title = $"{_localize["NotFound"]}",
                    Text = $"{_localize["SkillProfileNotFound"]}",
                    ActionButtonText = $"{_localize["OK"]}",
                    ShowSecondaryActionButton = false
                });
                await MauiControls.Shell.Current.GoToAsync("..");
                return;
            }
            SetState(s => 
            {
                s.Profile = profile;
                s.Title = profile.Title;
                s.Description = profile.Description;
                s.IsLoading = false;
            });
        }
        else
        {
            SetState(s => s.IsLoading = false);
        }
    }

    async Task Save()
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

        State.Profile.Title = State.Title;
        State.Profile.Description = State.Description;
        
        var result = await _skillsRepository.SaveAsync(State.Profile);
        if (result > 0)
            await AppShell.DisplayToastAsync($"{_localize["Saved"]}");
            
        await MauiControls.Shell.Current.GoToAsync("..");
    }

    async Task Delete()
    {
        var tcs = new TaskCompletionSource<bool>();
        var confirmPopup = new SimpleActionPopup
        {
            Title = $"{_localize["DeleteSkillProfile"]}",
            Text = $"{_localize["DeleteSkillProfileConfirm"]}",
            ActionButtonText = $"{_localize["Delete"]}",
            SecondaryActionButtonText = $"{_localize["Cancel"]}",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(confirmPopup);
        bool confirm = await tcs.Task;

        if (!confirm) return;

        var result = await _skillsRepository.DeleteAsync(State.Profile);
        if (result > 0)
            await AppShell.DisplayToastAsync($"{_localize["Deleted"]}");
            
        await MauiControls.Shell.Current.GoToAsync("..");
    }
}
