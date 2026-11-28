using Xunit.Sdk;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class SourceSliceTests
{
    private const string Sample = "class A\n{\n    void First() { one(); }\n    void Second() { two(); }\n}\n";

    [Fact]
    public void Between_GivesWhatTheIndexSliceGave()
    {
        var expected = Sample[Sample.IndexOf("void First(", StringComparison.Ordinal)
            ..Sample.IndexOf("void Second(", StringComparison.Ordinal)];

        Assert.Equal(expected, SourceSlice.Between(Sample, "void First(", "void Second("));
        Assert.Equal("void First() { one(); }\n    ", expected);
    }

    [Fact]
    public void From_And_Until_GiveWhatTheOpenEndedSlicesGave()
    {
        Assert.Equal(Sample[Sample.IndexOf("void Second(", StringComparison.Ordinal)..],
            SourceSlice.From(Sample, "void Second("));
        Assert.Equal(Sample[..Sample.IndexOf("void Second(", StringComparison.Ordinal)],
            SourceSlice.Until(Sample, "void Second("));
        // A marker of one character, where the slices used IndexOf(char).
        Assert.Equal(Sample[..Sample.IndexOf(';')], SourceSlice.Until(Sample, ";"));
    }

    [Fact]
    public void Between_AllowsAnEmptySliceWhenBothMarkersStartAtTheSamePlace()
    {
        Assert.Equal(string.Empty, SourceSlice.Between(Sample, "void First(", "void First() {"));
    }

    [Fact]
    public void AMissingStartMarker_FailsByNamingTheMarkerAndTheSource()
    {
        var browser = Sample;

        var failure = Assert.ThrowsAny<XunitException>(
            () => SourceSlice.Between(browser, "Task OpenWordAsync(", "void Second("));

        Assert.Contains("start marker \"Task OpenWordAsync(\"", failure.Message);
        Assert.Contains("was not found in 'browser'", failure.Message);
    }

    [Fact]
    public void AMissingEndMarker_FailsByNamingTheMarkerAndTheSource()
    {
        var browser = Sample;

        var failure = Assert.ThrowsAny<XunitException>(
            () => SourceSlice.Between(browser, "void First(", "Task<bool> OpenWordAsync("));

        Assert.Contains("end marker \"Task<bool> OpenWordAsync(\"", failure.Message);
        Assert.Contains("was not found in 'browser'", failure.Message);
        Assert.IsNotType<ArgumentOutOfRangeException>(failure);
    }

    [Fact]
    public void AnEndMarkerBeforeTheStartMarker_FailsByNamingBoth()
    {
        var failure = Assert.ThrowsAny<XunitException>(
            () => SourceSlice.Between(Sample, "void Second(", "void First("));

        Assert.Contains("end marker \"void First(\" comes before the start marker \"void Second(\"",
            failure.Message);
    }

    [Fact]
    public void From_And_Until_FailByNamingTheMissingMarker()
    {
        var from = Assert.ThrowsAny<XunitException>(() => SourceSlice.From(Sample, "void Third("));
        var until = Assert.ThrowsAny<XunitException>(() => SourceSlice.Until(Sample, "void Third("));

        Assert.Contains("start marker \"void Third(\" was not found in 'Sample'", from.Message);
        Assert.Contains("end marker \"void Third(\" was not found in 'Sample'", until.Message);
    }

    [Fact]
    public void AMarkerWithALineBreak_IsShownOnOneLine()
    {
        var failure = Assert.ThrowsAny<XunitException>(
            () => SourceSlice.From(Sample, "if (missing)\n    {"));

        Assert.Contains("\"if (missing)\\n    {\"", failure.Message);
    }
}
