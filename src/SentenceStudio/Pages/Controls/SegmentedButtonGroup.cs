using MauiReactor.Shapes;

namespace SentenceStudio.Pages.Controls;

partial class SegmentedButtonGroup : Component
{
    [Prop]
    VisualNode _left;

    [Prop]
    VisualNode _right;

    [Prop]
    double _cornerRadius = 6;

    [Prop]
    Thickness _margin = new(0);

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;
        return Border(
            Grid(rows: "Auto", columns: "*,Auto,*",
                _left.GridColumn(0),
                BoxView()
                    .WidthRequest(1)
                    .BackgroundColor(theme.GetOutline())
                    .GridColumn(1)
                    .VFill(),
                _right.GridColumn(2)
            )
            .ColumnSpacing(0)
        )
        .Stroke(theme.GetOutline())
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(_cornerRadius))
        .Padding(0)
        .Margin(_margin);
    }
}
