using AndroidX.Compose;
using AndroidX.Compose.UI.Graphics.Vector;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Today's Insight tiers and media icons (source Index.razor:186-283).
/// Struggling, new and doing-well tags show as severity-styled badges under a
/// visible tier heading, so a tier is never conveyed by color alone.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    /// <summary>
    /// Source GetMediaTypeIcon (Index.razor:1385-1392): five cases. The Compose
    /// facade carries only the core Material icons, so these are the closest
    /// ones, matching the Android Resources list.
    /// </summary>
    private static ImageVector ResourceMediaIcon(string? mediaType) => mediaType switch
    {
        "Video" => Icons.Default.PlayArrow,
        "Podcast" => Icons.Default.Call,
        "Text" => Icons.Default.Edit,
        "Vocabulary List" => Icons.Default.Menu,
        _ => Icons.Default.Info,
    };

    private static ComposableNode CategoryTier(
        string heading, ImageVector icon, IEnumerable<string> categories,
        AndroidX.Compose.Color accent, AndroidVisualPalette palette, string tier)
    {
        var badges = new FlowRow { Modifier.FillMaxWidth() };
        var index = 0;
        foreach (var category in categories)
            badges.Add(new Box
            {
                Modifier.Padding(end: 6, bottom: 6),
                TierBadge(category, accent, palette,
                    $"native.plan.insight.category.{tier}.{index++}"),
            });
        var caption = Body(heading, palette, secondary: true);
        caption.FontSize = new Sp(AndroidVisualTheme.CaptionSize + 1);
        return new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().TestTag($"native.plan.insight.category.{tier}"),
            new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Icon(icon, null)
                {
                    Modifier = Modifier.Size(16),
                    Tint = palette.SecondaryText,
                },
                caption,
            },
            badges,
        };
    }

    private static ComposableNode TierBadge(
        string label, AndroidX.Compose.Color accent, AndroidVisualPalette palette,
        string? tag = null)
    {
        var modifier = Modifier.Border(1, accent.WithOpacity(0.45f), 6);
        if (tag is not null)
            modifier = modifier.TestTag(tag);
        var badge = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(6),
            Color = accent.WithOpacity(0.14f),
        };
        badge.Add(new Text(label)
        {
            Modifier = Modifier.Padding(horizontal: 8, vertical: 3),
            Color = accent,
            FontSize = new Sp(AndroidVisualTheme.CaptionSize),
            FontWeight = FontWeight.SemiBold,
        });
        return badge;
    }
}
