using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Import;

/// <summary>What the results page opens: the resource (source /resources/edit/{id}) and a word (/vocabulary/edit/{id}).</summary>
internal sealed record ImportContentActions(Action<string> ViewResource, Action<string>? OpenWord);

/// <summary>
/// The drawn nodes the page changes in place, kept by the scene across draws: the slot that
/// holds the preview rows grid (moved into the next draw while the rows did not change) and
/// the "Selected for import" text the row checkboxes update.
/// </summary>
internal sealed class ImportContentDrawn
{
    internal SwiftView? RowsSlot { get; set; }
    internal SwiftView? SelectedCount { get; set; }

    internal void Clear()
    {
        RowsSlot = null;
        SelectedCount = null;
    }
}

/// <summary>
/// The source Import Content page (src/SentenceStudio.UI/Pages/ImportContent.razor) in SwiftUI:
/// the cards of the wizard (source, format hints, auto-detect gate, harvest, preview, target,
/// duplicate handling), the commit action, and the results with their filter. Typed text and
/// the row checkboxes update the coordinator without drawing the page again; what depends on
/// them here (the Preview button, the selected count, the title error) is updated in place.
/// </summary>
internal static class ImportContentView
{
    internal const string ScrollId = "native.import.content.scroll";
    internal const string ContentId = "native.import.content.scroll.content";
    internal const string RowsId = "native.import.content.rows";
    internal const string CommitId = "native.import.content.commit";

    private static readonly SourceColorPair WarningText = new("#664D03", "#FFDA6A");

    /// <summary>
    /// The page. With <paramref name="reuseRows"/> (the rows did not change since the last draw,
    /// <see cref="ImportContentCoordinator.RowsVersion"/>), the drawn rows grid moves into this
    /// draw as is, so a redraw for another part of the page keeps the rows and what was typed.
    /// </summary>
    internal static SwiftView Page(ImportContentCoordinator import, SourceUiStyle style,
        Func<string, string> text, ImportContentActions actions, ImportContentDrawn drawn,
        bool reuseRows)
    {
        var state = import.State;
        // The previous draw's rows: taken only when reused; the slot itself leaves with that draw.
        SwiftView? reusableRows = null;
        if (reuseRows && drawn.RowsSlot is { Children.Count: 1 } previousSlot)
        {
            reusableRows = previousSlot.Children[0];
            previousSlot.RemoveAt(0);
        }
        drawn.Clear();
        var items = new List<SwiftView>();
        if (state.IsCommitting)
            items.Add(Views.IndeterminateProgress(text("Import_CommitButton"))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(48)
                .WithAccessibilityIdentifier("native.import.content.committing"));
        // Blocked first: a result set before the device owner changed is never shown.
        else if (state.Blocked)
            items.Add(style.Alert(Views.Text(text("Import_ProfileNotFound")), success: false)
                .WithAccessibilityIdentifier("native.import.content.blocked"));
        else if (state.Result is { } result)
            items.AddRange(Results(import, state, result, style, text, actions));
        else
            items.AddRange(Wizard(import, state, style, text, drawn, reusableRows));
        // Rows taken from the last draw but not shown in this one (the page moved on) end here.
        if (reusableRows is not null
            && !ReferenceEquals(drawn.RowsSlot?.Children.FirstOrDefault(), reusableRows))
            reusableRows.Dispose();

        var content = Views.VStack(items.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(ContentId);
        content.Spacing = SourceUiLayout.FormGap;
        // A drag into the keyboard puts it away: the paste editor's Return adds a line.
        var page = style.Page(Views.Scroll(content).WithAccessibilityIdentifier(ScrollId)
            .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively));
        if (!state.CanCommit)
            return page;

        // Source PageHeader PrimaryActions: the Import button (with its spinner while committing).
        var commit = style.ViewportWidth < 768
            ? Views.Button(text("Import_CommitButton"), () => _ = import.CommitAsync())
            : style.PrimaryButton(text("Import_CommitButton"), () => _ = import.CommitAsync());
        commit.WithAccessibilityIdentifier(CommitId);
        SetCommitAvailability(commit, style, !state.IsCommitting && !state.IsParsing);
        return Views.Toolbar(page, commit);
    }

    /// <summary>
    /// The Import action's availability, with the source's disabled look for the primary
    /// button on wide pages (a plain toolbar button on a phone dims by itself).
    /// </summary>
    internal static void SetCommitAvailability(SwiftView commit, SourceUiStyle style, bool enabled)
    {
        if (style.ViewportWidth < 768)
            commit.IsEnabled = enabled;
        else
            SourceUiStyle.SetCustomButtonAvailability(commit, style.Appearance,
                SourceButtonRole.Primary, enabled);
    }

    /// <summary>
    /// The page's messages, pinned over its top (source toasts are fixed over the page): the
    /// errors and warnings stay until dismissed or the next action, the success toast expires.
    /// Null when there is nothing to show.
    /// </summary>
    internal static SwiftView? Messages(System.Collections.Immutable.ImmutableArray<NativeMessage> messages,
        NativeMessage? toast, SourceUiStyle style, Func<string, string> text, Action dismissBanners,
        Action dismissToast)
    {
        var items = new List<SwiftView>();
        if (style.MessageBanners(messages, text,
                index => $"native.import.content.message.{index}", dismissBanners,
                "native.import.content.message.dismiss") is { } banners)
            items.Add(style.Card(banners.WithPadding(8), elevated: true, radius: 8));
        if (toast is not null)
            items.Add(style.MessageToast(toast, text, dismissToast));
        if (items.Count == 0)
            return null;
        var stack = Views.VStack(items.ToArray()).WithFlexibleWidth();
        stack.Spacing = SourceUiLayout.ItemGap;
        return stack;
    }

