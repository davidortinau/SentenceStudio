using System.Reflection;
using System.Text.RegularExpressions;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Compose DropdownMenu and ModalBottomSheet render in their own window whose
/// semantics root does not inherit the shell's testTagsAsResourceId, so their
/// item tags (for example native.matching.hide-native) were unaddressable.
/// Every popup root must republish it.
/// </summary>
public sealed class AndroidPopupSemanticsTests
{
    [Fact]
    public void EveryDropdownMenuPublishesTestTagsInItsPopupWindow()
    {
        var visual = Source("AndroidSliceShell.Visual.cs");
        Assert.Contains("private static DropdownMenu PopupMenu(", visual);
        Assert.Matches(new Regex(
            @"PopupWindowSemantics\(Modifier modifier\) =>\s*modifier\.Semantics\(semantics => semantics\.TestTagsAsResourceId\(true\)\)"),
            visual);
        Assert.Matches(new Regex(
            @"PopupMenu\(bool expanded, Action onDismissRequest\) =>\s*new\(expanded, onDismissRequest\)\s*\{\s*Modifier = Modifier\.Semantics\(semantics => semantics\.TestTagsAsResourceId\(true\)\)"),
            visual);

        var popups = 0;
        foreach (var (name, text) in Sources())
        {
            foreach (Match match in Regex.Matches(text, @"new DropdownMenu\("))
            {
                var tail = text.Substring(match.Index, Math.Min(400, text.Length - match.Index));
                Assert.True(tail.Contains("PopupWindowSemantics(", StringComparison.Ordinal)
                        || tail.Contains("TestTagsAsResourceId(true)", StringComparison.Ordinal),
                    $"{name}: DropdownMenu without popup-window test-tag semantics");
                popups++;
            }
            popups += Regex.Matches(text, @"= PopupMenu\(").Count;
        }
        Assert.True(popups >= 11, $"expected the shell's menus to be counted, found {popups}");
    }

    [Fact]
    public void EveryModalBottomSheetPublishesTestTagsInItsWindow()
    {
        foreach (var (name, text) in Sources())
        {
            foreach (Match match in Regex.Matches(text, @"new ModalBottomSheet\("))
            {
                var tail = text.Substring(match.Index, Math.Min(260, text.Length - match.Index));
                var guarded = tail.Contains("PopupWindowSemantics(", StringComparison.Ordinal)
                    // Sam republishes it on the sheet's content panel.
                    || name == "AndroidSliceShell.Sam.Compose.cs"
                        && text.Contains("height.TestTag(\"native.sam.panel\")\n                .Semantics(semantics => semantics.TestTagsAsResourceId(true))",
                            StringComparison.Ordinal);
                Assert.True(guarded, $"{name}: ModalBottomSheet without popup-window test-tag semantics");
            }
        }
    }

    private static IEnumerable<(string Name, string Text)> Sources() =>
        Directory.EnumerateFiles(Root(), "AndroidSliceShell*.cs")
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));

    private static string Source(string name) => File.ReadAllText(Path.Combine(Root(), name));

    private static string Root() =>
        typeof(AndroidPopupSemanticsTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
}
