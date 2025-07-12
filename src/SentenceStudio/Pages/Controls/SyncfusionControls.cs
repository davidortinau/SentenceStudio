using MauiReactor.Internals;
using System.Linq;

namespace SentenceStudio.Pages.Controls;

[Scaffold(typeof(Syncfusion.Maui.Toolkit.SfView))]
public abstract partial class SfView
{
}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.Internals.PullToRefreshBase))]
partial class PullToRefreshBase
{

}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.PullToRefresh.SfPullToRefresh))]
partial class SfPullToRefresh
{
    protected override void OnAddChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);

        if (childControl is View view)
        {
            NativeControl.PullableContent = view;
        }
        

        base.OnAddChild(widget, childControl);
    }
}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.SfContentView))]
partial class SfContentView
{
}

partial class SfContentView<T>
{
    protected override void OnAddChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);
        if (childControl is MauiControls.View view)
        {
            NativeControl.Content = view;
        }
        base.OnAddChild(widget, childControl);
    }
}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.TextInputLayout.SfTextInputLayout))]
partial class SfTextInputLayout
{ }

partial interface ISfTextInputLayout
{
    public VisualNode? TrailingView { get; set; }
}

partial class SfTextInputLayout<T>
{
    VisualNode? ISfTextInputLayout.TrailingView { get; set; }

    protected override IEnumerable<VisualNode> RenderChildren()
    {
        var thisAsISfTextInputLayout = (ISfTextInputLayout)this;

        var children = base.RenderChildren();

        if (thisAsISfTextInputLayout.TrailingView != null)
        {
            children = children.Concat(new[] { thisAsISfTextInputLayout.TrailingView });
        }

        return children;
    }

    protected override void OnAddChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);

        var thisAsISfTextInputLayout = (ISfTextInputLayout)this;

        if (widget == thisAsISfTextInputLayout.TrailingView)
        {
            NativeControl.TrailingView = (View)childControl;
        }
        else
        {
            base.OnAddChild(widget, childControl);
        }
    }
}

partial class SfTextInputLayoutExtensions
{
    public static T TrailingView<T>(this T textInputLayout, VisualNode? trailingView) where T : ISfTextInputLayout
    {
        textInputLayout.TrailingView = trailingView;
        return textInputLayout;
    }
}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.Shimmer.SfShimmer))]
partial class SfShimmer
{
    
}

partial interface ISfShimmer
{
    public VisualNode? CustomView { get; set; }
}

partial class SfShimmer<T>
{
    VisualNode? ISfShimmer.CustomView { get; set; }

    protected override IEnumerable<VisualNode> RenderChildren()
    {
        var thisAsISfShimmer = (ISfShimmer)this;

        var children = base.RenderChildren();

        if (thisAsISfShimmer.CustomView != null)
        {
            children = children.Concat([thisAsISfShimmer.CustomView]);
        }

        return children;
    }

    protected override void OnAddChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);

        var thisAsISfShimmer = (ISfShimmer)this;

        if (widget == thisAsISfShimmer.CustomView)
        {
            NativeControl.CustomView = (View)childControl;
        }
        else
        {
            base.OnAddChild(widget, childControl);
        }
    }
}

partial class SfShimmerExtensions
{
    public static T CustomView<T>(this T shimmer, VisualNode? customView) where T : ISfShimmer
    {
        shimmer.CustomView = customView;
        return shimmer;
    }
}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.Shimmer.ShimmerView))]
partial class ShimmerView
{

}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.EffectsView.SfEffectsView))]
partial class SfEffectsView
{

}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.SegmentedControl.SfSegmentedControl))]
partial class SfSegmentedControl
{
    protected override void OnAddChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);

        if (childControl is Syncfusion.Maui.Toolkit.SegmentedControl.SfSegmentItem segmentItem)
        {
            if (NativeControl.ItemsSource is IList<Syncfusion.Maui.Toolkit.SegmentedControl.SfSegmentItem> existingList)
            {
                existingList.Add(segmentItem);
            }
            else
            {
                NativeControl.ItemsSource = new List<Syncfusion.Maui.Toolkit.SegmentedControl.SfSegmentItem> { segmentItem };
            }
        }

        base.OnAddChild(widget, childControl);
    }
}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.SegmentedControl.SfSegmentItem))]
partial class SfSegmentItem
{

}

// [Scaffold(typeof(Syncfusion.Maui.Toolkit.SegmentedControl.SelectionIndicatorSettings))]
// partial class SelectionIndicatorSettings
// {

// }

// [Scaffold(typeof(Syncfusion.Maui.Toolkit.SegmentedControl.SelectionIndicatorPlacement))]
// partial class SelectionIndicatorPlacement
// {

// }

