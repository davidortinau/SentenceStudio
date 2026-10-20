using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Skills;

internal static class SkillsView
{
    internal static SwiftView Page(
        SkillsListState state, SourceUiStyle style, Func<string, string> text,
        Action add, Action<string> edit, Action retry)
    {
        var items = new List<SwiftView>();
        if (state.FeedbackKey is { } feedback)
            items.Add(style.Alert(Views.Text(text(feedback)), success: true)
                .WithAccessibilityIdentifier("native.skills.feedback"));

        switch (state.Phase)
        {
            case SkillsListPhase.Loading:
                items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier("native.skills.loading"));
                break;
            case SkillsListPhase.Blocked:
            case SkillsListPhase.LoadError:
                items.Add(style.Alert(Views.Text(SkillProfileFormView.Error(
                            state.ErrorKey, text)), success: false)
                    .WithAccessibilityIdentifier("native.skills.error"));
                if (state.Phase == SkillsListPhase.LoadError)
                    items.Add(style.SecondaryButton(text(NativeText.Retry), retry)
                        .WithAccessibilityIdentifier("native.skills.retry"));
                break;
            case SkillsListPhase.Empty:
                var empty = Views.VStack(
                        style.Text(Views.Text(text("Skills_NoProfilesYet")),
                            SourceTextRole.Body1, SourceTextTone.Secondary),
                        style.PrimaryButton(text("Skills_AddFirstProfile"), add)
                            .WithAccessibilityIdentifier("native.skills.empty.add"))
                    .WithPadding(48)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.skills.empty");
                empty.Spacing = SourceUiLayout.LargeCardInset;
                items.Add(empty);
                break;
            case SkillsListPhase.Populated:
                var cards = Views.LazyVGrid(
                        Columns(style.ViewportWidth),
                        state.Profiles.Select(profile =>
                            Card(profile, style, edit)).ToArray())
                    .WithAccessibilityIdentifier("native.skills.grid");
                cards.Spacing = SourceUiLayout.FormGap;
                items.Add(cards);
                break;
        }

        var content = Views.VStack(items.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.skills.scroll.content");
        content.Spacing = SourceUiLayout.FormGap;
        var addMenu = Views.Button(text("Skills_AddSkillProfile"), add)
            .WithAccessibilityIdentifier("native.skills.add");
        addMenu.IsEnabled = state.Phase is SkillsListPhase.Empty
            or SkillsListPhase.Populated;
        var more = Views.Menu(text("More"), addMenu)
            .WithAccessibilityIdentifier("native.skills.more");
        var actions = style.ViewportWidth < 768
            ? new[] { more }
            : new[]
            {
                style.PrimaryButton(text("Common_Add"), add)
                    .WithAccessibilityIdentifier("native.skills.add.primary"),
                more
            };
        if (actions.Length > 1)
            actions[0].IsEnabled = addMenu.IsEnabled;
        return Views.Toolbar(
                style.Page(Views.Scroll(content)
                    .WithAccessibilityIdentifier("native.skills.scroll"))
                    .WithAccessibilityIdentifier("native.skills.screen"),
                actions);
    }

    private static SwiftView Card(SkillProfile profile, SourceUiStyle style,
        Action<string> edit)
    {
        var content = new List<SwiftView>
        {
            style.Text(Views.Text(profile.Title ?? string.Empty),
                    SourceTextRole.Title3, sourceHeading: true)
                .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}.title")
        };
        if (!string.IsNullOrEmpty(profile.Description))
            content.Add(style.Text(Views.Text(profile.Description),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithLineLimit(1)
                .WithAccessibilityIdentifier(
                    $"native.skills.item.{profile.Id}.description"));
        var label = Views.VStack(content.ToArray())
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth()
            .WithMinimumHeight(120);
        label.Spacing = 4;
        style.Card(label);
        return Views.Button(label, () => edit(profile.Id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithMinimumHeight(120)
            .WithAccessibilityLabel(profile.Title ?? string.Empty)
            .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}");
    }

    internal static int Columns(double width) =>
        width < 768 ? 1 : width < 992 ? 2 : 3;
}
