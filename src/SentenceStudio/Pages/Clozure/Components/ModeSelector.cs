using MauiReactor.Shapes;

namespace SentenceStudio.Pages.Clozure;

partial class ModeSelector : Component
{
    [Prop]
    Action<string> _onSelectedModeChanged;

    [Prop]
    string _selectedMode;

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;
        var isText = _selectedMode == "Text";
        var selectedColor = theme.OnPrimary;
        var unselectedColor = theme.GetOnBackground();

        return Border(
            HStack(spacing: 0,
                ImageButton()
                    .Source(BootstrapIcons.Create(BootstrapIcons.Keyboard, isText ? selectedColor : unselectedColor, 20))
                    .BackgroundColor(isText ? theme.Primary : Colors.Transparent)
                    .WidthRequest(40)
                    .HeightRequest(40)
                    .OnClicked(() => _onSelectedModeChanged?.Invoke("Text")),
                ImageButton()
                    .Source(BootstrapIcons.Create(BootstrapIcons.ListUl, !isText ? selectedColor : unselectedColor, 20))
                    .BackgroundColor(!isText ? theme.Primary : Colors.Transparent)
                    .WidthRequest(40)
                    .HeightRequest(40)
                    .OnClicked(() => _onSelectedModeChanged?.Invoke("MultipleChoice"))
            )
        )
        .Stroke(theme.GetOutline())
        .StrokeShape(new RoundRectangle().CornerRadius(4))
        .Padding(0)
        .HCenter();
    }
} 