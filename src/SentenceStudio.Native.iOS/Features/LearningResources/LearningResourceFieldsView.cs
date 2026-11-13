using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

/// <summary>
/// The Resource form's rows (Captain 2026-11-13: the native Form). A short field is a
/// LabeledContent row, its label beside it as in Settings and Contacts; a long text is a
/// Section with its label as the header; a choice is a system Picker row.
/// </summary>
internal static class LearningResourceFieldsView
{
    /// <summary>
    /// A short field: the label beside a text field, or beside the value when read only (a
    /// smart resource). The automation ID is on the field (or the read-only row).
    /// </summary>
    internal static SwiftView Field(
        string label, string? value, string id,
        Action<string> changed, bool readOnly, string? placeholder = null) =>
        readOnly
            ? Value(label, value, id)
            : Views.LabeledContent(label,
                // No prompt unless one is given: the label already sits beside the field.
                Views.TextField(placeholder ?? string.Empty, value ?? string.Empty, changed)
                    .WithAccessibilityLabel(label)
                    .WithAccessibilityIdentifier(id));

    /// <summary>A read-only value row: the label beside its value.</summary>
    internal static SwiftView Value(string label, string? value, string id) =>
        Views.LabeledContent(label, Views.Text(value ?? string.Empty))
            .WithAccessibilityIdentifier(id);

    /// <summary>
    /// A long text (description, transcript, translation) as its own Section: the label is the
    /// header and the row is a text editor, or the text itself when read only (Captain
    /// 2026-11-12: nothing on a smart resource is editable).
    /// </summary>
    internal static SwiftView Area(
        SourceUiStyle style,
        string label, string? value, string id,
        Action<string> changed, bool readOnly, int rows = 3,
        string? placeholder = null) =>
        Views.Section(label, readOnly
                ? Views.Text(value ?? string.Empty)
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier(id)
                : Editor(style, label, value, id, changed, rows, placeholder));

    /// <summary>A multiline text editor row, at least <paramref name="rows"/> lines high.</summary>
    internal static SwiftView Editor(
        SourceUiStyle style,
        string label, string? value, string id,
        Action<string> changed, int rows = 3, string? placeholder = null)
    {
        var input = Views.TextEditor(value ?? string.Empty, changed)
            .WithTextEditorScrollBackgroundHidden();
        if (placeholder is not null)
            input.WithTextEditorPlaceholder(placeholder)
                .WithPlaceholderColor(style.Appearance.TertiaryText.Light,
                    style.Appearance.TertiaryText.Dark);
        return input
            .WithMinimumHeight(AreaHeight(style, rows))
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
    }

    private static double AreaHeight(SourceUiStyle style, int rows)
    {
        var font = SourceUiTypography.For(SourceTextRole.Input,
            style.ViewportWidth, style.Appearance.Selection.FontScale,
            style.Appearance.Selection.ThemeId);
        return rows * font.Size * font.LineHeight;
    }

    /// <summary>
    /// A required choice (media type, language) as a system menu Picker row: the label beside
    /// the current value. A stored value outside the catalog stays selectable
    /// (<see cref="ResourceRequiredPicker"/>).
    /// </summary>
    internal static SwiftView Select(
        SourceUiStyle style,
        string label, string? current, IReadOnlyList<string> options,
        string id, Action<string> changed, bool readOnly)
    {
        if (readOnly)
            return Value(label, current, id);
        var picker = new ResourceRequiredPicker(current, options);
        return Views.Picker(label, picker.Options, picker.SelectedIndex,
                next => picker.Select(next, changed))
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier(id);
    }
}
