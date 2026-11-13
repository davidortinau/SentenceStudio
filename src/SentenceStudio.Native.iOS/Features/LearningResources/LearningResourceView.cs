using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal static class LearningResourceView
{
    /// <summary>
    /// The Resource form as a native grouped Form (Captain 2026-11-13): status rows first, then
    /// Basic Info (label-beside rows and system pickers), the long texts as their own Sections,
    /// and the vocabulary (native rows, swipe to remove).
    /// </summary>
    internal static SwiftView Page(
        SourceUiStyle style,
        LearningResourceViewState state,
        bool creating,
        LearningResourceActions actions,
        LearningResourceInputs inputs,
        Func<string, string> text)
    {
        var unavailable = state.Phase is NativeResourceDetailPhase.Idle
            or NativeResourceDetailPhase.Loading or NativeResourceDetailPhase.NotFound
            or NativeResourceDetailPhase.LoadError or NativeResourceDetailPhase.Blocked;
        var busy = state.IsSaving || state.IsReloading || state.IsGenerating
            || state.IsImporting || state.IsQuickAddAttaching
            || state.Phase is NativeResourceDetailPhase.Saving
                or NativeResourceDetailPhase.Deleting;
        if (creating && state.IsSaving)
            return FormPage(style, [Views.Section(string.Empty,
                    Views.IndeterminateProgress(text("Common_Save"))
                        .WithAccessibilityIdentifier("native.resources.form.status"))],
                creating);
        var notices = new List<SwiftView>();
        if (!creating && state.IsSaving)
            notices.Add(Views.IndeterminateProgress(text("Common_Save"))
                .WithAccessibilityIdentifier("native.resources.form.saving"));
        if (state.Status is { Length: > 0 } status)
            notices.Add(style.Text(Views.Text(status), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.form.status"));
        if (state.ErrorKey is { } error
            && !(state.IsSmartResource
                && state.Phase == NativeResourceDetailPhase.ReadOnly
                && error == "ResourceEdit_AutoUpdated"))
            notices.Add(style.Text(Views.Text(FormatError(error, text)),
                    SourceTextRole.Body2, SourceTextTone.Danger)
                .WithAccessibilityIdentifier(state.Phase == NativeResourceDetailPhase.Conflict
                    ? NativeAccess.ResourceConflict : "native.resources.form.error"));
        if (unavailable)
        {
            if (state.Status is null && state.ErrorKey is null)
                notices.Add(state.Phase == NativeResourceDetailPhase.Loading
                    ? Views.IndeterminateProgress(text("Common_Loading"))
                        .WithAccessibilityIdentifier("native.resources.form.status")
                    : Views.Text(text("NativeResources_LoadError"))
                        .WithAccessibilityIdentifier("native.resources.form.status"));
            if (state.Phase == NativeResourceDetailPhase.LoadError)
                notices.Add(Views.Button(text(NativeText.Retry), actions.Retry)
                    .WithAccessibilityIdentifier("native.resources.form.retry"));
            notices.Add(Views.Button(text("Common_Cancel"), actions.Cancel)
                .WithAccessibilityIdentifier(NativeAccess.ResourceCancel));
            var unavailableForm = FormPage(style,
                [Views.Section(string.Empty, notices.ToArray())], creating);
            unavailableForm.IsEnabled = !busy;
            return unavailableForm;
        }

        if (state.IsSmartResource)
        {
            var smartNotice = Views.VStack(
                    Views.Label(text("ResourceEdit_SmartResource"), "sparkles")
                        .WithForegroundColor(style.Appearance.InfoAlertText.Light,
                            style.Appearance.InfoAlertText.Dark),
                    style.Text(Views.Text(text("ResourceEdit_AutoUpdated")),
                        SourceTextRole.Body2, SourceTextTone.Secondary))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier("native.resources.form.readonly");
            smartNotice.Spacing = 4;
            notices.Insert(0, smartNotice);
        }
        var sections = new List<SwiftView>();
        if (notices.Count > 0)
            sections.Add(Views.Section(string.Empty, notices.ToArray()));

        var readOnly = state.IsSmartResource;
        var basic = new List<SwiftView>
        {
            LearningResourceFieldsView.Field(text(creating
                    ? "ResourceAdd_TitleLabel" : "ResourceEdit_Title"),
                state.Title, NativeAccess.ResourceTitle,
                value => actions.Change(nameof(LearningResourceViewState.Title), value),
                readOnly, creating ? text("ResourceAdd_TitlePlaceholder") : null),
            LearningResourceFieldsView.Select(style, text(creating
                    ? "ResourceAdd_MediaTypeLabel" : "ResourceEdit_MediaType"),
                state.MediaType, Constants.MediaTypes,
                "native.resources.form.media.type",
                value => actions.Change(nameof(LearningResourceViewState.MediaType), value),
                readOnly),
            LearningResourceFieldsView.Select(style, text(creating
                    ? "ResourceAdd_LanguageLabel" : "ResourceEdit_Language"),
                state.Language, Constants.Languages,
                "native.resources.form.language",
                value => actions.Change(nameof(LearningResourceViewState.Language), value),
                readOnly),
        };
        // Captain 2026-11-12: a smart resource hides what it cannot have (media, transcript,
        // translation); nothing on it is editable.
        if (!creating && !readOnly)
            basic.Add(LearningResourceFieldsView.Field(text("ResourceEdit_MediaUrl"),
                state.MediaUrl, "native.resources.form.media.url",
                value => actions.Change(nameof(LearningResourceViewState.MediaUrl), value),
                readOnly: false, placeholder: "https://..."));
        basic.Add(LearningResourceFieldsView.Field(text(creating
                ? "ResourceAdd_TagsLabel" : "ResourceEdit_Tags"),
            state.Tags, NativeAccess.ResourceTags,
            value => actions.Change(nameof(LearningResourceViewState.Tags), value),
            readOnly, creating ? text("ResourceAdd_TagsPlaceholder") : null));
        if (!creating)
        {
            basic.Add(LearningResourceFieldsView.Value(text("ResourceEdit_Created"),
                DateFormatter.DetailCreatedDate(state.CreatedAt, state.IsSmartResource),
                "native.resources.form.created"));
            basic.Add(LearningResourceFieldsView.Value(text("ResourceEdit_Updated"),
                DateFormatter.DetailUpdatedDate(state.UpdatedAt),
                "native.resources.form.updated"));
        }
        sections.Add(Views.Section(text(creating
                    ? "ResourceAdd_BasicInfo" : "ResourceEdit_BasicInfo"), basic.ToArray()));
        sections.Add(LearningResourceFieldsView.Area(style, text(creating
                ? "ResourceAdd_DescriptionLabel" : "ResourceEdit_Description"),
            state.Description, NativeAccess.ResourceDescription,
            value => actions.Change(nameof(LearningResourceViewState.Description), value),
            readOnly, placeholder: creating ? text("ResourceAdd_DescriptionPlaceholder") : null));

        if (creating && state.MediaType != "Vocabulary List")
        {
            sections.Add(Views.Section(text("ResourceAdd_MediaContent"),
                    LearningResourceFieldsView.Field(text("ResourceAdd_MediaUrlLabel"),
                        state.MediaUrl, "native.resources.form.media.url",
                        value => actions.Change(nameof(LearningResourceViewState.MediaUrl), value),
                        readOnly: false, placeholder: "https://...")));
            sections.Add(LearningResourceFieldsView.Area(style, text("ResourceAdd_TranscriptLabel"),
                state.Transcript, "native.resources.form.transcript",
                value => actions.Change(nameof(LearningResourceViewState.Transcript), value),
                readOnly: false, rows: 5,
                placeholder: text("ResourceAdd_TranscriptPlaceholder")));
            sections.Add(LearningResourceFieldsView.Area(style, text("ResourceAdd_TranslationLabel"),
                state.Translation, "native.resources.form.translation",
                value => actions.Change(nameof(LearningResourceViewState.Translation), value),
                readOnly: false, rows: 5,
                placeholder: text("ResourceAdd_TranslationPlaceholder")));
        }
        if (!creating && !readOnly)
        {
            sections.Add(LearningResourceFieldsView.Area(style, text("ResourceEdit_Transcript"),
                state.Transcript, "native.resources.form.transcript",
                value => actions.Change(nameof(LearningResourceViewState.Transcript), value),
                readOnly: false, rows: 6));
            sections.Add(LearningResourceFieldsView.Area(style, text("ResourceEdit_Translation"),
                state.Translation, "native.resources.form.translation",
                value => actions.Change(nameof(LearningResourceViewState.Translation), value),
                readOnly: false, rows: 6));
        }

        LearningResourceVocabularyView.AddVocabulary(sections, state,
            creating, busy, actions, inputs, style, text);
        if (state.HasDraft && (state.Phase is NativeResourceDetailPhase.Conflict
                or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.ReadOnly
            || state.ErrorKey is "NativeResources_ReloadError"
                or "NativeResources_SaveInvalid"))
            sections.Add(Views.Section(string.Empty,
                Views.Button(text("NativeResources_ReloadDiscard"), actions.Reload)
                    .WithAccessibilityIdentifier(NativeAccess.ResourceReload)));
        var form = FormPage(style, sections, creating);
        form.IsEnabled = !busy;
        return form;
    }

    internal static SwiftView WithToolbar(
        SwiftView form, SourceUiStyle style, LearningResourceViewState state,
        bool creating, bool navigationBlocked, LearningResourceActions actions,
        Action<SwiftView> registerSave, Func<string, string> text)
    {
        var toolbar = new List<SwiftView>();
        var editable = !state.IsSmartResource
            && state.Phase is (NativeResourceDetailPhase.Ready
                or NativeResourceDetailPhase.Creating
                or NativeResourceDetailPhase.SaveError);
        if (editable)
        {
            // Captain 2026-11-10 (the word editor's Save, for every editor): one Save, the
            // toolbar's last action at every width, as plain toolbar text. Captain 2026-11-13
            // (the Skill editor, for every editor): Delete is a trash icon beside it, not in
            // a More menu; it still asks first.
            if (!creating)
                toolbar.Add(Views.Button(Views.Label(text("Common_Delete"), "trash"),
                        actions.RequestDelete)
                    .WithAccessibilityIdentifier("native.resources.form.delete.request"));
            var save = Views.Button(text("Common_Save"), actions.Save)
                .WithAccessibilityIdentifier(NativeAccess.ResourceSave);
            // The coordinator's UpdateSaveButtons sets whether it is enabled, now and as the
            // form changes (one enable rule).
            registerSave(save);
            toolbar.Add(save);
        }
        return Views.Toolbar(form, toolbar.ToArray());
    }

    private static SwiftView FormPage(
        SourceUiStyle style, IEnumerable<SwiftView> sections, bool creating) =>
        // A drag of the rows puts the keyboard away; dragging inside a text editor keeps it
        // (the editors' Return adds a line).
        // (A Section has no automation ID: SwiftUI gives it to each of the Section's rows.)
        style.GroupedForm(Views.Form(sections.ToArray())
                .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively)
                .WithAccessibilityIdentifier("native.resources.form.scroll"))
            .WithAccessibilityIdentifier(creating
                ? "native.resources.add.screen" : "native.resources.edit.screen");

    private static string FormatError(string key, Func<string, string> text) =>
        key is "ResourceEdit_DeleteFailed" or "FailedToLoadVocabularyData"
            ? string.Format(CultureInfo.CurrentCulture,
                text(key), text("Common_Error"))
            : text(key);
}
