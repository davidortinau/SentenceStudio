using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Styling;

/// <summary>
/// The native form pattern (Captain 2026-11-13: the Resource form, departure 84, approved and
/// applied to every form): a grouped SwiftUI Form of Sections; a short field is a
/// LabeledContent row (its label beside it, an empty one prompting Required or Optional); a
/// long text is a Section with its label as the header and a text editor as the row; a value
/// is a LabeledContent row; actions are accent-colored rows. A Section has no automation ID
/// (SwiftUI gives a Section's identifier to every row in it).
/// </summary>
internal static class NativeForm
{
    /// <summary>The grouped Form page; a drag of the rows puts the keyboard away.</summary>
    internal static SwiftView Page(SourceUiStyle style, IEnumerable<SwiftView> sections,
        string formId, string screenId) =>
        style.GroupedForm(Views.Form(sections.ToArray())
                .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively)
                .WithAccessibilityIdentifier(formId))
            .WithAccessibilityIdentifier(screenId);

    /// <summary>A short field: the label beside a text field (or beside the value, read only).</summary>
    internal static SwiftView Field(string label, string? value, string id,
        Action<string> changed, string? prompt = null, bool readOnly = false,
        bool secure = false)
    {
        if (readOnly)
            return Value(label, value, id);
        var input = secure
            ? Views.SecureField(prompt ?? string.Empty, value ?? string.Empty, changed)
            : Views.TextField(prompt ?? string.Empty, value ?? string.Empty, changed);
        return Views.LabeledContent(label, input
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id));
    }

    /// <summary>A read-only value row: the label beside its value.</summary>
    internal static SwiftView Value(string label, string? value, string id) =>
        Views.LabeledContent(label, Views.Text(value ?? string.Empty))
            .WithAccessibilityIdentifier(id);

    /// <summary>A long text as its own Section: the label is the header, the editor the row.</summary>
    internal static SwiftView TextSection(SourceUiStyle style, string label, string? value,
        string id, Action<string> changed, int rows = 3, string? placeholder = null,
        bool readOnly = false) =>
        Views.Section(label, readOnly
            ? Views.Text(value ?? string.Empty).WithFlexibleWidth()
                .WithAccessibilityIdentifier(id)
            : Editor(style, label, value, id, changed, rows, placeholder));

    /// <summary>A multiline text editor row, at least <paramref name="rows"/> lines high.</summary>
    internal static SwiftView Editor(SourceUiStyle style, string label, string? value,
        string id, Action<string> changed, int rows = 3, string? placeholder = null)
    {
        var input = Views.TextEditor(value ?? string.Empty, changed)
            .WithTextEditorScrollBackgroundHidden();
        if (placeholder is not null)
            input.WithTextEditorPlaceholder(placeholder)
                .WithPlaceholderColor(style.Appearance.TertiaryText.Light,
                    style.Appearance.TertiaryText.Dark);
        var font = SourceUiTypography.For(SourceTextRole.Input, style.ViewportWidth,
            style.Appearance.Selection.FontScale, style.Appearance.Selection.ThemeId);
        return input
            .WithMinimumHeight(rows * font.Size * font.LineHeight)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
    }

    /// <summary>A choice as a system menu Picker row: the label beside the current value.</summary>
    internal static SwiftView Choice(SourceUiStyle style, string label,
        IReadOnlyList<string> options, int selected, string id, Action<int> changed) =>
        Views.Picker(label, options, selected, changed)
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier(id);

    /// <summary>
    /// A choice by value as a system menu Picker row. A stored value outside the options (one
    /// saved by another client) stays selectable, first, so the row never shows a blank
    /// choice; a change <paramref name="changed"/> refuses puts the picker back.
    /// </summary>
    internal static SwiftView Choice(SourceUiStyle style, string label,
        IReadOnlyList<SwiftPickerOption> options, string selected, string id,
        Func<string, bool> changed)
    {
        var list = options.ToList();
        if (!string.IsNullOrWhiteSpace(selected)
            && !list.Any(option => string.Equals(option.Value, selected, StringComparison.Ordinal)))
            list.Insert(0, new SwiftPickerOption(selected, selected));
        SwiftView? picker = null;
        var current = selected;
        var restoring = false;
        picker = Views.Picker(label, list, selected, next =>
            {
                if (restoring)
                    return;
                if (changed(next))
                {
                    current = next;
                    return;
                }
                restoring = true;
                try { picker!.SelectedValue = current; }
                finally { restoring = false; }
            })
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier(id);
        return picker;
    }

    /// <summary>A Form action row in the accent color, as a system Form draws its buttons.</summary>
    internal static SwiftView Action(SourceUiStyle style, SwiftView button) =>
        button.WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark);

    /// <summary>An action row with a symbol.</summary>
    internal static SwiftView Action(SourceUiStyle style, string title, string symbol,
        Action action, string id) =>
        Action(style, Views.Button(Views.Label(title, symbol), action)
            .WithAccessibilityIdentifier(id));

    /// <summary>A system bordered button in the accent color; its own target in a Form row.</summary>
    internal static SwiftView Bordered(SourceUiStyle style, string title, Action action, string id) =>
        Views.Button(title, action)
            .WithButtonStyle(SwiftButtonStyle.Bordered)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            // The Form's text ink would otherwise replace the accent label.
            .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier(id);

    /// <summary>A system prominent (filled) accent button; its own target in a Form row.</summary>
    internal static SwiftView Prominent(SourceUiStyle style, string title, Action action, string id) =>
        Views.Button(title, action)
            .WithButtonStyle(SwiftButtonStyle.BorderedProminent)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            // The Form's text ink would otherwise replace the prominent style's light label.
            .WithForegroundColor(style.Appearance.OnPrimary.Light, style.Appearance.OnPrimary.Dark)
            .WithAccessibilityIdentifier(id);

    /// <summary>A system prominent destructive button in the danger color.</summary>
    internal static SwiftView Destructive(SourceUiStyle style, string title, Action action, string id) =>
        Views.Button(title, action)
            .WithButtonRole(SwiftButtonRole.Destructive)
            .WithButtonStyle(SwiftButtonStyle.BorderedProminent)
            .WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
            .WithForegroundColor(style.Appearance.OnPrimary.Light, style.Appearance.OnPrimary.Dark)
            .WithAccessibilityIdentifier(id);

    /// <summary>A system switch row in the accent color.</summary>
    internal static SwiftView Switch(SourceUiStyle style, string title, bool isOn,
        Action<bool> changed, string id) =>
        Views.Toggle(title, isOn, changed)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier(id);

    /// <summary>A status or error row (red for an error), for the form's first Section.</summary>
    internal static SwiftView Notice(SourceUiStyle style, string message, string id,
        bool error) =>
        style.Text(Views.Text(message), SourceTextRole.Body2,
                error ? SourceTextTone.Danger : SourceTextTone.Secondary)
            .WithAccessibilityIdentifier(id);
}
