using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

internal sealed record VocabularyEditActions(
    Action<string, string> Change,
    Action<string, bool> ToggleResource,
    Action Save, Action Cancel, Action Retry, Action PlayAudio,
    Action<SwiftView> RegisterSave,
    Action FindDuplicates, Action RequestDelete,
    VocabularyExampleActions Examples,
    Action<VocabularyEditSection, SwiftView>? RegisterSection = null);

/// <summary>Source ReferenceSentencesSection's actions (a sentence by its ID, a suggestion by its key).</summary>
internal sealed record VocabularyExampleActions(
    Action<VocabularyExampleField, string> ChangeNew, Action<SpeechRegister> SetNewRegister,
    Action Add, Action Generate,
    Action<string, VocabularyExampleField, string> ChangeSuggestion, Action<string> Keep,
    Action<string> Discard,
    Action<string> StartEdit, Action<VocabularyExampleField, string> ChangeEdit,
    Action<SpeechRegister> SetEditRegister, Action SaveEdit, Action CancelEdit,
    Action<string> ToggleCore, Action<string> ToggleFlag, Action<string> Delete,
    Action<string, SwiftView>? RegisterButton = null);

/// <summary>
/// The parts of the editor the coordinator changes in place: the Reference Sentences and
/// Resource Associations cards (each in its own slot, so the page keeps its scroll) and the
/// encoding label, which counts the sentences.
/// </summary>
internal enum VocabularyEditSection { Examples, Resources, EncodingLabel }

