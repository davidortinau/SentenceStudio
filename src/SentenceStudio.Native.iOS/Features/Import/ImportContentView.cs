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
/// The source Import Content page (src/SentenceStudio.UI/Pages/ImportContent.razor) in SwiftUI,
/// as one grouped Form (Captain 2026-11-13, the native form pattern): the Sections of the wizard (source, format hints, auto-detect gate, harvest, preview, target,
/// duplicate handling), the commit action, and the results with their filter. Typed text and
/// the row checkboxes update the coordinator without drawing the page again; what depends on
/// them here (the Preview button, the selected count, the title error) is updated in place.
/// </summary>
internal static class ImportContentView
{
    internal const string ScrollId = "native.import.content.scroll";
    internal const string ContentId = "native.import.content.scroll.content";
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
        // The previous draw's rows: taken only when reused; their Section leaves with that draw.
        List<SwiftView>? reusableRows = null;
        if (reuseRows && drawn.RowsSlot is { Children.Count: > 0 } previousSlot)
        {
            reusableRows = [];
            while (previousSlot.Children.Count > 0)
            {
                reusableRows.Add(previousSlot.Children[0]);
                previousSlot.RemoveAt(0);
            }
        }
        drawn.Clear();
        var sections = new List<SwiftView>();
        if (state.IsCommitting)
            sections.Add(Views.Section(string.Empty,
                Views.IndeterminateProgress(text("Import_CommitButton"))
                    .WithAccessibilityIdentifier("native.import.content.committing")));
        // Blocked first: a result set before the device owner changed is never shown.
        else if (state.Blocked)
            sections.Add(Views.Section(string.Empty, NativeForm.Notice(style,
                text("Import_ProfileNotFound"), "native.import.content.blocked", error: true)));
        else if (state.Result is { } result)
            sections.AddRange(Results(import, state, result, style, text, actions));
        else
            sections.AddRange(Wizard(import, state, style, text, drawn, reusableRows));
        // Rows taken from the last draw but not shown in this one (the page moved on) end here.
        if (reusableRows is not null && drawn.RowsSlot is null)
            foreach (var row in reusableRows)
                row.Dispose();

        // Captain 2026-11-13 (the native form pattern): one grouped Form; a drag puts the
        // keyboard away (the paste editor's Return adds a line).
        var page = NativeForm.Page(style, sections, ContentId, ScrollId);
        if (!state.CanCommit)
            return page;

