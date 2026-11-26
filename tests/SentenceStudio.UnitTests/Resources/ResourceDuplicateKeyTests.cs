using System.Xml;
using System.Xml.Linq;
using FluentAssertions;

namespace SentenceStudio.UnitTests.Resources;

/// <summary>
/// A key is declared once in each <c>AppResources</c> file.
/// </summary>
/// <remarks>
/// The resource compiler keeps the first declaration of a name and drops every later one with
/// warning MSB3568, so a second declaration never reaches a screen: an edit to it changes
/// nothing, and a reader of the file cannot tell which wording ships. Nine keys were declared
/// twice in both files until issue 296, four of them with two different Korean values.
/// These read the .resx files on disk, not the compiled resources, because the compiled set has
/// already lost the duplicate.
/// </remarks>
public class ResourceDuplicateKeyTests
{
    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void No_key_is_declared_twice(string fileName)
    {
        var duplicates = XDocument.Load(PathTo(fileName), LoadOptions.SetLineInfo).Root!
            .Elements("data")
            .Where(data => data.Attribute("name") is not null)
            .GroupBy(data => data.Attribute("name")!.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} (lines "
                + string.Join(", ", group.Select(data => ((IXmlLineInfo)data).LineNumber)) + ")")
            .ToList();

        duplicates.Should().BeEmpty(
            $"the build keeps the first declaration of a key in {fileName} and ignores the rest, "
            + "so the later wording is never shown");
    }

    private static string PathTo(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Shared")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs from inside the repository");

        return Path.Combine(
            directory!.FullName, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);
    }
}
