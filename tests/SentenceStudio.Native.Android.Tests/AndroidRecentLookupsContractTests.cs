using System.Reflection;
using System.Text.RegularExpressions;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Recent lookups on Android (Captain 2026-11-04, decision 4 B): a Compose page opened from the
/// Vocabulary header and drawn over the list (Back returns), on the shared coordinator iOS uses,
/// with a test tag on every control. Behavior is covered by NativeLookupHistoryTests.
/// </summary>
public sealed class AndroidRecentLookupsContractTests
{
    [Fact]
    public void VocabularyHeaderOpensRecentLookups_AndBackClosesThemFirst()
    {
        var vocabulary = Source("AndroidSliceShell.Vocabulary.Compose.cs");
        Assert.Contains("RecentLookupsHeaderAction(palette,", vocabulary);
        Assert.Contains("if (IsRecentLookupsShown)\n            return RecentLookupsPage(palette);", vocabulary);

        var more = Source("AndroidSliceShell.More.cs");
        var vocabularyBack = more[more.IndexOf("case MoreScreen.Vocabulary when", StringComparison.Ordinal)..];
        Assert.True(vocabularyBack.IndexOf("HandleRecentLookupsBack()", StringComparison.Ordinal)
            < vocabularyBack.IndexOf("vocabulary.IsFormOpen", StringComparison.Ordinal));
        Assert.Contains("CloseRecentLookups();", more);
    }

    [Fact]
    public void PageIsComposeOnTheSharedCoordinator_WithATagOnEveryControl()
    {
        var page = Source("AndroidSliceShell.Lookups.Compose.cs");
        Assert.DoesNotContain("Android.Widget", page);
        Assert.DoesNotContain("AppCompat", page);
        Assert.Contains("new RecentLookupsCoordinator(", page);
        Assert.DoesNotMatch(new Regex(@"class\s+RecentLookupsCoordinator\b"), page);
        Assert.Contains("RecentLookupsCoordinator.SourceLine(", page);
        foreach (Match control in Regex.Matches(page,
                     @"new (TextButton|DropdownMenuItem)\([\s\S]*?\}\)?;"))
            Assert.Contains(".TestTag(", control.Value);
        Assert.Contains("RecentLookupsAccess.Destination(row, target)", page);
        Assert.Contains("PopupMenu(expanded: _moreMenu == key", page);
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidRecentLookupsContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
