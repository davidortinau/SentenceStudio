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
    Action<SwiftView> RegisterResourceCount);

internal static class VocabularyWordEditView
{
    internal static SwiftView Page(
        VocabularyEditPresentation state, SourceUiStyle style,
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
        var readOnly = detail.IsSharedWithAnotherOwner;
        if (readOnly)
            fields.Add(style.InfoAlert(style.Text(
                    Views.Text(text("NativeVocabulary_SharedReadOnly")),
                    SourceTextRole.Body2))
                .WithAccessibilityIdentifier("native.vocabulary.edit.readonly"));
        if (state.Saving)
            fields.Add(Views.IndeterminateProgress(text("Common_Save"))
                .WithAccessibilityIdentifier("native.vocabulary.edit.saving"));

        fields.Add(Card(style, "VocabWordEdit_VocabularyTerms", text,
            TargetField(state, style, text, actions, readOnly),
            TextField("VocabWordEdit_NativeLanguageTerm",
                "VocabWordEdit_NativePlaceholder", "native.vocabulary.edit.native",
                draft.NativeTerm, nameof(draft.NativeTerm), state, style, text,
                actions, readOnly)));
        fields.Add(EncodingCard(detail, state, style, text, actions, readOnly));
        if (detail.Examples.Count > 0)
        {
            var examples = detail.Examples.Select(example =>
                Views.VStack(
                    style.Text(Views.Text(example.TargetSentence),
                            SourceTextRole.Body1)
                        .WithAccessibilityIdentifier(
                            $"native.vocabulary.edit.example.{example.Id}.target"),
                    style.Text(Views.Text(example.NativeSentence ?? string.Empty),
                            SourceTextRole.Body2, SourceTextTone.Secondary)
                        .WithAccessibilityIdentifier(
                            $"native.vocabulary.edit.example.{example.Id}.native")))
                .ToArray();
            fields.Add(Card(style, "NativeVocabulary_ReferenceSentences", text,
                examples));
        }
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
        fields.Add(ResourceCard(detail, state, style, text, actions, readOnly));
        var cancel = style.OutlineButton(Views.Text(text("Common_Cancel")),
                actions.Cancel)
            .WithAccessibilityIdentifier("native.vocabulary.edit.cancel");
        cancel.IsEnabled = !state.Saving;
        var save = SaveButton(state, style, text, actions, readOnly)
            .WithAccessibilityIdentifier("native.vocabulary.edit.save");
        var buttons = Views.HStack(cancel, save)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.edit.actions");
        buttons.Spacing = SourceUiLayout.ItemGap;
        fields.Add(buttons);
        var toolbarSave = SaveButton(state, style, text, actions, readOnly)
            .WithAccessibilityIdentifier("native.vocabulary.edit.save.toolbar");
        return Views.Toolbar(PageContent(fields, style), toolbarSave)
            .WithAccessibilityIdentifier("native.vocabulary.edit.page");
    }

    private static SwiftView PageContent(
        List<SwiftView> fields, SourceUiStyle style)
    {
        var content = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.edit.scroll.content");
        content.Spacing = SourceUiLayout.PageGap;
        return style.Page(Views.Scroll(content)
            .WithAccessibilityIdentifier("native.vocabulary.edit.scroll"));
    }

    private static SwiftView Card(
        SourceUiStyle style, string titleKey, Func<string, string> text,
        params SwiftView[] content)
    {
        var heading = style.Text(Views.Text(text(titleKey)),
                SourceTextRole.Title3, sourceHeading: true)
            .WithAccessibilityIdentifier($"native.vocabulary.edit.section.{titleKey}");
        var items = Views.VStack([heading, ..content])
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth();
        items.Spacing = SourceUiLayout.FormGap;
        return style.Card(items);
    }

    private static SwiftView TargetField(
        VocabularyEditPresentation state, SourceUiStyle style,
        Func<string, string> text, VocabularyEditActions actions, bool readOnly)
    {
        var draft = state.Draft!;
        var field = style.TextInput(Views.TextField(
                text("VocabWordEdit_TargetPlaceholder"),
                draft.TargetTerm,
                value => actions.Change(nameof(draft.TargetTerm), value)))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.edit.target");
        field.IsEnabled = !readOnly && !state.Saving;
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
        string property, VocabularyEditPresentation state, SourceUiStyle style,
        Func<string, string> text, VocabularyEditActions actions, bool readOnly)
    {
        var field = readOnly
            ? style.Text(Views.Text(value), SourceTextRole.Body1)
            : style.TextInput(Views.TextField(
                text(placeholderKey), value,
                changed => actions.Change(property, changed)));
        if (!readOnly)
            field.IsEnabled = !state.Saving;
        return Views.VStack(
                style.Text(Views.Text(text(titleKey)),
                    SourceTextRole.Body2, SourceTextTone.Secondary),
                field.WithAccessibilityIdentifier(id))
            .WithFlexibleWidth();
    }

