using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Dashboard vocabulary and number statistics (source Index.razor:474-583).
/// Each section has its own loading, content, empty and error state, and shows
/// as soon as the statistics load, also while the plan is still loading.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private void AddLearningProgress(
        Column viewport, NativeDashboardState state, AndroidVisualPalette palette)
    {
        viewport.Add(Heading(Localize("Dashboard_VocabularyHeader"), palette, 22,
            Modifier.FillMaxWidth().Padding(vertical: 12)
                .TestTag("native.dashboard.vocab.title")
                .Semantics(semantics => semantics.Heading())));
        if (state.StatisticsLoading)
            viewport.Add(StatisticsLoading("native.dashboard.vocab.loading", palette));
        else if (state.VocabSummary is { } vocabulary)
            AddVocabularySummary(viewport, vocabulary, palette);
        // A load that failed or was blocked before the statistics were read says so
        // instead of claiming there is no vocabulary (shared with iOS).
        else if (NativeDashboardPresenter.VocabularySectionErrorKey(state) is { } vocabError)
            viewport.Add(StatusAlert(DashboardErrorText(vocabError), palette, error: true,
                automationId: "native.dashboard.vocab.error"));
        else
            viewport.Add(Card(palette, "native.dashboard.vocab.empty",
                Body(Localize("Dashboard_NoVocabData"), palette, secondary: true)));

        // Number mastery is Korean-only (as on iOS); other target languages omit it.
        if (!state.NumbersApplicable && state.NumberErrorKey is null)
            return;
        viewport.Add(Heading(Localize("NativeDashboard_NumbersMastery"), palette, 22,
            Modifier.FillMaxWidth().Padding(vertical: 12)
                .TestTag("native.dashboard.numbers.title")
                .Semantics(semantics => semantics.Heading())));
        if (state.StatisticsLoading)
        {
            viewport.Add(StatisticsLoading("native.dashboard.numbers.loading", palette));
            return;
        }
        if (state.NumberErrorKey is { } numberError)
        {
            viewport.Add(StatusAlert(Localize(numberError), palette,
                error: true, automationId: "native.dashboard.numbers.error"));
            return;
        }
        var numbers = state.NumberContexts.IsDefault ? [] : state.NumberContexts;
        if (numbers.IsEmpty)
        {
            viewport.Add(Card(palette, "native.dashboard.numbers.empty",
                Heading(Localize("NativeDashboard_NumbersEmptyTitle"), palette, 18),
                Body(Localize("NativeDashboard_NumbersEmptyDescription"), palette,
                    secondary: true)));
            return;
        }
        // Source col-6 col-md-4.
        var columns = WindowWidthDp >= 768 ? 3 : 2;
        for (var start = 0; start < numbers.Length; start += columns)
        {
            var row = new Row(Arrangement.SpacedBy(12))
            {
                Modifier.FillMaxWidth().Padding(bottom: 12),
            };
            for (var index = start; index < start + columns; index++)
                row.Add(index < numbers.Length
                    ? NumberMasteryCard(numbers[index], palette)
                    : new Box { Modifier.Weight(1f) });
            viewport.Add(row);
        }
    }

    /// <summary>
    /// Dashboard_UnableToLoadPlan carries a {0} reason; fill it as iOS
    /// DashboardView.DashboardErrorText does instead of showing the placeholder.
    /// </summary>
    private static string DashboardErrorText(string? errorKey)
    {
        var key = errorKey ?? NativeText.PlanError;
        var localized = Localize(key);
        return key == NativeText.PlanError
            ? string.Format(CultureInfo.CurrentCulture, localized,
                Localize(NativeText.UnexpectedError))
            : localized;
    }

    private ComposableNode StatisticsLoading(string tag, AndroidVisualPalette palette)
    {
        var label = Localize("Common_Loading");
        return new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(vertical: 16).TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            new CircularProgressIndicator { Modifier = Modifier.Size(24) },
            Body(label, palette, secondary: true),
        };
    }

    private ComposableNode NumberMasteryCard(
        NativeNumberContextStat number, AndroidVisualPalette palette)
    {
        var title = number.ContextCode;
        var correct = string.Format(CultureInfo.CurrentCulture,
            Localize("NativeDashboard_NumbersCorrect"),
            number.CorrectCount, number.TotalCount);
        var header = new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Heading(title, palette, 16),
        };
        string? due = null;
        if (number.DueCount > 0)
        {
            due = string.Format(CultureInfo.CurrentCulture,
                Localize("NativeDashboard_NumbersDue"), number.DueCount);
            header.Add(TierBadge(due, palette.WarningText, palette,
                $"native.dashboard.numbers.{title}.due"));
        }
        var label = due is null ? $"{title}. {correct}" : $"{title}. {due}. {correct}";
        var surface = new Surface
        {
            Modifier = Modifier.Weight(1f)
                .TestTag($"native.dashboard.numbers.{title}")
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        var caption = Body(correct, palette, secondary: true);
        caption.FontSize = new Sp(AndroidVisualTheme.CaptionSize + 1);
        surface.Add(new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(12),
            header,
            ProgressBar(Math.Clamp(number.MasteryPercentage, 0, 100) / 100f, palette,
                completed: true),
            caption,
        });
        return surface;
    }

    private void AddVocabularySummary(
        Column viewport, VocabProgressSummary summary, AndroidVisualPalette palette)
    {
        ComposableNode[] tiles =
        [
            VocabularyTile(summary.New, "Dashboard_VocabNew", "new",
                palette.Primary, palette),
            VocabularyTile(summary.Learning, "Dashboard_VocabLearning", "learning",
                palette.WarningText, palette),
            VocabularyTile(summary.Familiar, "Dashboard_VocabFamiliar", "familiar",
                palette.Primary, palette),
            VocabularyTile(summary.Review, "Dashboard_VocabReview", "review",
                palette.WarningText, palette),
            VocabularyTile(summary.Known, "Dashboard_VocabKnown", "known",
                palette.KnownText, palette),
        ];
        // Source col-6 col-md: two per row on a phone, one row of five from 768.
        var columns = WindowWidthDp >= 768 ? 5 : 2;
        for (var start = 0; start < tiles.Length; start += columns)
        {
            var row = new Row(Arrangement.SpacedBy(12))
            {
                Modifier.FillMaxWidth(),
            };
            for (var index = start; index < start + columns; index++)
                row.Add(index < tiles.Length ? tiles[index] : new Box { Modifier.Weight(1f) });
            viewport.Add(row);
        }
        var total = summary.New + summary.Learning + summary.Familiar
            + summary.Review + summary.Known;
        if (total > 0)
            viewport.Add(Card(palette, "native.dashboard.vocab.summary",
                Body($"{Localize("Dashboard_TotalWordsLabel")} " +
                    total.ToString(CultureInfo.CurrentCulture), palette),
                Body($"{Localize("Dashboard_WeeklyAccuracyLabel")} " +
                    $"{Math.Round(summary.SuccessRate7d * 100).ToString(CultureInfo.CurrentCulture)}%",
                    palette, secondary: true)));
    }

    private ComposableNode VocabularyTile(
        int count, string labelKey, string name, AndroidX.Compose.Color numberColor,
        AndroidVisualPalette palette)
    {
        var label = Localize(labelKey);
        var number = Heading(count.ToString(CultureInfo.CurrentCulture), palette, 24);
        number.Color = numberColor;
        number.Align = TextAlign.Center;
        var caption = Body(label, palette, secondary: true);
        caption.Align = TextAlign.Center;
        var tile = new Surface
        {
            Modifier = Modifier.Weight(1f).HeightIn(min: 88)
                .TestTag($"native.dashboard.vocab.stat.{name}")
                .Semantics(semantics =>
                    semantics.ContentDescription($"{count} {label}")),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        tile.Add(new Column(Arrangement.SpacedBy(4),
            Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().Padding(12),
            number,
            caption,
        });
        return tile;
    }
}
