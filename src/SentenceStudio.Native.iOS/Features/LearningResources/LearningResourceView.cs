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
            return FormPage(style,
                [Views.IndeterminateProgress(text("Common_Save"))
                    .WithAccessibilityIdentifier("native.resources.form.status")],
                creating);
        var fields = new List<SwiftView>();
        if (!creating && state.IsSaving)
            fields.Add(Views.IndeterminateProgress(text("Common_Save"))
                .WithAccessibilityIdentifier("native.resources.form.saving"));
        if (state.Status is { Length: > 0 } status)
            fields.Add(style.Text(Views.Text(status), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.form.status"));
        if (state.ErrorKey is { } error
            && !(state.IsSmartResource
                && state.Phase == NativeResourceDetailPhase.ReadOnly
                && error == "ResourceEdit_AutoUpdated"))
            fields.Add(style.Text(Views.Text(FormatError(error, text)),
                    SourceTextRole.Body2, SourceTextTone.Danger)
                .WithAccessibilityIdentifier(state.Phase == NativeResourceDetailPhase.Conflict
                    ? NativeAccess.ResourceConflict : "native.resources.form.error"));
        if (unavailable)
        {
            if (state.Status is null && state.ErrorKey is null)
                fields.Add(state.Phase == NativeResourceDetailPhase.Loading
                    ? Views.IndeterminateProgress(text("Common_Loading"))
                        .WithAccessibilityIdentifier("native.resources.form.status")
                    : style.Text(Views.Text(text("NativeResources_LoadError")),
                        SourceTextRole.Body1)
                        .WithAccessibilityIdentifier("native.resources.form.status"));
            if (state.Phase == NativeResourceDetailPhase.LoadError)
                fields.Add(Views.Button(text(NativeText.Retry), actions.Retry)
                    .WithAccessibilityIdentifier("native.resources.form.retry"));
            fields.Add(Views.Button(text("Common_Cancel"), actions.Cancel)
                .WithAccessibilityIdentifier(NativeAccess.ResourceCancel));
            var unavailableForm = FormPage(style, fields, creating);
            unavailableForm.IsEnabled = !busy;
            return unavailableForm;
        }

        if (state.IsSmartResource)
        {
            var smartNotice = Views.HStack(
                    Views.Image("sparkles")
                        .WithForegroundColor(style.Appearance.InfoAlertText.Light,
                            style.Appearance.InfoAlertText.Dark),
                    style.Text(Views.Text(text("ResourceEdit_SmartResource")),
                            SourceTextRole.Body2Strong)
                        .WithForegroundColor(style.Appearance.InfoAlertText.Light,
                            style.Appearance.InfoAlertText.Dark),
                    style.Text(Views.Text(text("ResourceEdit_AutoUpdated")),
                        SourceTextRole.Body2, SourceTextTone.Secondary))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier("native.resources.form.readonly");
            smartNotice.Spacing = SourceUiLayout.ItemGap;
            fields.Add(style.InfoAlert(smartNotice));
        }
        var basic = new List<SwiftView>();
        basic.Add(style.Text(Views.Text(text(creating
                ? "ResourceAdd_BasicInfo" : "ResourceEdit_BasicInfo")),
                SourceTextRole.Title3, sourceHeading: true)
            .WithAccessibilityIdentifier("native.resources.form.basic.heading"));
        basic.Add(LearningResourceFieldsView.Field(style, text(creating
                    ? "ResourceAdd_TitleLabel" : "ResourceEdit_Title"),
                state.Title, NativeAccess.ResourceTitle,
                value => actions.Change(nameof(LearningResourceViewState.Title), value),
                state.IsSmartResource,
                creating ? text("ResourceAdd_TitlePlaceholder") : null));
        basic.Add(LearningResourceFieldsView.Area(style, text(creating
                    ? "ResourceAdd_DescriptionLabel" : "ResourceEdit_Description"),
                state.Description, NativeAccess.ResourceDescription,
                value => actions.Change(nameof(LearningResourceViewState.Description), value),
                state.IsSmartResource, placeholder: creating
                    ? text("ResourceAdd_DescriptionPlaceholder") : null));
        var typeAndLanguage = Views.HStack(
                LearningResourceFieldsView.Select(style, text(creating
                        ? "ResourceAdd_MediaTypeLabel" : "ResourceEdit_MediaType"),
                    state.MediaType, Constants.MediaTypes,
                    "native.resources.form.media.type",
                    value => actions.Change(nameof(LearningResourceViewState.MediaType), value),
                    state.IsSmartResource,
                    creating ? null : "native.resources.edit.media-type"),
                LearningResourceFieldsView.Select(style, text(creating
                        ? "ResourceAdd_LanguageLabel" : "ResourceEdit_Language"),
                    state.Language, Constants.Languages,
                    "native.resources.form.language",
                    value => actions.Change(nameof(LearningResourceViewState.Language), value),
                    state.IsSmartResource,
                    creating ? null : "native.resources.edit.language"))
            .WithAdaptiveStack(680)
            .WithFlexibleWidth();
        typeAndLanguage.Spacing = SourceUiLayout.FormGap;
        basic.Add(typeAndLanguage);
        if (!creating)
        {
            basic.Add(LearningResourceFieldsView.Field(style, text("ResourceEdit_MediaUrl"),
                state.MediaUrl, "native.resources.form.media.url",
                value => actions.Change(nameof(LearningResourceViewState.MediaUrl), value),
                state.IsSmartResource));
        }
        basic.Add(LearningResourceFieldsView.Field(style, text(creating
                    ? "ResourceAdd_TagsLabel" : "ResourceEdit_Tags"),
                state.Tags, NativeAccess.ResourceTags,
                value => actions.Change(nameof(LearningResourceViewState.Tags), value),
                state.IsSmartResource,
                creating ? text("ResourceAdd_TagsPlaceholder") : null));
        if (!creating)
        {
            var created = Views.HStack(
                    style.Text(Views.Text(text("ResourceEdit_Created")),
                        SourceTextRole.Body2Strong, SourceTextTone.Secondary),
                    style.Text(Views.Text(DateFormatter.DetailCreatedDate(
                            state.CreatedAt, state.IsSmartResource)),
                        SourceTextRole.Body2))
                .WithAccessibilityIdentifier("native.resources.form.created");
            var updated = Views.HStack(
                    style.Text(Views.Text(text("ResourceEdit_Updated")),
                        SourceTextRole.Body2Strong, SourceTextTone.Secondary),
                    style.Text(Views.Text(DateFormatter.DetailUpdatedDate(
                            state.UpdatedAt)),
                        SourceTextRole.Body2))
                .WithAccessibilityIdentifier("native.resources.form.updated");
            var dates = Views.HStack(created, updated)
                .WithAdaptiveStack(680)
                .WithFlexibleWidth();
            dates.Spacing = SourceUiLayout.ItemGap;
            basic.Add(dates);
        }
        fields.Add(CardSection(style, "native.resources.form.basic.card", basic));
        if (creating && state.MediaType != "Vocabulary List")
        {
            var media = new List<SwiftView>
            {
                style.Text(Views.Text(text("ResourceAdd_MediaContent")),
                    SourceTextRole.Title3, sourceHeading: true),
                LearningResourceFieldsView.Field(style, text("ResourceAdd_MediaUrlLabel"),
                state.MediaUrl, "native.resources.form.media.url",
                value => actions.Change(nameof(LearningResourceViewState.MediaUrl), value),
                readOnly: false, placeholder: "https://..."),
                LearningResourceFieldsView.Area(style, text("ResourceAdd_TranscriptLabel"),
                    state.Transcript, "native.resources.form.transcript",
                    value => actions.Change(nameof(LearningResourceViewState.Transcript), value),
                    readOnly: false, rows: 5,
                    placeholder: text("ResourceAdd_TranscriptPlaceholder")),
                LearningResourceFieldsView.Area(style, text("ResourceAdd_TranslationLabel"),
                    state.Translation, "native.resources.form.translation",
                    value => actions.Change(nameof(LearningResourceViewState.Translation), value),
                    readOnly: false, rows: 5,
                    placeholder: text("ResourceAdd_TranslationPlaceholder")),
            };
            fields.Add(CardSection(style, "native.resources.form.media.card", media));
        }
        if (!creating)
        {
            fields.Add(CardSection(style, "native.resources.form.transcript.card", [
                LearningResourceFieldsView.Area(style, text("ResourceEdit_Transcript"),
                    state.Transcript, "native.resources.form.transcript",
                    value => actions.Change(nameof(LearningResourceViewState.Transcript), value),
                    state.IsSmartResource, heading: true, rows: 6)]));
            fields.Add(CardSection(style, "native.resources.form.translation.card", [
                LearningResourceFieldsView.Area(style, text("ResourceEdit_Translation"),
                    state.Translation, "native.resources.form.translation",
                    value => actions.Change(nameof(LearningResourceViewState.Translation), value),
                    state.IsSmartResource, heading: true, rows: 6)]));
        }

        var vocabulary = new List<SwiftView>();
        LearningResourceVocabularyView.AddVocabulary(vocabulary, state,
            creating, busy, actions, inputs, style, text);
        var vocabularyCard = CardSection(style,
            "native.resources.form.vocabulary.card", vocabulary);
        inputs.RegisterVocabularySection(vocabularyCard);
        fields.Add(vocabularyCard);
        if (creating)
        {
            var save = style.FullWidthPrimaryButton(
                    text("ResourceAdd_SaveResource"), actions.Save)
                .WithAccessibilityIdentifier("native.resources.add.save.bottom");
            inputs.RegisterSave(save);
            fields.Add(save);
        }
        if (state.HasDraft && (state.Phase is NativeResourceDetailPhase.Conflict
                or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.ReadOnly
            || state.ErrorKey is "NativeResources_ReloadError"
                or "NativeResources_SaveInvalid"))
            fields.Add(Views.Button(text("NativeResources_ReloadDiscard"), actions.Reload)
                .WithAccessibilityIdentifier(NativeAccess.ResourceReload));
        var form = FormPage(style, fields, creating);
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
        if (editable && style.ViewportWidth >= 768)
        {
            var save = style.PrimaryButton(text("Common_Save"), actions.Save)
                .WithAccessibilityIdentifier(NativeAccess.ResourceSave);
            registerSave(save);
            toolbar.Add(save);
        }
        if (editable)
        {
            var more = new List<SwiftView>();
            var menuSave = Views.Button(text("Common_Save"), actions.Save)
                .WithAccessibilityIdentifier(NativeAccess.ResourceSave);
            menuSave.IsEnabled = !navigationBlocked && state.CanSave;
            more.Add(menuSave);
            if (!creating && !state.IsSmartResource)
                more.Add(Views.Button(text("Common_Delete"), actions.RequestDelete)
                    .WithAccessibilityIdentifier("native.resources.form.delete.menu"));
            toolbar.Add(Views.Menu(text("More"), more.ToArray())
                .WithAccessibilityIdentifier("native.resources.form.more"));
        }
        return Views.Toolbar(form, toolbar.ToArray());
    }

    private static SwiftView FormPage(
        SourceUiStyle style, IEnumerable<SwiftView> fields, bool creating)
    {
        var content = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.resources.form.scroll.content");
        content.Spacing = SourceUiLayout.FormGap;
        return style.Page(Views.Scroll(content)
                .WithAccessibilityIdentifier("native.resources.form.scroll"))
            .WithAccessibilityIdentifier(creating
                ? "native.resources.add.screen" : "native.resources.edit.screen");
    }

    private static SwiftView CardSection(
        SourceUiStyle style, string id, IEnumerable<SwiftView> children)
    {
        var card = Views.VStack(children.ToArray())
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        card.Spacing = SourceUiLayout.FormGap;
        return style.Card(card);
    }

    private static string FormatError(string key, Func<string, string> text) =>
        key is "ResourceEdit_DeleteFailed" or "FailedToLoadVocabularyData"
            ? string.Format(CultureInfo.CurrentCulture,
                text(key), text("Common_Error"))
            : text(key);
}
