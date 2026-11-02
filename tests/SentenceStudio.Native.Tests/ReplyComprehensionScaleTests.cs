using System.ComponentModel;
using System.Reflection;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// B16: the Reply schema, the prompts and every display use one 0-100
/// comprehension scale; older fractional (0.0-1.0) values still display as
/// percentages instead of "0%".
/// </summary>
public sealed class ReplyComprehensionScaleTests
{
    [Fact]
    public void ReplySchema_DescribesComprehensionOnTheZeroToHundredScale()
    {
        var comprehension = Describe(nameof(Reply.Comprehension));
        Assert.Contains("0 to 100", comprehension);
        Assert.DoesNotContain("0.0", comprehension);
        Assert.Contains("never in the message", comprehension);

        var message = Describe(nameof(Reply.Message));
        Assert.Contains("never include a comprehension score", message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(0.85, 85)]
    [InlineData(0.004, 0)]
    [InlineData(1.0, 100)]
    [InlineData(1.5, 2)]
    [InlineData(85, 85)]
    [InlineData(84.6, 85)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public void NormalizeComprehension_MapsAnyModelScoreToAWholePercentage(double score, double expected) =>
        Assert.Equal(expected, Reply.NormalizeComprehension(score));

    private static string Describe(string property) =>
        typeof(Reply).GetProperty(property)!.GetCustomAttribute<DescriptionAttribute>()!.Description;
}
