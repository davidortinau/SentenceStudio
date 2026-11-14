using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Vocabulary;
using Color = AndroidX.Compose.Color;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The word editor's Save and More menu (Find Duplicates, Delete), its delete confirmation (source showDeleteConfirm modal) and the Vocabulary list's
/// duplicate manager (source Vocabulary.razor cleanup panel), in Compose on the shared
/// VocabularyBrowser.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private const string VocabularyWordMenu = "vocabulary.edit.more";

    /// <summary>
    /// Captain 2026-11-10 (as iOS): one Save, a plain text action at every width, then the
    /// overflow menu with Find Duplicates and Delete (no second Save, none under the cards).
    /// </summary>
    private ComposableNode VocabularyWordActions(VocabularyBrowser vocabulary,
        VocabularyBrowserEdit state, AndroidVisualPalette palette)
    {
        var actions = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically);
        var save = new TextButton(() => RunMoreOnUi(vocabulary.SaveAsync), enabled: state.CanSave)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag("native.vocabulary.edit.save")
                .Semantics(semantics => semantics.ContentDescription(Localize("Common_Save"))),
        };
        save.Add(new Text(Localize("Common_Save")));
        actions.Add(save);
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
            // The facade's Icon needs a Modifier (a null one throws in Material's Icon).
            Icon = new Icon(Icons.Default.Warning, null)
            {
                Modifier = Modifier.Size(24),
                Tint = palette.Danger,
            },
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

    private const string VocabularyListMenuKey = "vocabulary.list.more";

    /// <summary>The list's overflow menu: Clean Up Duplicates (Captain 2026-11-14).</summary>
    private ComposableNode VocabularyListMenu(VocabularyBrowser vocabulary,
        AndroidVisualPalette palette, bool enabled)
    {
        var more = new IconButton(() => ToggleMoreMenu(VocabularyListMenuKey), enabled: enabled)
        {
            Modifier = Modifier.TestTag("native.vocabulary.more")
                .Semantics(semantics => semantics.ContentDescription(Localize("More"))),
        };
        more.Add(new Icon(Icons.Default.MoreVert, Localize("More"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var menu = PopupMenu(expanded: _moreMenu == VocabularyListMenuKey,
            onDismissRequest: CloseMoreMenu);
        menu.Add(VocabularyMenuItem(Localize("Vocabulary_DuplicatesCleanUp"),
            "native.vocabulary.duplicates.open", enabled,
            () => RunMoreOnUi(vocabulary.OpenAllDuplicatesAsync)));
        return new Box { more, menu };
    }

    /// <summary>
    /// The duplicate page (docs/duplicate-merge-plan.md, as on iOS): one Merge for every safe
    /// group, then the groups that need review (what merging would lose, Merge or Skip) and the
    /// ones that cannot merge (why), in one lazy column.
    /// </summary>
    private ComposableNode VocabularyDuplicatesPage(VocabularyBrowser vocabulary,
        VocabularyDuplicatePanel panel, AndroidVisualPalette palette)
    {
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.vocabulary.duplicates"),
            ScreenHeader(panel.Title, palette, back: true,
                backAutomationId: "native.vocabulary.duplicates.back"),
        };
        var rows = new List<VocabularyRow>
        {
            new("summary", () => VocabularyDuplicatesSummary(vocabulary, panel, palette)),
        };
        var review = panel.ReviewGroups;
        if (review.Count > 0)
        {
            rows.Add(new("review", () => new Column(Arrangement.SpacedBy(4))
            {
                Modifier.FillMaxWidth().TestTag("native.vocabulary.duplicates.review"),
                Heading(Localize("Vocabulary_DuplicatesNeedReview"), palette, 18,
                    Modifier.Semantics(semantics => semantics.Heading())),
                Caption(Localize("Vocabulary_DuplicatesNeedReviewNote"), palette,
                    "native.vocabulary.duplicates.review.note"),
            }));
            foreach (var group in review)
            {
                var item = group;
                rows.Add(new($"review.{item.NormalizedTerm}",
                    () => VocabularyDuplicateReview(vocabulary, panel, item, palette)));
            }
        }
        var blocked = panel.BlockedGroups;
        if (blocked.Count > 0)
        {
            rows.Add(new("blocked", () => Heading(Localize("Vocabulary_DuplicatesCantMerge"),
                palette, 18, Modifier.TestTag("native.vocabulary.duplicates.blocked")
                    .Semantics(semantics => semantics.Heading()))));
            foreach (var group in blocked)
            {
                var item = group;
                rows.Add(new($"blocked.{item.NormalizedTerm}",
                    () => VocabularyDuplicateBlocked(vocabulary, panel, item, palette)));
            }
        }
        if (!panel.Running && panel.RemainingGroupCount > 0)
            rows.Add(new("more", () => Action(string.Format(CultureInfo.CurrentCulture,
                    Localize("Vocabulary_DuplicatesShowMore"), panel.RemainingGroupCount),
                "native.vocabulary.duplicates.more",
                () => RunMoreOnUi(vocabulary.ShowMoreDuplicatesAsync), true, primary: false)));
        page.Add(new LazyColumn<VocabularyRow>(rows, row => row.Content())
        {
            Key = row => row.Key,
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .Padding(horizontal: AndroidVisualTheme.PagePadding)
                .TestTag("native.vocabulary.duplicates.list"),
            VerticalArrangement = Arrangement.SpacedBy(12),
        });
        return page;
    }

    /// <summary>The scan's status, the safe groups' count and the one Merge for all of them, then the results.</summary>
    private ComposableNode VocabularyDuplicatesSummary(VocabularyBrowser vocabulary,
        VocabularyDuplicatePanel panel, AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>();
        if (panel.Running)
            items.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.vocabulary.duplicates.status"),
                new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                Body(panel.Status, palette, secondary: true),
            });
        else
            items.Add(Body(panel.Status, palette,
                modifier: Modifier.TestTag("native.vocabulary.duplicates.status")));
        if (!string.IsNullOrWhiteSpace(panel.FocusLabel))
            items.Add(Caption(VocabularyDuplicateFinder.FocusNote(panel.FocusLabel, Localize),
                palette, "native.vocabulary.duplicates.focus"));
        if (panel.CanMergeAll)
        {
            items.Add(Body(string.Format(CultureInfo.CurrentCulture,
                    Localize("Vocabulary_DuplicatesSafeSummary"), panel.SafeGroupCount,
                    panel.MergeAllCopyCount), palette,
                modifier: Modifier.TestTag("native.vocabulary.duplicates.summary")));
            items.Add(Action(string.Format(CultureInfo.CurrentCulture,
                    Localize("Vocabulary_DuplicatesMergeAll"), panel.MergeAllCopyCount),
                "native.vocabulary.duplicates.mergeall",
                () => RunMoreOnUi(vocabulary.MergeAllDuplicatesAsync), true));
        }
        else if (!panel.Running && panel.Groups.Count > 0)
            items.Add(Body(Localize("Vocabulary_DuplicatesNothingSafe"), palette,
                modifier: Modifier.TestTag("native.vocabulary.duplicates.summary")));
        if (!panel.Running)
            for (var index = 0; index < panel.Results.Count; index++)
                items.Add(StatusAlert(panel.Results[index], palette,
                    automationId: $"native.vocabulary.duplicates.result.{index}"));
        return Card(palette, "native.vocabulary.duplicates.top", items.ToArray());
    }

    /// <summary>
    /// A group to review: its term, what merging would lose (a warning icon and words, not color
    /// alone), its copies with the keeper first, then Merge and Skip. With three or more copies
    /// each copy has a checkbox (Captain 2026-11-14: "merge some matching duplicates but not
    /// all"): Merge folds the checked ones into the first checked copy.
    /// </summary>
    private ComposableNode VocabularyDuplicateReview(VocabularyBrowser vocabulary,
        VocabularyDuplicatePanel panel, NativeVocabularyDuplicateGroup group,
        AndroidVisualPalette palette)
    {
        var id = DuplicateGroupTag(group);
        var differences = string.Join(", ", VocabularyDuplicateFinder.Differences(group, Localize));
        var lost = Body(differences, palette);
        lost.FontWeight = FontWeight.SemiBold;
        var column = new List<ComposableNode>
        {
            Heading(group.DisplayTerm, palette, 17, Modifier.TestTag($"{id}.term")
                .Semantics(semantics => semantics.Heading())),
            new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag($"{id}.differences"),
                new Icon(Icons.Default.Warning, null)
                {
                    Modifier = Modifier.Size(18),
                    Tint = palette.WarningText,
                },
                lost,
            },
        };
        var term = group.NormalizedTerm;
        var choice = VocabularyDuplicatePanel.OffersChoice(group);
        if (choice)
            column.Add(Caption(Localize("Vocabulary_DuplicatesChooseHint"), palette, $"{id}.hint"));
        var keeper = panel.KeeperOf(group);
        var copies = VocabularyDuplicateFinder.KeeperFirst(group, keeper);
        for (var index = 0; index < copies.Count; index++)
        {
            var word = copies[index];
            var wordId = word.Id;
            // Identical copies read the same, so TalkBack also says which one (copy 2 of 3).
            var spoken = $"{word.NativeTerm}, {string.Format(CultureInfo.CurrentCulture,
                Localize("Vocabulary_DuplicatesCopyPosition"), index + 1, copies.Count)}";
            var row = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
            };
            if (choice)
                row.Add(new Checkbox(@checked: panel.IsIncluded(word),
                    onCheckedChange: _ => vocabulary.ToggleDuplicateCopy(term, wordId),
                    enabled: !panel.Running)
                {
                    Modifier = Modifier.TestTag($"native.vocabulary.duplicates.word.{wordId}.choose")
                        .Semantics(semantics => semantics.ContentDescription(spoken)),
                });
            row.Add(new Box { Modifier.Weight(1f), VocabularyDuplicateCopy(panel, group, word, keeper, palette) });
            // Captain 2026-11-14: "go directly to a vocabulary word so that I can modify the
            // entry". The editor opens over this page; Back or Save returns to it.
            var editLabel = Localize("Vocabulary_DuplicatesEdit");
            var edit = new TextButton(() => RunMoreOnUi(() => vocabulary.OpenWordAsync(wordId)),
                enabled: !panel.Running)
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag($"native.vocabulary.duplicates.word.{wordId}.edit")
                    .Semantics(semantics => semantics.ContentDescription($"{editLabel}, {spoken}")),
            };
            edit.Add(new Text(editLabel) { Color = palette.Primary });
            row.Add(edit);
            column.Add(row);
        }
        column.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Box
            {
                Modifier.Weight(1f),
                Action(Localize("Vocabulary_DuplicatesMerge"), $"{id}.merge",
                    () => RunMoreOnUi(() => vocabulary.MergeDuplicatesAsync(term)),
                    !panel.Running && panel.Chosen(group).Count >= 2, primary: false),
            },
            new Box
            {
                Modifier.Weight(1f),
                Action(Localize("Vocabulary_DuplicatesSkip"), $"{id}.skip",
                    () => vocabulary.SkipDuplicates(term), !panel.Running, primary: false),
            },
        });
        return Card(palette, id, column.ToArray());
    }

    /// <summary>A copy: its meaning, what differs and its memory aids, and which copy Merge keeps.</summary>
    private ComposableNode VocabularyDuplicateCopy(VocabularyDuplicatePanel panel,
        NativeVocabularyDuplicateGroup group, NativeVocabularyDuplicateWord word, string keeperId,
        AndroidVisualPalette palette)
    {
        var id = $"native.vocabulary.duplicates.word.{word.Id}";
        var keeper = word.Id == keeperId;
        var native = Body(word.NativeTerm, palette, modifier: Modifier.TestTag($"{id}.native"));
        if (keeper)
            native.FontWeight = FontWeight.SemiBold;
        var column = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().Padding(10),
            native,
            Caption(VocabularyDuplicateFinder.CopyDetails(group, word, Localize), palette,
                $"{id}.details"),
        };
        if (keeper)
            column.Add(DuplicateMarker(Icons.Default.CheckCircle,
                Localize("Vocabulary_DuplicatesKeep"), palette.Success, palette, $"{id}.keeper"));
        if (word.Id == panel.FocusedWordId)
            column.Add(DuplicateMarker(Icons.Default.Info,
                Localize("Vocabulary_DuplicatesCurrentWord"), palette.Primary, palette, $"{id}.current"));
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

    /// <summary>
    /// A group whose copies cannot all merge: its term and copies, and why. With a copy to keep,
    /// Merge still moves the learner's history and resources onto it (the shared copies stay).
    /// </summary>
    private ComposableNode VocabularyDuplicateBlocked(VocabularyBrowser vocabulary,
        VocabularyDuplicatePanel panel, NativeVocabularyDuplicateGroup group,
        AndroidVisualPalette palette)
    {
        var id = DuplicateGroupTag(group);
        var copies = string.Format(CultureInfo.CurrentCulture,
            Localize("Vocabulary_DuplicatesCopies"), group.Words.Count);
        var term = Body($"{group.DisplayTerm} ({copies})", palette,
            modifier: Modifier.TestTag($"{id}.term"));
        term.FontWeight = FontWeight.SemiBold;
        var reason = Caption(VocabularyDuplicateFinder.BlockedReason(group, Localize), palette,
            $"{id}.reason");
        if (string.IsNullOrWhiteSpace(group.RecommendedKeeperId))
            return Card(palette, id, term, reason);
        var normalized = group.NormalizedTerm;
        return Card(palette, id, term, reason,
            Action(Localize("Vocabulary_DuplicatesMerge"), $"{id}.merge",
                () => RunMoreOnUi(() => vocabulary.MergeDuplicatesAsync(normalized)),
                !panel.Running, primary: false));
    }

    /// <summary>An icon and its words (the state is never told by color alone).</summary>
    private static ComposableNode DuplicateMarker(AndroidX.Compose.UI.Graphics.Vector.ImageVector icon,
        string label, Color color, AndroidVisualPalette palette, string tag)
    {
        var text = Caption(label, palette);
        if (text is Text words)
            words.Color = color;
        return new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag(tag),
            new Icon(icon, null) { Modifier = Modifier.Size(16), Tint = color },
            text,
        };
    }

    /// <summary>A group's test tag, by its term (stable while other groups merge).</summary>
    private static string DuplicateGroupTag(NativeVocabularyDuplicateGroup group) =>
        $"native.vocabulary.duplicates.group.{Uri.EscapeDataString(group.NormalizedTerm)}";
}
