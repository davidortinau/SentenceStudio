namespace SentenceStudio.Pages.Clozure;

partial class ModeSelector : Component
{
    [Prop]
    Action<string> _onSelectedModeChanged;

    [Prop]
    string _selectedMode;

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
                .SelectedIndex(_selectedMode == "Text" ? 0 : 1)
                .OnSelectionChanged((s, e) => {
                    if(e.NewIndex == 0)
                    {
                        _onSelectedModeChanged?.Invoke("Text");
                    }
                    else
                    {
                        _onSelectedModeChanged?.Invoke("MultipleChoice");
                    }
                })
                .SegmentWidth(40)
                .SegmentHeight(40)
                .HCenter();
    }
} 