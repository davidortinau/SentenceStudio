using System.Text.RegularExpressions;

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

    /// <summary>
    /// The only fixed-size views in the head: the file, the member that builds the view, and why.
    /// All four are items of a YouTube row beside a title that truncates (issue 277). A row
    /// offers each item an equal share of its width first, and an item that can wrap takes no
    /// more, so these broke inside a word; fixed size is what keeps them on one line until the
    /// bridge has a layout priority modifier (issue 293), which replaces all four.
    /// </summary>
    private static readonly (string File, string Member, string Why)[] FixedSizeViews =
    [
        ("Features/Import/YouTubeImportView.cs", "ImportRow",
            "An import's date: at the largest app text size it broke after the month."),
        ("Features/Import/YouTubeImportView.cs", "OutlineWarningButton",
            "Retry, in an import's row and a video's: it keeps its width beside the truncated title."),
        ("Features/Import/YouTubeImportView.cs", "StatusBadge",
            "An import's status badge: a wide status with its spinner broke at every letter."),
        ("Features/Import/YouTubeChannelView.cs", "Badge",
            "A video's badge: it keeps its width beside the truncated title."),
    ];

    [Fact]
    public void NoViewInTheHeadIsFixedSizeButTheFourYouTubeRowItems()
    {
        // Rows keep their children's widths since bridge 0.1.0-preview.67 and a menu its label's
        // since 0.1.0-preview.68 (issues 260 and 276), and a fixed-size view cannot wrap at large
        // text. So a use is an exception with a reason, on the list above, and a use that is not
        // on it fails here: give the view its width another way, or add it with why.
        const string call = ".WithFixedSize(";
        var separator = Path.DirectorySeparatorChar;
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(HeadPath, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(HeadPath, file);
            if (relative.StartsWith($"obj{separator}", StringComparison.Ordinal) ||
                relative.StartsWith($"bin{separator}", StringComparison.Ordinal))
                continue;
            var source = File.ReadAllText(file);
            for (var at = source.IndexOf(call, StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf(call, at + call.Length, StringComparison.Ordinal))
                found.Add($"{relative.Replace(separator, '/')}: {EnclosingMember(source, at)}");
        }

        var listed = FixedSizeViews.Select(view => $"{view.File}: {view.Member}").ToList();
        // One use for each line of the list: a second use in a listed member is unlisted too.
        var unlisted = found.ToList();
        foreach (var use in listed)
            unlisted.Remove(use);
        var gone = listed.ToList();
        foreach (var use in found)
            gone.Remove(use);

        Assert.True(unlisted.Count == 0,
            $"WithFixedSize has a use that FixedSizeViews does not list: {string.Join("; ", unlisted)}.");
        Assert.True(gone.Count == 0,
            $"No longer fixed size, so take it off FixedSizeViews: {string.Join("; ", gone)}.");
    }

    /// <summary>The name of the type member whose body holds the position.</summary>
    private static string EnclosingMember(string source, int position)
    {
        var members = Regex.Matches(source[..position],
            @"^ {4}(?:private|internal|public|protected)\b[^\n;=]*?\b(\w+)\(", RegexOptions.Multiline);
        return members.Count == 0 ? "(no member)" : members[^1].Groups[1].Value;
    }
}
