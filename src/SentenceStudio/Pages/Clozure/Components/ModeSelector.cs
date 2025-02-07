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
        // State.SelectedMode = SelectedMode;
        base.OnPropsChanged();
    }

    public override VisualNode Render()
    {
        return HStack(spacing: 10,
            Button("Text")
                .Background(State.SelectedMode == "Text" ? 
                    (Color)Application.Current.Resources["Primary"] : 
                    Colors.Transparent)
                .TextColor(State.SelectedMode == "Text" ? 
                    Colors.White : 
                    (Color)Application.Current.Resources["Primary"])
                .OnClicked(() => 
                {
                    SetState(s => s.SelectedMode = "Text");
                    // OnSelectedModeChanged?.Invoke("Text");
                }),
            Button("Multiple Choice")
                .Background(State.SelectedMode == "MultipleChoice" ? 
                    (Color)Application.Current.Resources["Primary"] : 
                    Colors.Transparent)
                .TextColor(State.SelectedMode == "MultipleChoice" ? 
                    Colors.White : 
                    (Color)Application.Current.Resources["Primary"])
                .OnClicked(() => 
                {
                    SetState(s => s.SelectedMode = "MultipleChoice");
                    // OnSelectedModeChanged?.Invoke("MultipleChoice");
                })
        );
    }
} 