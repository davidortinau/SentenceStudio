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
        // Captain 2026-11-13: the native form pattern (departure 84), as the Resource form.
        var notices = new List<SwiftView>();
        var busy = state.Phase is SkillFormPhase.Saving or SkillFormPhase.Deleting;
        if (state.Phase is SkillFormPhase.Loading or SkillFormPhase.Saving
            or SkillFormPhase.Deleting)
            notices.Add(Views.IndeterminateProgress(text(state.Phase switch
                {
                    SkillFormPhase.Loading => "Common_Loading",
                    SkillFormPhase.Deleting => "Common_Delete",
                    _ => "Common_Save"
                }))
                .WithAccessibilityIdentifier("native.skills.form.progress"));
        if (state.StatusKey is { } status && state.ErrorKey is null)
            notices.Add(NativeForm.Notice(style, text(status), "native.skills.form.status",
                error: false));
        if (state.ErrorKey is { } error)
            notices.Add(NativeForm.Notice(style, Error(error, text), "native.skills.form.error",
                error: true));

        var editable = state.Phase is SkillFormPhase.Creating
            or SkillFormPhase.Editing or SkillFormPhase.SaveError
            or SkillFormPhase.DeleteError or SkillFormPhase.Saving
            or SkillFormPhase.Deleting;
        if (state.Phase == SkillFormPhase.LoadError)
            notices.Add(NativeForm.Action(style, Views.Button(text(NativeText.Retry), actions.Retry)
                .WithAccessibilityIdentifier("native.skills.form.retry")));
        var sections = new List<SwiftView>();
        if (notices.Count > 0)
            sections.Add(Views.Section(string.Empty, notices.ToArray()));
        if (editable)
        {
            var title = NativeForm.Field(text("SkillAdd_TitleLabel"), state.Title,
                "native.skills.form.title", actions.TitleChanged,
                text(state.Creating ? "SkillAdd_TitlePlaceholder" : "Common_Required"));
            // While a save or delete runs the inputs wait; the Form still scrolls to its notice.
            title.IsEnabled = !busy;
            var basic = new List<SwiftView> { title };
            if (!state.Creating && state.CreatedAt is { } created
                && state.UpdatedAt is { } updated)
            {
                basic.Add(NativeForm.Value(text("SkillEdit_Created"),
                    NativeResourceDateFormatter.DetailCreatedDate(created, isSmartResource: false),
                    "native.skills.form.created"));
                basic.Add(NativeForm.Value(text("SkillEdit_Updated"),
                    NativeResourceDateFormatter.DetailUpdatedDate(updated),
                    "native.skills.form.updated"));
            }
            sections.Add(Views.Section(string.Empty, basic.ToArray()));
            var description = NativeForm.TextSection(style, text("SkillAdd_DescriptionLabel"),
                state.Description, "native.skills.form.description",
                actions.DescriptionChanged, rows: 6,
                placeholder: state.Creating ? text("SkillAdd_DescriptionPlaceholder") : null);
            description.IsEnabled = !busy;
            sections.Add(description);
        }

        var form = NativeForm.Page(style, sections, "native.skills.form.scroll",
            state.Creating ? "native.skills.add.screen" : "native.skills.edit.screen");
        var page = form;
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
        // first). Each icon is a titled Label: its name for VoiceOver and, at the larger
        // accessibility text sizes, the system's long-press viewer.
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
}
