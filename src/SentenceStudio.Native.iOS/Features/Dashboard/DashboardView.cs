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
        if (isTablet && !string.IsNullOrWhiteSpace(profileName))
            items.Add(style.Text(Views.Text(string.Format(
                    CultureInfo.CurrentCulture,
                    text("Dashboard_WelcomeGreeting"), profileName)),
                    SourceTextRole.Title1, sourceHeading: true)
                .WithAccessibilityIdentifier("native.dashboard.welcome"));
        if (state.Phase is NativePhase.Ready or NativePhase.Empty)
        {
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
        }
        if (state.Mode == NativeDashboardMode.ChooseOwn
            && state.Phase is (NativePhase.Ready or NativePhase.Empty))
            ChooseOwnView.Add(items, state, style, text, actions.OpenResourceSelector,
                actions.OpenSkillSelector, actions.RetryChooseOwn,
                actions.ExpandActivities, actions.LaunchChooseOwnActivity, isTablet);
        else if (TodaysPlanView.AddPhaseContent(items, state, busy,
            style, text, actions.Generate, actions.RetryChooseOwn, insightExpanded,
            actions.ToggleInsight, actions.OpenQuiz))
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

    internal static SwiftView WithToolbar(
        SwiftView page, SourceUiStyle style, Func<string, string> text,
        bool isNewUser, bool isBusy, bool isRefreshing,
        bool canRegeneratePlan, string signOutTitle, DashboardToolbarActions actions) =>
        isNewUser
            ? Views.Toolbar(page, Menu(style, text, isBusy,
                canRegeneratePlan, signOutTitle, actions))
            : Views.Toolbar(page,
                Refresh(style, text, isBusy, isRefreshing, actions.Refresh),
                Menu(style, text, isBusy, canRegeneratePlan, signOutTitle, actions));

    private static SwiftView Refresh(
        SourceUiStyle style, Func<string, string> text,
        bool isBusy, bool isRefreshing, Action refresh)
    {
        var indicator = isRefreshing
            ? Views.IndeterminateProgress(text("Common_Refresh"))
                .WithAccessibilityIdentifier("native.dashboard.refresh.spinner")
            : Views.Image("arrow.clockwise");
        var button = Views.Button(indicator, refresh)
            .WithAccessibilityLabel(text("Common_Refresh"))
            .WithTintColor(style.Appearance.Primary.Light,
                style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier("native.dashboard.refresh");
        button.IsEnabled = !isBusy;
        return button;
    }

    private static SwiftView Menu(
        SourceUiStyle style, Func<string, string> text, bool isBusy,
        bool canRegeneratePlan, string signOutTitle, DashboardToolbarActions actions)
    {
        var menuActions = new List<SwiftView>();
        if (canRegeneratePlan)
            menuActions.Add(Views.Button(text(NativeText.Regenerate),
                    actions.RegeneratePlan)
                .WithAccessibilityIdentifier("native.dashboard.regenerate"));
        menuActions.Add(Views.Button(text("Nav_Profile"), actions.OpenProfile)
            .WithAccessibilityIdentifier("native.dashboard.profile"));
        menuActions.Add(Views.Button(text("Settings_Title"), actions.OpenSettings)
            .WithAccessibilityIdentifier("native.dashboard.settings"));
        menuActions.Add(Views.Button(text("Nav_Skills"), actions.OpenSkills)
            .WithAccessibilityIdentifier("native.dashboard.skills"));
        menuActions.Add(Views.Button(text("Nav_Vocabulary"), actions.OpenVocabulary)
            .WithAccessibilityIdentifier("native.dashboard.vocabulary"));
        menuActions.Add(Views.Button(text("Nav_Activity"), actions.OpenActivityLog)
            .WithAccessibilityIdentifier("native.dashboard.activity-log"));
        menuActions.Add(Views.Button(signOutTitle, actions.SignOut)
            .WithAccessibilityIdentifier("native.dashboard.sign-out"));
        var menu = Views.Menu(text("More"), menuActions.ToArray())
            .WithAccessibilityIdentifier("native.dashboard.menu");
        menu.IsEnabled = !isBusy;
        return menu;
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
