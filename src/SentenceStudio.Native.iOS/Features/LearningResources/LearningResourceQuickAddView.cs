using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal static class LearningResourceQuickAddView
{
    internal const string QuickAddFieldAutomationId =
        "native.resources.edit.quick.search";

    /// <summary>
    /// Quick add as Form rows (Captain 2026-11-13): the search field, then either why it is
    /// unavailable or, after "add new word", the meaning field and Add.
    /// </summary>
    internal static IReadOnlyList<SwiftView> QuickAdd(
        LearningResourceViewState state, bool busy, LearningResourceActions actions,
        LearningResourceInputs inputs, SourceUiStyle style, Func<string, string> text)
    {
        var label = text("ResourceEdit_QuickAddLabel");
        var input = Views.TextField(text("ResourceEdit_QuickAddPlaceholder"),
            inputs.QuickQuery, inputs.QuickQueryChanged);
        input.OnKeyPress(key => key switch
            {
                SwiftTextKey.ArrowUp =>
                    inputs.QuickKeyPressed(ResourceQuickAddKey.ArrowUp),
                SwiftTextKey.ArrowDown =>
                    inputs.QuickKeyPressed(ResourceQuickAddKey.ArrowDown),
                SwiftTextKey.Enter =>
                    inputs.QuickKeyPressed(ResourceQuickAddKey.Enter),
                SwiftTextKey.Escape =>
                    inputs.QuickKeyPressed(ResourceQuickAddKey.Escape),
                _ => false
            })
            .OnFocusChanged(focused => inputs.QuickFocusChanged(input, focused));
        input.WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(QuickAddFieldAutomationId);
        input.IsEnabled = state.CanQuickAdd && !busy;
        inputs.RegisterQuickSearch(input);
        // The field alone (its prompt says what it does): a label beside it would leave the
        // prompt too little width on a phone.
        var rows = new List<SwiftView> { input };

        if (!state.CanQuickAdd && state.Phase != NativeResourceDetailPhase.Conflict)
            rows.Add(Views.Text(text(
                    string.IsNullOrWhiteSpace(state.Language)
                    || string.Equals(state.Language, "Other",
                        StringComparison.OrdinalIgnoreCase)
                        ? "ResourceEdit_SetLanguageFirst"
                        : "NativeResources_SaveLanguageBeforeQuickAdd"))
                .WithFont(SwiftFontStyle.Caption)
                .WithAccessibilityIdentifier("native.resources.edit.quick.hint"));
        else if (!string.IsNullOrWhiteSpace(inputs.QuickQuery) && inputs.CreateWordExpanded)
        {
            rows.Add(LearningResourceFieldsView.Field(text("ResourceEdit_NativeTermPrompt"),
                inputs.QuickNativeTerm, "native.resources.edit.quick.native",
                inputs.QuickNativeTermChanged, readOnly: false));
            rows.Add(LearningResourceVocabularyView.Action(style, Views.Button(
                    Views.Label(label, "plus"), actions.CreateWord)
                .WithAccessibilityIdentifier("native.resources.edit.quick.create")));
        }
        return rows;
    }

    internal static SwiftView QuickAddSuggestions(
        LearningResourceViewState state, string query, int highlighted,
        SourceUiStyle style, Func<string, string> text,
        Action<string> attach, Action create,
        Action<SwiftView> registerOption)
    {
        var suggestions = new List<SwiftView>();
        if (state.CanQuickAdd && state.IsQuickAddSearching
            && !string.IsNullOrWhiteSpace(query))
            return Views.VStack(
                    Views.IndeterminateProgress(text("Common_Loading"))
                        .WithAccessibilityIdentifier(
                            "native.resources.edit.quick.loading"))
                .WithAccessibilityIdentifier("native.resources.edit.quick.listbox");
        if (!state.CanQuickAdd || !state.QuickAddResultsAvailable
            || string.IsNullOrWhiteSpace(query))
            return Views.VStack()
                .WithAccessibilityIdentifier("native.resources.edit.quick.listbox");

        for (var index = 0; index < state.QuickAddResults.Count; index++)
        {
            var candidate = state.QuickAddResults[index];
            var id = candidate.Id;
            var selected = index == highlighted;
            var label = Views.VStack(
                    style.Text(Views.Text(candidate.TargetTerm),
                        SourceTextRole.Body1Bold,
                        selected ? SourceTextTone.OnPrimary : SourceTextTone.Body),
                    style.Text(Views.Text(candidate.NativeTerm ?? string.Empty),
                        SourceTextRole.Body2,
                        selected ? SourceTextTone.OnPrimary : SourceTextTone.Secondary))
                .WithPadding(SourceUiLayout.ItemGap)
                .WithFlexibleWidth();
            if (selected)
                label.WithRoundedSurface(style.Appearance.Primary.Light,
                    style.Appearance.Primary.Dark,
                    style.Appearance.BootstrapRadius);
            var option = Views.Button(label, () => attach(id))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                .WithAccessibilityIdentifier(
                    $"native.resources.edit.quick.option.{id}");
            if (selected)
                option.WithAccessibilitySelected();
            registerOption(option);
            suggestions.Add(option);
        }
        if (suggestions.Count == 0)
        {
            suggestions.Add(style.Text(Views.Text(text("ResourceEdit_NoMatches")),
                SourceTextRole.Caption1, SourceTextTone.Secondary));
            var selected = highlighted == 0;
            var createLabel = style.Text(Views.Text(string.Format(
                    CultureInfo.CurrentCulture,
                    text("ResourceEdit_AddNewWord"), query.Trim())),
                SourceTextRole.Body2,
                selected ? SourceTextTone.OnPrimary : SourceTextTone.Body)
                .WithPadding(SourceUiLayout.ItemGap)
                .WithFlexibleWidth();
            if (selected)
                createLabel.WithRoundedSurface(
                    style.Appearance.Primary.Light, style.Appearance.Primary.Dark,
                    style.Appearance.BootstrapRadius);
            var createOption = Views.Button(createLabel, create)
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                .WithAccessibilityIdentifier("native.resources.edit.quick.new");
            if (selected)
                createOption.WithAccessibilitySelected();
            registerOption(createOption);
            suggestions.Add(createOption);
        }
        var list = Views.VStack(suggestions.ToArray())
            .WithAccessibilityIdentifier("native.resources.edit.quick.listbox");
        list.Spacing = 0;
        return list;
    }

}
