using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class TodaysPlanView
{
    internal static bool AddPhaseContent(
        List<SwiftView> items, NativeDashboardState state, bool busy,
        SourceUiStyle style, Func<string, string> text, Action generate,
        Action retryChooseOwn, bool insightExpanded, Action toggleInsight,
        Action<string>? openQuiz)
    {
        switch (state.Phase)
        {
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier("native.dashboard.loading.spinner"));
                items.Add(style.Text(Views.Text(text("Common_Loading")),
                        SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier("native.dashboard.status"));
                break;
            case NativePhase.PendingSync:
                items.Add(style.Card(Views.VStack(
                    style.Text(Views.Text(text("Native_SyncPending")),
                        SourceTextRole.Title3, sourceHeading: true),
                    style.SecondaryButton(text(NativeText.Retry), generate))
                    .WithPadding(SourceUiLayout.LargeCardInset))
                    .WithAccessibilityIdentifier("native.dashboard.sync.pending"));
                return true;
            case NativePhase.Empty:
                var empty = new List<SwiftView>
                {
                    style.Text(Views.Text(text(state.ErrorKey ?? NativeText.NoPlan)),
                            SourceTextRole.Body1)
                        .WithAccessibilityIdentifier("native.dashboard.status"),
                };
                if (!busy)
                    empty.Add(style.PrimaryButton(
                            text("Dashboard_GeneratePlan"), generate)
                        .WithAccessibilityIdentifier("native.dashboard.generate"));
                items.Add(style.Card(Views.VStack(empty.ToArray())
                        .WithPadding(SourceUiLayout.LargeCardInset))
                    .WithAccessibilityIdentifier("native.plan.empty"));
                break;
            case NativePhase.Ready:
                if (state.CurrentStreak > 0)
                {
                    var streak = string.Format(CultureInfo.CurrentCulture,
                        text("Dashboard_DayStreak"), state.CurrentStreak);
                    var streakBadge = style.Text(Views.Text(streak),
                            SourceTextRole.Badge)
                        .WithPadding(4, 8, 4, 8)
                        .WithForegroundColor("#212529", "#212529")
                        .WithRoundedSurface("#FFC233", "#FFC233",
                            style.Appearance.BootstrapRadius)
                        .WithAccessibilityIdentifier("native.plan.streak");
                    var streakRow = new List<SwiftView> { streakBadge };
                    if (state.LongestStreak > state.CurrentStreak)
                        streakRow.Add(style.Text(Views.Text(string.Format(
                                CultureInfo.CurrentCulture,
                                text("Dashboard_BestStreak"),
                                state.LongestStreak)), SourceTextRole.Small,
                                SourceTextTone.Secondary)
                            .WithAccessibilityIdentifier("native.plan.best-streak"));
                    var streakGroup = Views.HStack(streakRow.ToArray());
                    streakGroup.Spacing = SourceUiLayout.ItemGap;
                    items.Add(streakGroup);
                }
                var progressRow = Views.HStack(
                        style.Text(Views.Text(text("Dashboard_TodaysProgress")),
                                SourceTextRole.Body1Strong)
                            .WithAccessibilityIdentifier("native.plan.progress.title"),
                        style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                            text("Dashboard_ProgressCounts"),
                            state.CompletedCount, state.TotalCount,
                            state.MinutesSpent, state.EstimatedTotalMinutes)),
                            SourceTextRole.Small, SourceTextTone.Secondary)
                            .WithAccessibilityIdentifier("native.plan.progress.count"))
                        .WithAdaptiveStack(480)
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier("native.plan.progress.row");
                progressRow.Spacing = SourceUiLayout.ItemGap;
                var completed = Math.Clamp(state.CompletedCount, 0,
                    Math.Max(1, state.TotalCount));
                var track = style.Progress(Views.Progress(completed,
                        Math.Max(1, state.TotalCount), 8,
                        text("Dashboard_TodaysProgress")), completion: true)
                    .WithAccessibilityIdentifier("native.plan.progress.track");
                var progressCard = Views.VStack(progressRow, track)
                    .WithPadding(SourceUiLayout.CardInset);
                progressCard.Spacing = SourceUiLayout.ItemGap;
                items.Add(style.Card(progressCard)
                    .WithAccessibilityIdentifier("native.plan.progress"));
                PlanInsightView.Add(items, state, insightExpanded,
                    toggleInsight, style, text);

                foreach (var card in state.Cards)
                    items.Add(PlanCardView.Create(card, style, text, openQuiz));
                break;
            default:
                items.Add(style.Text(
                        Views.Text(DashboardView.DashboardErrorText(state.ErrorKey, text)),
                        SourceTextRole.Body1, SourceTextTone.Danger)
                    .WithAccessibilityIdentifier("native.dashboard.status"));
                if (state.Phase == NativePhase.Error && !busy)
                    items.Add(style.PrimaryButton(text(NativeText.Retry),
                            state.Mode == NativeDashboardMode.ChooseOwn
                                ? retryChooseOwn : generate)
                        .WithAccessibilityIdentifier("native.dashboard.retry"));
                break;
        }
        return false;
    }
}
