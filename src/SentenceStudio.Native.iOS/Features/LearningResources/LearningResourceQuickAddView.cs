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
    /// <summary>The add sheet's system search field.</summary>
    internal const string QuickAddFieldAutomationId =
        "native.resources.edit.quick.search";

    /// <summary>
    /// Quick add in the form (Captain 2026-11-13, the native pass): one "Add vocabulary" row
    /// that opens the add sheet, or why it is unavailable (language not set or not saved).
    /// </summary>
    internal static IReadOnlyList<SwiftView> FormRows(
        LearningResourceViewState state, bool busy, LearningResourceInputs inputs,
        SourceUiStyle style, Func<string, string> text)
    {
        var open = LearningResourceVocabularyView.Action(style, Views.Button(
                Views.Label(text("ResourceEdit_QuickAddLabel"), "plus"), inputs.OpenAddWords)
            .WithAccessibilityIdentifier("native.resources.edit.quick.add"));
        open.IsEnabled = state.CanQuickAdd && !busy;
        var rows = new List<SwiftView> { open };
        if (!state.CanQuickAdd && state.Phase != NativeResourceDetailPhase.Conflict)
            rows.Add(Views.Text(text(
                    string.IsNullOrWhiteSpace(state.Language)
                    || string.Equals(state.Language, "Other",
                        StringComparison.OrdinalIgnoreCase)
                        ? "ResourceEdit_SetLanguageFirst"
                        : "NativeResources_SaveLanguageBeforeQuickAdd"))
                .WithFont(SwiftFontStyle.Caption)
                .WithAccessibilityIdentifier("native.resources.edit.quick.hint"));
        return rows;
    }

    /// <summary>
    /// The add sheet's rows under the system search field: the matching words (tap one to add
    /// it), or, with no match, "No matching words found." and "Add '...' as a new word", which
    /// opens the meaning field and Add.
    /// </summary>
    internal static SwiftView[] SheetRows(
        LearningResourceViewState state, string query, bool busy, bool createExpanded,
        string nativeTerm, Action<string> nativeTermChanged,
        Action<string> attach, Action expandCreate, Action create,
        SourceUiStyle style, Func<string, string> text)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        if (state.IsQuickAddSearching)
            return [Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.resources.edit.quick.loading")];
        if (!state.QuickAddResultsAvailable)
            return [];
        if (state.QuickAddResults.Count > 0)
            return state.QuickAddResults.Select(candidate =>
            {
                var label = Views.VStack(
                        style.Text(Views.Text(candidate.TargetTerm),
                            SourceTextRole.Body1Bold, strongElement: true),
                        style.Text(Views.Text(candidate.NativeTerm ?? string.Empty),
                            SourceTextRole.Body2, SourceTextTone.Secondary))
                    .WithFlexibleWidth();
                label.Spacing = 2;
                var id = candidate.Id;
                var option = Views.Button(label, () => attach(id));
                // While an add runs the rows wait (a second tap would be refused anyway).
                option.IsEnabled = !busy;
                return option
                    .WithAccessibilityLabel(string.IsNullOrEmpty(candidate.NativeTerm)
                        ? candidate.TargetTerm
                        : $"{candidate.TargetTerm}, {candidate.NativeTerm}")
                    .WithAccessibilityIdentifier($"native.resources.edit.quick.option.{id}");
            }).ToArray();
        var rows = new List<SwiftView>
        {
            Views.Text(text("ResourceEdit_NoMatches"))
                .WithAccessibilityIdentifier("native.resources.edit.quick.none"),
        };
        if (!createExpanded)
            rows.Add(LearningResourceVocabularyView.Action(style, Views.Button(
                    Views.Label(string.Format(CultureInfo.CurrentCulture,
                        text("ResourceEdit_AddNewWord"), query.Trim()), "plus.circle"),
                    expandCreate)
                .WithAccessibilityIdentifier("native.resources.edit.quick.new")));
        else if (busy)
            rows.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.resources.edit.quick.adding"));
        else
        {
            // A field with its prompt ("Translation (optional)"), as the system's add forms do.
            rows.Add(Views.TextField(text("ResourceEdit_NativeTermPrompt"), nativeTerm,
                    nativeTermChanged)
                .WithAccessibilityLabel(text("ResourceEdit_NativeTermPrompt"))
                .WithAccessibilityIdentifier("native.resources.edit.quick.native"));
            rows.Add(LearningResourceVocabularyView.Action(style, Views.Button(
                    Views.Label(string.Format(CultureInfo.CurrentCulture,
                        text("ResourceEdit_AddNewWord"), query.Trim()), "plus.circle.fill"),
                    create)
                .WithAccessibilityIdentifier("native.resources.edit.quick.create")));
        }
        return rows.ToArray();
    }
}
