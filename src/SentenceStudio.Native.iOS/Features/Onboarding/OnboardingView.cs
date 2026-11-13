using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Onboarding;

/// <summary>The suggested-name list of the name step and its choose action.</summary>
internal sealed record OnboardingNameSuggestions(
    bool Loading, System.Collections.Immutable.ImmutableArray<string> Names, Action<string> Choose);

/// <summary>
/// Onboarding as a grouped Form (Captain 2026-11-13, the native form pattern; departure 93):
/// each of the six steps keeps its source content. The heading sits on the page above the
/// rows (the welcome step has no app mark, as in the source); a language is a checkable row (choosing it moves on, as the source does); the name
/// is a field beside its label with the suggested names as rows under it; the session length
/// and level are menu Picker rows; Back and Next are system buttons under the rows, with the
/// step dots under them; Sign Out is the last action row.
/// </summary>
internal static class OnboardingView
{
    internal const string FormId = "native.onboarding.form";

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
        var sections = new List<SwiftView>();
        var notices = new List<SwiftView>();
        if (pendingSync)
            notices.Add(NativeForm.Notice(style,
                $"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}",
                "native.onboarding.sync.pending", error: false));
        if (errorKey is not null)
            notices.Add(NativeForm.Notice(style, text(errorKey), "native.onboarding.error",
                error: true));

