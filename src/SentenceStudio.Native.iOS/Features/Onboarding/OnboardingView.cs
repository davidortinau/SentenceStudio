using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Onboarding;

internal static class OnboardingView
{
    internal static SwiftView Page(
        SourceUiStyle style,
        int step, NativeOnboardingDraft draft, string? errorKey,
        bool pendingSync, bool busy, int contentStepsDone,
        bool creatingStarter, bool finishAvailable,
        Func<string, string> text,
        Action<NativeOnboardingDraft> changed,
        Action back, Action next, Action<bool> finish,
        Action signOut, Action<SwiftView> registerNext)
    {
        var content = new List<SwiftView>();
        if (pendingSync)
            content.Add(style.Text(Views.Text(
                    $"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}"),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.onboarding.sync.pending"));
        if (errorKey is not null)
            content.Add(style.Text(Views.Text(text(errorKey)),
                    SourceTextRole.Body2, SourceTextTone.Danger)
                .WithAccessibilityIdentifier("native.onboarding.error"));

        switch (step)
        {
            case 0:
                content.Add(Heading(style, text("Onboarding_Welcome"), welcome: true));
                content.Add(style.Text(Views.Text(text("Onboarding_SetupJourney")),
                        SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(600, SwiftHorizontalAlignment.Center));
                var start = style.PrimaryButton(
                        text("Onboarding_GetStarted"), next)
                    .WithFlexibleWidth(600, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.onboarding.start");
                SourceUiStyle.SetCustomButtonAvailability(start, style.Appearance,
                    SourceButtonRole.Primary, !busy);
                content.Add(start);
                break;
            case 1:
                content.Add(Heading(style, text("Onboarding_NativeLanguage")));
                foreach (var language in NativeOnboardingOptions.NativeLanguages)
                    content.Add(Choice(style, language,
                        $"native.onboarding.native.{language}",
                        draft.NativeLanguage == language, !busy,
                        () =>
                        {
                            changed(draft with { NativeLanguage = language });
                            next();
                        }));
                break;
            case 2:
                content.Add(Heading(style, text("Onboarding_TargetLanguage")));
                foreach (var language in NativeOnboardingOptions.TargetLanguages)
                    content.Add(Choice(style, language,
                        $"native.onboarding.target.{language}",
                        draft.TargetLanguage == language, !busy,
                        () =>
                        {
                            changed(draft with { TargetLanguage = language });
                            next();
                        }));
                break;
            case 3:
                content.Add(Heading(style, text("Onboarding_YourName")));
                var nameInput = style.TextInput(Views.TextField(text("Onboarding_EnterName"),
                        draft.Name, name => changed(draft with { Name = name })))
                    .WithAccessibilityIdentifier("native.onboarding.name");
                nameInput.IsEnabled = !busy;
                content.Add(nameInput);
                break;
            case 4:
                content.Add(Heading(style, text("Onboarding_LearningPreferences")));
                content.Add(style.Text(Views.Text(text("Onboarding_StudyDuration")),
                    SourceTextRole.Body2, SourceTextTone.Secondary));
                var preferenceDraft = draft;
                SwiftView? minutesGroup = null;
                minutesGroup = PreferenceGroup(
                    NativeOnboardingOptions.SessionMinutes.Select((minutes, index) =>
                        Choice(style, $"{minutes.ToString(
                            System.Globalization.CultureInfo.CurrentCulture)} " +
                            text("PlanMinutesLabel"),
                        $"native.onboarding.minutes.{minutes}",
                        draft.PreferredSessionMinutes == minutes, !busy,
                        () =>
                        {
                            preferenceDraft = preferenceDraft with {
                                PreferredSessionMinutes = minutes
                            };
                            changed(preferenceDraft);
                            SelectPreference(minutesGroup!, style.Appearance, index);
                        }, GroupPosition(index,
                            NativeOnboardingOptions.SessionMinutes.Length))).ToArray(),
                    NativeOnboardingOptions.SessionMinutes.IndexOf(
                        draft.PreferredSessionMinutes), style.Appearance);
                content.Add(minutesGroup);
                content.Add(style.Text(Views.Text(text("Onboarding_TargetLevel")),
                    SourceTextRole.Body2, SourceTextTone.Secondary));
                SwiftView? levelGroup = null;
                levelGroup = PreferenceGroup(
                    NativeOnboardingOptions.CefrLevels.Select((level, index) =>
                        Choice(style, level,
                        $"native.onboarding.level.{level}",
                        draft.TargetCefrLevel == level, !busy,
                        () =>
                        {
                            preferenceDraft = preferenceDraft with {
                                TargetCefrLevel = level
                            };
                            changed(preferenceDraft);
                            SelectPreference(levelGroup!, style.Appearance, index);
                        }, GroupPosition(index,
                            NativeOnboardingOptions.CefrLevels.Length))).ToArray(),
                    NativeOnboardingOptions.CefrLevels.IndexOf(
                        draft.TargetCefrLevel), style.Appearance);
                content.Add(levelGroup);
                break;
            case 5:
                content.Add(Heading(style, text("Onboarding_AllSet")));
                content.Add(style.Text(Views.Text(text("Onboarding_CreateStarterContent")),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                if (!finishAvailable)
                    content.Add(style.Text(Views.Text(text("Coach_Hint_Unknown")),
                            SourceTextRole.Caption1, SourceTextTone.Secondary)
                        .WithAccessibilityIdentifier(
                            "native.onboarding.finish.unavailable"));
                if (busy)
                {
                    var steps = creatingStarter
                        ? new[]
                        {
                            "Onboarding_SavingProfile",
                            "Onboarding_GeneratingVocab",
                            "Onboarding_CreatingSkillProfile"
                        }
                        : ["Onboarding_SavingProfile"];
                    for (var index = 0; index < steps.Length; index++)
                        content.Add(Views.HStack(
                                Views.Image(index < contentStepsDone
                                    ? "checkmark.circle.fill" : "circle"),
                                Views.Text(text(steps[index])))
                            .WithAccessibilityIdentifier(
                                $"native.onboarding.progress.{index}"));
                }
                else
                {
                    var starter = style.PrimaryButton(
                            text("Onboarding_CreateStarter"), () => finish(true),
                            fullWidth: true)
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier(
                            "native.onboarding.finish.starter");
                    SourceUiStyle.SetCustomButtonAvailability(starter, style.Appearance,
                        SourceButtonRole.Primary, finishAvailable);
                    content.Add(starter);
                    var skip = style.SecondaryButton(
                            text("Onboarding_SkipSetup"), () => finish(false),
                            fullWidth: true)
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier(
                            "native.onboarding.finish.skip");
                    SourceUiStyle.SetCustomButtonAvailability(skip, style.Appearance,
                        SourceButtonRole.FilledSecondary, finishAvailable);
                    content.Add(skip);
                }
                break;
        }

        if (step is > 0 and < 5)
        {
            var backButton = style.SecondaryButton(
                    text("Onboarding_Back"), back)
                .WithAccessibilityIdentifier("native.onboarding.back");
            SourceUiStyle.SetCustomButtonAvailability(backButton, style.Appearance,
                SourceButtonRole.FilledSecondary, !busy);
            var nextButton = style.PrimaryButton(
                    text("Onboarding_Next"), next)
                .WithAccessibilityIdentifier("native.onboarding.next");
            SourceUiStyle.SetCustomButtonAvailability(nextButton, style.Appearance,
                SourceButtonRole.Primary, !busy && (step switch
            {
                1 => !string.IsNullOrWhiteSpace(draft.NativeLanguage),
                2 => !string.IsNullOrWhiteSpace(draft.TargetLanguage),
                3 => !string.IsNullOrWhiteSpace(draft.Name),
                _ => true,
            }));
            registerNext(nextButton);
            var navigation = Views.HStack(backButton, nextButton);
            navigation.Spacing = SourceUiLayout.ItemGap;
            content.Add(navigation);
        }
        var dots = Views.HStack(Enumerable.Range(0, 6)
                .Select(index =>
                {
                    var color = index == step
                        ? style.Appearance.Primary : style.Appearance.InactiveStepDot;
                    var dot = Views.Circle(8, color.Light, color.Dark);
                    if (index == step)
                        dot.WithAccessibilityLabel($"{step + 1} / 6")
                            .WithAccessibilityIdentifier("native.onboarding.step");
                    return dot;
                }).ToArray());
        dots.Spacing = 4;
        content.Add(dots
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.onboarding.progress"));
        var logout = style.TextLinkButton(
            text("Nav_Logout"), "arrow.left.square", signOut,
            "native.onboarding.signout");
        logout.IsEnabled = !busy;
        content.Add(logout);
        var column = Views.VStack(content.ToArray())
            .WithFlexibleWidth(600, SwiftHorizontalAlignment.Center);
        column.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(column)
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth));
        return style.Page(page)
            .WithAccessibilityIdentifier("native.onboarding.screen");
    }

    private static SwiftView Heading(
        SourceUiStyle style, string title, bool welcome = false)
    {
        var heading = style.Text(Views.Text(title),
            welcome ? SourceTextRole.Display : SourceTextRole.Title1,
            sourceHeading: true);
        if (welcome)
            heading.WithFlexibleWidth(600, SwiftHorizontalAlignment.Center);
        return heading.WithAccessibilityIdentifier("native.onboarding.heading");
    }

    private static SwiftView Choice(
        SourceUiStyle style, string label, string id, bool selected,
        bool enabled, Action action,
        SourceButtonGroupPosition groupPosition = SourceButtonGroupPosition.None)
    {
        var fullWidth = groupPosition == SourceButtonGroupPosition.None;
        var choice = (selected
                ? style.PrimaryButton(label, action, fullWidth: fullWidth,
                    groupPosition: groupPosition)
                : style.SecondaryButton(label, action, fullWidth: fullWidth,
                    groupPosition: groupPosition))
            .WithAccessibilityIdentifier(id);
        SourceUiStyle.SetCustomButtonAvailability(choice, style.Appearance,
            selected ? SourceButtonRole.Primary : SourceButtonRole.FilledSecondary,
            enabled, groupPosition);
        return choice;
    }

    private static SourceButtonGroupPosition GroupPosition(int index, int count) =>
        index == 0 ? SourceButtonGroupPosition.First
        : index == count - 1 ? SourceButtonGroupPosition.Last
        : SourceButtonGroupPosition.Middle;

    private static SwiftView PreferenceGroup(
        SwiftView[] buttons, int selectedIndex, SourceUiAppearance appearance) =>
        Views.ButtonGroup(buttons, wraps: true, selectedIndex: selectedIndex)
            .WithButtonGroupLayout(appearance.BootstrapBaseButtonBorderWidth)
            .WithFlexibleWidth();

    private static void SelectPreference(
        SwiftView group, SourceUiAppearance appearance, int selectedIndex)
    {
        group.SelectedIndex = selectedIndex;
        for (var index = 0; index < group.Children.Count; index++)
        {
            SourceUiStyle.SetCustomButtonAvailability(
                group.Children[index], appearance,
                index == selectedIndex
                    ? SourceButtonRole.Primary : SourceButtonRole.FilledSecondary,
                enabled: true, GroupPosition(index, group.Children.Count));
        }
    }
}
