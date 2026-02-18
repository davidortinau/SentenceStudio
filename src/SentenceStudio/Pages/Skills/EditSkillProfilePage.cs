using MauiReactor.Shapes;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

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
    LocalizationManager _localize => LocalizationManager.Instance;

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
                                    .FontSize(14)
                                    .Muted(),
                                Border(
                                    Entry()
                                        .Text(State.Title)
                                        .OnTextChanged(text => SetState(s => s.Title = text))
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
                                        .MinimumHeightRequest(300)
                                        .AutoSize(EditorAutoSizeOption.TextChanges)
                                        .OnTextChanged(text => SetState(s => s.Description = text))
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
                    .BackgroundColor(theme.GetSurface())
                    .Stroke(theme.GetOutline())
                    .StrokeThickness(1)
                    .StrokeShape(new RoundRectangle().CornerRadius(12))
                    .Padding(16)
                )
                .Padding(16)
            )
        ).OnAppearing(LoadProfile);
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