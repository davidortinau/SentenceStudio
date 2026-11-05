using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Vocabulary;
using Color = AndroidX.Compose.Color;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The word editor's More menu (source PageHeader SecondaryActions: Save, Find Duplicates,
/// Delete), its delete confirmation (source showDeleteConfirm modal) and the Vocabulary list's
/// duplicate manager (source Vocabulary.razor cleanup panel), in Compose on the shared
/// VocabularyBrowser.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private const string VocabularyWordMenu = "vocabulary.edit.more";

    /// <summary>
    /// Source PageHeader: the primary Save shows from the md breakpoint (768 dp); the More menu
    /// shows at every width, as on iOS (native.vocabulary.edit.more).
    /// </summary>
    private ComposableNode VocabularyWordActions(VocabularyBrowser vocabulary,
        VocabularyBrowserEdit state, AndroidVisualPalette palette)
    {
        var actions = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically);
        if (WindowWidthDp >= 768)
        {
            var save = new TextButton(() => RunMoreOnUi(vocabulary.SaveAsync), enabled: state.CanSave)
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag("native.vocabulary.edit.save.toolbar")
                    .Semantics(semantics => semantics.ContentDescription(Localize("Common_Save"))),
            };
            save.Add(new Text(Localize("Common_Save")));
            actions.Add(save);
        }
        var more = new IconButton(() => ToggleMoreMenu(VocabularyWordMenu),
            enabled: !state.Saving && !state.Deleting)
        {
            Modifier = Modifier.TestTag("native.vocabulary.edit.more")
                .Semantics(semantics => semantics.ContentDescription(Localize("More"))),
        };
        more.Add(new Icon(Icons.Default.MoreVert, Localize("More"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var menu = PopupMenu(expanded: _moreMenu == VocabularyWordMenu,
            onDismissRequest: CloseMoreMenu);
        menu.Add(VocabularyMenuItem(Localize("Common_Save"), "native.vocabulary.edit.save.menu",
            state.CanSave, () => RunMoreOnUi(vocabulary.SaveAsync)));
        menu.Add(VocabularyMenuItem(Localize("VocabWordEdit_FindDuplicates"),
            "native.vocabulary.edit.duplicates.menu", state.CanFindDuplicates,
            () => RunMoreOnUi(vocabulary.FindDuplicatesAsync)));
        menu.Add(new HorizontalDivider { Color = palette.Border });
        // Source: the Delete item is styled text-danger.
        menu.Add(VocabularyMenuItem(Localize("Common_Delete"), "native.vocabulary.edit.delete.menu",
            state.CanDelete, vocabulary.RequestDelete,
            state.CanDelete ? palette.Danger : null));
        actions.Add(new Box { more, menu });
        return actions;
    }

    private DropdownMenuItem VocabularyMenuItem(string label, string tag, bool enabled,
        Action action, Color? color = null)
    {
        var text = new Text(label);
        if (color is { } tint)
            text.Color = tint;
        return new(text: text, onClick: () =>
        {
            CloseMoreMenu();
            action();
        })
        {
            Enabled = enabled,
            Modifier = Modifier.TestTag(tag),
        };
    }

    /// <summary>Source ConfirmDelete modal: the word's term, Cancel and a red Delete.</summary>
    private ComposableNode VocabularyDeleteDialog(VocabularyBrowser vocabulary,
        VocabularyBrowserEdit state, NativeVocabularyDetail detail, AndroidVisualPalette palette)
    {
        var pending = state.Deleting;
        var confirm = new TextButton(() => RunMoreOnUi(vocabulary.ConfirmDeleteAsync), enabled: !pending)
        {
            Modifier = Modifier.TestTag("native.vocabulary.edit.delete.confirm"),
        };
        var label = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically);
        if (pending)
            label.Add(new CircularProgressIndicator { Modifier = Modifier.Size(16) });
        label.Add(new Text(Localize("Common_Delete")) { Color = palette.Danger });
        confirm.Add(label);
        var cancel = new TextButton(vocabulary.CancelDelete, enabled: !pending)
        {
            Modifier = Modifier.TestTag("native.vocabulary.edit.delete.cancel"),
        };
        cancel.Add(new Text(Localize("Common_Cancel")));
        return new AndroidX.Compose.AlertDialog(() =>
        {
            if (!pending)
                vocabulary.CancelDelete();
        })
        {
            Modifier = Modifier.TestTag("native.vocabulary.edit.delete"),
            Icon = new Icon(Icons.Default.Warning, null) { Tint = palette.Danger },
            Title = new Text(Localize("VocabWordEdit_ConfirmDelete")),
            Text = new Text(VocabularyWordCommands.DeleteMessage(detail, Localize)),
            ConfirmButton = confirm,
            DismissButton = cancel,
        };
    }

    /// <summary>Source: Find Duplicates beside the Vocabulary Terms heading.</summary>
    private ComposableNode VocabularyTermsHeading(VocabularyBrowser vocabulary,
        VocabularyBrowserEdit state, AndroidVisualPalette palette)
    {
        var label = Localize("VocabWordEdit_FindDuplicates");
        var find = new OutlinedButton(() => RunMoreOnUi(vocabulary.FindDuplicatesAsync),
            state.CanFindDuplicates)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag("native.vocabulary.edit.duplicates")
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        find.Add(new Text(label));
        var heading = Heading(Localize("VocabWordEdit_VocabularyTerms"), palette, 20,
            Modifier.Weight(1f)
                .TestTag("native.vocabulary.edit.section.VocabWordEdit_VocabularyTerms")
                .Semantics(semantics => semantics.Heading()));
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.edit.terms.header"),
            heading,
            find,
        };
    }

    /// <summary>Source Vocabulary.razor duplicate manager (vocabulary-cleanup-panel).</summary>
    private ComposableNode VocabularyDuplicatesPanel(VocabularyBrowser vocabulary,
        VocabularyDuplicatePanel panel, AndroidVisualPalette palette)
    {
        var close = new IconButton(vocabulary.CloseDuplicates, enabled: true)
        {
            Modifier = Modifier.TestTag("native.vocabulary.duplicates.close")
                .Semantics(semantics => semantics.ContentDescription(Localize("Common_Close"))),
        };
        close.Add(new Icon(Icons.Default.Close, Localize("Common_Close"))
        {
            Modifier = Modifier.Size(22),
            Tint = palette.Text,
        });
        var items = new List<ComposableNode>
        {
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                Heading(panel.Title, palette, 18, Modifier.Weight(1f)
                    .TestTag("native.vocabulary.duplicates.title")
                    .Semantics(semantics => semantics.Heading())),
                close,
            },
        };
        if (panel.Running)
            items.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.vocabulary.duplicates.status"),
                new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                Body(panel.Status, palette, secondary: true),
            });
        else
        {
            items.Add(Body(panel.Status, palette,
                modifier: Modifier.TestTag("native.vocabulary.duplicates.status")));
            if (!string.IsNullOrWhiteSpace(panel.FocusLabel))
                items.Add(Caption(VocabularyDuplicateFinder.FocusNote(panel.FocusLabel, Localize),
                    palette, "native.vocabulary.duplicates.focus"));
            items.Add(Caption(Localize("NativeVocabulary_DuplicatesCombinedNote"), palette));
            if (panel.Groups.Count == 0)
                items.Add(StatusAlert(Localize("Vocabulary_DuplicatesNoRecords"), palette,
                    automationId: "native.vocabulary.duplicates.empty"));
            for (var index = 0; index < panel.Groups.Count; index++)
                items.Add(VocabularyDuplicateGroup(vocabulary, panel, panel.Groups[index], index,
                    palette));
            if (panel.Groups.Count > 0 && panel.RemainingGroupCount > 0)
                items.Add(Action(string.Format(CultureInfo.CurrentCulture,
                        Localize("Vocabulary_DuplicatesShowMore"), panel.RemainingGroupCount),
                    "native.vocabulary.duplicates.more",
                    () => RunMoreOnUi(vocabulary.ShowMoreDuplicatesAsync), true, primary: false));
            for (var index = 0; index < panel.Results.Count; index++)
                items.Add(StatusAlert(panel.Results[index], palette,
                    automationId: $"native.vocabulary.duplicates.result.{index}"));
        }
        return Card(palette, "native.vocabulary.duplicates", items.ToArray());
    }

    private ComposableNode VocabularyDuplicateGroup(VocabularyBrowser vocabulary,
        VocabularyDuplicatePanel panel, NativeVocabularyDuplicateGroup group, int index,
        AndroidVisualPalette palette)
    {
        var id = $"native.vocabulary.duplicates.group.{index}";
        var badges = new FlowRow { Modifier.FillMaxWidth() };
        badges.Add(new Box
        {
            Modifier.Padding(end: 8, bottom: 6),
            Heading(group.DisplayTerm, palette, 17, Modifier.TestTag($"{id}.term")),
        });
        AddBadge(badges, string.Format(CultureInfo.CurrentCulture,
                Localize("Vocabulary_DuplicatesCopies"), group.Words.Count),
            palette.Elevated, palette.Text, $"{id}.copies");
        if (!group.CanMergeAutomatically)
            AddBadge(badges, Localize("NativeVocabulary_DuplicatesEditBeforeMerge"),
                Color.FromHex("#FFC233"), Color.FromHex("#212529"), $"{id}.blocked");
        if (panel.IsFocused(group))
            AddBadge(badges, Localize("Vocabulary_DuplicatesFocusedBadge"),
                palette.Primary, palette.OnPrimary, $"{id}.focused");
        var column = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(12),
            badges,
        };
        foreach (var word in group.Words)
            column.Add(VocabularyDuplicateWordRow(vocabulary, panel, group, word, palette));
        if (!group.CanMergeAutomatically)
        {
            var warning = new Surface
            {
                Modifier = Modifier.FillMaxWidth().TestTag($"{id}.unavailable")
                    .Border(1, palette.WarningText, AndroidVisualTheme.ControlRadius),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                Color = palette.Elevated,
            };
            var header = Body(Localize("NativeVocabulary_DuplicatesMergeUnavailable"), palette);
            header.Color = palette.WarningText;
            header.FontWeight = FontWeight.SemiBold;
            warning.Add(new Column(Arrangement.SpacedBy(4))
            {
                Modifier.FillMaxWidth().Padding(12),
                header,
                Caption(group.MergeBlockedReason
                    ?? Localize("NativeVocabulary_DuplicatesBlockedFallback"), palette),
                Caption(Localize("NativeVocabulary_DuplicatesMergeUnavailableHelp"), palette),
            });
            column.Add(warning);
        }
        else
        {
            var term = group.NormalizedTerm;
            column.Add(Action(Localize("NativeVocabulary_DuplicatesMergeKeepRecommended"),
                $"{id}.merge", () => RunMoreOnUi(() => vocabulary.MergeDuplicatesAsync(term)),
                !panel.Running, primary: false));
        }
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(id)
                .Border(panel.IsFocused(group) ? 2 : 1,
                    panel.IsFocused(group) ? palette.Primary : palette.Border,
                    AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Card,
        };
        surface.Add(column);
        return surface;
    }

    private ComposableNode VocabularyDuplicateWordRow(VocabularyBrowser vocabulary,
        VocabularyDuplicatePanel panel, NativeVocabularyDuplicateGroup group,
        NativeVocabularyDuplicateWord word, AndroidVisualPalette palette)
    {
        var id = $"native.vocabulary.duplicates.word.{word.Id}";
        var keeper = word.Id == group.RecommendedKeeperId;
        var badges = new FlowRow { Modifier.FillMaxWidth() };
        var native = Body(word.NativeTerm, palette, modifier: Modifier.TestTag($"{id}.native"));
        native.FontWeight = FontWeight.SemiBold;
        badges.Add(new Box { Modifier.Padding(end: 8, bottom: 6), native });
        AddBadge(badges, Localize(VocabularyDuplicateFinder.EncodingKey(word.EncodingLabel)),
            palette.Elevated, palette.Text, $"{id}.encoding");
        if (keeper)
            AddBadge(badges, Localize("Vocabulary_DuplicatesRecommendedKeeper"),
                palette.Success, Color.White, $"{id}.keeper");
        if (word.Id == panel.FocusedWordId)
            AddBadge(badges, Localize("Vocabulary_DuplicatesCurrentWord"),
                palette.Primary, palette.OnPrimary, $"{id}.current");
        var column = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().Padding(10),
            badges,
            Caption(VocabularyDuplicateFinder.MemoryAidSummary(word, Localize), palette,
                $"{id}.aids"),
        };
        if (keeper)
        {
            var note = Caption(Localize("NativeVocabulary_DuplicatesKeeperNote"), palette);
            if (note is Text text)
                text.Color = palette.Success;
            column.Add(note);
        }
        if (!group.CanMergeAutomatically)
        {
            var wordId = word.Id;
            column.Add(Action(Localize("NativeVocabulary_DuplicatesEditRecord"), $"{id}.edit",
                () => RunMoreOnUi(() => vocabulary.OpenWordAsync(wordId)), !panel.Running,
                primary: false));
        }
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(id)
                .Border(1, palette.Border, AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Card,
        };
        surface.Add(column);
        return surface;
    }

    private static void AddBadge(FlowRow row, string value, Color fill, Color text, string tag)
    {
        var badge = new Surface
        {
            Modifier = Modifier.TestTag(tag),
            Shape = Shape.RoundedCorners(12),
            Color = fill,
        };
        badge.Add(new Text(value)
        {
            Modifier = Modifier.Padding(horizontal: 10, vertical: 4),
            Color = text,
            FontSize = new Sp(AndroidVisualTheme.CaptionSize),
        });
        row.Add(new Box { Modifier.Padding(end: 6, bottom: 6), badge });
    }
}
