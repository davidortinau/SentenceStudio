using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using MauiReactor.Animations;
using MauiReactor.Shapes;
using MauiReactor.Internals;
using MauiReactor;

namespace ReactorCustomLayouts;

public partial interface IHorizontalWrapLayout : IStackBase
{
}

public partial class HorizontalWrapLayout<T> : StackBase<T>, IHorizontalWrapLayout where T : CustomLayouts.HorizontalWrapLayout, new()
{
    public HorizontalWrapLayout(Action<T?>? componentRefAction = null) : base(componentRefAction)
    {
        HorizontalWrapLayoutStyles.Default?.Invoke(this);
    }

    partial void OnBeginAnimate();
    partial void OnEndAnimate();
    protected override void OnThemeChanged()
    {
        if (ThemeKey != null && HorizontalWrapLayoutStyles.Themes.TryGetValue(ThemeKey, out var styleAction))
        {
            styleAction(this);
        }

        base.OnThemeChanged();
    }

    partial void Migrated(VisualNode newNode);
    protected override void OnMigrated(VisualNode newNode)
    {
        Migrated(newNode);
        base.OnMigrated(newNode);
    }
}

public partial class HorizontalWrapLayout : HorizontalWrapLayout<CustomLayouts.HorizontalWrapLayout>
{
    public HorizontalWrapLayout(Action<CustomLayouts.HorizontalWrapLayout?>? componentRefAction = null) : base(componentRefAction)
    {
    }

    public HorizontalWrapLayout(params IEnumerable<VisualNode?>? children)
    {
        if (children != null)
        {
            this.AddChildren(children);
        }
    }
}

public static partial class HorizontalWrapLayoutExtensions
{
}

public static partial class HorizontalWrapLayoutStyles
{
    public static Action<IHorizontalWrapLayout>? Default { get; set; }
    public static Dictionary<string, Action<IHorizontalWrapLayout>> Themes { get; } = [];
}