internal static partial class VocabularyWordEditView
{
    internal static SwiftView Page(
        VocabularyBrowserEdit state, SourceUiStyle style,
        Func<string, string> text, VocabularyEditActions actions)
    {
        var fields = new List<SwiftView>();
        if (state.Loading)
            fields.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.vocabulary.edit.status"));
        if (state.ErrorKey is { } error)
            fields.Add(style.Alert(
                    style.Text(Views.Text(text(error)), SourceTextRole.Body2),
                    success: false)
                .WithAccessibilityIdentifier("native.vocabulary.edit.error"));
        if (state.DeleteError is { } deleteError)
            fields.Add(style.Alert(
                    style.Text(Views.Text(deleteError), SourceTextRole.Body2),
                    success: false)
                .WithAccessibilityIdentifier("native.vocabulary.edit.delete.error"));
        if (state.Detail is null || state.Draft is null)
        {
            if (!state.Loading)
                fields.Add(Views.Button(text("Common_Retry"), actions.Retry)
                    .WithAccessibilityIdentifier("native.vocabulary.edit.retry"));
            fields.Add(Views.Button(text("Common_Cancel"), actions.Cancel)
                .WithAccessibilityIdentifier("native.vocabulary.edit.cancel"));
            return PageContent(fields, style);
        }

        var detail = state.Detail;
        var draft = state.Draft;
        if (state.Saving)
            fields.Add(Views.IndeterminateProgress(text("Common_Save"))
                .WithAccessibilityIdentifier("native.vocabulary.edit.saving"));

        // Source: the Vocabulary Terms header carries Find Duplicates for an existing word.
        var findDuplicates = style.OutlineButton(
                Views.Text(text("VocabWordEdit_FindDuplicates")), actions.FindDuplicates)
            .WithAccessibilityIdentifier("native.vocabulary.edit.duplicates");
        findDuplicates.IsEnabled = state.CanFindDuplicates;
        fields.Add(CardWithHeaderAction(style, "VocabWordEdit_VocabularyTerms", text,
            findDuplicates,
            TargetField(state, style, text, actions),
            TextField("VocabWordEdit_NativeLanguageTerm",
                "VocabWordEdit_NativePlaceholder", "native.vocabulary.edit.native",
                draft.NativeTerm, nameof(draft.NativeTerm), state, style, text,
                actions)));
        fields.Add(EncodingCard(detail, state, style, text, actions));
        // Source: Reference Sentences for an existing word (every word the editor opens).
        fields.Add(Section(VocabularyEditSection.Examples,
            ExamplesCard(state, style, text, actions.Examples), actions));
        if (draft.LexicalUnitType is LexicalUnitType.Phrase or LexicalUnitType.Sentence
            || detail.HasConstituentLinks)
        {
            var constituents = new List<SwiftView>();
            if (detail.HasConstituentLinks)
                constituents.Add(style.Text(Views.Text(
                        text("NativeVocabulary_ConstituentsLocked")),
                        SourceTextRole.Body2, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(
                        "native.vocabulary.edit.constituents.warning"));
            if (detail.Constituents.Count == 0)
                constituents.Add(style.Text(Views.Text(
                        text("NativeVocabulary_NoConstituents")),
                        SourceTextRole.Body2, SourceTextTone.Secondary));
            else
                constituents.AddRange(detail.Constituents.Select(constituent =>
                    style.Text(Views.Text(
                            $"{constituent.TargetTerm} · {constituent.NativeTerm}"),
                            SourceTextRole.Body2)
                        .WithAccessibilityIdentifier(
                            $"native.vocabulary.edit.constituent.{constituent.Id}")));
            fields.Add(Card(style, "NativeVocabulary_Constituents", text,
                constituents.ToArray()));
        }
        fields.Add(ProgressCard(detail, style, text));
        fields.Add(HistoryCard(detail, style, text));
        fields.Add(Section(VocabularyEditSection.Resources,
            ResourceCard(detail, state, style, text, actions), actions));
        // The More menu's Delete is disabled for a word the server would keep; this says why.
        if (VocabularyWordCommands.DeleteBlockedReason(detail, text) is { } deleteBlocked)
            fields.Add(style.Text(Views.Text(deleteBlocked), SourceTextRole.Small,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.delete.blocked"));
        // Captain 2026-11-10: one Save, the toolbar's primary action at every width, as plain
        // toolbar text (no filled button, no second Save in the More menu, none at the bottom).
        // Back leaves without saving, as the source's Cancel did.
        var save = Views.Button(text("Common_Save"), actions.Save)
            .WithAccessibilityIdentifier("native.vocabulary.edit.save");
        save.IsEnabled = state.CanSave;
        // VocabularyCoordinator.UpdateSaveButtons follows typing (one enable rule).
        actions.RegisterSave(save);
        var menuDuplicates = Views.Button(text("VocabWordEdit_FindDuplicates"),
                actions.FindDuplicates)
            .WithAccessibilityIdentifier("native.vocabulary.edit.duplicates.menu");
        menuDuplicates.IsEnabled = state.CanFindDuplicates;
        var menuDelete = Views.Button(text("Common_Delete"), actions.RequestDelete)
            .WithAccessibilityIdentifier("native.vocabulary.edit.delete.menu");
        menuDelete.IsEnabled = state.CanDelete;
        var more = Views.Menu(text("More"), menuDuplicates, menuDelete)
            .WithAccessibilityIdentifier("native.vocabulary.edit.more");
        // Save last: the trailing place of a confirming action.
        return Views.Toolbar(PageContent(fields, style), more, save)
            .WithAccessibilityIdentifier("native.vocabulary.edit.page");
    }

    /// <summary>A card in its own slot, which the coordinator refills when only that card changed.</summary>
    private static SwiftView Section(
        VocabularyEditSection section, SwiftView card, VocabularyEditActions actions)
    {
        var slot = Views.VStack(card).WithFlexibleWidth()
            .WithAccessibilityIdentifier($"native.vocabulary.edit.slot.{section}");
        actions.RegisterSection?.Invoke(section, slot);
        return slot;
    }

    private static SwiftView PageContent(
        List<SwiftView> fields, SourceUiStyle style)
    {
        var content = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.edit.scroll.content");
        content.Spacing = SourceUiLayout.PageGap;
        // A drag into the keyboard puts it away: the mnemonic's Return adds a line.
        return style.Page(Views.Scroll(content)
            .WithAccessibilityIdentifier("native.vocabulary.edit.scroll")
            .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively));
    }

    private static SwiftView Card(
        SourceUiStyle style, string titleKey, Func<string, string> text,
        params SwiftView[] content) =>
        CardWithHeaderAction(style, titleKey, text, headerAction: null, content);

    // Not a Card overload: with a SwiftView? parameter first, Card(style, key, text, row, ...)
    // would bind its first row as the header action.
    private static SwiftView CardWithHeaderAction(
        SourceUiStyle style, string titleKey, Func<string, string> text,
        SwiftView? headerAction, params SwiftView[] content)
    {
        var title = style.Text(Views.Text(text(titleKey)),
                SourceTextRole.Title3, sourceHeading: true)
            .WithAccessibilityIdentifier($"native.vocabulary.edit.section.{titleKey}");
        var heading = title;
        if (headerAction is not null)
        {
            heading = Views.HStack(title.WithFlexibleWidth(), headerAction)
                .WithAdaptiveStack(420)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(
                    $"native.vocabulary.edit.section.{titleKey}.header");
            heading.Spacing = SourceUiLayout.ItemGap;
        }
        var items = Views.VStack([heading, ..content])
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth();
        items.Spacing = SourceUiLayout.FormGap;
        return style.Card(items);
    }

    private static SwiftView TargetField(
        VocabularyBrowserEdit state, SourceUiStyle style,
        Func<string, string> text, VocabularyEditActions actions)
    {
        var draft = state.Draft!;
        var field = style.TextInput(Views.TextField(
                text("VocabWordEdit_TargetPlaceholder"),
                draft.TargetTerm,
                value => actions.Change(nameof(draft.TargetTerm), value)))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.edit.target");
        field.IsEnabled = !state.Saving;
        var play = Views.Button(Views.Image(
                    state.PlayingAudio ? "speaker.wave.3.fill" : "speaker.wave.2"),
                actions.PlayAudio)
            .WithAccessibilityLabel(text("VocabWordEdit_PlayAudio"))
            .WithAccessibilityIdentifier("native.vocabulary.edit.audio");
        play.IsEnabled = !state.PlayingAudio && !state.Saving
            && !string.IsNullOrWhiteSpace(draft.TargetTerm);
        var row = Views.HStack(field, play).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap;
        return Views.VStack(
                style.Text(Views.Text(text("VocabWordEdit_TargetLanguageTerm")),
                    SourceTextRole.Body2, SourceTextTone.Secondary),
                row)
            .WithAccessibilityIdentifier("native.vocabulary.edit.target.field");
    }

    private static SwiftView TextField(
        string titleKey, string placeholderKey, string id, string value,
        string property, VocabularyBrowserEdit state, SourceUiStyle style,
        Func<string, string> text, VocabularyEditActions actions)
    {
        var field = style.TextInput(Views.TextField(
            text(placeholderKey), value,
            changed => actions.Change(property, changed)));
        field.IsEnabled = !state.Saving;
        return Views.VStack(
                style.Text(Views.Text(text(titleKey)),
                    SourceTextRole.Body2, SourceTextTone.Secondary),
                field.WithAccessibilityIdentifier(id))
            .WithFlexibleWidth();
    }

    private static SwiftView EncodingCard(
        NativeVocabularyDetail detail, VocabularyBrowserEdit state,
        SourceUiStyle style, Func<string, string> text,
        VocabularyEditActions actions)
    {
        var draft = state.Draft!;
        var encoding = style.Text(Views.Text(EncodingText(detail.Word.EncodingLabel, text)),
                SourceTextRole.Body2)
            .WithAccessibilityIdentifier("native.vocabulary.edit.encoding");
        // The coordinator updates it in place as sentences are added or removed.
        actions.RegisterSection?.Invoke(VocabularyEditSection.EncodingLabel, encoding);
        var fields = new List<SwiftView> {
            encoding,
            TextField("VocabWordEdit_Lemma",
                "VocabWordEdit_LemmaPlaceholder", "native.vocabulary.edit.lemma",
                draft.Lemma ?? string.Empty, nameof(draft.Lemma),
                state, style, text, actions)
        };
        var languages = new[] { string.Empty }.Concat(Constants.Languages)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedLanguage = Array.FindIndex(languages, value =>
            value.Equals(draft.Language ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
        if (selectedLanguage < 0)
            selectedLanguage = 0;
        var language = style.Input(Views.Picker(
                text("VocabWordEdit_Language"),
                languages.Select(value => value.Length == 0
                        ? text("VocabWordEdit_NotSet") : value).ToArray(),
                selectedLanguage,
                index => actions.Change(nameof(draft.Language),
                    languages[Math.Clamp(index, 0, languages.Length - 1)]))
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithFlexibleWidth())
            .WithAccessibilityIdentifier("native.vocabulary.edit.language");
        language.IsEnabled = !state.Saving;
        fields.Add(Views.VStack(
            style.Text(Views.Text(text("VocabWordEdit_Language")),
                SourceTextRole.Body2, SourceTextTone.Secondary),
            language));

        var types = draft.LexicalUnitType is LexicalUnitType.Unknown
                or LexicalUnitType.Idiom
            ? new[] { draft.LexicalUnitType, LexicalUnitType.Word,
                LexicalUnitType.Phrase, LexicalUnitType.Sentence }
            : new[] { LexicalUnitType.Word, LexicalUnitType.Phrase,
                LexicalUnitType.Sentence };
        var type = style.Input(Views.Picker(
                text("NativeResources_TypeFilter"),
                types.Select(value => text(value switch {
                    LexicalUnitType.Phrase => "Vocabulary_FilterTypePhrase",
                    LexicalUnitType.Sentence => "Vocabulary_FilterTypeSentence",
                    LexicalUnitType.Unknown => "VocabWordEdit_Unknown",
                    LexicalUnitType.Idiom => "NativeResources_LexicalIdiom",
                    _ => "Vocabulary_FilterTypeWord"
                })).ToArray(),
                Array.IndexOf(types, draft.LexicalUnitType),
                index => actions.Change(nameof(draft.LexicalUnitType),
                    types[Math.Clamp(index, 0, types.Length - 1)].ToString()))
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithFlexibleWidth())
            .WithAccessibilityIdentifier("native.vocabulary.edit.type");
        type.IsEnabled = !state.Saving && !detail.HasConstituentLinks;
        fields.Add(Views.VStack(
            style.Text(Views.Text(text("NativeResources_TypeFilter")),
                SourceTextRole.Body2, SourceTextTone.Secondary),
            type));
        fields.Add(TextField("VocabWordEdit_Tags",
            "VocabWordEdit_TagsPlaceholder", "native.vocabulary.edit.tags",
            draft.Tags ?? string.Empty, nameof(draft.Tags), state, style,
            text, actions));
        var mnemonic = Views.TextEditor(draft.MnemonicText ?? string.Empty,
                state.Saving ? null
                    : value => actions.Change(nameof(draft.MnemonicText), value))
            .WithTextEditorScrollBackgroundHidden()
            .WithMinimumHeight(110);
        mnemonic.IsEnabled = !state.Saving;
        fields.Add(Views.VStack(
            style.Text(Views.Text(text("VocabWordEdit_MnemonicStory")),
                SourceTextRole.Body2, SourceTextTone.Secondary),
            style.Input(mnemonic)
                .WithAccessibilityIdentifier("native.vocabulary.edit.mnemonic")));
        fields.Add(TextField("VocabWordEdit_MnemonicImageUrl",
            "VocabWordEdit_MnemonicImageUrl",
            "native.vocabulary.edit.image.url", draft.MnemonicImageUri ?? string.Empty,
            nameof(draft.MnemonicImageUri), state, style, text, actions));
        if (state.Photo is { } photo)
            fields.Add(Views.ImageFromBytes(
                    photo.Bytes, text("VocabWordEdit_MnemonicAlt"), 120)
                .WithAccessibilityIdentifier("native.vocabulary.edit.image.preview"));
        return Card(style, "VocabWordEdit_EncodingMemoryAids",
            text, fields.ToArray());
    }

    /// <summary>"Encoding strength" and the word's label (Basic, Good or Strong).</summary>
    internal static string EncodingText(string label, Func<string, string> text) =>
        $"{text("VocabWordEdit_EncodingStrength")} "
        + text(VocabularyExampleText.EncodingKey(label));

    private static SwiftView ProgressCard(
        NativeVocabularyDetail detail, SourceUiStyle style,
        Func<string, string> text)
    {
        var progress = detail.Progress;
        var status = text(detail.Word.IsKnown ? "VocabWordEdit_Known"
            : detail.Word.Status switch {
            LearningStatus.Known => "VocabWordEdit_Known",
            LearningStatus.Familiar => "VocabWordEdit_Familiar",
            LearningStatus.Learning => "VocabWordEdit_Learning",
            _ => "VocabWordEdit_Unknown"
        });
        // NextReviewDate is a UTC instant; "due now / tomorrow" is the learner's local day.
        var next = progress?.NextReviewDate?.ToLocalTime() is not { } review
            ? text("VocabWordEdit_NotScheduled")
            : review.Date <= DateTime.Now.Date
                ? text("VocabWordEdit_DueNow")
                : review.Date == DateTime.Now.Date.AddDays(1)
                    ? text("VocabWordEdit_Tomorrow")
                    : review.Date <= DateTime.Now.Date.AddDays(7)
                        ? string.Format(CultureInfo.CurrentCulture,
                            text("VocabWordEdit_DaysAway"),
                            (review.Date - DateTime.Now.Date).Days)
                        : review.ToString("MMM d", CultureInfo.CurrentCulture);
        var summary = progress is null
            ? text("VocabWordEdit_StartPracticing")
            : string.Join("\n", [
                $"{text("NativeVocabQuiz_Details_CurrentStreak")}: "
                    + progress.CurrentStreak.ToString("F1", CultureInfo.CurrentCulture),
                $"{text("NativeVocabQuiz_Details_ProductionInStreak")}: "
                    + $"{progress.ProductionInStreak}/2",
                $"{text("NativeVocabQuiz_Details_MasteryScore")}: "
                    + progress.MasteryScore.ToString("P0", CultureInfo.CurrentCulture)
            ]);
        return Card(style, "VocabWordEdit_LearningProgress", text,
            Value("VocabWordEdit_Status", detail.Word.IsKnown
                ? $"{status} · {text("VocabWordEdit_EarnedThrough")}" : status,
                "native.vocabulary.edit.progress.status", style, text),
            Value("VocabWordEdit_Details", summary,
                "native.vocabulary.edit.progress.details", style, text),
            Value("VocabWordEdit_NextReview", next,
                "native.vocabulary.edit.progress.review", style, text));
    }

    private static SwiftView HistoryCard(
        NativeVocabularyDetail detail, SourceUiStyle style,
        Func<string, string> text)
    {
        if (detail.History.Count == 0)
            return Card(style, "VocabWordEdit_HistoryTitle", text,
                style.Text(Views.Text(text("VocabWordEdit_HistoryEmpty")),
                        SourceTextRole.Body2, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(
                        "native.vocabulary.edit.history.empty"));
        var attempts = detail.History.Count(item => !item.IsPassive);
        var exposures = detail.History.Count - attempts;
        var items = new List<SwiftView> {
            style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    text("VocabWordEdit_HistorySummary"), attempts, exposures)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.history.count")
        };
        items.AddRange(detail.History.Select(item =>
            style.Text(Views.Text(
                    $"{text(item.IsPassive ? "VocabWordEdit_HistoryExposure"
                        : item.WasCorrect ? "VocabWordEdit_HistoryCorrect"
                            : "VocabWordEdit_HistoryIncorrect")} · "
                    + $"{(item.Activity.Length == 0
                        ? text("VocabWordEdit_HistoryPractice") : item.Activity)} "
                    + $"· {item.LearnedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture)}"
                    + (string.IsNullOrWhiteSpace(item.ResourceTitle)
                        ? string.Empty : $" · {item.ResourceTitle}")),
                    SourceTextRole.Body2,
                    item.IsPassive ? SourceTextTone.Secondary : SourceTextTone.Body)
                .WithAccessibilityIdentifier(
                    $"native.vocabulary.edit.history.{item.Id}")));
        return Card(style, "VocabWordEdit_HistoryTitle", text, items.ToArray());
    }