    private static SwiftView EncodingCard(
        NativeVocabularyDetail detail, VocabularyEditPresentation state,
        SourceUiStyle style, Func<string, string> text,
        VocabularyEditActions actions, bool readOnly)
    {
        var draft = state.Draft!;
        var fields = new List<SwiftView> {
            style.Text(Views.Text(
                    $"{text("VocabWordEdit_EncodingStrength")} "
                    + text(detail.Word.EncodingLabel switch {
                        "Strong" => "Vocabulary_Strong",
                        "Good" => "Vocabulary_Good",
                        _ => "Vocabulary_Basic"
                    })), SourceTextRole.Body2)
                .WithAccessibilityIdentifier("native.vocabulary.edit.encoding"),
            TextField("VocabWordEdit_Lemma",
                "VocabWordEdit_LemmaPlaceholder", "native.vocabulary.edit.lemma",
                draft.Lemma ?? string.Empty, nameof(draft.Lemma),
                state, style, text, actions, readOnly)
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
        language.IsEnabled = !readOnly && !state.Saving;
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
        type.IsEnabled = !readOnly && !state.Saving && !detail.HasConstituentLinks;
        fields.Add(Views.VStack(
            style.Text(Views.Text(text("NativeResources_TypeFilter")),
                SourceTextRole.Body2, SourceTextTone.Secondary),
            type));
        fields.Add(TextField("VocabWordEdit_Tags",
            "VocabWordEdit_TagsPlaceholder", "native.vocabulary.edit.tags",
            draft.Tags ?? string.Empty, nameof(draft.Tags), state, style,
            text, actions, readOnly));
        // Mnemonic story/image are global fields with no author, so native
        // hides them until per-learner storage exists (Captain, option B).
        fields.Add(style.Text(Views.Text(text("NativeVocabulary_MemoryAidsHidden")),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier("native.vocabulary.edit.memory-aids.hidden"));
        return Card(style, "VocabWordEdit_EncodingMemoryAids",
            text, fields.ToArray());
    }

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
        var next = progress?.NextReviewDate is not { } review
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
                    + $"· {item.LearnedAt.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)}"
                    + (string.IsNullOrWhiteSpace(item.ResourceTitle)
                        ? string.Empty : $" · {item.ResourceTitle}")),
                    SourceTextRole.Body2,
                    item.IsPassive ? SourceTextTone.Secondary : SourceTextTone.Body)
                .WithAccessibilityIdentifier(
                    $"native.vocabulary.edit.history.{item.Id}")));
        return Card(style, "VocabWordEdit_HistoryTitle", text, items.ToArray());
    }

    private static SwiftView ResourceCard(
        NativeVocabularyDetail detail, VocabularyEditPresentation state,
        SourceUiStyle style, Func<string, string> text,
        VocabularyEditActions actions, bool readOnly)
    {
        var selected = state.Draft!.ResourceIds.ToHashSet(StringComparer.Ordinal);
        var selectedCount = style.Text(Views.Text(
                    string.Format(CultureInfo.CurrentCulture,
                        text("VocabWordEdit_SelectedCount"), selected.Count)),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.resources.count");
        actions.RegisterResourceCount(selectedCount);
        var fields = new List<SwiftView> {
            selectedCount,
            style.Text(Views.Text(text("VocabWordEdit_SelectResources")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
        };
        if (detail.AvailableResources.Count == 0)
            fields.Add(style.Text(Views.Text(text("VocabWordEdit_NoResources")),
                    SourceTextRole.Body2, SourceTextTone.Secondary));
        else
            fields.AddRange(detail.AvailableResources.Select(resource =>
            {
                var toggle = Views.Toggle(resource.Title,
                        selected.Contains(resource.Id),
                        isSelected => actions.ToggleResource(resource.Id, isSelected))
                    .WithAccessibilityIdentifier(
                        $"native.vocabulary.edit.resource.{resource.Id}");
                toggle.IsEnabled = !readOnly && !state.Saving
                    && !resource.IsSmartResource;
                return toggle;
            }));
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

    private static SwiftView SaveButton(
        VocabularyEditPresentation state, SourceUiStyle style,
        Func<string, string> text, VocabularyEditActions actions, bool readOnly)
    {
        var button = style.PrimaryButton(text("Common_Save"), actions.Save);
        button.IsEnabled = !readOnly && !state.Saving
            && state.Draft is { } draft
            && !string.IsNullOrWhiteSpace(draft.TargetTerm)
            && !string.IsNullOrWhiteSpace(draft.NativeTerm);
        actions.RegisterSave(button);
        return button;
    }
}
