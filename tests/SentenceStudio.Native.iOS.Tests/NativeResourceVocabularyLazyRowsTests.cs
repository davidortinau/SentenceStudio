using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// AF-R11: Resource Edit vocabulary rows are a SwiftUI lazy container inside the
/// form's ScrollView (AGENTS.md native virtualization), while keeping the word
/// row, count heading, No Vocab Yet and read-only smart-resource behavior, and the
/// coordinator's in-place quick-add append into the same container.
/// </summary>
public sealed class NativeResourceVocabularyLazyRowsTests
{
    [Fact]
    public void EditVocabularyRowsAreAOneColumnLazyGridThatTheCoordinatorAppendsTo()
    {
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var create = Between(vocabulary, "internal static SwiftView CreateEditVocabularyRows()",
            "internal static void AppendEditVocabularyRow(");
        Assert.Contains("Views.LazyVGrid(1)", create);
        Assert.Contains("rows.Spacing = 0;", create);
        Assert.DoesNotContain("Views.VStack()", create);
        Assert.Contains("rows.Kind != \"grid\"", vocabulary);

        // Source row contract is unchanged.
        Assert.Contains("text(\"ResourceEdit_VocabularySection\"), state.Vocabulary.Count", vocabulary);
        Assert.Contains("text(\"ResourceEdit_NoVocabYet\")", vocabulary);
        Assert.Contains("if (!isSmartResource && !busy)\n            cells.Add(RemoveWordButton(", vocabulary);
        Assert.Contains("LearningResourceVocabularyView.CreateEditVocabularyRows()", coordinator);
        Assert.Contains("LearningResourceVocabularyView.AppendEditVocabularyRow(rows,", coordinator);
    }

    [Fact]
    public void TheFormScrollViewHostsTheLazyRows()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        Assert.Contains("style.Page(Views.Scroll(content)", form);
        Assert.Contains("LearningResourceVocabularyView.AddVocabulary(vocabulary, state,", form);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} not found.");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} not found.");
        return source[from..to];
    }

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string Source(string relative) => File.ReadAllText(
        Path.Combine(HeadPath, relative));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
