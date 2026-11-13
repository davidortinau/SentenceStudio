using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// AF-R11: Resource Edit vocabulary rows stay lazy (AGENTS.md native virtualization). Captain
/// 2026-11-13: native Resource form. Each word is now a row of the form's vocabulary Section,
/// and a SwiftUI Form is a lazy List, so the one-column LazyVGrid inside a ScrollView is gone.
/// The word row, count heading, No Vocab Yet and read-only smart-resource behavior stay, and the
/// coordinator still appends a quick-added word to the mounted Section in place.
/// </summary>
public sealed class NativeResourceVocabularyLazyRowsTests
{
    [Fact]
    public void EditVocabularyRowsAreSectionRowsThatTheCoordinatorAppendsTo()
    {
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var add = Between(vocabulary, "internal static void AddVocabulary(",
            "internal static string VocabularyHeader(");
        Assert.Contains("rows.AddRange(state.Vocabulary.Select(word =>", add);
        Assert.Contains("Views.Section(VocabularyHeader(state.Vocabulary.Count, text)", add);
        Assert.Contains("inputs.RegisterVocabularySection(section);", add);
        Assert.DoesNotContain("LazyVGrid", vocabulary);

        // Source row contract is unchanged: the count heading, No Vocab Yet, and no removal on a
        // smart resource or while busy.
        Assert.Contains("text(\"ResourceEdit_VocabularySection\"), count)", vocabulary);
        Assert.Contains("text(\"ResourceEdit_NoVocabYet\")", vocabulary);
        var row = Between(vocabulary, "internal static SwiftView EditVocabularyRow(",
            "internal static SwiftView StagedRow(");
        Assert.Contains("if (isSmartResource || busy)", row);
        Assert.Contains("Views.SwipeRow(content, [RemoveAction(", row);
        Assert.Contains("section.Add(LearningResourceVocabularyView.EditVocabularyRow(word,",
            coordinator);
    }

    [Fact]
    public void TheFormHostsTheLazyRows()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        Assert.Contains("style.GroupedForm(Views.Form(sections.ToArray())", form);
        Assert.Contains("LearningResourceVocabularyView.AddVocabulary(sections, state,", form);
        Assert.DoesNotContain("Views.Scroll(", form);
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