        switch (step)
        {
            case 0:
                sections.Add(Heading(style, text("Onboarding_Welcome"), welcome: true));
                AddNotices(sections, notices);
                sections.Add(Views.Section(string.Empty,
                    NativeForm.Notice(style, text("Onboarding_SetupJourney"),
                        "native.onboarding.intro", error: false)));
                var start = NativeForm.Prominent(style, text("Onboarding_GetStarted"), next,
                    "native.onboarding.start");
                start.IsEnabled = !busy;
                sections.Add(Views.Section(string.Empty, NativeForm.CenteredRow(start)));
                break;
            case 1:
                sections.Add(Heading(style, text("Onboarding_NativeLanguage")));
                AddNotices(sections, notices);
                sections.Add(Views.Section(string.Empty,
                    NativeOnboardingOptions.NativeLanguages.Select(language => Choice(style,
                        language, $"native.onboarding.native.{language}",
                        draft.NativeLanguage == language, !busy,
                        () =>
                        {
                            changed(draft with { NativeLanguage = language });
                            next();
                        })).ToArray()));
                break;
            case 2:
                sections.Add(Heading(style, text("Onboarding_TargetLanguage")));
                AddNotices(sections, notices);
                sections.Add(Views.Section(string.Empty,
                    NativeOnboardingOptions.TargetLanguages.Select(language => Choice(style,
                        language, $"native.onboarding.target.{language}",
                        draft.TargetLanguage == language, !busy,
                        () =>
                        {
                            changed(draft with { TargetLanguage = language });
                            next();
                        })).ToArray()));
                break;
            case 3:
                sections.Add(Heading(style, text("Onboarding_YourName")));
                AddNotices(sections, notices);
                // The heading names the field (source: heading and placeholder only).
                var nameInput = Views.TextField(text("Onboarding_EnterName"), draft.Name,
                        name => changed(draft with { Name = name }))
                    .WithAccessibilityLabel(text("Onboarding_YourName"))
                    .WithAccessibilityIdentifier("native.onboarding.name");
                nameInput.IsEnabled = !busy;
                sections.Add(Views.Section(string.Empty, nameInput));
                if (suggestions is { } names && SuggestedNames(style, names, busy, text) is { } list)
                    sections.Add(list);
                break;
            case 4:
                sections.Add(Heading(style, text("Onboarding_LearningPreferences")));
                AddNotices(sections, notices);
                // The two pickers change one draft: each keeps the other's latest choice.
                var preferenceDraft = draft;
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                // A stored length outside the options (saved by another client) stays, first,
                // with the same unit as the rest.
                var minuteValues = NativeOnboardingOptions.SessionMinutes.ToList();
                if (!minuteValues.Contains(draft.PreferredSessionMinutes))
                    minuteValues.Insert(0, draft.PreferredSessionMinutes);
                var minutes = NativeForm.Choice(style, text("Onboarding_StudyDuration"),
                    minuteValues.Select(value => new SwiftPickerOption(
                        value.ToString(culture),
                        $"{value.ToString(culture)} {text("PlanMinutesLabel")}")).ToArray(),
                    draft.PreferredSessionMinutes.ToString(culture),
                    "native.onboarding.minutes",
                    value =>
                    {
                        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                culture, out var chosen)
                            || !minuteValues.Contains(chosen))
                            return false;
                        preferenceDraft = preferenceDraft with { PreferredSessionMinutes = chosen };
                        changed(preferenceDraft);
                        return true;
                    });
                minutes.IsEnabled = !busy;
                var level = NativeForm.Choice(style, text("Onboarding_TargetLevel"),
                    NativeOnboardingOptions.CefrLevels.Select(value =>
                        new SwiftPickerOption(value, value)).ToArray(),
                    draft.TargetCefrLevel,
                    "native.onboarding.level",
                    value =>
                    {
                        if (string.IsNullOrWhiteSpace(value))
                            return false;
                        preferenceDraft = preferenceDraft with { TargetCefrLevel = value };
                        changed(preferenceDraft);
                        return true;
                    });
                level.IsEnabled = !busy;
                sections.Add(Views.Section(string.Empty, minutes, level));
                break;
            case 5:
                sections.Add(Heading(style, text("Onboarding_AllSet")));
                AddNotices(sections, notices);
                sections.Add(Views.Section(string.Empty,
                    NativeForm.Notice(style, text("Onboarding_CreateStarterContent"),
                        "native.onboarding.intro", error: false)));
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
                    sections.Add(Views.Section(string.Empty, steps.Select((key, index) =>
                        ProgressRow(style, text(key), index < contentStepsDone,
                            index == contentStepsDone,
                            $"native.onboarding.progress.{index}")).ToArray()));
                }
                else
                {
                    sections.Add(Views.Section(string.Empty, NativeForm.CenteredRow(
                        NativeForm.Prominent(style, text("Onboarding_CreateStarter"),
                            () => finish(true), "native.onboarding.finish.starter"))));
                    sections.Add(Views.Section(string.Empty, NativeForm.Action(style,
                        Views.Button(text("Onboarding_SkipSetup"), () => finish(false))
                            .WithAccessibilityIdentifier("native.onboarding.finish.skip"))));
                }
                break;
        }

        if (step is > 0 and < 5)
        {
            var backButton = NativeForm.Bordered(style, text("Onboarding_Back"), back,
                "native.onboarding.back");
            backButton.IsEnabled = !busy;
            var nextButton = NativeForm.Prominent(style, text("Onboarding_Next"), next,
                "native.onboarding.next");
            nextButton.IsEnabled = NativeOnboardingOptions.CanGoNext(step, draft, busy);
            registerNext(nextButton);
            var navigation = Views.HStack(backButton, nextButton)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithListRowBackgroundHidden();
            navigation.Spacing = SourceUiLayout.ItemGap;
            sections.Add(Views.Section(string.Empty, navigation));
        }
        // Their own Section: two background-less rows in one Section draw a separator between.
        sections.Add(Views.Section(string.Empty, StepDots(style, step)));

        var logout = NativeForm.Action(style, text("Nav_Logout"), "arrow.left.square", signOut,
            "native.onboarding.signout");
        logout.IsEnabled = !busy;
        sections.Add(Views.Section(string.Empty, logout));
        return NativeForm.Page(style, sections, FormId, "native.onboarding.screen");
    }

    private static void AddNotices(List<SwiftView> sections, List<SwiftView> notices)
    {
        if (notices.Count > 0)
            sections.Add(Views.Section(string.Empty, notices.ToArray()));
    }

    /// <summary>The six step dots, the current one in the accent color and named for VoiceOver.</summary>
    private static SwiftView StepDots(SourceUiStyle style, int step)
    {
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
        return dots
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithListRowBackgroundHidden()
            .WithAccessibilityIdentifier("native.onboarding.progress");
    }

    /// <summary>
    /// Source Onboarding.razor:59-75: while names load, a spinner with "Generating names";
    /// then "Or choose a suggested name" over the names, each a row that fills the name field.
    /// </summary>
    private static SwiftView? SuggestedNames(SourceUiStyle style,
        OnboardingNameSuggestions suggestions, bool busy, Func<string, string> text)
    {
        if (suggestions.Loading)
        {
            var loading = Views.HStack(
                    Views.IndeterminateProgress(text("Onboarding_GeneratingNames")),
                    style.Text(Views.Text(text("Onboarding_GeneratingNames")),
                        SourceTextRole.Body2, SourceTextTone.Secondary))
                .WithAccessibilityIdentifier("native.onboarding.names.loading");
            loading.Spacing = SourceUiLayout.ItemGap;
            return Views.Section(string.Empty, loading);
        }
        if (suggestions.Names.IsDefaultOrEmpty)
            return null;
        var rows = suggestions.Names.Select((name, index) =>
        {
            var choice = NativeForm.Action(style, Views.Button(name, () => suggestions.Choose(name))
                .WithAccessibilityIdentifier($"native.onboarding.names.{index}"));
            choice.IsEnabled = !busy;
            return choice;
        }).ToArray();
        return Views.Section(text("Onboarding_OrChooseSuggested"), rows);
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

    /// <summary>The step heading on the page above the rows (the welcome one centered and larger).</summary>
    private static SwiftView Heading(SourceUiStyle style, string title, bool welcome = false) =>
        Views.Section(string.Empty, style.Text(Views.Text(title),
                welcome ? SourceTextRole.Display : SourceTextRole.Title1, sourceHeading: true)
            .WithFlexibleWidth(alignment: welcome
                ? SwiftHorizontalAlignment.Center : SwiftHorizontalAlignment.Leading)
            .WithListRowBackgroundHidden()
            .WithAccessibilityIdentifier("native.onboarding.heading"));

    /// <summary>
    /// A language as a row: its name, and a checkmark on the chosen one (not color alone),
    /// selected for VoiceOver.
    /// </summary>
    private static SwiftView Choice(
        SourceUiStyle style, string label, string id, bool selected, bool enabled,
        Action action)
    {
        var appearance = style.Appearance;
        var line = Views.HStack(
                style.Text(Views.Text(label), SourceTextRole.Body1).WithFlexibleWidth(),
                Views.Image("checkmark")
                    .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
                    .WithHidden(!selected))
            .WithFlexibleWidth();
        // The row's ink is set (a Form button is otherwise accent), so a disabled row dims itself.
        var ink = enabled ? appearance.Text : appearance.SecondaryText;
        var choice = Views.Button(line, action)
            .WithForegroundColor(ink.Light, ink.Dark)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(label)
            .WithAccessibilitySelected(selected)
            .WithAccessibilityIdentifier(id);
        choice.IsEnabled = enabled;
        return choice;
    }
}
