using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Choose My Own activity columns: the source col-6 col-md-4 col-lg-3 widths at the
/// default text size, and fewer columns as text grows (AF-D11 follow-up: iPad at the
/// largest accessibility size kept four columns and broke labels mid-word).
/// </summary>
public sealed class NativeAdaptiveLayoutTests
{
    [Theory]
    // Default text: the source breakpoints, never one column.
    [InlineData(320, 1.0, 2)]
    [InlineData(402, 1.0, 2)]
    [InlineData(767, 1.0, 2)]
    [InlineData(768, 1.0, 3)]
    [InlineData(991, 1.0, 3)]
    [InlineData(992, 1.0, 4)]
    [InlineData(1366, 1.0, 4)]
    // Smaller text never adds columns.
    [InlineData(700, 0.85, 2)]
    [InlineData(900, 0.85, 3)]
    // iPad portrait (1032 pt): xxLarge (21/17), xxxLarge (23/17), AX1 (28/17), AX5 (53/17).
    [InlineData(1032, 21.0 / 17, 3)]
    [InlineData(1032, 23.0 / 17, 2)]
    [InlineData(1032, 28.0 / 17, 2)]
    [InlineData(1032, 53.0 / 17, 2)]
    // iPad mini portrait (744 pt) at AX5.
    [InlineData(744, 53.0 / 17, 1)]
    // iPhone (402 pt): xxLarge keeps two columns, xxxLarge and larger use one.
    [InlineData(402, 21.0 / 17, 2)]
    [InlineData(402, 23.0 / 17, 1)]
    // The app's 150% text size on an iPad landscape window.
    [InlineData(1366, 1.5, 3)]
    // Android: a Pixel tablet at 200% system font scale.
    [InlineData(1280, 2.0, 2)]
    public void ColumnsFollowWidthAndTextScale(double width, double textScale, int columns) =>
        Assert.Equal(columns, NativeAdaptiveLayout.ActivityColumns(width, textScale));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    public void InvalidScalesCountAsTheDefaultTextSize(double textScale)
    {
        Assert.Equal(4, NativeAdaptiveLayout.ActivityColumns(1032, textScale));
        Assert.Equal(1032, NativeAdaptiveLayout.EffectiveWidth(1032, textScale));
    }
}
