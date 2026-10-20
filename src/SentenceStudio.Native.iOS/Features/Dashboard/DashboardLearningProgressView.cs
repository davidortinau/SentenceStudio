using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class DashboardLearningProgressView
{
    private static string NumberContextSymbol(string context) => context switch
    {
        "Time" => "clock",
        "Counting" => "cup.and.saucer",
        "Age" => "birthday.cake",
        _ => "number"
    };

    internal static void AddSections(
        List<SwiftView> items, NativeDashboardState state, SourceUiStyle style,
        Func<string, string> text)
    {
        items.Add(style.Text(Views.Text(text("Dashboard_VocabularyHeader")),
                SourceTextRole.Title2, sourceHeading: true)
            .WithAccessibilityIdentifier("native.dashboard.vocab.title"));
        if (state.Phase == NativePhase.Loading)
        {
            items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.dashboard.vocab.spinner"));
            items.Add(style.Text(Views.Text(text("Common_Loading")),
                    SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.dashboard.vocab.loading"));
        }
        else if (state.VocabSummary is { } summary)
            AddVocabularySummary(items, summary, style, text);
        else if (state.Phase is (NativePhase.Error or NativePhase.Blocked))
            items.Add(style.BorderedCard(
                    style.Text(Views.Text(text(state.ErrorKey
                            ?? NativeText.PlanError)),
                            SourceTextRole.Body1, SourceTextTone.Danger)
                        .WithPadding(SourceUiLayout.CardInset))
                .WithAccessibilityIdentifier("native.dashboard.vocab.error"));
        else
            items.Add(style.Card(
                    style.Text(Views.Text(text("Dashboard_NoVocabData")),
                            SourceTextRole.Body1, SourceTextTone.Secondary)
                        .WithPadding(SourceUiLayout.LargeCardInset))
                .WithAccessibilityIdentifier("native.dashboard.vocab.empty"));

        items.Add(Views.HStack(
                Views.Image("number"),
                style.Text(Views.Text(text("NativeDashboard_NumbersMastery")),
                    SourceTextRole.Title2, sourceHeading: true))
            .WithAccessibilityIdentifier("native.dashboard.numbers.title"));
        if (state.Phase == NativePhase.Loading)
        {
            items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.dashboard.numbers.spinner"));
            items.Add(style.Text(Views.Text(text("Common_Loading")),
                SourceTextRole.Body1, SourceTextTone.Secondary));
            return;
        }
        if (state.NumberErrorKey is { } errorKey)
        {
            items.Add(style.BorderedCard(
                    style.Text(Views.Text(text(errorKey)),
                            SourceTextRole.Body1, SourceTextTone.Danger)
                        .WithPadding(SourceUiLayout.CardInset))
                .WithAccessibilityIdentifier("native.dashboard.numbers.error"));
            return;
        }
        var contexts = state.NumberContexts.IsDefault
            ? ImmutableArray<NativeNumberContextStat>.Empty
            : state.NumberContexts;
        if (contexts.IsEmpty)
        {
            items.Add(style.Card(Views.VStack(
                    style.Text(Views.Text(text("NativeDashboard_NumbersEmptyTitle")),
                        SourceTextRole.Title3, sourceHeading: true),
                    style.Text(Views.Text(
                            text("NativeDashboard_NumbersEmptyDescription")),
                        SourceTextRole.Body2, SourceTextTone.Secondary))
                    .WithPadding(SourceUiLayout.LargeCardInset))
                .WithAccessibilityIdentifier("native.dashboard.numbers.empty"));
            return;
        }
        var cards = contexts.Select(context =>
        {
            var title = Views.HStack(
                Views.Image(NumberContextSymbol(context.ContextCode)),
                style.Text(Views.Text(context.ContextCode),
                    SourceTextRole.Body2Strong).WithFlexibleWidth());
            if (context.DueCount > 0)
                title.Add(style.Text(Views.Text(string.Format(
                            CultureInfo.CurrentCulture,
                            text("NativeDashboard_NumbersDue"),
                            context.DueCount)), SourceTextRole.BadgeSmall)
                    .WithForegroundColor("#212529", "#212529")
                    .WithPadding(5, 9, 5, 9)
                    .WithRoundedSurface(style.Appearance.Warning.Light,
                        style.Appearance.Warning.Dark,
                        style.Appearance.BootstrapRadius));
            var body = Views.VStack(
                title,
                style.Progress(Views.Progress(
                    Math.Clamp(context.MasteryPercentage, 0, 100), 100, 8,
                    context.ContextCode), completion: true),
                style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text("NativeDashboard_NumbersCorrect"),
                        context.CorrectCount, context.TotalCount)),
                    SourceTextRole.Small, SourceTextTone.Secondary))
                .WithPadding(SourceUiLayout.CardInset);
            body.Spacing = SourceUiLayout.ItemGap;
            return style.Card(body);
        }).ToArray();
        var grid = Views.LazyVGrid(
            style.ViewportWidth < 768 ? 2 : 3, cards)
            .WithAccessibilityIdentifier("native.dashboard.numbers.grid");
        grid.Spacing = SourceUiLayout.ItemGap;
        items.Add(grid);
    }

    private static void AddVocabularySummary(
        List<SwiftView> items, VocabProgressSummary summary, SourceUiStyle style,
        Func<string, string> text)
    {
        var tiles = Views.LazyVGrid(style.ViewportWidth < 768 ? 2 : 5,
            VocabularyTile(summary.New, text("Dashboard_VocabNew"), "new", style,
                SourceTextTone.Primary),
            VocabularyTile(summary.Learning, text("Dashboard_VocabLearning"),
                "learning", style),
            VocabularyTile(summary.Familiar, text("Dashboard_VocabFamiliar"),
                "familiar", style, SourceTextTone.Primary),
            VocabularyTile(summary.Review, text("Dashboard_VocabReview"),
                "review", style),
            VocabularyTile(summary.Known, text("Dashboard_VocabKnown"),
                "known", style, SourceTextTone.Success));
        tiles.Spacing = SourceUiLayout.ItemGap;
        items.Add(tiles);

        var total = summary.New + summary.Learning + summary.Familiar
            + summary.Review + summary.Known;
        if (total > 0)
            items.Add(style.Card(Views.HStack(
                style.Text(Views.Text($"{text("Dashboard_TotalWordsLabel")} " +
                        total.ToString(CultureInfo.CurrentCulture)),
                        SourceTextRole.Body1)
                    .WithAccessibilityIdentifier("native.dashboard.vocab.total"),
                style.Text(Views.Text($"{text("Dashboard_WeeklyAccuracyLabel")} " +
                        $"{Math.Round(summary.SuccessRate7d * 100).ToString(CultureInfo.CurrentCulture)}%"),
                        SourceTextRole.Body2, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier("native.dashboard.vocab.accuracy"))
                .WithAdaptiveStack(340)
                .WithPadding(SourceUiLayout.CardInset))
                .WithAccessibilityIdentifier("native.dashboard.vocab.summary"));
    }

    private static SwiftView VocabularyTile(
        int count, string label, string name, SourceUiStyle style,
        SourceTextTone tone = SourceTextTone.Body)
    {
        var tile = style.Card(Views.VStack(
            style.Text(Views.Text(count.ToString(CultureInfo.CurrentCulture)),
                    SourceTextRole.StatNumber, tone)
                .WithAccessibilityIdentifier($"native.dashboard.vocab.stat.{name}.value"),
            style.Text(Views.Text(label), SourceTextRole.Small,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"native.dashboard.vocab.stat.{name}.label"))
            .WithPadding(SourceUiLayout.CardInset))
            .WithAccessibilityIdentifier($"native.dashboard.vocab.stat.{name}");
        tile.Spacing = SourceUiLayout.ItemGap;
        return tile;
    }
}