    // ---- Wizard (source steps 1-6) ----

    private static IEnumerable<SwiftView> Wizard(ImportContentCoordinator import,
        ImportContentState state, SourceUiStyle style, Func<string, string> text,
        ImportContentDrawn drawn, SwiftView? reusableRows)
    {
        var locked = state.SourceLocked;
        SwiftView? preview = null;

        // Step 1: Source
        var paste = Views.TextEditor(state.RawText, value =>
            {
                import.SetRawText(value);
                if (preview is not null)
                    SourceUiStyle.SetCustomButtonAvailability(preview, style.Appearance,
                        SourceButtonRole.Primary, import.CanPreview);
            })
            .WithTextEditorScrollBackgroundHidden()
            .WithTextEditorPlaceholder(text("Import_PastePlaceholder"))
            .WithPlaceholderColor(style.Appearance.TertiaryText.Light,
                style.Appearance.TertiaryText.Dark);
        var pasteInput = style.Input(paste)
            .WithMinimumHeight(Rows(style, 8))
            .WithAccessibilityLabel(text("Import_PasteContentLabel"))
            .WithAccessibilityIdentifier("native.import.content.text");
        pasteInput.IsEnabled = !locked;
        yield return Card(style, "native.import.content.source",
            Heading(style, text("Import_Step1_Title")),
            Label(style, text("Import_PasteContentLabel")),
            pasteInput,
            Caption(style, text("Import_PasteHint")));

        // Step 2: Format hints
        var contentType = Choice(style, text("Import_ContentTypeLabel"),
            [
                new(nameof(ContentType.Vocabulary), text("Import_ContentType_Vocabulary")),
                new(nameof(ContentType.Phrases), text("Import_ContentType_Phrases")),
                new(nameof(ContentType.Sentences), text("Import_ContentType_Sentences")),
                new(nameof(ContentType.Transcript), text("Import_ContentType_Transcript")),
                new(nameof(ContentType.Auto), text("Import_ContentType_Auto")),
            ],
            state.ContentType.ToString(), "native.import.content.type",
            value => import.SetContentType(Enum.Parse<ContentType>(value)), !locked);
        var delimiter = Choice(style, text("Import_DelimiterLabel"),
            [
                new(ImportDelimiters.Auto, text("Import_Delimiter_Auto")),
                new(ImportDelimiters.Comma, text("Import_Delimiter_Comma")),
                new(ImportDelimiters.Tab, text("Import_Delimiter_Tab")),
                new(ImportDelimiters.Pipe, text("Import_Delimiter_Pipe")),
            ],
            state.Delimiter, "native.import.content.delimiter", import.SetDelimiter, !locked);
        var header = Views.Toggle(text("Import_HasHeaderRow"), state.HasHeaderRow,
                import.SetHasHeaderRow)
            .WithAccessibilityIdentifier("native.import.content.header");
        header.IsEnabled = !locked;
        var hints = Views.HStack(contentType, delimiter, header)
            .WithAdaptiveStack(680)
            .WithFlexibleWidth();
        hints.Spacing = SourceUiLayout.FormGap;

        preview = style.PrimaryButton(text("Import_PreviewButton"), () => _ = import.PreviewAsync())
            .WithAccessibilityIdentifier("native.import.content.preview");
        SourceUiStyle.SetCustomButtonAvailability(preview, style.Appearance,
            SourceButtonRole.Primary, import.CanPreview);
        var previewRow = state.IsParsing
            ? Views.HStack(Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier("native.import.content.parsing"), preview)
            : Views.HStack(preview);
        previewRow.Spacing = SourceUiLayout.ItemGap;
        yield return Card(style, "native.import.content.format",
            Heading(style, text("Import_Step2_Title")),
            hints,
            previewRow.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing));

        // Auto-detect confidence gate
        if (state.AutoDetect is { } detected && state.Step >= ImportContentStep.Harvest)
            yield return AutoDetectGate(import, state, detected, style, text);

        if (state.ShowTypeChooser)
        {
            var cancel = Views.Button(text("Common_Cancel"), import.CloseTypeChooser)
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithForegroundColor(style.Appearance.SecondaryText.Light,
                    style.Appearance.SecondaryText.Dark)
                .WithAccessibilityIdentifier("native.import.content.chooser.cancel");
            yield return Card(style, "native.import.content.chooser",
                style.Text(Views.Text(text("Import_SelectContentType")), SourceTextRole.Body1Strong),
                TypeButtons(import, state, style, text, "native.import.content.chooser"),
                cancel);
        }

        // Harvest: what to extract
        if (state.Step >= ImportContentStep.Harvest && state.HarvestStepReady)
        {
            var harvest = new List<SwiftView>
            {
                Heading(style, text("Import_HarvestTitle")),
                Caption(style, text("Import_HarvestDescription")),
                Harvest(import, state.HarvestTranscript, ImportHarvest.Transcript,
                    "Import_HarvestTranscriptLabel", "Import_HarvestTranscriptHint", "transcript", style, text),
                Harvest(import, state.HarvestSentences, ImportHarvest.Sentences,
                    "Import_HarvestSentencesLabel", "Import_HarvestSentencesHint", "sentences", style, text),
                Harvest(import, state.HarvestPhrases, ImportHarvest.Phrases,
                    "Import_HarvestPhrasesLabel", "Import_HarvestPhrasesHint", "phrases", style, text),
                Harvest(import, state.HarvestWords, ImportHarvest.Words,
                    "Import_HarvestWordsLabel", "Import_HarvestWordsHint", "words", style, text),
            };
            if (state.ShowHarvestValidationError)
                harvest.Add(style.Alert(Views.Text(text("Import_HarvestValidationError")), success: false,
                        compact: true)
                    .WithAccessibilityIdentifier("native.import.content.harvest.error"));
            yield return Card(style, "native.import.content.harvest", harvest.ToArray());
        }