        // Source PageHeader PrimaryActions: Import, the toolbar's plain action at every width.
        var commit = Views.Button(text("Import_CommitButton"), () => _ = import.CommitAsync())
            .WithAccessibilityIdentifier(CommitId);
        SetCommitAvailability(commit, !state.IsCommitting && !state.IsParsing);
        return Views.Toolbar(page, commit);
    }

    /// <summary>The Import action's availability (a plain toolbar button dims by itself).</summary>
    internal static void SetCommitAvailability(SwiftView commit, bool enabled)
    {
        if (commit.IsEnabled != enabled)
            commit.IsEnabled = enabled;
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
        ImportContentDrawn drawn, List<SwiftView>? reusableRows)
    {
        var locked = state.SourceLocked;
        SwiftView? preview = null;

        // Step 1: Source
        var paste = NativeForm.Editor(style, text("Import_PasteContentLabel"), state.RawText,
            "native.import.content.text", value =>
            {
                import.SetRawText(value);
                if (preview is not null && preview.IsEnabled != import.CanPreview)
                    preview.IsEnabled = import.CanPreview;
            }, rows: 8, placeholder: text("Import_PastePlaceholder"));
        paste.IsEnabled = !locked;
        yield return Views.Section(text("Import_Step1_Title"), paste,
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
        var header = NativeForm.Switch(style, text("Import_HasHeaderRow"), state.HasHeaderRow,
            import.SetHasHeaderRow, "native.import.content.header");
        header.IsEnabled = !locked;
        preview = NativeForm.Action(style, text("Import_PreviewButton"), "eye",
            () => _ = import.PreviewAsync(), "native.import.content.preview");
        preview.IsEnabled = import.CanPreview;
        var format = new List<SwiftView> { contentType, delimiter, header, preview };
        if (state.IsParsing)
            format.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.import.content.parsing"));
        yield return Views.Section(text("Import_Step2_Title"), format.ToArray());

        // Auto-detect confidence gate
        if (state.AutoDetect is { } detected && state.Step >= ImportContentStep.Harvest)
            yield return AutoDetectGate(import, state, detected, style, text);

        if (state.ShowTypeChooser)
        {
            var cancel = NativeForm.Bordered(style, text("Common_Cancel"), import.CloseTypeChooser,
                "native.import.content.chooser.cancel");
            yield return Views.Section(text("Import_SelectContentType"),
                TypeButtons(import, state, style, text, "native.import.content.chooser"),
                cancel);
        }

        // Harvest: what to extract
        if (state.Step >= ImportContentStep.Harvest && state.HarvestStepReady)
        {
            var harvest = new List<SwiftView>
            {
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
                harvest.Add(NativeForm.Notice(style, text("Import_HarvestValidationError"),
                    "native.import.content.harvest.error", error: true));
            yield return Views.Section(text("Import_HarvestTitle"), harvest.ToArray());
        }

        if (!state.HasPreview || state.Step < ImportContentStep.Preview)
            yield break;

        yield return PreviewSection(import, state, style, text, drawn);
        // The preview rows are their own Section, one Form row each (the Form draws only the
        // rows on screen); unchanged rows move in from the last draw with their typed text.
        var rows = reusableRows?.ToArray() ?? PreviewRows(import, state, style, text, drawn);
        var rowsSection = Views.Section(string.Empty, rows);
        drawn.RowsSlot = rowsSection;
        yield return rowsSection;
        yield return TargetSection(import, state, style, text);
        yield return DedupSection(import, state, style, text);
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
                var change = NativeForm.Action(style, text("Import_AutoDetectChange"), "pencil",
                    import.OpenTypeChooser, "native.import.content.autodetect.change");
                return Views.Section(string.Empty,
                    Views.Label(Format(text("Import_AutoDetected"), type, confidence), "sparkles")
                        .WithAccessibilityIdentifier("native.import.content.autodetect"),
                    change);
            }
            case ImportConfidenceBand.Medium:
            {
                var rows = new List<SwiftView>
                {
                    style.Text(Views.Text(Format(text("Import_AutoDetectLooksLike"), type, confidence)),
                            SourceTextRole.Body2)
                        .WithAccessibilityIdentifier("native.import.content.autodetect"),
                };
                if (!state.AutoDetectConfirmed)
                {
                    var yes = NativeForm.Prominent(style, Format(text("Import_AutoDetectConfirm"), type),
                        () => _ = import.ConfirmAutoDetectAsync(),
                        "native.import.content.autodetect.confirm");
                    yes.IsEnabled = !state.IsParsing;
                    rows.Add(yes);
                    rows.Add(NativeForm.Action(style, text("Import_AutoDetectPickDifferent"), "pencil",
                        import.OpenTypeChooser, "native.import.content.autodetect.pick"));
                }
                else
                    rows.Add(style.Text(Views.Text(Format(text("Import_AutoDetectConfirmedAs"),
                            TypeName(state.ResolvedContentType, text))), SourceTextRole.Caption1,
                        SourceTextTone.Success)
                        .WithAccessibilityIdentifier("native.import.content.autodetect.confirmed"));
                return Views.Section(text("Import_AutoDetectNotSure"), rows.ToArray());
            }
            default:
            {
                var rows = new List<SwiftView>();
                if (!string.IsNullOrEmpty(detected.Note))
                    rows.Add(Caption(style, Format(text("Import_AutoDetectLowHint"), type, confidence)));
                rows.Add(style.Text(Views.Text(text("Import_AutoDetectSelectType")), SourceTextRole.Body2)
                    .WithAccessibilityIdentifier("native.import.content.autodetect"));
                rows.Add(TypeButtons(import, state, style, text, "native.import.content.autodetect"));
                return Views.Section(text("Import_AutoDetectFailed"), rows.ToArray());
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
            var id = $"{idPrefix}.type.{type.ToString().ToLowerInvariant()}";
            // System bordered buttons: each its own target in the row; the chosen one filled.
            var button = (selected
                    ? NativeForm.Prominent(style, label, Pick, id)
                    : NativeForm.Bordered(style, label, Pick, id))
                .WithAccessibilitySelected(selected);
            button.IsEnabled = !state.IsParsing;
            return button;
        }).ToArray();
        var grid = Views.LazyVGrid(style.ViewportWidth < 520 ? 2 : 4, buttons).WithFlexibleWidth();
        grid.Spacing = SourceUiLayout.ItemGap;
        return grid;
    }

    private static SwiftView Harvest(ImportContentCoordinator import, bool isOn, ImportHarvest harvest,
        string labelKey, string hintKey, string id, SourceUiStyle style, Func<string, string> text)
    {
        var column = Views.VStack(
            NativeForm.Switch(style, text(labelKey), isOn, value => import.SetHarvest(harvest, value),
                $"native.import.content.harvest.{id}"),
            Caption(style, text(hintKey)));
        column.Spacing = 2;
        return column.WithFlexibleWidth();
    }

    // ---- Preview, target, duplicate handling ----

    private static SwiftView PreviewSection(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text, ImportContentDrawn drawn)
    {
        var format = ImportServiceText.DetectedFormat(state.DetectedFormat, text);
        var rows = new List<SwiftView>
        {
            NativeForm.Value(text("Import_DetectedFormat"), format,
                "native.import.content.format.detected"),
        };
        var warnings = state.Warnings.IsDefault ? [] : state.Warnings;
        for (var index = 0; index < warnings.Length; index++)
        {
            var message = ImportServiceText.Message(warnings[index], text);
            rows.Add(Views.Label(message, "exclamationmark.triangle")
                .WithForegroundColor(WarningText.Light, WarningText.Dark)
                .WithAccessibilityLabel($"{text("NativeMessage_Warning")}: {message}")
                .WithAccessibilityIdentifier($"native.import.content.warning.{index}"));
        }

        var count = style.Text(Views.Text(SelectedCount(state.SelectedCount, state.Rows.Length, text)),
                SourceTextRole.Caption1, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier("native.import.content.selected.count");
        drawn.SelectedCount = count;
        rows.Add(NativeForm.Switch(style, text("Vocabulary_SelectAll"), state.SelectAllRows,
            import.SetSelectAll, "native.import.content.select-all"));
        rows.Add(count);
        return Views.Section(text("Import_Step3_Title"), rows.ToArray());
    }

    private static SwiftView[] PreviewRows(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text, ImportContentDrawn drawn)
    {
        var total = state.Rows.Length;
        return state.Rows.Select(row => PreviewRow(import, row, style, text, drawn, total)).ToArray();
    }

    private static SwiftView PreviewRow(ImportContentCoordinator import, ImportPreviewRow row,
        SourceUiStyle style, Func<string, string> text, ImportContentDrawn drawn, int total)
    {
        var id = $"native.import.content.row.{row.Index}";
        var number = row.RowNumber.ToString(CultureInfo.CurrentCulture);
        var select = NativeForm.Switch(style, $"#{number}", row.Selected, value =>
                {
                    var selected = import.SetRowSelected(row.Index, value);
                    if (drawn.SelectedCount is { } count)
                        count.Text = SelectedCount(selected, total, text);
                }, $"{id}.select")
            .WithAccessibilityLabel(Format(text("Import_SelectRow"), number));
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

        // The row's two terms, each beside its label (one Form row per preview line).
        var target = NativeForm.Field(text("Import_TargetLanguageTerm"), row.Target, $"{id}.target",
            value => import.SetRowTarget(row.Index, value));
        var native = NativeForm.Field(text("Import_NativeLanguageTerm"), row.Native, $"{id}.native",
            value => import.SetRowNative(row.Index, value));

        var column = Views.VStack(select, badgeRow, target, Views.Divider(), native)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        column.Spacing = SourceUiLayout.ItemGap;
        return column;
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

    private static SwiftView TargetSection(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text)
    {
        // The two targets as the system segmented control (the source's two radios).
        var mode = Views.Picker(text("Import_Step5_Title"),
                [
                    new SwiftPickerOption(nameof(ImportTargetMode.New), text("Import_CreateNewResource")),
                    new SwiftPickerOption(nameof(ImportTargetMode.Existing),
                        text("Import_AddToExistingResource")),
                ],
                state.TargetMode.ToString(),
                value => import.SetTargetMode(Enum.Parse<ImportTargetMode>(value)))
            .WithPickerStyle(SwiftPickerStyle.Segmented)
            .WithAccessibilityIdentifier("native.import.content.target.mode");
        var rows = new List<SwiftView> { mode };
        if (state.TargetMode == ImportTargetMode.New)
        {
            // The error sits in the title's row; typing empties it in place, so the row closes
            // up to the field (a hidden view would keep its room).
            var error = state.ShowTitleValidationError
                ? NativeForm.Notice(style, text("Import_NewResourceTitleRequired"),
                    "native.import.content.title.error", error: true)
                : null;
            var title = NativeForm.Field(text("Import_ResourceTitleLabel"), state.NewResourceTitle,
                "native.import.content.title", value =>
                {
                    if (import.SetNewResourceTitle(value) && error is not null)
                    {
                        error.Text = string.Empty;
                        error.WithHidden(true);
                    }
                }, text("Import_ResourceTitlePlaceholder"));
            if (error is null)
                rows.Add(title);
            else
            {
                var titleRow = Views.VStack(title, error).WithFlexibleWidth();
                titleRow.Spacing = 4;
                rows.Add(titleRow);
            }
            rows.Add(NativeForm.Field(text("Import_ResourceDescriptionLabel"),
                state.NewResourceDescription, "native.import.content.description",
                import.SetNewResourceDescription, text("Import_ResourceDescriptionPlaceholder")));
            var languages = ImportContentCoordinator.Languages
                .Select(language => new SwiftPickerOption(language, language)).ToArray();
            rows.Add(Choice(style, text("Import_TargetLanguageLabel"), languages, state.TargetLanguage,
                "native.import.content.target.language", import.SetTargetLanguage, true));
            rows.Add(Choice(style, text("Import_NativeLanguageLabel"), languages, state.NativeLanguage,
                "native.import.content.native.language", import.SetNativeLanguage, true));
        }
        else
        {
            const string none = "native.import.content.resource.none";
            var options = new List<SwiftPickerOption>
            {
                new(none, text("Import_SelectResourcePlaceholder"))
            };
            options.AddRange(state.Resources.Select(resource =>
                new SwiftPickerOption(resource.Id, $"{resource.Title} ({resource.Language})")));
            // A selected id not (or not yet) in the list shows the placeholder, never the raw id.
            var selectedResource = options.Any(option => option.Value == state.SelectedResourceId)
                ? state.SelectedResourceId : none;
            rows.Add(Choice(style, text("Import_SelectExistingResourceLabel"), options,
                selectedResource,
                "native.import.content.resource",
                value => import.SetSelectedResource(value == none ? string.Empty : value),
                !state.ResourcesLoading));
            if (state.ResourcesLoading)
                rows.Add(Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier("native.import.content.resource.loading"));
            else if (state.ResourcesErrorKey is { } resourcesError)
            {
                // The list could not be read: say so and offer Retry instead of an empty select.
                rows.Add(NativeForm.Notice(style, text(resourcesError),
                    "native.import.content.resource.error", error: true));
                rows.Add(NativeForm.Action(style, text(NativeText.Retry), "arrow.clockwise",
                    () => _ = import.RetryResourcesAsync(), "native.import.content.resource.retry"));
            }
        }
        return Views.Section(text("Import_Step5_Title"), rows.ToArray());
    }

    private static SwiftView DedupSection(ImportContentCoordinator import, ImportContentState state,
        SourceUiStyle style, Func<string, string> text) =>
        Views.Section(text("Import_Step6_Title"),
            Option(style, text("Import_DedupMode_Skip"), text("Import_DedupMode_Skip_Help"),
                state.DedupMode == DedupMode.Skip, () => import.SetDedupMode(DedupMode.Skip),
                "native.import.content.dedup.skip"),
            Option(style, text("Import_DedupMode_Update"), text("Import_DedupMode_Update_Help"),
                state.DedupMode == DedupMode.Update, () => import.SetDedupMode(DedupMode.Update),
                "native.import.content.dedup.update", warningHelp: true),
            Option(style, text("Import_DedupMode_ImportAll"), text("Import_DedupMode_ImportAll_Help"),
                state.DedupMode == DedupMode.ImportAll, () => import.SetDedupMode(DedupMode.ImportAll),
                "native.import.content.dedup.import-all"));

    // ---- Results (source step 7) ----

    private static IEnumerable<SwiftView> Results(ImportContentCoordinator import, ImportContentState state,
        ContentImportResult result, SourceUiStyle style, Func<string, string> text,
        ImportContentActions actions)
    {
        var complete = Views.Label(text("Import_ImportComplete"), "checkmark.circle.fill")
            .WithForegroundColor(style.Appearance.Success.Light, style.Appearance.Success.Dark)
            .WithAccessibilityIdentifier("native.import.content.complete");
        yield return Views.Section(string.Empty,
            complete,
            Stat(style, result.CreatedCount, text("Import_Created"), style.Appearance.Success, "created"),
            Stat(style, result.SkippedCount, text("Import_Skipped"), new("#0DCAF0", "#6EDFF6"), "skipped"),
            Stat(style, result.UpdatedCount, text("Import_Updated"), style.Appearance.Warning, "updated"),
            Stat(style, result.FailedCount, text("Import_Failed"), style.Appearance.Danger, "failed"),
            NativeForm.Action(style, text("Import_ViewResource"), "book", () =>
                {
                    if (!string.IsNullOrEmpty(result.ResourceId))
                        actions.ViewResource(result.ResourceId);
                }, "native.import.content.view-resource"),
            NativeForm.Action(style, text("Import_ImportMore"), "arrow.clockwise",
                import.StartNewImport, "native.import.content.import-more"));

        if (result.Items.Count == 0)
            yield break;
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
        var filter = Views.LazyVGrid(style.ViewportWidth < 520 ? 3 : 5, pills.ToArray()).WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.import.content.filter");
        filter.Spacing = SourceUiLayout.ItemGap;
        // Each result is a Form row (the Form draws only the rows on screen).
        var rows = new List<SwiftView> { filter };
        rows.AddRange(state.FilteredItems.Select((item, index) =>
            ResultRow(item, index, style, text, actions)));
        yield return Views.Section(text("Import_ResultDetails"), rows.ToArray());
    }

    /// <summary>A count as a value row, the number in its status color (the label names it).</summary>
    private static SwiftView Stat(SourceUiStyle style, int value, string label, SourceColorPair color,
        string id) =>
        Views.LabeledContent(label, style.Text(Views.Text(value.ToString(CultureInfo.CurrentCulture)),
                    SourceTextRole.Body1Strong)
                .WithForegroundColor(color.Light, color.Dark))
            .WithAccessibilityLabel($"{label}: {value.ToString(CultureInfo.CurrentCulture)}")
            .WithAccessibilityIdentifier($"native.import.content.stat.{id}");

    private static SwiftView Pill(ImportContentCoordinator import, SourceUiStyle style, string label,
        int count, ImportItemStatus? status, ImportItemStatus? current, string id)
    {
        var selected = status == current;
        var title = $"{label} {count.ToString(CultureInfo.CurrentCulture)}";
        var pillId = $"native.import.content.filter.{id}";
        return (selected
                ? NativeForm.Prominent(style, title, () => import.SetResultFilter(status), pillId)
                : NativeForm.Bordered(style, title, () => import.SetResultFilter(status), pillId))
            .WithAccessibilitySelected(selected);
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
        return row;
    }

    // ---- Small pieces ----

    /// <summary>
    /// A choice row with its help under it (the source's radio): the system selection-list
    /// look, a checkmark on the chosen one.
    /// </summary>
    private static SwiftView Option(SourceUiStyle style, string label, string? help, bool selected,
        Action select, string id, bool warningHelp = false)
    {
        var lines = new List<SwiftView> { style.Text(Views.Text(label), SourceTextRole.Body1) };
        if (help is not null)
        {
            var helpText = style.Text(Views.Text(help), SourceTextRole.Caption1, SourceTextTone.Secondary);
            if (warningHelp)
                helpText.WithForegroundColor(style.Appearance.Warning.Light, style.Appearance.Warning.Dark);
            lines.Add(helpText);
        }
        var text = Views.VStack(lines.ToArray()).WithFlexibleWidth();
        text.Spacing = 2;
        var check = Views.Image("checkmark")
            .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithHidden(!selected);
        var row = Views.HStack(text, check).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap;
        return Views.Button(row, select)
            .WithForegroundColor(style.Appearance.Text.Light, style.Appearance.Text.Dark)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(help is null ? label : $"{label}. {help}")
            .WithAccessibilitySelected(selected)
            .WithAccessibilityIdentifier(id);
    }

    private static SwiftView Choice(SourceUiStyle style, string label, IReadOnlyList<SwiftPickerOption> options,
        string selected, string id, Action<string> changed, bool enabled)
    {
        var picker = NativeForm.Choice(style, label, options, selected, id, value =>
        {
            changed(value);
            return true;
        });
        picker.IsEnabled = enabled;
        return picker;
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
}
