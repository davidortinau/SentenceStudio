using Microsoft.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

/// <summary>
/// Updates a mounted Vocabulary page from a fresh render without remounting its search field,
/// so typing keeps focus (and Korean composition) while the results, counts and filter column
/// change, and the wide filter column and counts row keep their scroll positions.
/// </summary>
internal static class VocabularyListUpdate
{
    internal const string SearchId = "native.vocabulary.search";

    /// <summary>
    /// The parts rebuilt on each update, each with how many of its first children stay mounted:
    /// the search row keeps its field, the phone page keeps its search row, the wide results keep
    /// their header and the header keeps its counts row (whose badges take the new counts in
    /// place, so a counts row scrolled sideways stays where it is).
    /// </summary>
    private static readonly (string Id, int Keep)[] Regions =
    [
        ("native.vocabulary.search.row", 1),
        ("native.vocabulary.scroll.content", 1),
        ("native.vocabulary.filters.panel.content", 0),
        ("native.vocabulary.results.header", 1),
        ("native.vocabulary.results.content", 1),
    ];

    /// <summary>
    /// Moves <paramref name="next"/>'s regions into <paramref name="current"/>, gives the mounted
    /// texts and toolbar buttons the new text, symbol, label and state. False (nothing changed)
    /// when the two have different outlines: the caller mounts <paramref name="next"/> instead.
    /// The caller still owns <paramref name="next"/> and disposes it.
    /// </summary>
    internal static bool TryUpdateInPlace(SwiftView current, SwiftView next)
    {
        if (current.Kind != "toolbar")
            return false;
        var mounted = Outline(current);
        var fresh = Outline(next);
        if (mounted.Shape != fresh.Shape)
            return false;
        foreach (var (id, keep) in Regions)
        {
            if (!mounted.Nodes.TryGetValue(id, out var region)
                || !fresh.Nodes.TryGetValue(id, out var nextRegion))
                continue;
            while (region.Children.Count > keep)
            {
                var old = region.Children[keep];
                region.RemoveAt(keep);
                old.Dispose();
            }
            while (nextRegion.Children.Count > keep)
            {
                var child = nextRegion.Children[keep];
                nextRegion.RemoveAt(keep);
                region.Add(child);
            }
        }
        // Outside the regions, texts (the counts) take the new text and fixed widths (the filter
        // column, after the page reports its own width) the new width, in place.
        foreach (var (id, node) in mounted.Nodes)
        {
            if (!fresh.Nodes.TryGetValue(id, out var nextNode))
                continue;
            if (node.Kind == "text" && node.Text != nextNode.Text)
                node.Text = nextNode.Text;
            if (nextNode.FixedWidth is { } width && node.FixedWidth != width)
                node.WithFixedWidth(width);
        }
        // A toolbar's buttons cannot be replaced: each keeps its node and takes the new symbol,
        // label and state (its action reads the state when tapped).
        for (var index = 1; index < current.Children.Count; index++)
            CopyButtonState(current.Children[index], next.Children[index]);
        return true;
    }

    /// <summary>
    /// The page's outline (every node's kind and ID, without what the rebuilt regions hold) and
    /// its outlined nodes by ID, in one walk that never enters a region's rebuilt children (the
    /// word cards). Two renders with the same outline differ only inside the regions, in the
    /// outlined texts and in the toolbar buttons.
    /// </summary>
    internal static (string Shape, Dictionary<string, SwiftView> Nodes) Outline(SwiftView root)
    {
        var shape = new System.Text.StringBuilder();
        var nodes = new Dictionary<string, SwiftView>(StringComparer.Ordinal);
        Append(root);
        return (shape.ToString(), nodes);

        void Append(SwiftView node)
        {
            var id = node.AccessibilityIdentifier;
            shape.Append(node.Kind).Append('#').Append(id).Append('[');
            if (!string.IsNullOrEmpty(id))
                nodes.TryAdd(id, node);
            var count = node.Children.Count;
            foreach (var (region, keep) in Regions)
            {
                if (region == id)
                {
                    count = Math.Min(keep, count);
                    break;
                }
            }
            for (var index = 0; index < count; index++)
                Append(node.Children[index]);
            shape.Append(']');
        }
    }

    /// <summary>Copies a rebuilt toolbar button's look and state onto the mounted one.</summary>
    private static void CopyButtonState(SwiftView button, SwiftView next)
    {
        if (button.Kind != "button" || next.Kind != "button")
            return;
        if (button.IsEnabled != next.IsEnabled)
            button.IsEnabled = next.IsEnabled;
        if (button.Text != next.Text)
            button.Text = next.Text;
        if (button.AccessibilityLabel != next.AccessibilityLabel)
            button.WithAccessibilityLabel(next.AccessibilityLabel);
        if (button.AccessibilitySelected != next.AccessibilitySelected)
            button.WithAccessibilitySelected(next.AccessibilitySelected);
        for (var index = 0; index < button.Children.Count
            && index < next.Children.Count; index++)
        {
            var child = button.Children[index];
            var nextChild = next.Children[index];
            if (child.IsEnabled != nextChild.IsEnabled)
                child.IsEnabled = nextChild.IsEnabled;
            if (child.Kind == "image" && nextChild.Kind == "image")
            {
                if (child.SystemImageName != nextChild.SystemImageName)
                    child.SystemImageName = nextChild.SystemImageName;
            }
            else if (child.Kind == nextChild.Kind && child.Text != nextChild.Text)
                child.Text = nextChild.Text;
        }
    }
}