        if (!state.HasPreview || state.Step < ImportContentStep.Preview)
            yield break;

        yield return PreviewCard(import, state, style, text, drawn, reusableRows);
        yield return TargetCard(import, state, style, text);
        yield return DedupCard(import, state, style, text);
    }

    private static SwiftView AutoDetectGate(ImportContentCoordinator import, ImportContentState state,
        ImportAutoDetect detected, SourceUiStyle style, Func<string, string> text)
    {
        var type = TypeName(detected.ContentType, text);
        var confidence = Percent(detected.Confidence);
        switch (detected.Band)
        {
            case ImportConfidenceBand.High:
            {
                var change = style.OutlineButton(IconLabel("pencil", text("Import_AutoDetectChange")),
                        import.OpenTypeChooser)
                    .WithAccessibilityIdentifier("native.import.content.autodetect.change");
                var row = Views.HStack(
                    Views.Image("sparkles"),
                    style.Text(Views.Text(Format(text("Import_AutoDetected"), type, confidence)),
                        SourceTextRole.Body2).WithFlexibleWidth(),
                    change);
                row.Spacing = SourceUiLayout.ItemGap;
                return style.InfoAlert(row).WithAccessibilityIdentifier("native.import.content.autodetect");
            }
            case ImportConfidenceBand.Medium:
            {
                var children = new List<SwiftView>
                {
                    IconHeading(style, "questionmark.circle", text("Import_AutoDetectNotSure")),
                    style.Text(Views.Text(Format(text("Import_AutoDetectLooksLike"), type, confidence)),
                        SourceTextRole.Body2),
                };
                if (!state.AutoDetectConfirmed)
                {
                    var yes = style.PrimaryButton(Format(text("Import_AutoDetectConfirm"), type),
                            () => _ = import.ConfirmAutoDetectAsync())
                        .WithAccessibilityIdentifier("native.import.content.autodetect.confirm");
                    var other = style.OutlineButton(
                            IconLabel("pencil", text("Import_AutoDetectPickDifferent")),
                            import.OpenTypeChooser)
                        .WithAccessibilityIdentifier("native.import.content.autodetect.pick");
                    SourceUiStyle.SetCustomButtonAvailability(yes, style.Appearance,
                        SourceButtonRole.Primary, !state.IsParsing);
                    var buttons = Views.HStack(yes, other).WithAdaptiveStack(420);
                    buttons.Spacing = SourceUiLayout.ItemGap;
                    children.Add(buttons);
                }
                else
                    children.Add(style.Text(Views.Text(Format(text("Import_AutoDetectConfirmedAs"),
                            TypeName(state.ResolvedContentType, text))), SourceTextRole.Caption1,
                        SourceTextTone.Success)
                        .WithAccessibilityIdentifier("native.import.content.autodetect.confirmed"));
                return WarningAlert(style, children).WithAccessibilityIdentifier("native.import.content.autodetect");
            }
            default:
            {
                var children = new List<SwiftView>
                {
                    IconHeading(style, "exclamationmark.circle", text("Import_AutoDetectFailed")),
                };
                if (!string.IsNullOrEmpty(detected.Note))
                    children.Add(Caption(style, Format(text("Import_AutoDetectLowHint"), type, confidence)));
                children.Add(style.Text(Views.Text(text("Import_AutoDetectSelectType")), SourceTextRole.Body2));
                children.Add(TypeButtons(import, state, style, text, "native.import.content.autodetect"));
                var column = Views.VStack(children.ToArray()).WithFlexibleWidth();
                column.Spacing = SourceUiLayout.ItemGap;
                return style.BorderedCard(column.WithPadding(SourceUiLayout.CardInset), radius: 8)
                    .WithAccessibilityIdentifier("native.import.content.autodetect");
            }
        }
    }

    /// <summary>The four type buttons of the low-confidence gate and the chooser (source: no Auto).</summary>
    private static SwiftView TypeButtons(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text, string idPrefix)
    {
        ContentType[] types = [ContentType.Vocabulary, ContentType.Phrases,
            ContentType.Sentences, ContentType.Transcript];
        var buttons = types.Select(type =>
        {
            var selected = state.ResolvedContentType == type;
            var label = TypeName(type, text);
            void Pick() => _ = import.OverrideContentTypeAsync(type);
            var button = (selected
                    ? style.PrimaryButton(label, Pick)
                    : style.OutlineButton(Views.Text(label), Pick))
                .WithAccessibilitySelected(selected)
                .WithAccessibilityIdentifier($"{idPrefix}.type.{type.ToString().ToLowerInvariant()}");
            if (state.IsParsing)
                SourceUiStyle.SetCustomButtonAvailability(button, style.Appearance,
                    selected ? SourceButtonRole.Primary : SourceButtonRole.OutlineSecondary, false);
            return button;
        }).ToArray();
        var grid = Views.LazyVGrid(style.ViewportWidth < 520 ? 2 : 4, buttons).WithFlexibleWidth();
        grid.Spacing = SourceUiLayout.ItemGap;
        return grid;
    }

    private static SwiftView Harvest(ImportContentCoordinator import, bool isOn, ImportHarvest harvest,
        string labelKey, string hintKey, string id, SourceUiStyle style, Func<string, string> text)
    {
        var toggle = Views.Toggle(text(labelKey), isOn, value => import.SetHarvest(harvest, value))
            .WithAccessibilityIdentifier($"native.import.content.harvest.{id}");
        var column = Views.VStack(
            style.Text(toggle, SourceTextRole.Body2Strong),
            Caption(style, text(hintKey)));
        column.Spacing = 2;
        return column.WithFlexibleWidth();
    }

    // ---- Preview, target, duplicate handling ----

    private static SwiftView PreviewCard(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text, ImportContentDrawn drawn,
        SwiftView? reusableRows)
    {
        var format = ImportServiceText.DetectedFormat(state.DetectedFormat, text);
        var children = new List<SwiftView>
        {
            Heading(style, text("Import_Step3_Title")),
            style.Text(Views.Text($"{text("Import_DetectedFormat")}: {format}"),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.import.content.format.detected"),
        };
        if (!state.Warnings.IsDefaultOrEmpty)
        {
            var lines = state.Warnings.Select(warning =>
            {
                var line = Views.HStack(
                    Views.Image("exclamationmark.triangle")
                        .WithAccessibilityLabel(text("NativeMessage_Warning")),
                    style.Text(Views.Text(ImportServiceText.Message(warning, text)),
                        SourceTextRole.Caption1).WithFlexibleWidth());
                line.Spacing = 4;
                return line;
            }).ToList();
            children.Add(WarningAlert(style, lines, compact: true)
                .WithAccessibilityIdentifier("native.import.content.warnings"));
        }

        var count = style.Text(Views.Text(SelectedCount(state.SelectedCount, state.Rows.Length, text)),
                SourceTextRole.Caption1, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier("native.import.content.selected.count");
        drawn.SelectedCount = count;
        children.Add(style.Text(Views.Toggle(text("Vocabulary_SelectAll"), state.SelectAllRows,
                    import.SetSelectAll), SourceTextRole.Body2Strong)
            .WithAccessibilityIdentifier("native.import.content.select-all"));
        // A lazy grid draws only the rows on screen (a long AI preview stays cheap).
        var rowsSlot = Views.VStack(reusableRows ?? PreviewRows(import, state, style, text, drawn))
            .WithFlexibleWidth();
        rowsSlot.Spacing = 0;
        drawn.RowsSlot = rowsSlot;
        children.Add(rowsSlot);
        children.Add(count);
        return Card(style, "native.import.content.preview.card", children.ToArray());
    }

    private static SwiftView PreviewRows(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text, ImportContentDrawn drawn)
    {
        var total = state.Rows.Length;
        var grid = Views.LazyVGrid(1, state.Rows
                .Select(row => PreviewRow(import, row, style, text, drawn, total)).ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(RowsId);
        grid.Spacing = SourceUiLayout.ItemGap;
        return grid;
    }

    private static SwiftView PreviewRow(ImportContentCoordinator import, ImportPreviewRow row,
        SourceUiStyle style, Func<string, string> text, ImportContentDrawn drawn, int total)
    {
        var id = $"native.import.content.row.{row.Index}";
        var number = row.RowNumber.ToString(CultureInfo.CurrentCulture);
        var select = style.Text(Views.Toggle($"#{number}", row.Selected, value =>
                {
                    var selected = import.SetRowSelected(row.Index, value);
                    if (drawn.SelectedCount is { } count)
                        count.Text = SelectedCount(selected, total, text);
                }), SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithAccessibilityLabel(Format(text("Import_SelectRow"), number))
            .WithAccessibilityIdentifier($"{id}.select");
        select.IsEnabled = row.Status != RowStatus.Error;

        var badges = new List<SwiftView> { StatusBadge(row, style, text, id) };
        if (row.Duplicate)
            badges.Add(Badge(style, "doc.on.doc", text("Import_PreviewDuplicateBadge"),
                    new("#FFF3CD", "#332701"), WarningText, $"{id}.duplicate")
                .WithAccessibilityLabel(
                    $"{text("Import_PreviewDuplicateBadge")}: {DuplicateReason(row.DuplicateReason, text)}"));
        if (row.AiTranslated)
            badges.Add(Badge(style, "sparkles", text("Import_AiTranslated_Badge"),
                    new("#CFF4FC", "#032830"), new("#055160", "#6EDFF6"), $"{id}.ai")
                .WithAccessibilityLabel(
                    $"{text("Import_AiTranslated_Badge")}: {text("Import_AiTranslated_Tooltip")}"));
        var badgeRow = Views.HStack(badges.ToArray());
        badgeRow.Spacing = 6;

        var target = style.TextInput(Views.TextField(text("Import_TargetLanguageTerm"), row.Target,
                value => import.SetRowTarget(row.Index, value)))
            .WithAccessibilityLabel(text("Import_TargetLanguageTerm"))
            .WithAccessibilityIdentifier($"{id}.target");
        var native = style.TextInput(Views.TextField(text("Import_NativeLanguageTerm"), row.Native,
                value => import.SetRowNative(row.Index, value)))
            .WithAccessibilityLabel(text("Import_NativeLanguageTerm"))
            .WithAccessibilityIdentifier($"{id}.native");
        var fields = Views.HStack(target, native).WithAdaptiveStack(680).WithFlexibleWidth();
        fields.Spacing = SourceUiLayout.ItemGap;

        var column = Views.VStack(select, badgeRow, fields)
            .WithPadding(SourceUiLayout.ItemGap + 4)
            .WithFlexibleWidth();
        column.Spacing = SourceUiLayout.ItemGap;
        // Source: error rows are table-danger, warning rows table-warning.
        var surface = row.Status switch
        {
            RowStatus.Error => style.Appearance.DangerAlertFill,
            RowStatus.Warning => new SourceColorPair("#FFF3CD", "#332701"),
            _ => style.Appearance.ListRowSurface
        };
        return column.WithRoundedSurface(surface.Light, surface.Dark, 8,
                style.Appearance.Border.Light, style.Appearance.Border.Dark, 1)
            .WithAccessibilityIdentifier(id);
    }

    private static SwiftView StatusBadge(ImportPreviewRow row, SourceUiStyle style,
        Func<string, string> text, string id)
    {
        var (symbol, key, fill, foreground) = row.Status switch
        {
            RowStatus.Ok => ("checkmark", "Common_OKButton", style.Appearance.Success,
                new SourceColorPair("#FFFFFF", "#FFFFFF")),
            RowStatus.Warning => ("exclamationmark.triangle", "NativeMessage_Warning",
                style.Appearance.Warning, new SourceColorPair("#000000", "#000000")),
            _ => ("xmark", "Common_Error", style.Appearance.Danger,
                new SourceColorPair("#FFFFFF", "#FFFFFF"))
        };
        var badge = Badge(style, symbol, text(key), fill, foreground, $"{id}.status");
        // Source shows the row's error as the badge's title (a hover tooltip).
        return row.Error is { Length: > 0 } error
            ? badge.WithAccessibilityLabel($"{text(key)}: {ImportServiceText.Message(error, text)}")
            : badge;
    }

    private static SwiftView TargetCard(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text)
    {
        var children = new List<SwiftView>
        {
            Heading(style, text("Import_Step5_Title")),
            Radio(style, text("Import_CreateNewResource"), null,
                state.TargetMode == ImportTargetMode.New,
                () => import.SetTargetMode(ImportTargetMode.New), "native.import.content.target.new"),
            Radio(style, text("Import_AddToExistingResource"), null,
                state.TargetMode == ImportTargetMode.Existing,
                () => import.SetTargetMode(ImportTargetMode.Existing), "native.import.content.target.existing"),
        };
        if (state.TargetMode == ImportTargetMode.New)
        {
            // The error takes room only while it is shown; typing hides it in place.
            var error = state.ShowTitleValidationError
                ? style.Text(Views.Text(text("Import_NewResourceTitleRequired")),
                        SourceTextRole.Caption1, SourceTextTone.Danger)
                    .WithAccessibilityIdentifier("native.import.content.title.error")
                : null;
            var title = style.TextInput(Views.TextField(text("Import_ResourceTitlePlaceholder"),
                    state.NewResourceTitle, value =>
                    {
                        if (import.SetNewResourceTitle(value))
                            error?.WithHidden(true);
                    }))
                .WithAccessibilityLabel(text("Import_ResourceTitleLabel"))
                .WithAccessibilityIdentifier("native.import.content.title");
            var titleField = (error is not null
                    ? Views.VStack(Label(style, text("Import_ResourceTitleLabel")), title, error)
                    : Views.VStack(Label(style, text("Import_ResourceTitleLabel")), title))
                .WithFlexibleWidth();
            titleField.Spacing = SourceUiLayout.ItemGap;
            var description = style.TextInput(Views.TextField(
                    text("Import_ResourceDescriptionPlaceholder"), state.NewResourceDescription,
                    import.SetNewResourceDescription))
                .WithAccessibilityLabel(text("Import_ResourceDescriptionLabel"))
                .WithAccessibilityIdentifier("native.import.content.description");
            var descriptionField = Views.VStack(Label(style, text("Import_ResourceDescriptionLabel")),
                description).WithFlexibleWidth();
            descriptionField.Spacing = SourceUiLayout.ItemGap;
            var names = Views.HStack(titleField, descriptionField).WithAdaptiveStack(680).WithFlexibleWidth();
            names.Spacing = SourceUiLayout.FormGap;
            children.Add(names);

            var languages = ImportContentCoordinator.Languages
                .Select(language => new SwiftPickerOption(language, language)).ToArray();
            var languagePickers = Views.HStack(
                    Choice(style, text("Import_TargetLanguageLabel"), languages, state.TargetLanguage,
                        "native.import.content.target.language", import.SetTargetLanguage, true),
                    Choice(style, text("Import_NativeLanguageLabel"), languages, state.NativeLanguage,
                        "native.import.content.native.language", import.SetNativeLanguage, true))
                .WithAdaptiveStack(680)
                .WithFlexibleWidth();
            languagePickers.Spacing = SourceUiLayout.FormGap;
            children.Add(languagePickers);
        }
        else
        {
            const string none = "native.import.content.resource.none";
            var options = new List<SwiftPickerOption>
            {
                new(none, $"-- {text("Import_SelectResourcePlaceholder")} --")
            };
            options.AddRange(state.Resources.Select(resource =>
                new SwiftPickerOption(resource.Id, $"{resource.Title} ({resource.Language})")));
            children.Add(Choice(style, text("Import_SelectExistingResourceLabel"), options,
                state.SelectedResourceId.Length == 0 ? none : state.SelectedResourceId,
                "native.import.content.resource",
                value => import.SetSelectedResource(value == none ? string.Empty : value),
                !state.ResourcesLoading));
            if (state.ResourcesLoading)
                children.Add(Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier("native.import.content.resource.loading"));
            else if (state.ResourcesErrorKey is { } resourcesError)
            {
                // The list could not be read: say so and offer Retry instead of an empty select.
                var retry = style.SecondaryButton(text(NativeText.Retry),
                        () => _ = import.RetryResourcesAsync())
                    .WithAccessibilityIdentifier("native.import.content.resource.retry");
                var failure = Views.VStack(
                    style.Alert(Views.Text(text(resourcesError)), success: false, compact: true)
                        .WithAccessibilityIdentifier("native.import.content.resource.error"),
                    retry).WithFlexibleWidth();
                failure.Spacing = SourceUiLayout.ItemGap;
                children.Add(failure);
            }
        }
        return Card(style, "native.import.content.target", children.ToArray());
    }

    private static SwiftView DedupCard(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text) =>
        Card(style, "native.import.content.dedup",
            Heading(style, text("Import_Step6_Title")),
            Radio(style, text("Import_DedupMode_Skip"), text("Import_DedupMode_Skip_Help"),
                state.DedupMode == DedupMode.Skip, () => import.SetDedupMode(DedupMode.Skip),
                "native.import.content.dedup.skip"),
            Radio(style, text("Import_DedupMode_Update"), text("Import_DedupMode_Update_Help"),
                state.DedupMode == DedupMode.Update, () => import.SetDedupMode(DedupMode.Update),
                "native.import.content.dedup.update", warningHelp: true),
            Radio(style, text("Import_DedupMode_ImportAll"), text("Import_DedupMode_ImportAll_Help"),
                state.DedupMode == DedupMode.ImportAll, () => import.SetDedupMode(DedupMode.ImportAll),
                "native.import.content.dedup.import-all"));

    // ---- Results (source step 7) ----

    private static IEnumerable<SwiftView> Results(ImportContentCoordinator import, ImportContentState state,
        ContentImportResult result, SourceUiStyle style, Func<string, string> text,
        ImportContentActions actions)
    {
        var complete = Views.HStack(
            Views.Image("checkmark.circle.fill")
                .WithForegroundColor(style.Appearance.Success.Light, style.Appearance.Success.Dark)
                ,
            style.Text(Views.Text(text("Import_ImportComplete")), SourceTextRole.Title3, sourceHeading: true));
        complete.Spacing = SourceUiLayout.ItemGap;
        yield return complete.WithAccessibilityIdentifier("native.import.content.complete");

        var stats = Views.LazyVGrid(style.ViewportWidth < 768 ? 2 : 4,
            Stat(style, result.CreatedCount, text("Import_Created"), style.Appearance.Success, "created"),
            Stat(style, result.SkippedCount, text("Import_Skipped"), new("#0DCAF0", "#6EDFF6"), "skipped"),
            Stat(style, result.UpdatedCount, text("Import_Updated"), style.Appearance.Warning, "updated"),
            Stat(style, result.FailedCount, text("Import_Failed"), style.Appearance.Danger, "failed"))
            .WithFlexibleWidth();
        stats.Spacing = SourceUiLayout.FormGap;
        yield return stats.WithAccessibilityIdentifier("native.import.content.stats");

        var view = style.LabelButton(IconLabel("book", text("Import_ViewResource")),
                () =>
                {
                    if (!string.IsNullOrEmpty(result.ResourceId))
                        actions.ViewResource(result.ResourceId);
                }, SourceButtonRole.Primary)
            .WithAccessibilityIdentifier("native.import.content.view-resource");
        var more = style.LabelButton(IconLabel("arrow.clockwise", text("Import_ImportMore")),
                import.StartNewImport, SourceButtonRole.FilledSecondary)
            .WithAccessibilityIdentifier("native.import.content.import-more");
        var buttons = Views.HStack(view, more).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading);
        buttons.Spacing = SourceUiLayout.ItemGap;
        yield return buttons;

        if (result.Items.Count == 0)
            yield break;
        var details = Views.HStack(Views.Image("list.bullet"),
            style.Text(Views.Text(text("Import_ResultDetails")), SourceTextRole.Title3, sourceHeading: true));
        details.Spacing = SourceUiLayout.ItemGap;
        yield return details;

        var pills = new List<SwiftView>
        {
            Pill(import, style, text("Import_FilterAll"), result.Items.Count, null, state.ResultFilter, "all")
        };
        if (result.CreatedCount > 0)
            pills.Add(Pill(import, style, text("Import_Created"), result.CreatedCount,
                ImportItemStatus.Created, state.ResultFilter, "created"));
        if (result.UpdatedCount > 0)
            pills.Add(Pill(import, style, text("Import_Updated"), result.UpdatedCount,
                ImportItemStatus.Updated, state.ResultFilter, "updated"));
        if (result.SkippedCount > 0)
            pills.Add(Pill(import, style, text("Import_Skipped"), result.SkippedCount,
                ImportItemStatus.Skipped, state.ResultFilter, "skipped"));
        if (result.FailedCount > 0)
            pills.Add(Pill(import, style, text("Import_Failed"), result.FailedCount,
                ImportItemStatus.Failed, state.ResultFilter, "failed"));
        var filter = Views.LazyVGrid(style.ViewportWidth < 520 ? 3 : 5, pills.ToArray()).WithFlexibleWidth();
        filter.Spacing = SourceUiLayout.ItemGap;
        yield return filter.WithAccessibilityIdentifier("native.import.content.filter");

        var rows = state.FilteredItems.Select((item, index) =>
            ResultRow(item, index, style, text, actions)).ToArray();
        // A lazy grid draws only the result rows on screen.
        var list = Views.LazyVGrid(1, rows).WithFlexibleWidth();
        list.Spacing = 0;
        yield return style.BorderedCard(list, radius: 8).WithAccessibilityIdentifier("native.import.content.items");
    }

    private static SwiftView Stat(SourceUiStyle style, int value, string label, SourceColorPair color,
        string id)
    {
        var number = style.Text(Views.Text(value.ToString(CultureInfo.CurrentCulture)), SourceTextRole.Title1)
            .WithForegroundColor(color.Light, color.Dark);
        var column = Views.VStack(number,
                style.Text(Views.Text(label), SourceTextRole.Caption1, SourceTextTone.Secondary))
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityLabel($"{label}: {value.ToString(CultureInfo.CurrentCulture)}")
            .WithAccessibilityIdentifier($"native.import.content.stat.{id}");
        column.Spacing = 2;
        return style.Card(column);
    }

    private static SwiftView Pill(ImportContentCoordinator import, SourceUiStyle style, string label,
        int count, ImportItemStatus? status, ImportItemStatus? current, string id)
    {
        var selected = status == current;
        var title = $"{label} {count.ToString(CultureInfo.CurrentCulture)}";
        var pill = (selected
                ? style.PrimaryButton(title, () => import.SetResultFilter(status), fullWidth: true)
                : style.SecondaryButton(title, () => import.SetResultFilter(status), fullWidth: true))
            .WithAccessibilitySelected(selected)
            .WithAccessibilityIdentifier($"native.import.content.filter.{id}");
        return pill;
    }

    private static SwiftView ResultRow(ContentImportItemResult item, int index, SourceUiStyle style,
        Func<string, string> text, ImportContentActions actions)
    {
        var id = $"native.import.content.item.{index}";
        var clickable = ImportContentCoordinator.CanOpenWord(item) && actions.OpenWord is not null;
        var lemma = new List<SwiftView>
        {
            style.Text(Views.Text(item.Lemma), SourceTextRole.Body1Strong)
        };
        if (clickable)
            lemma.Add(Views.Image("arrow.up.right.square")
                .WithForegroundColor(style.Appearance.SecondaryText.Light, style.Appearance.SecondaryText.Dark)
                );
        var lemmaRow = Views.HStack(lemma.ToArray());
        lemmaRow.Spacing = 4;
        var (typeSymbol, typeKey) = item.Type switch
        {
            LexicalUnitType.Word => ("textformat", "Vocabulary_FilterTypeWord"),
            LexicalUnitType.Phrase => ("quote.bubble", "Vocabulary_FilterTypePhrase"),
            LexicalUnitType.Sentence => ("text.alignleft", "Vocabulary_FilterTypeSentence"),
            _ => ("questionmark.circle", "Vocabulary_Unknown")
        };
        var (statusSymbol, statusKey, statusFill, statusText) = item.Status switch
        {
            ImportItemStatus.Created => ("plus.circle", "Import_Created", style.Appearance.Success,
                new SourceColorPair("#FFFFFF", "#FFFFFF")),
            ImportItemStatus.Updated => ("pencil", "Import_Updated", style.Appearance.Warning,
                new SourceColorPair("#000000", "#000000")),
            ImportItemStatus.Skipped => ("forward", "Import_Skipped", new SourceColorPair("#0DCAF0", "#0DCAF0"),
                new SourceColorPair("#000000", "#000000")),
            ImportItemStatus.Failed => ("xmark.circle", "Import_Failed", style.Appearance.Danger,
                new SourceColorPair("#FFFFFF", "#FFFFFF")),
            _ => ("questionmark.circle", "Vocabulary_Unknown", style.Appearance.SecondaryText,
                new SourceColorPair("#FFFFFF", "#FFFFFF"))
        };
        var badges = new List<SwiftView>
        {
            Badge(style, typeSymbol, text(typeKey), style.Appearance.SecondarySurface,
                style.Appearance.Text, $"{id}.type"),
            Badge(style, statusSymbol, text(statusKey), statusFill, statusText, $"{id}.status"),
        };
        var badgeRow = Views.HStack(badges.ToArray());
        badgeRow.Spacing = 6;
        var column = new List<SwiftView>
        {
            lemmaRow,
            style.Text(Views.Text(item.NativeLanguageTerm), SourceTextRole.Body2, SourceTextTone.Secondary),
            badgeRow
        };
        var reasonText = ImportServiceText.Message(item.Reason, text);
        if (reasonText.Length > 0)
        {
            var reason = Views.HStack(Views.Image("exclamationmark.circle"),
                style.Text(Views.Text(reasonText), SourceTextRole.Caption1, SourceTextTone.Danger));
            reason.Spacing = 4;
            column.Add(reason.WithForegroundColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark));
        }
        var body = Views.VStack(column.ToArray())
            .WithPadding(12, 16, 12, 16)
            .WithFlexibleWidth();
        body.Spacing = 4;
        var row = Views.Button(body, () =>
            {
                if (clickable && item.VocabularyWordId is { } word)
                    actions.OpenWord!(word);
            })
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            // The badges' type and status are part of what VoiceOver reads, not color only.
            .WithAccessibilityLabel(string.Join(", ", new[]
                {
                    item.Lemma, item.NativeLanguageTerm, text(typeKey), text(statusKey), reasonText
                }.Where(part => !string.IsNullOrWhiteSpace(part))))
            .WithAccessibilityIdentifier(id);
        row.IsEnabled = clickable;
        return index == 0 ? row : Views.VStack(Views.Divider(), row).WithFlexibleWidth();
    }

    // ---- Small pieces ----

    private static SwiftView Card(SourceUiStyle style, string id, params SwiftView[] children)
    {
        var card = Views.VStack(children)
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        card.Spacing = SourceUiLayout.FormGap;
        return style.Card(card);
    }

    private static SwiftView WarningAlert(SourceUiStyle style, IEnumerable<SwiftView> children,
        bool compact = false)
    {
        var column = Views.VStack(children.ToArray()).WithFlexibleWidth();
        column.Spacing = SourceUiLayout.ItemGap;
        var vertical = compact ? 8 : 16;
        return column.WithForegroundColor(WarningText.Light, WarningText.Dark)
            .WithPadding(vertical, 16, vertical, 16)
            .WithFlexibleWidth()
            .WithRoundedSurface("#FFF3CD", "#332701", style.Appearance.BootstrapRadius,
                "#FFE69C", "#997404", 1);
    }

    private static SwiftView Radio(SourceUiStyle style, string label, string? help, bool selected,
        Action select, string id, bool warningHelp = false)
    {
        var lines = new List<SwiftView> { style.Text(Views.Text(label), SourceTextRole.Body2Strong) };
        if (help is not null)
        {
            var helpText = style.Text(Views.Text(help), SourceTextRole.Caption1, SourceTextTone.Secondary);
            if (warningHelp)
                helpText.WithForegroundColor(style.Appearance.Warning.Light, style.Appearance.Warning.Dark);
            lines.Add(helpText);
        }
        var text = Views.VStack(lines.ToArray()).WithFlexibleWidth();
        text.Spacing = 2;
        var symbol = Views.Image(selected ? "largecircle.fill.circle" : "circle")
            .WithFixedSymbolSize(20)
            .WithForegroundColor(
                selected ? style.Appearance.Primary.Light : style.Appearance.SecondaryText.Light,
                selected ? style.Appearance.Primary.Dark : style.Appearance.SecondaryText.Dark)
            ;
        var row = Views.HStack(symbol, text).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap + 4;
        return Views.Button(row, select)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(help is null ? label : $"{label}. {help}")
            .WithAccessibilitySelected(selected)
            .WithAccessibilityIdentifier(id);
    }

    private static SwiftView Choice(SourceUiStyle style, string label, IReadOnlyList<SwiftPickerOption> options,
        string selected, string id, Action<string> changed, bool enabled)
    {
        var picker = style.Input(Views.Picker(string.Empty, options, selected, changed)
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading))
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
        picker.IsEnabled = enabled;
        var field = Views.VStack(Label(style, label), picker).WithFlexibleWidth();
        field.Spacing = SourceUiLayout.ItemGap;
        return field;
    }

    private static SwiftView Badge(SourceUiStyle style, string symbol, string label, SourceColorPair fill,
        SourceColorPair foreground, string id)
    {
        var content = Views.HStack(Views.Image(symbol),
            style.Text(Views.Text(label), SourceTextRole.BadgeSmall));
        content.Spacing = 3;
        return content
            .WithPadding(4, 8, 4, 8)
            .WithRoundedSurface(fill.Light, fill.Dark, 6)
            .WithForegroundColor(foreground.Light, foreground.Dark)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
    }

    private static SwiftView IconLabel(string symbol, string title)
    {
        var label = Views.HStack(Views.Image(symbol), Views.Text(title));
        label.Spacing = 4;
        return label;
    }

    private static SwiftView IconHeading(SourceUiStyle style, string symbol, string title)
    {
        var row = Views.HStack(Views.Image(symbol),
            style.Text(Views.Text(title), SourceTextRole.Body2Strong));
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    private static SwiftView Heading(SourceUiStyle style, string title) =>
        style.Text(Views.Text(title), SourceTextRole.Title3, sourceHeading: true);

    private static SwiftView Label(SourceUiStyle style, string title) =>
        style.Text(Views.Text(title), SourceTextRole.Body2, SourceTextTone.Secondary);

    private static SwiftView Caption(SourceUiStyle style, string value) =>
        style.Text(Views.Text(value), SourceTextRole.Caption1, SourceTextTone.Secondary);

    private static string SelectedCount(int selected, int total, Func<string, string> text) =>
        $"{text("Import_SelectedCount")}: {selected.ToString(CultureInfo.CurrentCulture)} / "
        + total.ToString(CultureInfo.CurrentCulture);

    private static string DuplicateReason(string? reason, Func<string, string> text) => reason switch
    {
        "AlreadyInVocabulary" => text("Import_PreviewDuplicate_AlreadyInVocabulary"),
        "DuplicateWithinBatch" => text("Import_PreviewDuplicate_DuplicateWithinBatch"),
        _ => text("Import_PreviewDuplicateBadge")
    };

    internal static string TypeName(ContentType type, Func<string, string> text) => type switch
    {
        ContentType.Vocabulary => text("Import_ContentType_Vocabulary"),
        ContentType.Phrases => text("Import_ContentType_Phrases"),
        ContentType.Sentences => text("Import_ContentType_Sentences"),
        ContentType.Transcript => text("Import_ContentType_Transcript"),
        _ => text("Import_ContentType_Auto")
    };

    private static string Percent(float confidence) =>
        (confidence * 100).ToString("F0", CultureInfo.CurrentCulture) + "%";

    private static string Format(string template, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, template, values);

    private static double Rows(SourceUiStyle style, int rows)
    {
        var font = SourceUiTypography.For(SourceTextRole.Input, style.ViewportWidth,
            style.Appearance.Selection.FontScale, style.Appearance.Selection.ThemeId);
        return rows * font.Size * font.LineHeight + 2 * SourceUiLayout.ItemGap;
    }
}
