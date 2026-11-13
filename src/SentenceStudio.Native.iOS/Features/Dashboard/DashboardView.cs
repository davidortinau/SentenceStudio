using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class DashboardView
{
    internal static SwiftView Page(
        SourceUiStyle style,
        NativeDashboardState state,
        bool busy,
        Func<string, string> text,
        bool starterBusy,
        string? starterErrorKey,
        string? profileName,
        bool insightExpanded,
        DashboardActions actions,
        bool isTablet)
    {
        var items = new List<SwiftView>();
        if (state.IsNewUser)
            return DashboardPage(DashboardWelcomeView.Sections(
                style, busy, text, actions.CreateStarter, starterBusy,
                starterErrorKey, actions.AddResource, isTablet), style);
        // The presenter reads the name on every load, so a rename shows here;
        // the sign-in name covers the first render before that load.
        var name = state.ProfileName ?? profileName;
        if (isTablet && !string.IsNullOrWhiteSpace(name))
            items.Add(style.Text(Views.Text(string.Format(
                    CultureInfo.CurrentCulture,
                    text("Dashboard_WelcomeGreeting"), name)),
                    SourceTextRole.Title1, sourceHeading: true)
                .WithAccessibilityIdentifier("native.dashboard.welcome"));
        // Source Index.razor:105-117: every returning-learner Dashboard shows the
        // mode switch, whatever the plan phase.
        var switcher = Views.ButtonGroup([
            ModeButton(style, text(NativeText.Today),
                state.Mode == NativeDashboardMode.TodaysPlan,
                () => actions.SelectMode(NativeDashboardMode.TodaysPlan),
                SourceButtonGroupPosition.First)
                .WithAccessibilityIdentifier("native.dashboard.mode.today"),
            ModeButton(style, text("Dashboard_ChooseMyOwn"),
                state.Mode == NativeDashboardMode.ChooseOwn,
                () => actions.SelectMode(NativeDashboardMode.ChooseOwn),
                SourceButtonGroupPosition.Last)
                .WithAccessibilityIdentifier("native.dashboard.mode.chooseown")],
                selectedIndex: state.Mode == NativeDashboardMode.TodaysPlan ? 0 : 1)
            .WithButtonGroupLayout(style.Appearance.BootstrapBaseButtonBorderWidth,
                fillAvailableWidth: true)
            .WithFlexibleWidth();
        items.Add(switcher);
        // Local plan/progress is shown, but the last sync did not confirm it with the
        // server (offline or the server did not answer). Same text as the PendingSync phase.
        if (state.Phase is (NativePhase.Ready or NativePhase.Empty))
        {
            if (state.HasPendingSync)
                items.Add(style.Text(Views.Text(text("Native_SyncPending")),
                        SourceTextRole.Caption1, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier("native.dashboard.sync.caption"));
        }
        if (state.Mode == NativeDashboardMode.ChooseOwn
            && NativeDashboardPresenter.ShowsChooseOwn(state))
            ChooseOwnView.Add(items, state, style, text, actions.OpenResourceSelector,
                actions.OpenSkillSelector, actions.RetryChooseOwn,
                actions.ExpandActivities, actions.LaunchChooseOwnActivity, isTablet);
        else if (TodaysPlanView.AddPhaseContent(items, state, busy,
            style, text, actions.Generate, actions.RetryChooseOwn, insightExpanded,
            actions.ToggleInsight, actions.OpenQuiz, actions.OpenResource,
            actions.PreviewPlanVocabulary))
            return DashboardPage(items, style);
        DashboardLearningProgressView.AddSections(items, state, style, text);
        if (busy)
        {
            items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.dashboard.busy.spinner"));
            items.Add(style.Text(Views.Text(text("Common_Loading")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.dashboard.busy"));
        }

        return DashboardPage(items, style);
    }

    /// <summary>
    /// The Study toolbar (iOS tab shell trial, 2026-11-05): History opens the practice log;
    /// Regenerate shows only where it applies; Refresh as before. Profile, Settings,
    /// Skills, Vocabulary, Minimal Pairs and Sign out moved to the Library and Settings tabs.
    /// </summary>
    internal static SwiftView WithToolbar(
        SwiftView page, SourceUiStyle style, Func<string, string> text,
        bool isNewUser, bool isBusy, bool isRefreshing,
        bool canRegeneratePlan, DashboardToolbarActions actions)
    {
        var items = new List<SwiftView> { History(style, text, isBusy, actions.OpenHistory) };
        if (!isNewUser)
        {
            if (canRegeneratePlan)
                items.Add(Regenerate(style, text, isBusy, actions.RegeneratePlan));
            items.Add(Refresh(style, text, isBusy, isRefreshing, actions.Refresh));
        }
        return Views.Toolbar(page, items.ToArray());
    }

    private static SwiftView History(
        SourceUiStyle style, Func<string, string> text, bool isBusy, Action openHistory)
    {
        var button = Views.Button(Views.Label(text("Nav_History"), "calendar"), openHistory)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier("native.study.history");
        button.IsEnabled = !isBusy;
        return button;
    }

    private static SwiftView Regenerate(
        SourceUiStyle style, Func<string, string> text, bool isBusy, Action regenerate)
    {
        var button = Views.Button(Views.Label(text(NativeText.Regenerate), "wand.and.stars"),
                regenerate)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier("native.dashboard.regenerate");
        button.IsEnabled = !isBusy;
        return button;
    }

    private static SwiftView Refresh(
        SourceUiStyle style, Func<string, string> text,
        bool isBusy, bool isRefreshing, Action refresh)
    {
        var indicator = isRefreshing
            ? Views.IndeterminateProgress(text("Common_Refresh"))
                .WithAccessibilityIdentifier("native.dashboard.refresh.spinner")
            : Views.Label(text("Common_Refresh"), "arrow.clockwise");
        var button = Views.Button(indicator, refresh)
            .WithAccessibilityLabel(text("Common_Refresh"))
            .WithTintColor(style.Appearance.Primary.Light,
                style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier("native.dashboard.refresh");
        button.IsEnabled = !isBusy;
        return button;
    }

    internal static string DashboardErrorText(
        string? errorKey, Func<string, string> text)
    {
        var key = errorKey ?? NativeText.PlanError;
        var localized = text(key);
        return key == NativeText.PlanError
            ? string.Format(CultureInfo.CurrentCulture, localized,
                text(NativeText.UnexpectedError))
            : localized;
    }

    private static SwiftView DashboardPage(
        List<SwiftView> items, SourceUiStyle style)
    {
        var content = Views.VStack(items.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.dashboard.scroll.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(content);
        return style.Page(page).WithAccessibilityIdentifier(NativeAccess.Plan);
    }

    private static SwiftView ModeButton(
        SourceUiStyle style, string label, bool selected, Action action,
        SourceButtonGroupPosition groupPosition)
    {
        return selected
            ? style.PrimaryButton(label, action, fullWidth: true,
                groupPosition: groupPosition)
            : style.OutlineButton(Views.Text(label), action, fullWidth: true,
                groupPosition: groupPosition);
    }
}
