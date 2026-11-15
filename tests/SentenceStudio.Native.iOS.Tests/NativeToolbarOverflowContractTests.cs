namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Issue #218 (iPhone Duo, folded, landscape): when a toolbar runs out of room, iOS moves its
/// last items into a "More" (...) menu that lists each item by its title. An icon-only item
/// (an Image with only an accessibility label) has no title, so the menu had nothing to show
/// and the More button did nothing. Every toolbar icon is a titled Label, as departure 83 made
/// the browse pages' icons.
/// </summary>
public sealed class NativeToolbarOverflowContractTests
{
    private static string Source(string relative) => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "SentenceStudio.Native.iOS", relative)).Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void SamPanelHeaderActionsAreTitled()
    {
        var sam = Source("Features/Sam/SamView.cs");
        var start = sam.IndexOf("internal static SwiftView[] HeaderActions(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = sam.IndexOf("return items.ToArray();", start, StringComparison.Ordinal);
        var header = sam[start..end];
        Assert.DoesNotContain("Views.Image(", header);
        Assert.Contains("Views.Label(text(\"Coach_ConversationsToggle\"), \"list.bullet\")", header);
        Assert.Contains("Views.Label(text(\"Coach_NewConversation\"), \"square.and.pencil\")", header);
        Assert.Contains("Views.Label(text(compact ? \"Coach_PanelExpand\" : \"Coach_PanelCompact\")", header);
        Assert.Contains("Views.Label(text(\"Coach_PanelClose\"), \"xmark\")", header);
    }

    [Fact]
    public void ActivityToolbarIconsAreTitled()
    {
        var matching = Source("Features/Activities/VocabularyMatchingScreen.cs");
        Assert.Contains("Views.Menu(Views.Label(text(\"More\"), \"ellipsis.circle\"), toggle)", matching);
        Assert.DoesNotContain("Views.Menu(Views.Image(\"ellipsis.circle\")", matching);

        var scene = Source("Features/Activities/SceneScreen.cs");
        Assert.Contains("Views.Button(Views.Label(text(NativeSceneText.ImageGallery), \"photo.on.rectangle.angled\")",
            scene);
        Assert.DoesNotContain("Views.Button(Views.Image(\"photo.on.rectangle.angled\")", scene);

        var conversation = Source("Features/Activities/ConversationScreen.cs");
        Assert.Contains("Views.Menu(Views.Label(text(NativeConversationText.Scenario), \"theatermasks\"), actions)",
            conversation);
        Assert.Contains(": Views.Label(text(NativeConversationText.ChangeLanguage), \"translate\");", conversation);
        Assert.DoesNotContain("Views.Menu(Views.Image(\"theatermasks\")", conversation);
    }
}
