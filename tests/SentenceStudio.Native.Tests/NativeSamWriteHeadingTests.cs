using System.Xml.Linq;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Native;
using Xunit;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Every write card the server can send has its own heading on the native heads, in English and
/// Korean. The web and MAUI surface has the same check in SamWriteResourceTests.
/// </summary>
public class NativeSamWriteHeadingTests
{
    [Fact]
    public void Every_change_kind_has_its_own_heading_in_english_and_korean()
    {
        var english = Load("AppResources.resx");
        var korean = Load("AppResources.ko.resx");
        var headings = Enum.GetValues<CoachWriteChangeKind>()
            .ToDictionary(kind => kind, NativeSamWritePresentation.HeadingKey);

        Assert.Equal("Coach_WriteKindUnknown", headings[CoachWriteChangeKind.Unknown]);

        var known = headings.Where(pair => pair.Key != CoachWriteChangeKind.Unknown)
            .Select(pair => pair.Value)
            .ToList();
        Assert.Equal(known.Count, known.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("Coach_WriteKindUnknown", known);

        foreach (var key in headings.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(english.GetValueOrDefault(key)), $"English is missing {key}");
            Assert.False(string.IsNullOrWhiteSpace(korean.GetValueOrDefault(key)), $"Korean is missing {key}");
        }
    }

    [Theory]
    [InlineData(CoachWriteChangeKind.ExampleSentenceAdd, "Coach_WriteKindExampleSentenceAdd")]
    [InlineData(CoachWriteChangeKind.ExampleSentenceEdit, "Coach_WriteKindExampleSentenceEdit")]
    [InlineData(CoachWriteChangeKind.ExampleSentenceRemove, "Coach_WriteKindExampleSentenceRemove")]
    public void Example_sentence_cards_are_named(CoachWriteChangeKind kind, string key) =>
        Assert.Equal(key, NativeSamWritePresentation.HeadingKey(kind));

    private static Dictionary<string, string> Load(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName)))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);

        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .GroupBy(d => d.Attribute("name")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
    }
}
