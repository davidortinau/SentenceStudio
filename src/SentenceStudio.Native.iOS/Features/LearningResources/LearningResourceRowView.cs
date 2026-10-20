using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal static class LearningResourceRowView
{
    internal static SwiftView Row(
        NativeResourceItem resource,
        Func<string, string> text,
        Action<string> open,
        SourceUiStyle style,
        bool list)
    {
        var title = string.IsNullOrWhiteSpace(resource.Title)
            ? text("NativeResources_Untitled") : resource.Title;
        var media = string.Join(" · ", new[] { resource.MediaType, resource.Language }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var date = DateFormatter.ListDate(resource.CreatedAt,
            resource.IsSmartResource);
        var metadata = string.Join(" · ", new[] { media, date }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var icon = Views.Image(resource.MediaType switch
            {
                "Video" => "video",
                "Podcast" => "mic",
                "Image" => "photo",
                "Vocabulary List" => "rectangle.stack",
                "Article" => "doc.text",
                _ => "folder",
            }).WithForegroundColor(style.Appearance.Primary.Light,
                style.Appearance.Primary.Dark);
        var name = style.Text(Views.Text(title),
                list ? SourceTextRole.ListTitle : SourceTextRole.Title3,
                sourceHeading: !list)
            .WithLineLimit(1)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(
                $"{NativeAccess.ResourceRow}.{resource.Id}.title");
        var time = style.Text(Views.Text(date), SourceTextRole.Small,
                SourceTextTone.Secondary)
            .WithAccessibilityIdentifier(
                $"{NativeAccess.ResourceRow}.{resource.Id}.created");
        SwiftView label;
        if (list)
        {
            SwiftView[] contents = SourceUiLayout.ShowResourceListMetadata(style.ViewportWidth)
                ? [
                    icon,
                    name,
                    style.Text(Views.Text(media), SourceTextRole.Small,
                            SourceTextTone.Secondary)
                        .WithAccessibilityIdentifier(
                            $"{NativeAccess.ResourceRow}.{resource.Id}.metadata"),
                    time
                ]
                : [icon, name, time];
            label = Views.HStack(contents);
        }
        else
        {
            label = Views.HStack(icon,
                Views.VStack(
                    name,
                    style.Text(Views.Text(media), SourceTextRole.Small,
                            SourceTextTone.Secondary)
                        .WithAccessibilityIdentifier(
                            $"{NativeAccess.ResourceRow}.{resource.Id}.metadata"))
                    .WithFlexibleWidth(),
                time);
        }
        label.WithFlexibleWidth();
        if (list)
        {
            label.WithPadding(8, SourceUiLayout.CardInset,
                8, SourceUiLayout.CardInset);
        }
        else
        {
            label.WithPadding(SourceUiLayout.CardInset);
            style.Card(label);
        }
        return Views.Button(label, () => open(resource.Id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{NativeAccess.ResourceRow}.{resource.Id}")
            .WithAccessibilityLabel($"{title}, {metadata}")
            .WithMinimumHeight(list ? SourceUiLayout.MinimumTouchHeight : 76);
    }

}
