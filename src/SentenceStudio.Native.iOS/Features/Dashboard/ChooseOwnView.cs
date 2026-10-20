using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class ChooseOwnView
{
    private static string ChooseOwnSymbol(string route) => route switch
    {
        "conversation" => "bubble.left.and.bubble.right",
        "scene" => "photo",
        "translation" => "character.book.closed",
        "cloze" => "puzzlepiece",
        "writing" => "square.and.pencil",
        "diary" => "book.pages",
        "reading" => "book",
        "vocab-quiz" => "checklist",
        "vocab-matching" => "square.grid.3x3",
        "shadowing" => "waveform",
        "how-do-you-say" => "bubble.left",
        "minimal-pairs" => "ear",
        "word-association" => "lightbulb",
        "numberdrill" => "number",
        _ => "questionmark"
    };

    internal static void Add(
        List<SwiftView> items, NativeDashboardState state, SourceUiStyle style,
        Func<string, string> text, Action openResourceSelector,
        Action openSkillSelector, Action retryChooseOwn,
        Action<bool> expandActivities,
        Action<string>? launchChooseOwnActivity, bool isTablet)
    {
        if (state.ChooseOwn is not { } choice)
        {
            items.Add(style.Text(Views.Text(text("Common_Loading")),
                    SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.dashboard.chooseown.status"));
            return;
        }
        if (choice.ErrorKey is { } selectionError)
        {
            items.Add(style.Text(Views.Text(text(selectionError)),
                    SourceTextRole.Body2, SourceTextTone.Danger)
                .WithAccessibilityIdentifier("native.dashboard.chooseown.error"));
            items.Add(Views.Button(text(NativeText.Retry), retryChooseOwn)
                .WithAccessibilityIdentifier("native.dashboard.chooseown.retry"));
            return;
        }

        var resources = choice.Resources.IsDefault
            ? ImmutableArray<NativeChooseOwnOption>.Empty : choice.Resources;
        var skills = choice.Skills.IsDefault
            ? ImmutableArray<NativeChooseOwnOption>.Empty : choice.Skills;
        var selected = choice.SelectedResourceIds.IsDefault
            ? ImmutableArray<string>.Empty : choice.SelectedResourceIds;
        var selectedTitles = resources
            .Where(option => selected.Contains(option.Id, StringComparer.Ordinal))
            .Select(option => option.Title).ToArray();
        var skillTitle = skills.FirstOrDefault(option =>
            option.Id == choice.SelectedSkillId)?.Title;

        var selectors = Views.VStack(
            style.SecondaryButton(
                    $"{text("Dashboard_LearningResourcesLabel")} ({selected.Length})",
                    openResourceSelector, fullWidth: true)
                .WithAccessibilityIdentifier("native.dashboard.chooseown.resources"),
            style.Text(Views.Text(string.Join(", ", selectedTitles)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.dashboard.chooseown.resources.selected"),
            style.SecondaryButton(
                    text("Dashboard_SkillProfileLabel"), openSkillSelector,
                    fullWidth: true)
                .WithAccessibilityIdentifier("native.dashboard.chooseown.skill"),
            style.Text(Views.Text(skillTitle ?? string.Empty),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.dashboard.chooseown.skill.selected"))
            .WithPadding(SourceUiLayout.CardInset)
            .WithAccessibilityIdentifier("native.dashboard.chooseown.selectors");
        selectors.Spacing = SourceUiLayout.ItemGap;
        items.Add(style.Card(selectors));

        items.Add(style.Text(Views.Text(text("Dashboard_Activities")),
                SourceTextRole.Title2, sourceHeading: true)
            .WithAccessibilityIdentifier("native.dashboard.chooseown.activities.title"));
        if (!isTablet && NativeDashboardPresenter.ChooseOwnActivities.Length > 6)
            items.Add(style.SecondaryButton(
                    text(choice.ShowAllActivities
                        ? "Dashboard_ShowLess" : "Dashboard_SeeAll"),
                    () => expandActivities(!choice.ShowAllActivities))
                .WithAccessibilityIdentifier("native.dashboard.chooseown.activities.toggle"));

        var visible = isTablet || choice.ShowAllActivities
            ? NativeDashboardPresenter.ChooseOwnActivities
            : NativeDashboardPresenter.ChooseOwnActivities.Take(6);
        var activities = Views.LazyVGrid(
            SourceUiLayout.ActivityColumns(style.ViewportWidth),
            visible.Select(activity =>
            {
                var content = style.Card(Views.VStack(
                    Views.Image(ChooseOwnSymbol(activity.Route))
                        .WithForegroundColor(style.Appearance.Primary.Light,
                            style.Appearance.Primary.Dark),
                    style.Text(Views.Text(text(activity.LabelKey)),
                            SourceTextRole.Body1Strong)
                        .WithAccessibilityIdentifier(
                            "native.dashboard.chooseown.activity.title"))
                    .WithPadding(SourceUiLayout.CardInset));
                var canLaunch = activity.Route == "vocab-quiz"
                    && launchChooseOwnActivity is not null;
                var button = Views.Button(content,
                        () =>
                        {
                            if (canLaunch)
                                launchChooseOwnActivity!(activity.Route);
                        })
                    .WithButtonStyle(SwiftButtonStyle.Plain)
                    .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                    .WithAccessibilityLabel(text(activity.LabelKey))
                    .WithAccessibilityIdentifier(
                        $"native.dashboard.chooseown.activity.{activity.Route}");
                button.IsEnabled = canLaunch;
                return button;
            })
                .ToArray())
            .WithAccessibilityIdentifier("native.dashboard.chooseown.grid");
        activities.Spacing = SourceUiLayout.ItemGap;
        items.Add(activities);
    }

}
