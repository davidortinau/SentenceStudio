namespace SentenceStudio.Pages.Clozure;

class ModeSelectorState
{
    public string SelectedMode { get; set; } = "Text";
}

partial class ModeSelector : Component<ModeSelectorState>
{
    [Prop]
    Action<string> _onSelectedModeChanged;

    [Prop]
    string _selectedMode;

    protected override void OnPropsChanged()
    {
        base.OnPropsChanged();
    }

    public override VisualNode Render()
    {
        return HStack(spacing: 10,
            ImageButton()
                .Source(SegoeFluentIcons.KeyboardStandard.ToImageSource())
                .Aspect(Aspect.Center)
                .Background(State.SelectedMode == "Text" ? 
                    (Color)Application.Current.Resources["Primary"] : 
                    Colors.Transparent)
                // .TextColor(State.SelectedMode == "Text" ? 
                //     Colors.White : 
                //     (Color)Application.Current.Resources["Primary"])
                .OnClicked(() => 
                {
                    SetState(s => s.SelectedMode = "Text");
                    _onSelectedModeChanged?.Invoke("Text");
                }),
            ImageButton()
                .Source(SegoeFluentIcons.MultiSelect.ToImageSource())
                .Aspect(Aspect.Center)
                .Background(State.SelectedMode == "MultipleChoice" ? 
                    (Color)Application.Current.Resources["Primary"] : 
                    Colors.Transparent)
                .OnClicked(() => 
                {
                    SetState(s => s.SelectedMode = "MultipleChoice");
                    _onSelectedModeChanged?.Invoke("MultipleChoice");
                })
        ).HCenter();
    }
} 