    /// <summary>
    /// Captain 2026-11-10: the word's own resources only, each with Remove, and Add to
    /// Resource listing the others (the source lists every resource with a checkbox). A smart
    /// resource fills itself, so it is shown but not removed or offered. Changes wait for Save.
    /// </summary>
    internal static SwiftView ResourceCard(
        NativeVocabularyDetail detail, VocabularyBrowserEdit state,
        SourceUiStyle style, Func<string, string> text,
        VocabularyEditActions actions)
    {
        var selected = state.SelectedResources;
        var fields = new List<SwiftView> {
            style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text("VocabWordEdit_SelectedCount"), selected.Count)),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.resources.count")
        };
        if (detail.AvailableResources.Count == 0)
            fields.Add(style.Text(Views.Text(text("VocabWordEdit_NoResources")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.resources.none"));
        else if (selected.Count == 0)
            fields.Add(style.Text(Views.Text(text("VocabWordEdit_NoResourcesSelected")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.resources.empty"));
        fields.AddRange(selected.Select(resource =>
        {
            var title = style.Text(Views.Text(resource.Title), SourceTextRole.Body1)
                .WithFlexibleWidth();
            SwiftView trailing;
            if (resource.IsSmartResource)
                trailing = style.Text(Views.Text(text("SmartResource")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary);
            else
            {
                trailing = Views.Button(Views.Image("minus.circle"),
                        () => actions.ToggleResource(resource.Id, false))
                    .WithAccessibilityLabel(string.Format(CultureInfo.CurrentCulture,
                        text("VocabWordEdit_RemoveFromResource"), resource.Title))
                    .WithAccessibilityIdentifier(
                        $"native.vocabulary.edit.resource.{resource.Id}.remove");
                trailing.IsEnabled = !state.Saving;
            }
            var row = Views.HStack(title, trailing).WithFlexibleWidth()
                .WithAccessibilityIdentifier($"native.vocabulary.edit.resource.{resource.Id}");
            row.Spacing = SourceUiLayout.ItemGap;
            return row;
        }));
        var addable = state.AddableResources
            .Select(resource => Views.Button(resource.Title,
                    () => actions.ToggleResource(resource.Id, true))
                .WithAccessibilityIdentifier(
                    $"native.vocabulary.edit.resources.add.{resource.Id}"))
            .ToArray();
        if (addable.Length > 0)
        {
            var label = Views.HStack(Views.Image("plus.circle"),
                Views.Text(text("VocabWordEdit_AddToResource")));
            label.Spacing = 6;
            var add = Views.Menu(label, addable)
                .WithAccessibilityIdentifier("native.vocabulary.edit.resources.add");
            add.IsEnabled = !state.Saving;
            fields.Add(add);
        }
        return Card(style, "VocabWordEdit_ResourceAssociations", text,
            fields.ToArray());
    }

    private static SwiftView Value(
        string key, string value, string id,
        SourceUiStyle style, Func<string, string> text) =>
        Views.VStack(
            style.Text(Views.Text(text(key)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary),
            style.Text(Views.Text(value), SourceTextRole.Body2)
                .WithAccessibilityIdentifier(id))
            .WithAccessibilityIdentifier($"{id}.row");
}