// [Scaffold(typeof(Syncfusion.Maui.Toolkit.SegmentedControl.SfSegmentedControlStyles))]
// partial class SfSegmentedControlStyles
// {

// }

[Scaffold(typeof(Syncfusion.Maui.Toolkit.Charts.ChartBase))]
partial class ChartBase
{

}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.Charts.SfCircularChart))]
partial class SfCircularChart
{
    protected override void OnAddChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);

        if (childControl is Syncfusion.Maui.Toolkit.Charts.ChartLegend chartLegend)
        {
            NativeControl.Legend = chartLegend;
        }
        else if (childControl is Syncfusion.Maui.Toolkit.Charts.ChartSeries chartSeries)
        { 
            NativeControl.Series.Add(chartSeries);
        }

        base.OnAddChild(widget, childControl);
    }
}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.Charts.ChartLegend))]
partial class ChartLegend
{
    partial class ChartLegendWithCustomMaximumSizeCoefficient : Syncfusion.Maui.Toolkit.Charts.ChartLegend
    {
        protected override double GetMaximumSizeCoefficient()
        {
            return 0.5;
        }
    }

    protected override void OnMount()
    {
        _nativeControl ??= new ChartLegendWithCustomMaximumSizeCoefficient();

        base.OnMount();
    }

    protected override void OnAddChild(VisualNode widget, BindableObject childNativeControl)
    {
        Validate.EnsureNotNull(NativeControl);

        if (childNativeControl is Syncfusion.Maui.Toolkit.Charts.ChartLegendLabelStyle chartLegendLabelStyle)
        {
            NativeControl.LabelStyle = chartLegendLabelStyle;
        }

        base.OnAddChild(widget, childNativeControl);
    }
}


[Scaffold(typeof(Syncfusion.Maui.Toolkit.Charts.ChartLegendLabelStyle))]
partial class ChartLegendLabelStyle
{

}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.Charts.ChartSeries))]
partial class ChartSeries
{

}

partial interface IChartSeries
{
    public object? ItemsSource { get; set; }
}

partial class ChartSeries<T>
{
    object? IChartSeries.ItemsSource { get; set; }

    partial void OnBeginUpdate()
    {
        Validate.EnsureNotNull(NativeControl);

        var thisAsIChartSeries = (IChartSeries)this;
        SetPropertyValue(NativeControl, Syncfusion.Maui.Toolkit.Charts.ChartSeries.ItemsSourceProperty, thisAsIChartSeries.ItemsSource);
    }
}

partial class ChartSeriesExtensions
{
    public static T ItemsSource<T>(this T chartSeries, object? itemsSource) where T : IChartSeries
    {
        chartSeries.ItemsSource = itemsSource;
        return chartSeries;
    }
}



[Scaffold(typeof(Syncfusion.Maui.Toolkit.Charts.CircularSeries))]
partial class CircularSeries
{

}


[Scaffold(typeof(Syncfusion.Maui.Toolkit.Charts.RadialBarSeries))]
partial class RadialBarSeries
{

}

[Scaffold(typeof(Syncfusion.Maui.Toolkit.BottomSheet.SfBottomSheet))]
partial class SfBottomSheet
{
    protected override void OnAddChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);

        if (childControl is View view)
        {
            NativeControl.BottomSheetContent = view;
        }

        base.OnAddChild(widget, childControl);
    }

    protected override void OnRemoveChild(VisualNode widget, BindableObject childControl)
    {
        Validate.EnsureNotNull(NativeControl);

        if (childControl is View)
        {
            NativeControl.BottomSheetContent = null!;
        }

        base.OnRemoveChild(widget, childControl);
    }
}

// [Scaffold(typeof(Syncfusion.Maui.Inputs.SfComboBox))]
// partial class SfComboBox
// {
// }

// [Scaffold(typeof(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase))]
// partial class DropDownListBase<T>{}

// partial interface ISfComboBox
// {
//     public SelectionMode SelectionMode { get; set; }
// }

// partial class SfComboBox<T>
// {
//     SelectionMode ISfComboBox.SelectionMode { get; set; }

//     partial void OnBeginUpdate()
//     {
//         Validate.EnsureNotNull(NativeControl);

//         var thisAsISfComboBox = (ISfComboBox)this;
        
//         // Set selection mode to multiple if specified
//         if (thisAsISfComboBox.SelectionMode == SelectionMode.Multiple)
//         {
//             SetPropertyValue(NativeControl, "SelectionMode", Syncfusion.Maui.Toolkit.ComboBox.ComboBoxSelectionMode.Multiple);
//         }
//     }
// }

// partial class SfComboBoxExtensions
// {
//     public static T SelectionMode<T>(this T comboBox, SelectionMode selectionMode) where T : ISfComboBox
//     {
//         comboBox.SelectionMode = selectionMode;
//         return comboBox;
//     }
// }
