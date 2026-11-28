namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Issue #287: a saved example sentence's row in the word editor is its text (flexible) and its
/// actions menu. A menu's custom label fills the width it is offered by default, so until
/// bridge 0.1.0-preview.68 the menu was fixed at 44 points. It asks the bridge for a label at
/// its own width now, as the conversation's voice pace menu does (issue #276), and the label's
/// padding makes the target.
/// </summary>
public sealed class NativeExampleActionsMenuContractTests
{
    private static string HeadPath => Path.Combine(RepoRoot(), "src", "SentenceStudio.Native.iOS");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void TheActionsMenuKeepsItsLabelsOwnWidthWithoutAFixedWidth()
    {
        var examples = File.ReadAllText(Path.Combine(HeadPath, "Features", "Vocabulary",
            "VocabularyWordEditView.Examples.cs")).Replace("\r\n", "\n");
        var start = examples.IndexOf("private static SwiftView Sentence(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The saved sentence row was not found.");
        var row = examples[start..];
        // To the end of the method that builds it.
        row = SourceSlice.Until(row, "\n    }\n");
        Assert.Contains("Views.Menu(", row);
        Assert.Contains("$\"{id}.actions\"", row);
        Assert.Contains(".WithMenuLabelFittingContent()", row);
        Assert.DoesNotContain("WithFixedWidth", row);
        Assert.DoesNotContain("WithFixedSize", row);
        // The row the menu sits in: the text takes the free width, the menu its own.
        Assert.Contains("var textColumn = Views.VStack(lines.ToArray()).WithFlexibleWidth();", row);
        Assert.Contains("var row = Views.HStack(textColumn, menu).WithFlexibleWidth()", row);
    }
}
