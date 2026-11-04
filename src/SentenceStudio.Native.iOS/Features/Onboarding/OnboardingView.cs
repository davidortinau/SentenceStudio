using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Onboarding;

/// <summary>The suggested-name list of the name step and its choose action.</summary>
internal sealed record OnboardingNameSuggestions(
    bool Loading, System.Collections.Immutable.ImmutableArray<string> Names, Action<string> Choose);

internal static class OnboardingView
{
    internal static SwiftView Page(
        SourceUiStyle style,
        int step, NativeOnboardingDraft draft, string? errorKey,
        bool pendingSync, bool busy, int contentStepsDone,
        bool creatingStarter,
        Func<string, string> text,
        Action<NativeOnboardingDraft> changed,
        Action back, Action next, Action<bool> finish,
        Action signOut, Action<SwiftView> registerNext,
        OnboardingNameSuggestions? suggestions = null)
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
                if (suggestions is { } names)
                    AddSuggestedNames(content, style, names, busy, text);
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
                        content.Add(ProgressRow(style, text(steps[index]),
                            index < contentStepsDone, index == contentStepsDone,
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
                        SourceButtonRole.Primary, true);
                    content.Add(starter);
                    var skip = style.SecondaryButton(
                            text("Onboarding_SkipSetup"), () => finish(false),
                            fullWidth: true)
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier(
                            "native.onboarding.finish.skip");
                    SourceUiStyle.SetCustomButtonAvailability(skip, style.Appearance,
                        SourceButtonRole.FilledSecondary, true);
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

    /// <summary>
    /// Source Onboarding.razor:59-75: while names load, a spinner with
    /// "Generating names"; then "Or choose a suggested name" over a two-column
    /// grid whose buttons fill the name field.
    /// </summary>
    private static void AddSuggestedNames(List<SwiftView> content, SourceUiStyle style,
        OnboardingNameSuggestions suggestions, bool busy, Func<string, string> text)
    {
        if (suggestions.Loading)
        {
            var loading = Views.HStack(
                    Views.IndeterminateProgress(text("Onboarding_GeneratingNames")),
                    style.Text(Views.Text(text("Onboarding_GeneratingNames")),
                        SourceTextRole.Caption1, SourceTextTone.Tertiary))
                .WithAccessibilityIdentifier("native.onboarding.names.loading");
            loading.Spacing = 4;
            content.Add(loading);
            return;
        }
        if (suggestions.Names.IsDefaultOrEmpty)
            return;
        content.Add(style.Text(Views.Text(text("Onboarding_OrChooseSuggested")),
                SourceTextRole.Caption1, SourceTextTone.Tertiary)
            .WithAccessibilityIdentifier("native.onboarding.names.caption"));
        var buttons = suggestions.Names.Select((name, index) =>
        {
            var choice = style.SecondaryButton(name, () => suggestions.Choose(name),
                    fullWidth: true)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"native.onboarding.names.{index}");
            SourceUiStyle.SetCustomButtonAvailability(choice, style.Appearance,
                SourceButtonRole.FilledSecondary, !busy);
            return choice;
        }).ToArray();
        var grid = Views.LazyVGrid(2, buttons)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.onboarding.names");
        grid.Spacing = SourceUiLayout.ItemGap;
        content.Add(grid);
    }

    /// <summary>Source Onboarding.razor:119-165: done, in progress, or waiting.</summary>
    private static SwiftView ProgressRow(
        SourceUiStyle style, string label, bool done, bool current, string id)
    {
        var appearance = style.Appearance;
        SwiftView marker = current
            ? Views.IndeterminateProgress(label)
            : Views.Image(done ? "checkmark.circle.fill" : "circle")
                .WithForegroundColor(
                    done ? appearance.SuccessAlertText.Light : appearance.InactiveStepDot.Light,
                    done ? appearance.SuccessAlertText.Dark : appearance.InactiveStepDot.Dark);
        var row = Views.HStack(marker,
                style.Text(Views.Text(label), SourceTextRole.Body2))
            .WithAccessibilityIdentifier(id);
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
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
