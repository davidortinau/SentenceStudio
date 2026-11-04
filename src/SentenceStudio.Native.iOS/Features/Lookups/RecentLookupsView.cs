using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Lookups;

internal sealed record RecentLookupsActions(
    Action Reload, Action<string, string> Add, Action DismissNotice);

/// <summary>
/// Recent lookups (decision 4 B), pushed from the Vocabulary list: the learner's lookups newest
/// first in cards like the Vocabulary cards, each with an "Add to vocabulary" menu of the
/// learner's resources in that language. State and rules live in the shared
/// <see cref="RecentLookupsCoordinator"/>.
/// </summary>
internal static class RecentLookupsView
{
    internal static SwiftView Page(
        RecentLookupsState state, SourceUiStyle style, Func<string, string> text,
        RecentLookupsActions actions)
    {
        var content = new List<SwiftView>();
        if (state.Notice is { } notice)
            content.Add(notice.IsError
                ? style.MessageBanner(notice, text, RecentLookupsAccess.Notice)
                : style.MessageToast(notice, text, actions.DismissNotice)
                    .WithAccessibilityIdentifier(RecentLookupsAccess.Notice));

        switch (state.Phase)
        {
            case RecentLookupsPhase.Blocked:
                content.Add(style.Alert(style.Text(Views.Text(text("Vocabulary_NoActiveProfile")),
                        SourceTextRole.Body2), success: false)
                    .WithAccessibilityIdentifier(RecentLookupsAccess.Status));
                break;
            case RecentLookupsPhase.LoadError:
                content.Add(style.Alert(style.Text(Views.Text(text("Lookups_LoadFailed")),
                        SourceTextRole.Body2), success: false)
                    .WithAccessibilityIdentifier(RecentLookupsAccess.Status));
                content.Add(style.SecondaryButton(text("Common_Retry"), actions.Reload)
                    .WithAccessibilityIdentifier(RecentLookupsAccess.Retry));
                break;
            case RecentLookupsPhase.Empty:
                content.Add(style.Text(Views.Text(text("Lookups_Empty")),
                        SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithPadding(48)
                    .WithAccessibilityIdentifier(RecentLookupsAccess.Status));
                break;
            case RecentLookupsPhase.Ready:
                var list = Views.LazyVGrid(1, state.Rows
                        .Select(row => Row(row, state.AddingId, style, text, actions.Add))
                        .ToArray())
                    .WithAccessibilityIdentifier(RecentLookupsAccess.List);
                list.Spacing = SourceUiLayout.ItemGap;
                content.Add(list);
                break;
            default:
                content.Add(Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier(RecentLookupsAccess.Status));
                break;
        }

        var inner = Views.VStack(content.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.lookups.scroll.content");
        inner.Spacing = SourceUiLayout.PageGap;
        return style.Page(Views.Scroll(inner)
                .WithAccessibilityIdentifier("native.lookups.scroll"))
            .WithAccessibilityIdentifier(RecentLookupsAccess.Page);
    }

    private static SwiftView Row(
        RecentLookupRow row, string? addingId, SourceUiStyle style,
        Func<string, string> text, Action<string, string> add)
    {
        var source = RecentLookupsCoordinator.SourceLine(row, text,
            CultureInfo.CurrentUICulture, TimeZoneInfo.Local);
        var title = style.Text(Views.Text(row.Text), SourceTextRole.Title3, sourceHeading: true)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{RecentLookupsAccess.Row(row)}.text");
        var translation = style.Text(Views.Text(row.Translation),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{RecentLookupsAccess.Row(row)}.translation");
        var details = style.Text(Views.Text(source), SourceTextRole.Small, SourceTextTone.Secondary)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{RecentLookupsAccess.Row(row)}.source");
        var summary = Views.VStack(title, translation, details)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{RecentLookupsAccess.Row(row)}.summary");
        summary.Spacing = 4;
        var card = style.Card(Views.VStack(summary, AddMenu(row, addingId, style, text, add))
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth());
        card.Spacing = SourceUiLayout.ItemGap;
        return card.WithAccessibilityIdentifier(RecentLookupsAccess.Row(row));
    }

    private static SwiftView AddMenu(
        RecentLookupRow row, string? addingId, SourceUiStyle style,
        Func<string, string> text, Action<string, string> add)
    {
        var label = string.Format(CultureInfo.CurrentCulture,
            text("Lookups_AddToVocabularyFor"), row.Text);
        if (row.Destinations.Count == 0)
            return style.Text(Views.Text(text("Lookups_NoResource")),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(RecentLookupsAccess.Add(row));
        var adding = addingId == row.Id;
        var face = Views.HStack(
                Views.Image(adding ? "hourglass" : "plus.circle"),
                Views.Text(text("Lookups_AddToVocabulary")))
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark);
        face.Spacing = 6;
        var menu = Views.Menu(face, DestinationButtons(row, add))
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(RecentLookupsAccess.Add(row));
        menu.IsEnabled = addingId is null;
        return menu;
    }

    /// <summary>
    /// The menu's actions: one Button per destination resource and nothing else. The facade's
    /// Menu refuses any action that is not a Button (a heading Text threw and left the page on
    /// Loading, seen on the simulator 2026-11-04).
    /// </summary>
    internal static SwiftView[] DestinationButtons(RecentLookupRow row, Action<string, string> add) =>
        row.Destinations.Select(destination =>
                Views.Button(destination.Title, () => add(row.Id, destination.ResourceId))
                    .WithAccessibilityIdentifier(RecentLookupsAccess.Destination(row, destination)))
            .ToArray();
}
