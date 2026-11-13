using Microsoft.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Browse;

/// <summary>
/// Updates a mounted browse page (<see cref="BrowserPage"/>) from a fresh render without
/// remounting its search field, so typing keeps focus (and Korean composition) while the
/// results, counts and filter column change, and the wide filter column and counts row keep
/// their scroll positions.
/// </summary>
internal sealed class BrowserPageUpdate
{
    internal static readonly BrowserPageUpdate Vocabulary = new("native.vocabulary", headerKeep: 1);

    /// <summary>The Resources header is its count alone, shown only while there are resources (source).</summary>
    internal static readonly BrowserPageUpdate Resources = new("native.resources", headerKeep: 0);

    /// <summary>
    /// The parts rebuilt on each update, each with how many of its first children stay mounted:
    /// the wide page's search row keeps its field, the wide results keep
    /// their header and the header keeps <c>headerKeep</c> rows (Vocabulary's counts: their
    /// badges and texts take the new values in place, so a counts row scrolled sideways stays
    /// where it is).
    /// </summary>
    private readonly (string Id, int Keep)[] Regions;

    private BrowserPageUpdate(string prefix, int headerKeep)
    {
        SearchId = $"{prefix}.search";
        Regions =
        [
            ($"{prefix}.search.row", 1),
            ($"{prefix}.scroll.content", 0),
            ($"{prefix}.filters.panel.content", 0),
            ($"{prefix}.results.header", headerKeep),
            ($"{prefix}.results.content", 1),
        ];
    }

    /// <summary>The page's search field.</summary>
    internal string SearchId { get; }

    /// <summary>
    /// Moves <paramref name="next"/>'s regions into <paramref name="current"/>, gives the mounted
    /// texts and toolbar buttons the new text, symbol, label and state. False (nothing changed)
    /// when the two have different outlines: the caller mounts <paramref name="next"/> instead.
    /// The caller still owns <paramref name="next"/> and disposes it.
    /// </summary>
    internal bool TryUpdateInPlace(SwiftView current, SwiftView next)
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
        // Outside the regions, texts (the counts) take the new text, fixed widths (the filter
        // column, after the page reports its own width) the new width, and controls (the search
        // field, the page while a starter resource is made) the new availability, in place.
        foreach (var (id, node) in mounted.Nodes)
        {
            if (!fresh.Nodes.TryGetValue(id, out var nextNode))
                continue;
            if (node.Kind == "text" && node.Text != nextNode.Text)
                node.Text = nextNode.Text;
            if (node.IsEnabled != nextNode.IsEnabled)
                node.IsEnabled = nextNode.IsEnabled;
            if (nextNode.FixedWidth is { } width && node.FixedWidth != width)
                node.WithFixedWidth(width);
            // The system search field keeps its node (and focus); its filter tokens follow.
            if (node.Kind == "searchable")
                node.SearchTokens = nextNode.SearchTokens;
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
    internal (string Shape, Dictionary<string, SwiftView> Nodes) Outline(SwiftView root)
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

    /// <summary>
    /// The filter menu keeps its node: its symbol, availability and label follow, and each
    /// submenu's choice and option titles and each item's title take the new values (the
    /// outline already matched, so the menus hold the same items).
    /// </summary>
    private static void CopyMenuState(SwiftView menu, SwiftView next)
    {
        if (menu.IsEnabled != next.IsEnabled)
            menu.IsEnabled = next.IsEnabled;
        if (menu.AccessibilityLabel != next.AccessibilityLabel)
            menu.WithAccessibilityLabel(next.AccessibilityLabel);
        CopyItems(menu, next);

        static void CopyItems(SwiftView node, SwiftView fresh)
        {
            for (var index = 0; index < node.Children.Count && index < fresh.Children.Count; index++)
            {
                var child = node.Children[index];
                var nextChild = fresh.Children[index];
                if (child.Kind != nextChild.Kind)
                    continue;
                if (child.Kind == "image")
                {
                    if (child.SystemImageName != nextChild.SystemImageName)
                        child.SystemImageName = nextChild.SystemImageName;
                    continue;
                }
                if (child.Text != nextChild.Text)
                    child.Text = nextChild.Text;
                if (child.IsEnabled != nextChild.IsEnabled)
                    child.IsEnabled = nextChild.IsEnabled;
                if (child.Kind == "button" && child.AccessibilitySelected != nextChild.AccessibilitySelected)
                    child.WithAccessibilitySelected(nextChild.AccessibilitySelected);
                if (child.Kind == "picker" && child.SelectedIndex != nextChild.SelectedIndex)
                    child.SelectedIndex = nextChild.SelectedIndex;
                CopyItems(child, nextChild);
            }
        }
    }

    /// <summary>Copies a rebuilt toolbar button's look and state onto the mounted one.</summary>
    private static void CopyButtonState(SwiftView button, SwiftView next)
    {
        if (button.Kind == "menu" && next.Kind == "menu")
        {
            CopyMenuState(button, next);
            return;
        }
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
