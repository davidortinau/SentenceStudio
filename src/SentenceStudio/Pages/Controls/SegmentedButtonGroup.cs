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
        var cr = _cornerRadius;

        return Grid(rows: "Auto", columns: "*,*",
            // Left button with rounded left corners, flat right corners
            Border(_left)
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(cr, 0, cr, 0))
                .Padding(0)
                .GridColumn(0),
            // Right button with flat left corners, rounded right corners
            Border(_right)
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(0, cr, 0, cr))
                .Padding(0)
                .Margin(-1, 0, 0, 0) // overlap left border for seamless join
                .GridColumn(1)
        )
        .ColumnSpacing(0)
        .Margin(_margin);
    }
}
