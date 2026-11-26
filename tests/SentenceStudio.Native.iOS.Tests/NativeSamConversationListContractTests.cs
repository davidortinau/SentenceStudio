namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Issue #286: a row of Sam's conversation list is the conversation's button (flexible) beside
/// its actions menu, whose label is an icon. A menu's custom label fills the width it is offered
/// by default, so the menu shared the row half and half with the title. It asks the bridge for a
/// label at its own width (0.1.0-preview.68, measured in the bridge's MenuLabelWidthTests), as
/// the voice pace menu does (issue #276).
/// </summary>
public sealed class NativeSamConversationListContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void AConversationsActionsMenuStaysIconSizedBesideItsTitle()
    {
        var sam = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SentenceStudio.Native.iOS",
            "Features", "Sam", "SamView.cs")).Replace("\r\n", "\n");
        var start = sam.IndexOf("var menu = Views.Menu(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The conversation's actions menu was not found.");
        // The one statement that builds the menu.
        var menu = sam[start..];
        menu = menu[..(menu.IndexOf(";\n", StringComparison.Ordinal) + 1)];
        Assert.Contains("native.sam.conversation.{index}.actions", menu);
        Assert.Contains(".WithMenuLabelFittingContent();", menu);
        // Neither workaround: fixed size cannot shrink, a fixed width does not follow the icon.
        Assert.DoesNotContain("WithFixedSize", menu);
        Assert.DoesNotContain("WithFixedWidth", menu);
        // The row the menu sits in: the title takes the free width, the menu its own.
        Assert.Contains("var line = Views.HStack(button.WithFlexibleWidth(), menu);", sam);
    }
}
