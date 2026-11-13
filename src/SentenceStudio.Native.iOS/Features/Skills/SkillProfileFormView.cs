using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Skills;

internal sealed record SkillProfileFormActions(
    Action<string> TitleChanged, Action<string> DescriptionChanged,
    Action Save, Action Retry, Action RequestDelete, Action? ToggleArchived = null);

internal static class SkillProfileFormView
{
    internal static SwiftView Page(SkillProfileFormState state,
        SourceUiStyle style, Func<string, string> text,
        SkillProfileFormActions actions)
    {
        var fields = new List<SwiftView>();
        var busy = state.Phase is SkillFormPhase.Saving or SkillFormPhase.Deleting;
        if (state.Phase is SkillFormPhase.Loading or SkillFormPhase.Saving
            or SkillFormPhase.Deleting)
            fields.Add(Views.IndeterminateProgress(text(state.Phase switch
                {
                    SkillFormPhase.Loading => "Common_Loading",
                    SkillFormPhase.Deleting => "Common_Delete",
                    _ => "Common_Save"
                }))
                .WithAccessibilityIdentifier("native.skills.form.progress"));
        if (state.StatusKey is { } status && state.ErrorKey is null)
            fields.Add(style.Alert(Views.Text(text(status)), success: true)
                .WithAccessibilityIdentifier("native.skills.form.status"));
        if (state.ErrorKey is { } error)
            fields.Add(style.Alert(Views.Text(Error(error, text)), success: false)
                .WithAccessibilityIdentifier("native.skills.form.error"));

        var editable = state.Phase is SkillFormPhase.Creating
            or SkillFormPhase.Editing or SkillFormPhase.SaveError
            or SkillFormPhase.DeleteError or SkillFormPhase.Saving
            or SkillFormPhase.Deleting;
        if (editable)
        {
            var title = style.Input(Views.TextField(
                    state.Creating ? text("SkillAdd_TitlePlaceholder") : string.Empty,
                    state.Title, actions.TitleChanged))
                .WithAccessibilityLabel(text("SkillAdd_TitleLabel"))
                .WithAccessibilityIdentifier("native.skills.form.title");
            title.IsEnabled = !busy;
            var description = Views.TextEditor(state.Description,
                    actions.DescriptionChanged)
                .WithTextEditorScrollBackgroundHidden();
            if (state.Creating)
                description.WithTextEditorPlaceholder(
                        text("SkillAdd_DescriptionPlaceholder"))
                    .WithPlaceholderColor(style.Appearance.TertiaryText.Light,
                        style.Appearance.TertiaryText.Dark);
            var editor = style.Input(description)
                .WithMinimumHeight(DescriptionHeight(style))
                .WithAccessibilityLabel(text("SkillAdd_DescriptionLabel"))
                .WithAccessibilityIdentifier("native.skills.form.description");
            editor.IsEnabled = !busy;
            fields.Add(Card(style, "native.skills.form.fields",
            [
                Views.VStack(
                    style.Text(Views.Text(text("SkillAdd_TitleLabel")),
                        SourceTextRole.Body2, SourceTextTone.Secondary),
                    title),
                Views.VStack(
                    style.Text(Views.Text(text("SkillAdd_DescriptionLabel")),
                        SourceTextRole.Body2, SourceTextTone.Secondary),
                    editor)
            ]));

            if (!state.Creating && state.CreatedAt is { } created
                && state.UpdatedAt is { } updated)
            {
                var dates = Views.HStack(
                        Date(style, text("SkillEdit_Created"),
                            NativeResourceDateFormatter.DetailCreatedDate(
                                created, isSmartResource: false)),
                        Date(style, text("SkillEdit_Updated"),
                            NativeResourceDateFormatter.DetailUpdatedDate(updated)))
                    .WithAdaptiveStack(680)
                    .WithFlexibleWidth();
                dates.Spacing = SourceUiLayout.ItemGap;
                fields.Add(Card(style, "native.skills.form.details",
                [
                    style.Text(Views.Text(text("SkillEdit_DetailsTitle")),
                        SourceTextRole.Title3, sourceHeading: true),
                    dates
                ]));
            }

        }
        else if (state.Phase == SkillFormPhase.LoadError)
            fields.Add(style.SecondaryButton(text(NativeText.Retry), actions.Retry)
                .WithAccessibilityIdentifier("native.skills.form.retry"));

        var content = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.skills.form.scroll.content");
        content.Spacing = SourceUiLayout.FormGap;
        // A drag into the keyboard puts it away: the description's Return adds a line.
        var page = style.Page(Views.Scroll(content)
                .WithAccessibilityIdentifier("native.skills.form.scroll")
                .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively))
            .WithAccessibilityIdentifier(state.Creating
                ? "native.skills.add.screen" : "native.skills.edit.screen");
        if (!editable)
            return page;

