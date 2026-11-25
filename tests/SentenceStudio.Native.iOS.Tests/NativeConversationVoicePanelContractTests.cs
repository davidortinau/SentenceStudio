namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Issue #276: the conversation's voice panel is the status (flexible), the voice pace menu and
/// End. A menu's custom label fills the width it is offered by default, which would take half
/// the status's room, so until bridge 0.1.0-preview.68 the menu was fixed-size. It asks the
/// bridge for a label at its own width now (measured in the bridge's MenuLabelWidthTests).
/// </summary>
public sealed class NativeConversationVoicePanelContractTests
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
    public void TheVoicePaceMenuStaysIconSizedWithoutFixedSize()
    {
        var conversation = File.ReadAllText(
            Path.Combine(HeadPath, "Features", "Activities", "ConversationScreen.cs")).Replace("\r\n", "\n");
        var start = conversation.IndexOf("return Views.Menu(label,", StringComparison.Ordinal);
        Assert.True(start >= 0, "The voice pace menu was not found.");
        var menu = conversation[start..];
        // To the end of the method that builds it.
        menu = menu[..menu.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("NativeConversationAccess.VoicePace", menu);
        Assert.Contains(".WithMenuLabelFittingContent();", menu);
        Assert.DoesNotContain("WithFixedSize", menu);
        // The row the menu sits in: the status takes the free width, the menu and End their own.
        Assert.Contains("var row = Views.HStack(middle, _voicePace, end).WithFlexibleWidth();", conversation);
    }

    [Fact]
    public void NoViewInTheHeadIsFixedSize()
    {
        // The pace menu was the last use (issues 260 and 276): rows keep their children's widths
        // since bridge 0.1.0-preview.67, and a fixed-size view cannot wrap at large text.
        var separator = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(HeadPath, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(HeadPath, file);
            if (relative.StartsWith($"obj{separator}", StringComparison.Ordinal) ||
                relative.StartsWith($"bin{separator}", StringComparison.Ordinal))
                continue;
            Assert.False(File.ReadAllText(file).Contains(".WithFixedSize(", StringComparison.Ordinal),
                $"{relative} uses WithFixedSize.");
        }
    }
}
