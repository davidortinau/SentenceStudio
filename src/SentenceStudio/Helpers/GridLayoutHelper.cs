namespace SentenceStudio.Helpers;

/// <summary>
/// Helper class for calculating responsive grid layouts in CollectionViews
/// </summary>
public static class GridLayoutHelper
{
    /// <summary>
    /// Calculates an optimal responsive GridItemsLayout based on screen width and desired item width
    /// </summary>
    /// <param name="desiredItemWidth">The minimum desired width for each item in device-independent pixels</param>
    /// <param name="orientation">The layout orientation (Vertical or Horizontal)</param>
    /// <param name="minColumns">Minimum number of columns (default: 1)</param>
    /// <param name="maxColumns">Maximum number of columns (default: 3)</param>
    /// <param name="customSpacing">Custom spacing between items. If null, uses MyTheme.LayoutSpacing</param>
    /// <param name="customPadding">Custom container padding. If null, uses MyTheme.LayoutPadding.Left</param>
    /// <returns>A configured GridItemsLayout with optimal span</returns>
    public static GridItemsLayout CalculateResponsiveLayout(
        double desiredItemWidth,
        ItemsLayoutOrientation orientation = ItemsLayoutOrientation.Vertical,
        int minColumns = 1,
        int maxColumns = 3,
        double? customSpacing = null,
        double? customPadding = null)
    {
        var span = CalculateOptimalSpan(desiredItemWidth, minColumns, maxColumns, customSpacing, customPadding);
        var spacing = customSpacing ?? 16;

        return new GridItemsLayout(span, orientation)
        {
            VerticalItemSpacing = spacing,
            HorizontalItemSpacing = spacing
        };
    }

    /// <summary>
    /// Calculates the optimal number of columns/rows (span) for a grid layout
    /// </summary>
    /// <param name="desiredItemWidth">The minimum desired width for each item in device-independent pixels</param>
    /// <param name="minColumns">Minimum number of columns (default: 1)</param>
    /// <param name="maxColumns">Maximum number of columns (default: 3)</param>
    /// <param name="customSpacing">Custom spacing between items. If null, uses 16 (Bootstrap default)</param>
    /// <param name="customPadding">Custom container padding. If null, uses 16</param>
    /// <returns>The optimal span (number of columns/rows)</returns>
    public static int CalculateOptimalSpan(
        double desiredItemWidth,
        int minColumns = 1,
        int maxColumns = 3,
        double? customSpacing = null,
        double? customPadding = null)
    {
        // Get screen width in device-independent pixels
        var screenWidth = DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density;

        // Use provided values or defaults
        var spacing = customSpacing ?? 16;
        var containerPadding = customPadding ?? 16;

        // Calculate available width after accounting for container padding on both sides
        var availableWidth = screenWidth - (containerPadding * 2);

        // Calculate how many items can fit including spacing
        var itemWidthWithSpacing = desiredItemWidth + spacing;
        var calculatedSpan = Math.Max(minColumns, (int)(availableWidth / itemWidthWithSpacing));

        // Clamp between min and max columns
        return Math.Max(minColumns, Math.Min(maxColumns, calculatedSpan));
    }
}