        // Captain 2026-11-10 (the word editor's Save, for every editor): one Save, the
        // toolbar's last action at every width, as plain toolbar text, and no Save or Cancel
        // under the form (Back leaves without saving).
        var save = Views.Button(text("Common_Save"), actions.Save)
            .WithAccessibilityIdentifier("native.skills.form.save");
        // Not while a list action on the skills runs (it could undo the archive or delete).
        save.IsEnabled = !busy && !state.Busy;
        if (state.Creating)
            return Views.Toolbar(page, save);
        // Captain 2026-11-13: Save stays plain text, which the system draws apart from the
        // icons; the skill's actions are icons (Archive or Restore, then Delete, which asks
        // first). Each icon is
        // a titled Label, so a long press shows its name as the tab bar's do.
        var delete = Views.Button(Views.Label(text("Common_Delete"), "trash"), actions.RequestDelete)
            .WithAccessibilityIdentifier("native.skills.edit.delete.request");
        delete.IsEnabled = !busy && !state.Busy;
        if (actions.ToggleArchived is not { } toggle)
            return Views.Toolbar(page, delete, save);
        var archive = Views.Button(Views.Label(
                    text(state.Archived ? "Skills_Restore" : "Skills_Archive"),
                    state.Archived ? "arrow.uturn.backward" : "archivebox"),
                toggle)
            .WithAccessibilityIdentifier(state.Archived
                ? "native.skills.form.restore" : "native.skills.form.archive");
        archive.IsEnabled = !busy && !state.Busy;
        return Views.Toolbar(page, archive, delete, save);
    }

    internal static string Error(string? errorKey, Func<string, string> text)
    {
        var key = errorKey ?? NativeText.UnexpectedError;
        var message = text(key);
        return key is "SkillAdd_SaveFailed" or "SkillEdit_LoadFailed"
            or "SkillEdit_SaveFailed" or "SkillEdit_DeleteFailed"
            ? string.Format(CultureInfo.CurrentCulture, message,
                text(NativeText.UnexpectedError))
            : message;
    }

    private static SwiftView Date(
        SourceUiStyle style, string name, string value) =>
        Views.HStack(
                style.Text(Views.Text(name), SourceTextRole.Body2Strong),
                style.Text(Views.Text(value), SourceTextRole.Body2))
            .WithAccessibilityLabel($"{name}: {value}");

    private static SwiftView Card(
        SourceUiStyle style, string id, IEnumerable<SwiftView> children)
    {
        var card = Views.VStack(children.ToArray())
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        card.Spacing = SourceUiLayout.FormGap;
        return style.Card(card);
    }

    private static double DescriptionHeight(SourceUiStyle style)
    {
        var font = SourceUiTypography.For(SourceTextRole.Input,
            style.ViewportWidth, style.Appearance.Selection.FontScale,
            style.Appearance.Selection.ThemeId);
        return 6 * font.Size * font.LineHeight + 2 * SourceUiLayout.ItemGap;
    }
}
