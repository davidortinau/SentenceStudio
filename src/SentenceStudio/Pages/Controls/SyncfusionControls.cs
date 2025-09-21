using MauiReactor.Internals;
using Syncfusion.Maui.Inputs;
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
    public VisualNode? LeadingView { get; set; }
    public VisualNode? TrailingView { get; set; }
}

partial class SfTextInputLayout<T>
{
    VisualNode? ISfTextInputLayout.LeadingView { get; set; }
    VisualNode? ISfTextInputLayout.TrailingView { get; set; }

    protected override IEnumerable<VisualNode> RenderChildren()
    {
        var thisAsISfTextInputLayout = (ISfTextInputLayout)this;

        var children = base.RenderChildren();

        if (thisAsISfTextInputLayout.LeadingView != null)
        {
            children = children.Concat(new[] { thisAsISfTextInputLayout.LeadingView });
        }

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

        if (widget == thisAsISfTextInputLayout.LeadingView)
        {
            NativeControl.LeadingView = (View)childControl;
        }
        else if (widget == thisAsISfTextInputLayout.TrailingView)
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
    public static T LeadingView<T>(this T textInputLayout, VisualNode? leadingView) where T : ISfTextInputLayout
    {
        textInputLayout.LeadingView = leadingView;
        return textInputLayout;
    }
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

// Manual SfComboBox wrapper - avoiding scaffold inheritance issues
public partial class SfComboBox : MauiReactor.VisualNode<Syncfusion.Maui.Inputs.SfComboBox>
{
    private System.EventHandler<Syncfusion.Maui.Inputs.SelectionChangedEventArgs> _selectionChangedHandler;

    public SfComboBox()
    {
    }

    protected override void OnMount()
    {
        _nativeControl ??= new Syncfusion.Maui.Inputs.SfComboBox();
        base.OnMount();

        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfComboBox)NativeControl).SelectionChanged += _selectionChangedHandler;
        }
    }

    protected override void OnUnmount()
    {
        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfComboBox)NativeControl).SelectionChanged -= _selectionChangedHandler;
        }

        base.OnUnmount();
    }

    internal void SetSelectionChangedHandler(System.EventHandler<Syncfusion.Maui.Inputs.SelectionChangedEventArgs> handler)
    {
        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfComboBox)NativeControl).SelectionChanged -= _selectionChangedHandler;
        }

        _selectionChangedHandler = handler;

        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfComboBox)NativeControl).SelectionChanged += _selectionChangedHandler;
        }
    }
}

