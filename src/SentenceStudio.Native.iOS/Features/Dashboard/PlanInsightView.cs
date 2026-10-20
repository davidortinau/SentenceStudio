using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class PlanInsightView
{
    internal static void Add(
        List<SwiftView> items, NativeDashboardState state,
        bool insightExpanded, Action toggleInsight, SourceUiStyle style,
        Func<string, string> text)
    {
        if (state.Narrative is { } narrative)
        {
            var insightHeader = Views.HStack(
                Views.Image("lightbulb")
                    .WithForegroundColor(style.Appearance.Primary.Light,
                        style.Appearance.Primary.Dark),
                style.Text(Views.Text(text("Dashboard_TodaysInsight")),
                    SourceTextRole.Body2)
                    .WithFlexibleWidth(),
                Views.Image(insightExpanded ? "chevron.up" : "chevron.down")
                    .WithAccessibilityLabel(text(insightExpanded
                        ? "Dashboard_InsightExpanded"
                        : "Dashboard_InsightCollapsed"))
                    .WithAccessibilityIdentifier("native.plan.insight.chevron"))
                .WithFlexibleWidth()
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
            insightHeader.Spacing = SourceUiLayout.ItemGap;
            var insight = new List<SwiftView>
            {
                Views.Button(insightHeader, toggleInsight)
                    .WithButtonStyle(SwiftButtonStyle.Plain)
                    .WithFlexibleWidth()
                    .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                    .WithAccessibilityIdentifier("native.plan.insight.toggle"),
            };
            if (insightExpanded)
            {
                if (!string.IsNullOrWhiteSpace(narrative.Story))
                    insight.Add(style.Text(Views.Text(narrative.Story),
                            SourceTextRole.Body2)
                        .WithAccessibilityIdentifier("native.plan.insight.story"));
                foreach (var resource in narrative.Resources)
                {
                    if (string.IsNullOrWhiteSpace(resource.Title))
                        continue;
                    var title = Views.HStack(
                            Views.Image(ResourceSymbol(resource.MediaType))
                                .WithForegroundColor(
                                    style.Appearance.Primary.Light,
                                    style.Appearance.Primary.Dark),
                            style.Text(Views.Text(resource.Title),
                                SourceTextRole.Body2Strong,
                                SourceTextTone.Primary))
                        .WithAccessibilityIdentifier(
                            "native.plan.insight.resource.title");
                    title.Spacing = SourceUiLayout.ItemGap;
                    insight.Add(title);
                    if (!string.IsNullOrWhiteSpace(resource.SelectionReason))
                        insight.Add(style.Text(Views.Text(resource.SelectionReason),
                                SourceTextRole.Small, SourceTextTone.Secondary)
                            .WithAccessibilityIdentifier(
                                "native.plan.insight.resource.reason"));
                }
                if (narrative.VocabInsight is { } vocab)
                {
                    insight.Add(style.Text(Views.Text(string.Join(" · ",
                    [
                        $"{vocab.NewCount} {text("Dashboard_InsightNew")}",
                        $"{vocab.ReviewCount} {text("Dashboard_InsightReview")}",
                        $"{vocab.TotalDue} {text("Dashboard_InsightTotalDue")}",
                    ])), SourceTextRole.SmallStrong)
                        .WithAccessibilityIdentifier("native.plan.insight.vocab"));
                    if (vocab.AverageMastery > 0)
                        insight.Add(style.Text(Views.Text(string.Format(
                            CultureInfo.CurrentCulture,
                            text("Dashboard_InsightMastery"),
                            (vocab.AverageMastery * 100).ToString("F0",
                                CultureInfo.CurrentCulture))),
                            SourceTextRole.Small, SourceTextTone.Secondary));
                    if (!state.StrugglingCategories.IsEmpty)
                        insight.Add(CategoryRow(
                            state.StrugglingCategories,
                            text("Dashboard_StrugglingWith"),
                            "native.plan.insight.struggling",
                            style, CategoryKind.Struggling));
                    if (!state.NewCategories.IsEmpty)
                        insight.Add(CategoryRow(
                            state.NewCategories,
                            text("Dashboard_NewToYou"),
                            "native.plan.insight.new",
                            style, CategoryKind.New));
                    if (!state.DoingWellCategories.IsEmpty)
                        insight.Add(CategoryRow(
                            state.DoingWellCategories, null,
                            "native.plan.insight.doingwell",
                            style, CategoryKind.DoingWell));
                    if (!string.IsNullOrWhiteSpace(vocab.PatternInsight))
                    {
                        var pattern = Views.HStack(
                            Views.Image("lightbulb"),
                            style.Text(Views.Text(vocab.PatternInsight),
                                    SourceTextRole.Small)
                                .WithForegroundColor(
                                    style.Appearance.InfoAlertText.Light,
                                    style.Appearance.InfoAlertText.Dark));
                        pattern.Spacing = 4;
                        insight.Add(style.InfoAlert(pattern, compact: true)
                            .WithAccessibilityIdentifier(
                                "native.plan.insight.pattern"));
                    }
                    if (vocab.SampleStrugglingWords.Count > 0)
                    {
                        var hasStruggling = !state.StrugglingCategories.IsEmpty;
                        var sampleWords = Views.HStack(
                            Views.Image(hasStruggling
                                    ? "arrow.turn.down.right" : "sparkles")
                                .WithForegroundColor(
                                    style.Appearance.SecondaryText.Light,
                                    style.Appearance.SecondaryText.Dark),
                            style.Text(Views.Text(string.Format(
                                CultureInfo.CurrentCulture,
                                text(hasStruggling
                                    ? "Dashboard_WordsToReview"
                                    : "Dashboard_WordsToDiscover"),
                                string.Join(", ", vocab.SampleStrugglingWords))),
                                SourceTextRole.Small, SourceTextTone.Secondary))
                            .WithAccessibilityIdentifier(
                                "native.plan.insight.sample-words");
                        sampleWords.Spacing = SourceUiLayout.ItemGap;
                        insight.Add(sampleWords);
                    }
                }
                if (narrative.FocusAreas.Count > 0)
                {
                    insight.Add(style.Text(Views.Text(text("Dashboard_FocusAreas")),
                            SourceTextRole.Body2Strong)
                        .WithAccessibilityIdentifier("native.plan.insight.focus.title"));
                    foreach (var area in narrative.FocusAreas)
                        insight.Add(style.Text(Views.Text($"\u2022 {area}"),
                            SourceTextRole.Small));
                }
            }
            var insightPanel = Views.VStack(insight.ToArray())
                .WithPadding(SourceUiLayout.CardInset);
            insightPanel.Spacing = SourceUiLayout.PageGap;
            items.Add(style.Card(insightPanel));
        }
        else if (!string.IsNullOrWhiteSpace(state.Rationale))
        {
            var rationale = new List<SwiftView>();
            var names = state.Cards.Select(card => card.ResourceTitle)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length > 0)
                rationale.Add(style.Text(Views.Text(string.Join(", ", names)),
                    SourceTextRole.Small, SourceTextTone.Secondary));
            rationale.Add(style.Text(Views.Text(state.Rationale),
                SourceTextRole.Small, SourceTextTone.Secondary));
            var fallback = Views.VStack(rationale.ToArray())
                .WithPadding(SourceUiLayout.CardInset)
                .WithAccessibilityIdentifier("native.plan.rationale");
            fallback.Spacing = SourceUiLayout.ItemGap;
            items.Add(style.Card(fallback));
        }

    }

    private enum CategoryKind { Struggling, New, DoingWell }

    private static SwiftView CategoryRow(
        ImmutableArray<TagInsightDto> categories,
        string? heading, string identifier, SourceUiStyle style,
        CategoryKind kind)
    {
        var fill = kind switch
        {
            CategoryKind.Struggling => style.Appearance.Warning,
            CategoryKind.New => style.Appearance.BootstrapSecondary,
            _ => style.Appearance.Success
        };
        var foreground = kind switch
        {
            CategoryKind.Struggling => new SourceColorPair("#212529", "#212529"),
            CategoryKind.New => style.Appearance.OnBootstrapSecondary,
            _ => new SourceColorPair("#FFFFFF", "#FFFFFF")
        };
        var pills = categories.Select(category =>
        {
            var title = kind == CategoryKind.New
                ? category.Tag
                : $"{category.Tag} ({category.AverageAccuracy:P0})";
            return style.Text(Views.Text(title), SourceTextRole.BadgeSmall)
                .WithForegroundColor(foreground.Light, foreground.Dark)
                .WithPadding(5, 9, 5, 9)
                .WithRoundedSurface(fill.Light, fill.Dark,
                    style.Appearance.BootstrapRadius);
        }).ToArray();
        var grid = Views.LazyVGrid(style.ViewportWidth < 768 ? 2 : 3, pills);
        grid.Spacing = SourceUiLayout.ItemGap;
        var group = heading is null
            ? Views.VStack(grid)
            : Views.VStack(
                Views.HStack(
                    Views.Image(kind == CategoryKind.Struggling
                            ? "exclamationmark.triangle" : "sparkles")
                        .WithForegroundColor(style.Appearance.SecondaryText.Light,
                            style.Appearance.SecondaryText.Dark),
                    style.Text(Views.Text(heading),
                        SourceTextRole.Small, SourceTextTone.Secondary)),
                grid);
        group.Spacing = SourceUiLayout.ItemGap;
        return group.WithAccessibilityIdentifier(identifier);
    }

    private static string ResourceSymbol(string mediaType) => mediaType switch
    {
        "Video" => "video",
        "Podcast" => "mic",
        "Text" => "text.book.closed",
        "Vocabulary List" => "list.bullet",
        _ => "book"
    };

}
