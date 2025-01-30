namespace ReactorCustomLayouts;

public partial class HorizontalWrapLayout
{
    public HorizontalWrapLayout(double spacing) => this.Spacing(spacing);
}

public class HWrap : HorizontalWrapLayout
{
    public HWrap() { }

    public HWrap(double spacing)
        => this.Spacing(spacing);

    public HWrap(Action<CustomLayouts.HorizontalWrapLayout?> componentRefAction, double spacing)
        : base(componentRefAction)
        => this.Spacing(spacing);

    public HWrap(Action<CustomLayouts.HorizontalWrapLayout?> componentRefAction)
        :base(componentRefAction)
    { }
}

public partial class Component
{
    public static HWrap HWrap(params IEnumerable<VisualNode?> children)
    {
        var HWrap = new HWrap();
        HWrap.AddChildren(children);
        return HWrap;
    }

    public static HWrap HWrap(double spacing, params IEnumerable<VisualNode?> children)
    {
        var HWrap = new HWrap();
        HWrap.AddChildren(children);
        HWrap.Spacing(spacing);
        return HWrap;
    }

    public static HWrap HWrap(Action<CustomLayouts.HorizontalWrapLayout?> componentRefAction, params IEnumerable<VisualNode?> children)
    {
        var HWrap = new HWrap(componentRefAction);
        HWrap.AddChildren(children);
        return HWrap;
    }

    public static HWrap HWrap(Action<CustomLayouts.HorizontalWrapLayout?> componentRefAction, double spacing, params IEnumerable<VisualNode?> children)
    {
        var HWrap = new HWrap(componentRefAction);
        HWrap.AddChildren(children);
        HWrap.Spacing(spacing);
        return HWrap;
    }
}