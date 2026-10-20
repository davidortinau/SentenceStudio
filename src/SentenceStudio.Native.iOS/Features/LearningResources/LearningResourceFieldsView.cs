using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal static class LearningResourceFieldsView
{
    internal static SwiftView Field(
        SourceUiStyle style,
        string label, string? value, string id,
        Action<string> changed, bool readOnly, string? placeholder = null) =>
        readOnly
            ? Views.VStack(
                style.Text(Views.Text(label), SourceTextRole.Body2,
                    SourceTextTone.Secondary),
                style.Text(Views.Text(value ?? string.Empty), SourceTextRole.Body1))
                .WithAccessibilityIdentifier(id)
            : Views.VStack(
                style.Text(Views.Text(label), SourceTextRole.Body2,
                    SourceTextTone.Secondary),
                style.TextInput(Views.TextField(placeholder ?? label,
                        value ?? string.Empty, changed))
                    .WithAccessibilityIdentifier(id));

    internal static SwiftView Area(
        SourceUiStyle style,
        string label, string? value, string id,
        Action<string> changed, bool readOnly, bool heading = false, int rows = 3,
        string? placeholder = null)
    {
        var input = Views.TextEditor(value ?? string.Empty,
            readOnly ? null : changed)
            .WithTextEditorScrollBackgroundHidden();
        if (!readOnly && placeholder is not null)
            input.WithTextEditorPlaceholder(placeholder)
                .WithPlaceholderColor(style.Appearance.TertiaryText.Light,
                    style.Appearance.TertiaryText.Dark);
        var editor = style.Input(input)
            .WithMinimumHeight(AreaHeight(style, rows))
            .WithAccessibilityIdentifier(id);
        editor.IsEnabled = !readOnly;
        return Views.VStack(
                style.Text(Views.Text(label),
                    heading ? SourceTextRole.Title3 : SourceTextRole.Body2,
                    heading ? SourceTextTone.Body : SourceTextTone.Secondary,
                    sourceHeading: heading),
                editor);
    }

    private static double AreaHeight(SourceUiStyle style, int rows)
    {
        var font = SourceUiTypography.For(SourceTextRole.Input,
            style.ViewportWidth, style.Appearance.Selection.FontScale,
            style.Appearance.Selection.ThemeId);
        return rows * font.Size * font.LineHeight + 2 * SourceUiLayout.ItemGap;
    }

    internal static SwiftView Select(
        SourceUiStyle style,
        string label, string? current, IReadOnlyList<string> options,
        string id, Action<string> changed, bool readOnly, string? editMenuId)
    {
        if (readOnly)
            return Views.VStack(
                    style.Text(Views.Text(label), SourceTextRole.Body2,
                        SourceTextTone.Secondary),
                    style.Text(Views.Text(current ?? string.Empty), SourceTextRole.Body1))
                .WithAccessibilityIdentifier(id);
        var picker = new ResourceRequiredPicker(current, options);
        var fieldLabel = style.Text(Views.Text(label), SourceTextRole.Body2,
            SourceTextTone.Secondary);
        if (editMenuId is null)
            return Views.VStack(
                    fieldLabel,
                    style.Input(Views.Picker(string.Empty, picker.Options,
                            picker.SelectedIndex, next => picker.Select(next, changed))
                        .WithPickerStyle(SwiftPickerStyle.Menu)
                        .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading))
                        .WithTintColor(style.Appearance.Primary.Light,
                            style.Appearance.Primary.Dark)
                        .WithAccessibilityLabel(label)
                        .WithAccessibilityIdentifier(id))
                .WithFlexibleWidth();

        var selectedValue = picker.Options[picker.SelectedIndex];
        var selectedText = Views.Text(selectedValue)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{editMenuId}.value");
        var chevron = Views.Image("chevron.up.chevron.down")
            .WithForegroundColor(style.Appearance.SecondaryText.Light,
                style.Appearance.SecondaryText.Dark);
        var menuLabel = style.Input(Views.HStack(selectedText, chevron)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{editMenuId}.label"));
        menuLabel.Spacing = SourceUiLayout.ItemGap;

        SwiftView? menu = null;
        var choices = new SwiftView[picker.Options.Count];
        for (var index = 0; index < choices.Length; index++)
        {
            var choice = index;
            var value = picker.Options[index];
            var optionLabel = index == picker.SelectedIndex
                ? Views.HStack(Views.Text(value).WithFlexibleWidth(),
                    Views.Image("checkmark"))
                : Views.Text(value);
            choices[index] = Views.Button(optionLabel,
                    () =>
                    {
                        selectedText.Text = value;
                        menu!.Text = value;
                        for (var i = 0; i < choices.Length; i++)
                            choices[i].WithAccessibilitySelected(i == choice);
                        picker.Select(choice, changed);
                    })
                .WithAccessibilityIdentifier($"{editMenuId}.option.{index}")
                .WithAccessibilityLabel(value)
                .WithAccessibilitySelected(index == picker.SelectedIndex);
        }

        menu = Views.Menu(menuLabel, choices)
            .WithAccessibilityIdentifier(id)
            .WithAccessibilityLabel(label)
            .WithFlexibleWidth();
        menu.Text = selectedValue;
        return Views.VStack(fieldLabel, menu).WithFlexibleWidth();
    }

}
