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
        return new SfSegmentedControl{
                    new SfSegmentItem().ImageSource(ApplicationTheme.IconKeyboard),
                    new SfSegmentItem().ImageSource(ApplicationTheme.IconMultiSelect)
                }
                .Background(Colors.Transparent)
                .ShowSeparator(true)
                .SegmentCornerRadius(0)
                .Stroke(Theme.IsLightTheme ? ApplicationTheme.Black : ApplicationTheme.White)
                .StrokeThickness(1)
                .SelectedIndex(State.SelectedMode == "Text" ? 0 : 1)
                .OnSelectionChanged((s, e) => {
                    if(e.NewIndex == 0)
                    {
                        SetState(s => s.SelectedMode = "Text");
                        _onSelectedModeChanged?.Invoke("Text");
                    }
                    else
                    {
                        SetState(s => s.SelectedMode = "MultipleChoice");
                        _onSelectedModeChanged?.Invoke("MultipleChoice");
                    }
                })
                .SegmentWidth(40)
                .SegmentHeight(40)
                .HCenter();
    }
} 