// Extension methods for common SfComboBox properties
public static partial class SfComboBoxExtensions
{
    public static SfComboBox ItemsSource(this SfComboBox comboBox, System.Collections.IEnumerable itemsSource)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.ItemsSourceProperty, itemsSource);
        return comboBox;
    }

    public static SfComboBox DisplayMemberPath(this SfComboBox comboBox, string displayMemberPath)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.DisplayMemberPathProperty, displayMemberPath);
        return comboBox;
    }

    public static SfComboBox SelectedItem(this SfComboBox comboBox, object selectedItem)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.SelectedItemProperty, selectedItem);
        return comboBox;
    }

    public static SfComboBox SelectedIndex(this SfComboBox comboBox, int selectedIndex)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.SelectedIndexProperty, selectedIndex);
        return comboBox;
    }

    public static SfComboBox SelectionMode(this SfComboBox comboBox, Syncfusion.Maui.Inputs.ComboBoxSelectionMode selectionMode)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.SelectionModeProperty, selectionMode);
        return comboBox;
    }

    public static SfComboBox IsEditable(this SfComboBox comboBox, bool isEditable)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.IsEditableProperty, isEditable);
        return comboBox;
    }

    public static SfComboBox Text(this SfComboBox comboBox, string text)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.TextProperty, text);
        return comboBox;
    }

    public static SfComboBox OnSelectionChanged(this SfComboBox comboBox, EventHandler<Syncfusion.Maui.Inputs.SelectionChangedEventArgs> handler)
    {
        comboBox.SetSelectionChangedHandler(handler);
        return comboBox;
    }

    public static SfComboBox DropDownBackground(this SfComboBox comboBox, Brush brush)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.BackgroundProperty, brush);
        return comboBox;
    }

    public static SfComboBox DropDownBackgroundColor(this SfComboBox comboBox, Color brush)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.BackgroundColorProperty, brush);
        return comboBox;
    }

    public static SfComboBox BackgroundColor(this SfComboBox comboBox, Color color)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.BackgroundColorProperty, color);
        return comboBox;
    }

    public static SfComboBox PlaceholderText(this SfComboBox comboBox, string text)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.PlaceholderProperty, text);
        return comboBox;
    }

    public static SfComboBox PlaceholderColor(this SfComboBox comboBox, Color color)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.PlaceholderColorProperty, color);
        return comboBox;
    }

    public static SfComboBox SelectedItems(this SfComboBox comboBox, System.Collections.IList selectedItems)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.SelectedItemsProperty, selectedItems);
        return comboBox;
    }

    public static SfComboBox TokenItemStyle(this SfComboBox comboBox, Style style)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.TokenItemStyleProperty, style);
        return comboBox;
    }

    public static SfComboBox ItemPadding(this SfComboBox comboBox, Thickness value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.ItemPaddingProperty, value);
        return comboBox;
    }

    public static SfComboBox HeightRequest(this SfComboBox comboBox, double value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.HeightRequestProperty, value);
        return comboBox;
    }

    public static SfComboBox TextColor(this SfComboBox comboBox, Color value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.TextColorProperty, value);
        return comboBox;
    }

    public static SfComboBox MinimumHeightRequest(this SfComboBox comboBox, double value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.MinimumHeightRequestProperty, value);
        return comboBox;
    }

    // --- Additional helpers and property wrappers ---
    static BindableProperty? FindBindableProperty(string propertyName)
    {
        // Try candidate types where properties may be declared
        var candidates = new[] {
            typeof(Syncfusion.Maui.Inputs.SfComboBox),
            typeof(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase)
        };

        foreach (var t in candidates)
        {
            var field = t.GetField(propertyName + "Property", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field != null && field.GetValue(null) is BindableProperty bp)
                return bp;
        }

        return null;
    }

    static void SetIfBindableExists(this SfComboBox comboBox, string propertyName, object? value)
    {
        var bp = FindBindableProperty(propertyName);
        if (bp != null)
        {
            comboBox.Set(bp, value);
        }
    }

    public static SfComboBox ItemTemplate(this SfComboBox comboBox, DataTemplate? template)
    {
        comboBox.SetIfBindableExists("ItemTemplate", template);
        return comboBox;
    }

    public static SfComboBox SelectedValue(this SfComboBox comboBox, object? value)
    {
        comboBox.SetIfBindableExists("SelectedValue", value);
        return comboBox;
    }

    public static SfComboBox SelectedValuePath(this SfComboBox comboBox, string? path)
    {
        comboBox.SetIfBindableExists("SelectedValuePath", path);
        return comboBox;
    }

    public static SfComboBox AllowFiltering(this SfComboBox comboBox, bool allow)
    {
        comboBox.SetIfBindableExists("AllowFiltering", allow);
        return comboBox;
    }

    public static SfComboBox ShowDropDownOnFocus(this SfComboBox comboBox, bool show)
    {
        comboBox.SetIfBindableExists("ShowDropDownOnFocus", show);
        return comboBox;
    }

    public static SfComboBox DropDownHeight(this SfComboBox comboBox, double height)
    {
        comboBox.SetIfBindableExists("DropDownHeight", height);
        return comboBox;
    }

    public static SfComboBox MultiSelectionDisplayMode(this SfComboBox comboBox, ComboBoxMultiSelectionDisplayMode value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.MultiSelectionDisplayModeProperty, value);
        return comboBox;
    }

    public static SfComboBox ShowBorder(this SfComboBox comboBox, bool value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.ShowBorderProperty, value);
        return comboBox;
    }

    public static SfComboBox FontSize(this SfComboBox comboBox, bool value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.FontSizeProperty, value);
        return comboBox;
    }

    public static SfComboBox Background(this SfComboBox comboBox, Brush value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.BackgroundProperty, value);
        return comboBox;
    }

    public static SfComboBox ClearButtonIconColor(this SfComboBox comboBox, Color value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.ClearButtonIconColorProperty, value);
        return comboBox;
    }

    public static SfComboBox TokensWrapMode(this SfComboBox comboBox, ComboBoxTokensWrapMode value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.TokensWrapModeProperty, value);
        return comboBox;
    }

    public static SfComboBox EnableAutoSize(this SfComboBox comboBox, bool value)
    {
        comboBox.Set(Syncfusion.Maui.Inputs.SfComboBox.EnableAutoSizeProperty, value);
        return comboBox;
    }

    public static SfComboBox DropDownWidth(this SfComboBox comboBox, double width)
    {
        comboBox.SetIfBindableExists("DropDownWidth", width);
        return comboBox;
    }

    public static SfComboBox IsDropDownOpen(this SfComboBox comboBox, bool isOpen)
    {
        comboBox.SetIfBindableExists("IsDropDownOpen", isOpen);
        return comboBox;
    }

    public static SfComboBox MaxDropDownHeight(this SfComboBox comboBox, double maxHeight)
    {
        comboBox.SetIfBindableExists("MaxDropDownHeight", maxHeight);
        return comboBox;
    }

}




