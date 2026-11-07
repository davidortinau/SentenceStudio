using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using Color = AndroidX.Compose.Color;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Import Content (source ImportContent.razor; iOS ImportContentView): the wizard cards
/// (source, format hints, auto-detect gate, harvest, editable preview, target, duplicate
/// handling), the Import action in the header (iOS departure 30), and the results with their
/// filter. The page is one lazy list, so a long preview or result list draws only what is on
/// screen. Errors and warnings stay pinned above it until dismissed or the next action; success
/// is the shell's snackbar.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private sealed record ImportItem(string Key, Func<ComposableNode> Content);


    private ComposableNode RenderImportContent(AndroidVisualPalette palette)
    {
        if (IsImportWordOpen && _importWords is { } words)
            return VocabularyWordPage(words, palette);

        var import = _importContent;
        var header = (TopAppBar)ScreenHeader(Localize("Import_Content_Title"), palette, back: true,
            backAutomationId: "native.import.content.back");
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.import.content.screen"),
        };
        if (import is not { IsOpen: true })
        {
            page.Add(header);
            page.Add(new Column(Arrangement.SpacedBy(12), Alignment.Horizontal.CenterHorizontally)
            {
                Modifier.FillMaxWidth().Padding(AndroidVisualTheme.PagePadding),
                import is null
                    ? StatusAlert(Localize("Import_ProfileNotFound"), palette, error: true,
                        automationId: "native.import.content.blocked")
                    : new CircularProgressIndicator
                    {
                        Modifier = Modifier.Size(28).TestTag("native.import.content.loading"),
                    },
            });
            return page;
        }

        var state = import.State;
        if (state.CanCommit)
        {
            // Source PageHeader PrimaryActions: the Import button, shown at every width.
            var label = Localize("Import_CommitButton");
            var commit = new AndroidX.Compose.Button(() => ImportCommand(coordinator => coordinator.CommitAsync()),
                enabled: !state.IsCommitting && !state.IsParsing)
            {
                Modifier = Modifier.Padding(end: 8).TestTag("native.import.content.commit")
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            commit.Add(new Text(label));
            header.Actions = commit;
        }
        page.Add(header);
        if (MessageBanners(state.Messages, palette, index => $"native.import.content.message.{index}",
                () => _importContent?.DismissMessages(), "native.import.content.message.dismiss",
                Localize("Common_Close")) is { } banners)
            page.Add(new Box
            {
                Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
                banners,
            });

        var items = new List<ImportItem>();
        if (state.IsCommitting)
            items.Add(new("committing", () => new Column(Arrangement.SpacedBy(12),
                Alignment.Horizontal.CenterHorizontally)
            {
                Modifier.FillMaxWidth().Padding(48).TestTag("native.import.content.committing"),
                new CircularProgressIndicator { Modifier = Modifier.Size(28) },
                Body(Localize("Import_CommitButton"), palette, secondary: true),
            }));
        // Blocked first: a result set before the device owner changed is never shown.
        else if (state.Blocked)
            items.Add(new("blocked", () => StatusAlert(Localize("Import_ProfileNotFound"), palette,
                error: true, automationId: "native.import.content.blocked")));
        else if (state.Result is { } result)
            ImportResults(items, state, result, palette);
        else
            ImportWizard(items, import, state, palette);

        page.Add(ImportLazyList(items, "native.import.content.scroll"));
        return page;
    }

    // ------------------------------------------------------------ Wizard (source steps 1-6)

    private void ImportWizard(List<ImportItem> items, ImportContentCoordinator import,
        ImportContentState state, AndroidVisualPalette palette)
    {
        var locked = state.SourceLocked;
        items.Add(new("source", () =>
        {
            var label = Localize("Import_PasteContentLabel");
            var paste = new OutlinedTextField(state.RawText,
                value => ImportEdit(coordinator => coordinator.SetRawText(value)),
                enabled: !locked, singleLine: false, minLines: 8)
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.import.content.text")
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Label = new Text(label),
                Placeholder = new Text(Localize("Import_PastePlaceholder")),
            };
            return Card(palette, "native.import.content.source",
                ImportHeading(Localize("Import_Step1_Title"), palette),
                paste,
                ImportCaption(Localize("Import_PasteHint"), palette));
        }));

        items.Add(new("format", () =>
        {
            var contentType = ImportChoice(Localize("Import_ContentTypeLabel"),
                [
                    (nameof(ContentType.Vocabulary), Localize("Import_ContentType_Vocabulary")),
                    (nameof(ContentType.Phrases), Localize("Import_ContentType_Phrases")),
                    (nameof(ContentType.Sentences), Localize("Import_ContentType_Sentences")),
                    (nameof(ContentType.Transcript), Localize("Import_ContentType_Transcript")),
                    (nameof(ContentType.Auto), Localize("Import_ContentType_Auto")),
                ],
                state.ContentType.ToString(), "native.import.content.type",
                value => ImportEdit(coordinator => coordinator.SetContentType(Enum.Parse<ContentType>(value))),
                !locked, palette);
            var delimiter = ImportChoice(Localize("Import_DelimiterLabel"),
                [
                    (ImportDelimiters.Auto, Localize("Import_Delimiter_Auto")),
                    (ImportDelimiters.Comma, Localize("Import_Delimiter_Comma")),
                    (ImportDelimiters.Tab, Localize("Import_Delimiter_Tab")),
                    (ImportDelimiters.Pipe, Localize("Import_Delimiter_Pipe")),
                ],
                state.Delimiter, "native.import.content.delimiter",
                value => ImportEdit(coordinator => coordinator.SetDelimiter(value)), !locked, palette);
            var headerRow = SettingSwitch(Localize("Import_HasHeaderRow"), "native.import.content.header",
                state.HasHeaderRow, value => ImportEdit(coordinator => coordinator.SetHasHeaderRow(value)),
                !locked, palette);
            var previewLabel = Localize("Import_PreviewButton");
            var preview = new AndroidX.Compose.Button(() => ImportCommand(coordinator => coordinator.PreviewAsync()),
                enabled: import.CanPreview)
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag("native.import.content.preview")
                    .Semantics(semantics => semantics.ContentDescription(previewLabel)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            preview.Add(new Text(previewLabel));
            var previewRow = new Row(Arrangement.End, Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
            };
            if (state.IsParsing)
                previewRow.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Padding(end: 12).Size(24).TestTag("native.import.content.parsing")
                        .Semantics(semantics => semantics.ContentDescription(Localize("Common_Loading"))),
                });
            previewRow.Add(preview);
            return Card(palette, "native.import.content.format",
                ImportHeading(Localize("Import_Step2_Title"), palette),
                contentType, delimiter, headerRow, previewRow);
        }));

        if (state.AutoDetect is { } detected && state.Step >= ImportContentStep.Harvest)
            items.Add(new("autodetect", () => ImportAutoDetectGate(state, detected, palette)));

        if (state.ShowTypeChooser)
            items.Add(new("chooser", () => Card(palette, "native.import.content.chooser",
                Body(Localize("Import_SelectContentType"), palette),
                ImportTypeButtons(state, "native.import.content.chooser"),
                ImportTextAction(Localize("Common_Cancel"), "native.import.content.chooser.cancel",
                    () => ImportEdit(coordinator => coordinator.CloseTypeChooser()), true))));

        if (state.Step >= ImportContentStep.Harvest && state.HarvestStepReady)
            items.Add(new("harvest", () =>
            {
                var children = new List<ComposableNode>
                {
                    ImportHeading(Localize("Import_HarvestTitle"), palette),
                    ImportCaption(Localize("Import_HarvestDescription"), palette),
                    ImportHarvestOption(state.HarvestTranscript, ImportHarvest.Transcript,
                        "Import_HarvestTranscriptLabel", "Import_HarvestTranscriptHint", "transcript", palette),
                    ImportHarvestOption(state.HarvestSentences, ImportHarvest.Sentences,
                        "Import_HarvestSentencesLabel", "Import_HarvestSentencesHint", "sentences", palette),
                    ImportHarvestOption(state.HarvestPhrases, ImportHarvest.Phrases,
                        "Import_HarvestPhrasesLabel", "Import_HarvestPhrasesHint", "phrases", palette),
                    ImportHarvestOption(state.HarvestWords, ImportHarvest.Words,
                        "Import_HarvestWordsLabel", "Import_HarvestWordsHint", "words", palette),
                };
                if (state.ShowHarvestValidationError)
                    children.Add(StatusAlert(Localize("Import_HarvestValidationError"), palette, error: true,
                        automationId: "native.import.content.harvest.error"));
                return Card(palette, "native.import.content.harvest", children.ToArray());
            }));

        if (!state.HasPreview || state.Step < ImportContentStep.Preview)
            return;

        items.Add(new("preview", () => ImportPreviewHeader(state, palette)));
        foreach (var row in state.Rows)
            items.Add(new($"row.{row.Index}", () => ImportPreviewRowCard(row, palette)));
        items.Add(new("preview.count", () => new Text(ImportSelectedCount(state.SelectedCount, state.Rows.Length))
        {
            Modifier = Modifier.TestTag("native.import.content.selected.count"),
            Color = palette.SecondaryText,
            FontSize = new Sp(13),
        }));
        items.Add(new("target", () => ImportTargetCard(state, palette)));
        items.Add(new("dedup", () => ImportDedupCard(state, palette)));
    }

    private ComposableNode ImportAutoDetectGate(ImportContentState state, ImportAutoDetect detected,
        AndroidVisualPalette palette)
    {
        var type = ImportTypeName(detected.ContentType);
        var confidence = (detected.Confidence * 100).ToString("F0", CultureInfo.CurrentCulture) + "%";
        switch (detected.Band)
        {
            case ImportConfidenceBand.High:
                return ImportAlert(palette, warning: false, "native.import.content.autodetect",
                    new Text(ImportFormat(Localize("Import_AutoDetected"), type, confidence))
                    {
                        Color = palette.Text,
                        FontSize = new Sp(15),
                    },
                    ImportOutlineAction(Localize("Import_AutoDetectChange"), "native.import.content.autodetect.change",
                        () => ImportEdit(coordinator => coordinator.OpenTypeChooser()), true));
            case ImportConfidenceBand.Medium:
            {
                var children = new List<ComposableNode>
                {
                    new Text(Localize("Import_AutoDetectNotSure"))
                    {
                        Color = palette.Text,
                        FontSize = new Sp(15),
                        FontWeight = FontWeight.SemiBold,
                    },
                    new Text(ImportFormat(Localize("Import_AutoDetectLooksLike"), type, confidence))
                    {
                        Color = palette.Text,
                        FontSize = new Sp(15),
                    },
                };
                if (!state.AutoDetectConfirmed)
                {
                    children.Add(Action(ImportFormat(Localize("Import_AutoDetectConfirm"), type),
                        "native.import.content.autodetect.confirm",
                        () => ImportCommand(coordinator => coordinator.ConfirmAutoDetectAsync()),
                        !state.IsParsing));
                    children.Add(ImportOutlineAction(Localize("Import_AutoDetectPickDifferent"),
                        "native.import.content.autodetect.pick",
                        () => ImportEdit(coordinator => coordinator.OpenTypeChooser()), true));
                }
                else
                    children.Add(new Text(ImportFormat(Localize("Import_AutoDetectConfirmedAs"),
                        ImportTypeName(state.ResolvedContentType)))
                    {
                        Modifier = Modifier.TestTag("native.import.content.autodetect.confirmed"),
                        Color = palette.Success,
                        FontSize = new Sp(13),
                    });
                return ImportAlert(palette, warning: true, "native.import.content.autodetect",
                    children.ToArray());
            }
            default:
            {
                var children = new List<ComposableNode>
                {
                    Body(Localize("Import_AutoDetectFailed"), palette),
                };
                if (!string.IsNullOrEmpty(detected.Note))
                    children.Add(ImportCaption(ImportFormat(Localize("Import_AutoDetectLowHint"), type, confidence),
                        palette));
                children.Add(Body(Localize("Import_AutoDetectSelectType"), palette));
                children.Add(ImportTypeButtons(state, "native.import.content.autodetect"));
                return Card(palette, "native.import.content.autodetect", children.ToArray());
            }
        }
    }

    /// <summary>The four type buttons of the low-confidence gate and the chooser (source: no Auto).</summary>
    private ComposableNode ImportTypeButtons(ImportContentState state, string idPrefix)
    {
        ContentType[] types = [ContentType.Vocabulary, ContentType.Phrases,
            ContentType.Sentences, ContentType.Transcript];
        var row = new FlowRow { Modifier.FillMaxWidth() };
        foreach (var type in types)
        {
            var selected = state.ResolvedContentType == type;
            var label = ImportTypeName(type);
            var chip = new FilterChip(selected,
                () => ImportCommand(coordinator => coordinator.OverrideContentTypeAsync(type)),
                enabled: !state.IsParsing)
            {
                Modifier = Modifier.Padding(end: 8)
                    .TestTag($"{idPrefix}.type.{type.ToString().ToLowerInvariant()}")
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(label);
                        semantics.Selected(selected);
                    }),
                Label = new Text(label),
            };
            row.Add(chip);
        }
        return row;
    }

    private ComposableNode ImportHarvestOption(bool isOn, ImportHarvest harvest, string labelKey,
        string hintKey, string id, AndroidVisualPalette palette) =>
        new Column(Arrangement.SpacedBy(2))
        {
            Modifier.FillMaxWidth(),
            SettingSwitch(Localize(labelKey), $"native.import.content.harvest.{id}", isOn,
                value => ImportEdit(coordinator => coordinator.SetHarvest(harvest, value)), true, palette),
            ImportCaption(Localize(hintKey), palette),
        };

    // ------------------------------------------------------------ Preview, target, duplicate handling

    private ComposableNode ImportPreviewHeader(ImportContentState state, AndroidVisualPalette palette)
    {
        var format = ImportServiceText.DetectedFormat(state.DetectedFormat, Localize);
        var children = new List<ComposableNode>
        {
            ImportHeading(Localize("Import_Step3_Title"), palette),
            new Text($"{Localize("Import_DetectedFormat")}: {format}")
            {
                Modifier = Modifier.TestTag("native.import.content.format.detected"),
                Color = palette.SecondaryText,
                FontSize = new Sp(13),
            },
        };
        if (!state.Warnings.IsDefaultOrEmpty)
        {
            var warningLabel = Localize("NativeMessage_Warning");
            children.Add(ImportAlert(palette, warning: true, "native.import.content.warnings",
                state.Warnings.Select(warning => (ComposableNode)new Text(
                    $"{warningLabel}: {ImportServiceText.Message(warning, Localize)}")
                {
                    Color = palette.Text,
                    FontSize = new Sp(13),
                }).ToArray()));
        }
        children.Add(SettingSwitch(Localize("Vocabulary_SelectAll"), "native.import.content.select-all",
            state.SelectAllRows, value => ImportEdit(coordinator => coordinator.SetSelectAll(value)), true,
            palette));
        return Card(palette, "native.import.content.preview.card", children.ToArray());
    }

    private ComposableNode ImportPreviewRowCard(ImportPreviewRow row, AndroidVisualPalette palette)
    {
        var id = $"native.import.content.row.{row.Index}";
        var number = row.RowNumber.ToString(CultureInfo.CurrentCulture);
        var selectLabel = ImportFormat(Localize("Import_SelectRow"), number);
        var select = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Checkbox(@checked: row.Selected,
                onCheckedChange: value => ImportEdit(coordinator => coordinator.SetRowSelected(row.Index, value)),
                enabled: row.Status != RowStatus.Error)
            {
                Modifier = Modifier.TestTag($"{id}.select")
                    .Semantics(semantics => semantics.ContentDescription(selectLabel)),
            },
            Body($"#{number}", palette, secondary: true),
        };

        var badges = new FlowRow { Modifier.FillMaxWidth() };
        badges.Add(ImportRowStatusBadge(row, palette, id));
        if (row.Duplicate)
            badges.Add(ImportBadge(Localize("Import_PreviewDuplicateBadge"), palette.Warning, Color.FromHex("#000000"),
                $"{id}.duplicate",
                $"{Localize("Import_PreviewDuplicateBadge")}: {ImportDuplicateReason(row.DuplicateReason)}"));
        if (row.AiTranslated)
            badges.Add(ImportBadge(Localize("Import_AiTranslated_Badge"), palette.SelectedContainer, palette.Text,
                $"{id}.ai", $"{Localize("Import_AiTranslated_Badge")}: {Localize("Import_AiTranslated_Tooltip")}"));

        var targetLabel = Localize("Import_TargetLanguageTerm");
        var target = new OutlinedTextField(row.Target,
            value => ImportEdit(coordinator => coordinator.SetRowTarget(row.Index, value)), singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag($"{id}.target")
                .Semantics(semantics => semantics.ContentDescription(targetLabel)),
            Label = new Text(targetLabel),
        };
        var nativeLabel = Localize("Import_NativeLanguageTerm");
        var native = new OutlinedTextField(row.Native,
            value => ImportEdit(coordinator => coordinator.SetRowNative(row.Index, value)), singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag($"{id}.native")
                .Semantics(semantics => semantics.ContentDescription(nativeLabel)),
            Label = new Text(nativeLabel),
        };
        // Source: error rows are table-danger, warning rows table-warning.
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(id)
                .Border(1, row.Status == RowStatus.Error ? palette.Danger : palette.Border,
                    AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = row.Status switch
            {
                RowStatus.Error => palette.DangerSurface,
                RowStatus.Warning => palette.Elevated,
                _ => palette.Card,
            },
        };
        surface.Add(new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(12),
            select,
            badges,
            target,
            native,
        });
        return surface;
    }

    private ComposableNode ImportRowStatusBadge(ImportPreviewRow row, AndroidVisualPalette palette, string id)
    {
        var (key, fill, text) = row.Status switch
        {
            RowStatus.Ok => ("Common_OKButton", palette.Success, Color.FromHex("#FFFFFF")),
            RowStatus.Warning => ("NativeMessage_Warning", palette.Warning, Color.FromHex("#000000")),
            _ => ("Common_Error", palette.Danger, Color.FromHex("#FFFFFF")),
        };
        var label = Localize(key);
        // Source shows the row's error as the badge's title (a hover tooltip): TalkBack reads it here.
        var description = row.Error is { Length: > 0 } error
            ? $"{label}: {ImportServiceText.Message(error, Localize)}"
            : label;
        return ImportBadge(label, fill, text, $"{id}.status", description);
    }

    private ComposableNode ImportTargetCard(ImportContentState state, AndroidVisualPalette palette)
    {
        var children = new List<ComposableNode>
        {
            ImportHeading(Localize("Import_Step5_Title"), palette),
            ImportRadio(Localize("Import_CreateNewResource"), null, state.TargetMode == ImportTargetMode.New,
                () => ImportEdit(coordinator => coordinator.SetTargetMode(ImportTargetMode.New)),
                "native.import.content.target.new", palette),
            ImportRadio(Localize("Import_AddToExistingResource"), null,
                state.TargetMode == ImportTargetMode.Existing,
                () => ImportEdit(coordinator => coordinator.SetTargetMode(ImportTargetMode.Existing)),
                "native.import.content.target.existing", palette),
        };
        if (state.TargetMode == ImportTargetMode.New)
        {
            var titleLabel = Localize("Import_ResourceTitleLabel");
            children.Add(new OutlinedTextField(state.NewResourceTitle,
                value => ImportEdit(coordinator => coordinator.SetNewResourceTitle(value)), singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.import.content.title")
                    .Semantics(semantics => semantics.ContentDescription(titleLabel)),
                Label = new Text(titleLabel),
                Placeholder = new Text(Localize("Import_ResourceTitlePlaceholder")),
            });
            if (state.ShowTitleValidationError)
                children.Add(new Text(Localize("Import_NewResourceTitleRequired"))
                {
                    Modifier = Modifier.TestTag("native.import.content.title.error"),
                    Color = palette.Danger,
                    FontSize = new Sp(13),
                });
            var descriptionLabel = Localize("Import_ResourceDescriptionLabel");
            children.Add(new OutlinedTextField(state.NewResourceDescription,
                value => ImportEdit(coordinator => coordinator.SetNewResourceDescription(value)), singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.import.content.description")
                    .Semantics(semantics => semantics.ContentDescription(descriptionLabel)),
                Label = new Text(descriptionLabel),
                Placeholder = new Text(Localize("Import_ResourceDescriptionPlaceholder")),
            });
            var languages = ImportContentCoordinator.Languages.Select(language => (language, language)).ToArray();
            children.Add(ImportChoice(Localize("Import_TargetLanguageLabel"), languages, state.TargetLanguage,
                "native.import.content.target.language",
                value => ImportEdit(coordinator => coordinator.SetTargetLanguage(value)), true, palette));
            children.Add(ImportChoice(Localize("Import_NativeLanguageLabel"), languages, state.NativeLanguage,
                "native.import.content.native.language",
                value => ImportEdit(coordinator => coordinator.SetNativeLanguage(value)), true, palette));
        }
        else
        {
            const string none = "native.import.content.resource.none";
            var options = new List<(string, string)>
            {
                (none, $"-- {Localize("Import_SelectResourcePlaceholder")} --"),
            };
            options.AddRange(state.Resources.Select(resource =>
                (resource.Id, $"{resource.Title} ({resource.Language})")));
            children.Add(ImportChoice(Localize("Import_SelectExistingResourceLabel"), options,
                state.SelectedResourceId.Length == 0 ? none : state.SelectedResourceId,
                "native.import.content.resource",
                value => ImportEdit(coordinator => coordinator.SetSelectedResource(value == none ? string.Empty : value)),
                !state.ResourcesLoading, palette));
            if (state.ResourcesLoading)
                children.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(24).TestTag("native.import.content.resource.loading")
                        .Semantics(semantics => semantics.ContentDescription(Localize("Common_Loading"))),
                });
            else if (state.ResourcesErrorKey is { } resourcesError)
            {
                // The list could not be read: say so and offer Retry instead of an empty select.
                children.Add(StatusAlert(Localize(resourcesError), palette, error: true,
                    automationId: "native.import.content.resource.error"));
                children.Add(Action(Localize(NativeText.Retry), "native.import.content.resource.retry",
                    () => ImportCommand(coordinator => coordinator.RetryResourcesAsync()), true, primary: false));
            }
        }
        return Card(palette, "native.import.content.target", children.ToArray());
    }

    private ComposableNode ImportDedupCard(ImportContentState state, AndroidVisualPalette palette) =>
        Card(palette, "native.import.content.dedup",
            ImportHeading(Localize("Import_Step6_Title"), palette),
            ImportRadio(Localize("Import_DedupMode_Skip"), Localize("Import_DedupMode_Skip_Help"),
                state.DedupMode == DedupMode.Skip,
                () => ImportEdit(coordinator => coordinator.SetDedupMode(DedupMode.Skip)),
                "native.import.content.dedup.skip", palette),
            ImportRadio(Localize("Import_DedupMode_Update"), Localize("Import_DedupMode_Update_Help"),
                state.DedupMode == DedupMode.Update,
                () => ImportEdit(coordinator => coordinator.SetDedupMode(DedupMode.Update)),
                "native.import.content.dedup.update", palette, warningHelp: true),
            ImportRadio(Localize("Import_DedupMode_ImportAll"), Localize("Import_DedupMode_ImportAll_Help"),
                state.DedupMode == DedupMode.ImportAll,
                () => ImportEdit(coordinator => coordinator.SetDedupMode(DedupMode.ImportAll)),
                "native.import.content.dedup.import-all", palette));

    // ------------------------------------------------------------ Results (source step 7)

    private void ImportResults(List<ImportItem> items, ImportContentState state, ContentImportResult result,
        AndroidVisualPalette palette)
    {
        items.Add(new("complete", () => new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(top: 8).TestTag("native.import.content.complete"),
            new Icon(Icons.Default.CheckCircle, null) { Modifier = Modifier.Size(28), Tint = palette.Success },
            Heading(Localize("Import_ImportComplete"), palette, 22,
                Modifier.Semantics(semantics => semantics.Heading())),
        }));
        // Two columns, like the source's grid at phone width.
        items.Add(new("stats", () => new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().TestTag("native.import.content.stats"),
            new Row(Arrangement.SpacedBy(8))
            {
                Modifier.FillMaxWidth(),
                ImportStat(result.CreatedCount, Localize("Import_Created"), palette.Success, "created", palette),
                ImportStat(result.SkippedCount, Localize("Import_Skipped"), palette.Primary, "skipped", palette),
            },
            new Row(Arrangement.SpacedBy(8))
            {
                Modifier.FillMaxWidth(),
                ImportStat(result.UpdatedCount, Localize("Import_Updated"), palette.Warning, "updated", palette),
                ImportStat(result.FailedCount, Localize("Import_Failed"), palette.Danger, "failed", palette),
            },
        }));
        items.Add(new("actions", () => new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth(),
            Action(Localize("Import_ViewResource"), "native.import.content.view-resource",
                () => ViewImportedResource(result.ResourceId), !string.IsNullOrEmpty(result.ResourceId)),
            Action(Localize("Import_ImportMore"), "native.import.content.import-more",
                () => ImportEdit(coordinator => coordinator.StartNewImport()), true, primary: false),
        }));
        if (result.Items.Count == 0)
            return;
        items.Add(new("details", () => Heading(Localize("Import_ResultDetails"), palette, 20,
            Modifier.Semantics(semantics => semantics.Heading()))));
        items.Add(new("filter", () =>
        {
            var filter = new FlowRow { Modifier.FillMaxWidth().TestTag("native.import.content.filter") };
            filter.Add(ImportFilterPill(Localize("Import_FilterAll"), result.Items.Count, null, state.ResultFilter, "all"));
            if (result.CreatedCount > 0)
                filter.Add(ImportFilterPill(Localize("Import_Created"), result.CreatedCount,
                    ImportItemStatus.Created, state.ResultFilter, "created"));
            if (result.UpdatedCount > 0)
                filter.Add(ImportFilterPill(Localize("Import_Updated"), result.UpdatedCount,
                    ImportItemStatus.Updated, state.ResultFilter, "updated"));
            if (result.SkippedCount > 0)
                filter.Add(ImportFilterPill(Localize("Import_Skipped"), result.SkippedCount,
                    ImportItemStatus.Skipped, state.ResultFilter, "skipped"));
            if (result.FailedCount > 0)
                filter.Add(ImportFilterPill(Localize("Import_Failed"), result.FailedCount,
                    ImportItemStatus.Failed, state.ResultFilter, "failed"));
            return filter;
        }));
        var index = 0;
        foreach (var item in state.FilteredItems)
        {
            var rowIndex = index++;
            items.Add(new($"item.{rowIndex}.{item.VocabularyWordId}.{item.Lemma}",
                () => ImportResultRow(item, rowIndex, palette)));
        }
    }

    private static ComposableNode ImportStat(int value, string label, Color color, string id,
        AndroidVisualPalette palette)
    {
        var count = value.ToString(CultureInfo.CurrentCulture);
        var surface = new Surface
        {
            Modifier = Modifier.Weight(1f)
                .TestTag($"native.import.content.stat.{id}")
                .Semantics(semantics => semantics.ContentDescription($"{label}: {count}")),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        surface.Add(new Column(Arrangement.SpacedBy(2), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().Padding(AndroidVisualTheme.CardPadding),
            new Text(count) { Color = color, FontSize = new Sp(28), FontWeight = FontWeight.Bold },
            new Text(label) { Color = palette.SecondaryText, FontSize = new Sp(13) },
        });
        return surface;
    }

    private ComposableNode ImportFilterPill(string label, int count, ImportItemStatus? status,
        ImportItemStatus? current, string id)
    {
        var selected = status == current;
        var title = $"{label} {count.ToString(CultureInfo.CurrentCulture)}";
        return new FilterChip(selected, () => ImportEdit(coordinator => coordinator.SetResultFilter(status)),
            enabled: true)
        {
            Modifier = Modifier.Padding(end: 8).TestTag($"native.import.content.filter.{id}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(title);
                    semantics.Selected(selected);
                }),
            Label = new Text(title),
        };
    }

    private ComposableNode ImportResultRow(ContentImportItemResult item, int index, AndroidVisualPalette palette)
    {
        var id = $"native.import.content.item.{index}";
        var clickable = ImportContentCoordinator.CanOpenWord(item);
        var typeKey = item.Type switch
        {
            LexicalUnitType.Word => "Vocabulary_FilterTypeWord",
            LexicalUnitType.Phrase => "Vocabulary_FilterTypePhrase",
            LexicalUnitType.Sentence => "Vocabulary_FilterTypeSentence",
            _ => "Vocabulary_Unknown",
        };
        var (statusKey, statusFill, statusText) = item.Status switch
        {
            ImportItemStatus.Created => ("Import_Created", palette.Success, Color.FromHex("#FFFFFF")),
            ImportItemStatus.Updated => ("Import_Updated", palette.Warning, Color.FromHex("#000000")),
            ImportItemStatus.Skipped => ("Import_Skipped", Color.FromHex("#0DCAF0"), Color.FromHex("#000000")),
            ImportItemStatus.Failed => ("Import_Failed", palette.Danger, Color.FromHex("#FFFFFF")),
            _ => ("Vocabulary_Unknown", palette.SecondaryText, Color.FromHex("#FFFFFF")),
        };
        var reason = ImportServiceText.Message(item.Reason, Localize);
        // The type and status are part of what TalkBack reads, not color only.
        var description = string.Join(", ", new[]
        {
            item.Lemma, item.NativeLanguageTerm, Localize(typeKey), Localize(statusKey), reason,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var lemma = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            new Text(item.Lemma) { Color = palette.Text, FontSize = new Sp(16), FontWeight = FontWeight.SemiBold },
        };
        if (clickable)
            lemma.Add(new Icon(Icons.AutoMirrored.Default.KeyboardArrowRight, null)
            {
                Modifier = Modifier.Size(18),
                Tint = palette.SecondaryText,
            });
        var column = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().Padding(horizontal: 16, vertical: 12),
            lemma,
            Body(item.NativeLanguageTerm, palette, secondary: true),
            new FlowRow
            {
                ImportBadge(Localize(typeKey), palette.SecondarySurface, palette.Text, $"{id}.type", Localize(typeKey)),
                ImportBadge(Localize(statusKey), statusFill, statusText, $"{id}.status", Localize(statusKey)),
            },
        };
        if (reason.Length > 0)
            column.Add(new Text(reason) { Color = palette.Danger, FontSize = new Sp(13) });
        var modifier = Modifier.FillMaxWidth().TestTag(id);
        modifier = clickable && item.VocabularyWordId is { } wordId
            ? modifier.Clickable(() => OpenImportedWord(wordId))
            : modifier;
        var surface = new Surface
        {
            Modifier = modifier.Semantics(semantics =>
            {
                semantics.ContentDescription(description);
                if (clickable)
                    semantics.Role(SemanticsRole.Button);
            }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Card,
        };
        surface.Add(column);
        return surface;
    }

    // ------------------------------------------------------------ Small pieces

    private static Text ImportHeading(string value, AndroidVisualPalette palette) =>
        Heading(value, palette, 20, Modifier.Semantics(semantics => semantics.Heading()));

    private static Text ImportCaption(string value, AndroidVisualPalette palette) => new(value)
    {
        Color = palette.SecondaryText,
        FontSize = new Sp(13),
    };

    /// <summary>An info or warning alert in the theme's colors (warning: a warning-colored border).</summary>
    private static ComposableNode ImportAlert(AndroidVisualPalette palette, bool warning, string tag,
        params ComposableNode[] children)
    {
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(tag)
                .Border(1, warning ? palette.Warning : palette.Primary, AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Elevated,
            ContentColor = palette.Text,
        };
        var column = new Column(Arrangement.SpacedBy(8)) { Modifier.FillMaxWidth().Padding(16) };
        foreach (var child in children)
            column.Add(child);
        surface.Add(column);
        return surface;
    }

    private static ComposableNode ImportBadge(string label, Color fill, Color text, string tag, string description)
    {
        var surface = new Surface
        {
            Modifier = Modifier.Padding(end: 6, bottom: 4).TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(description)),
            Shape = Shape.RoundedCorners(6),
            Color = fill,
        };
        surface.Add(new Text(label)
        {
            Modifier = Modifier.Padding(horizontal: 8, vertical: 4),
            Color = text,
            FontSize = new Sp(12),
            FontWeight = FontWeight.SemiBold,
        });
        return surface;
    }

    private static ComposableNode ImportOutlineAction(string label, string tag, Action command, bool enabled)
    {
        var button = new OutlinedButton(command, enabled)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight).TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Icon(Icons.Default.Edit, null) { Modifier = Modifier.Size(18).Padding(end: 4) });
        button.Add(new Text(label));
        return button;
    }

    private static ComposableNode ImportTextAction(string label, string tag, Action command, bool enabled)
    {
        var button = new TextButton(command, enabled)
        {
            Modifier = Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Text(label));
        return button;
    }

    /// <summary>A radio row: the whole row selects (one TalkBack node with the radio role).</summary>
    private static ComposableNode ImportRadio(string label, string? help, bool selected, Action select,
        string tag, AndroidVisualPalette palette, bool warningHelp = false)
    {
        var lines = new Column(Arrangement.SpacedBy(2))
        {
            Modifier.Weight(1f),
            Body(label, palette),
        };
        if (help is not null)
            lines.Add(new Text(help)
            {
                Color = warningHelp ? palette.WarningText : palette.SecondaryText,
                FontSize = new Sp(13),
            });
        return new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .Selectable(selected, select)
                .TestTag(tag)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(help is null ? label : $"{label}. {help}");
                    semantics.Role(SemanticsRole.RadioButton);
                    semantics.Selected(selected);
                }),
            new AndroidX.Compose.RadioButton(selected, select, true),
            lines,
        };
    }

    /// <summary>A labeled select: an outlined button that opens a menu of the options.</summary>
    private ComposableNode ImportChoice(string label, IReadOnlyList<(string Value, string Label)> options,
        string selected, string tag, Action<string> changed, bool enabled, AndroidVisualPalette palette)
    {
        var current = options.FirstOrDefault(option => option.Value == selected).Label ?? selected;
        var anchor = new OutlinedButton(() => ToggleImportMenu(tag), enabled: enabled)
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight).TestTag(tag)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription($"{label}: {current}");
                    semantics.StateDescription(current);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        anchor.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Text(current) { Color = palette.Text },
            new Icon(Icons.Default.ArrowDropDown, null) { Modifier = Modifier.Size(20), Tint = palette.Text },
        });
        var menu = PopupMenu(expanded: enabled && _importMenu == tag, onDismissRequest: CloseImportMenu);
        foreach (var option in options)
        {
            var value = option.Value;
            var isSelected = value == selected;
            var item = new DropdownMenuItem(text: new Text(option.Label), onClick: () =>
            {
                CloseImportMenu();
                changed(value);
            })
            {
                Modifier = Modifier.TestTag($"{tag}.option.{value}")
                    .Semantics(semantics => semantics.Selected(isSelected)),
            };
            if (isSelected)
                item.LeadingIcon = new Icon(Icons.Default.Check, null)
                {
                    Modifier = Modifier.Size(18),
                    Tint = palette.Primary,
                };
            menu.Add(item);
        }
        return new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth(),
            Body(label, palette, secondary: true),
            new Box { anchor, menu },
        };
    }

    private string ImportTypeName(ContentType type) => type switch
    {
        ContentType.Vocabulary => Localize("Import_ContentType_Vocabulary"),
        ContentType.Phrases => Localize("Import_ContentType_Phrases"),
        ContentType.Sentences => Localize("Import_ContentType_Sentences"),
        ContentType.Transcript => Localize("Import_ContentType_Transcript"),
        _ => Localize("Import_ContentType_Auto"),
    };

    private string ImportDuplicateReason(string? reason) => reason switch
    {
        "AlreadyInVocabulary" => Localize("Import_PreviewDuplicate_AlreadyInVocabulary"),
        "DuplicateWithinBatch" => Localize("Import_PreviewDuplicate_DuplicateWithinBatch"),
        _ => Localize("Import_PreviewDuplicateBadge"),
    };

    private static string ImportSelectedCount(int selected, int total) =>
        $"{Localize("Import_SelectedCount")}: {selected.ToString(CultureInfo.CurrentCulture)} / "
        + total.ToString(CultureInfo.CurrentCulture);

    private static string ImportFormat(string template, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, template, values);
}
