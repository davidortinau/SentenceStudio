using System.Collections.Immutable;
using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Native;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Jetpack Compose presentation of the one Sam experience: the Dashboard entry
/// (Extended FAB), and a Material 3 ModalBottomSheet holding the chat stream,
/// the conversation shelf, state notices and the composer. Rendered only
/// through the Microsoft.AndroidX.Compose facade; no Android views. Source:
/// SentenceStudio.UI Shared/Sam and Shared/Coach (frozen reference 59c72262),
/// matching the iOS SamView ids (native.sam.*).
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private LazyListState _samList = null!;
    private LazyListState _samConversationsList = null!;
    private SheetStateHolder _samSheetState = null!;
    private string? _samListOwner;
    private string? _samLastKey;
    private string? _samLastConversation;
    private bool _samWasAtEnd = true;
    private int _samFollowVersion;
    private AndroidSamFollow _samFollow;
    private readonly MutableState<AndroidX.Compose.UI.Text.Input.TextFieldValue> _samDraft =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));
    private string _samDraftSynced = string.Empty;

    private static string SamText(string key, CultureInfo culture) =>
        LocalizationManager.Instance.GetString(key, culture);

    private string SamPersona(NativeSamState state) =>
        AndroidSamProjection.Persona(state, SamText);

    /// <summary>Remembers Sam's Compose state holders and schedules the follow-newest scroll.</summary>
    private void RememberSam(IComposer composer)
    {
        _samList = composer.Remember(() => new LazyListState());
        _samConversationsList = composer.Remember(() => new LazyListState());
        // The panel's own height is the compact/expanded size, so the sheet never
        // rests half-open with the composer below the screen edge.
        _samSheetState = composer.Remember(() => new SheetStateHolder(skipPartiallyExpanded: true));

        var state = SamState;
        var rows = AndroidSamProjection.Rows(state);
        // The status row trails every other row and outlives a transcript load, so
        // follow the newest content row; the scroll target still includes status.
        var lastKey = IsSamPresented && !state.ConversationsOpen
            ? AndroidSamProjection.NewestContentKey(rows) : null;
        var owner = Auth.UserId;
        if (!string.Equals(_samListOwner, owner, StringComparison.Ordinal))
        {
            _samListOwner = owner;
            _samLastKey = null;
            _samLastConversation = null;
        }
        // Read during composition so a change at the list's end recomposes; this
        // is the position before the rows below are laid out.
        var atEnd = !_samList.CanScrollForward;
        var follow = AndroidSamProjection.Follow(_samLastKey, _samLastConversation, lastKey,
            state.ConversationId, _samWasAtEnd, _samList.IsScrollInProgress,
            AndroidSamProjection.NewestIsOwnSend(state, rows));
        _samWasAtEnd = atEnd;
        if (lastKey is null)
        {
            // Closed panel or shelf: the next open re-anchors at the newest row.
            _samLastKey = null;
            _samLastConversation = null;
        }
        else
        {
            _samLastKey = lastKey;
            _samLastConversation = state.ConversationId;
        }
        if (follow != AndroidSamFollow.None)
        {
            _samFollowVersion++;
            _samFollow = follow;
        }
        _samFollowTarget = rows.Length - 1;
    }

    private int _samFollowTarget = -1;

    /// <summary>
    /// Runs the follow-newest scroll inside the sheet's own composition, so the
    /// request reaches the list that window actually lays out.
    /// </summary>
    private void FollowNewestSamRow(IComposer composer)
    {
        var version = _samFollowVersion;
        var target = _samFollowTarget;
        var mode = _samFollow;
        composer.LaunchedEffect(version, async cancellationToken =>
        {
            if (version == 0 || target < 0)
                return;
            try
            {
                if (mode == AndroidSamFollow.Jump)
                    await _samList.ScrollToItemAsync(target, 0, cancellationToken);
                else
                    await _samList.AnimateScrollToItemAsync(target, 0, cancellationToken);
                _samWasAtEnd = true;
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>
    /// Hosts Sam over every signed-in screen (source MainLayout): the floating
    /// button on a tab root, a top app bar action on any other screen, and the
    /// panel sheet wherever it is open.
    /// </summary>
    private ComposableNode WithSam(ComposableNode screen, AndroidVisualPalette palette)
    {
        var placement = SamEntryPlacement;
        if (placement == AndroidSamEntryPlacement.TopBar)
            AddSamTopBarAction(palette);
        var host = new Box { Modifier.FillMaxSize(), screen };
        if (placement == AndroidSamEntryPlacement.Floating)
            host.Add(RenderSamFab(palette));
        if (IsSamPresented)
            host.Add(RenderSamSheet(palette));
        return host;
    }

    /// <summary>
    /// The first top app bar the screen being composed created (ScreenHeader and the
    /// resource form record it); reset before each screen is built.
    /// </summary>
    private TopAppBar? _screenHeaderBar;

    /// <summary>
    /// Puts the Sam entry first in the screen's own top app bar actions, after the
    /// screen set them, so no screen has to add it.
    /// </summary>
    private void AddSamTopBarAction(AndroidVisualPalette palette)
    {
        if (_screenHeaderBar is not { } bar)
            return;
        var label = AndroidSamProjection.EntryLabel(SamPersona(SamState), Localize);
        var sam = new IconButton(OpenSam, enabled: true)
        {
            Modifier = Modifier.TestTag("native.sam.topbar")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                }),
        };
        sam.Add(new Icon(Icons.Default.Face, null)
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Primary,
        });
        if (bar.Actions is { } existing)
            bar.Actions = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                sam, existing
            };
        else
            bar.Actions = sam;
    }

    /// <summary>Source SamFab: the one entry, bottom trailing, labelled "Ask {name}".</summary>
    private ComposableNode RenderSamFab(AndroidVisualPalette palette)
    {
        var label = AndroidSamProjection.EntryLabel(SamPersona(SamState), Localize);
        return new ExtendedFloatingActionButton(OpenSam, expanded: true)
        {
            Modifier = Modifier.Align(Alignment.BottomEnd).Padding(16)
                .TestTag("native.sam.fab")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                }),
            ContainerColor = palette.Primary,
            ContentColor = palette.OnPrimary,
            Icon = new Icon(Icons.Default.Face, null) { Modifier = Modifier.Size(22), Tint = palette.OnPrimary },
            Text = new Text(label) { Color = palette.OnPrimary },
        };
    }

    /// <summary>The panel: one modal bottom sheet; compact on phones, full on tablets.</summary>
    private ComposableNode RenderSamSheet(AndroidVisualPalette palette)
    {
        var state = SamState;
        var persona = SamPersona(state);
        var width = WindowWidthDp;
        var height = AndroidSamProjection.UsesCompactHeight(state, width)
            ? Modifier.FillMaxWidth().Height((int)(WindowHeightDp * AndroidSamProjection.CompactHeightFraction))
            : Modifier.FillMaxWidth().FillMaxHeight(1f);
        var panel = new Column
        {
            // The sheet is its own window; publish test tags as resource ids there too.
            height.TestTag("native.sam.panel")
                .Semantics(semantics => semantics.TestTagsAsResourceId(true)),
            new BackHandler(ToggleSamConversations, enabled: state.ConversationsOpen),
            SamHeader(state, persona, palette, AndroidSamProjection.IsResizable(width)),
            new HorizontalDivider { Color = palette.Border },
        };
        if (state.ConversationsOpen)
            panel.Add(SamConversations(state, palette));
        else
        {
            panel.Add(SamStream(state, persona, palette));
            panel.Add(new Composed(composer =>
            {
                FollowNewestSamRow(composer);
                return null;
            }));
            panel.Add(new HorizontalDivider { Color = palette.Border });
            // Typing recomposes only this scope: the draft is read here, and the
            // presenter's non-structural draft updates never re-render the shell.
            panel.Add(new Composed(composer =>
            {
                var typed = _samDraft.Value.Text ?? string.Empty;
                composer.LaunchedEffect(typed, _ =>
                {
                    // The field owns its TextFieldValue (selection, IME composition such
                    // as Korean); the presenter's draft follows each committed edit.
                    if (!string.Equals(typed, _samDraftSynced, StringComparison.Ordinal) && _sam is { } sam)
                    {
                        _samDraftSynced = typed;
                        sam.SetDraft(typed);
                    }
                    return Task.CompletedTask;
                });
                return SamComposer(SamState with { Draft = typed }, persona, palette);
            }));
        }
        var sheet = new ModalBottomSheet(onDismissRequest: CloseSam, sheetState: _samSheetState)
        {
            Modifier = Modifier.TestTag("native.sam.sheet"),
        };
        sheet.Add(panel);
        return sheet;
    }

    /// <summary>Header actions: conversations, new, compact/expand, close (source SamPanel header).</summary>
    private ComposableNode SamHeader(NativeSamState state, string persona,
        AndroidVisualPalette palette, bool resizable)
    {
        var row = new Row(Arrangement.SpacedBy(2), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(horizontal: 8, vertical: 4).TestTag("native.sam.header"),
        };
        if (state.ConversationsOpen)
            row.Add(SamIcon(Icons.AutoMirrored.Default.ArrowBack, Localize("Common_Back"),
                "native.sam.conversations.back", ToggleSamConversations, true, palette));
        row.Add(Heading(state.ConversationsOpen ? Localize("Coach_Conversations") : persona, palette, 20,
            Modifier.Weight(1f).Padding(horizontal: 8).Semantics(semantics => semantics.Heading())));
        var enabled = !state.Busy;
        if (!state.ConversationsOpen)
            row.Add(SamIcon(Icons.AutoMirrored.Default.List, Localize("Coach_ConversationsToggle"),
                "native.sam.conversations.toggle", ToggleSamConversations, enabled, palette));
        row.Add(SamIcon(Icons.Default.Create, Localize("Coach_NewConversation"),
            "native.sam.new", NewSamConversation, enabled, palette));
        if (resizable && !state.ConversationsOpen)
        {
            var compact = state.Size == NativeSamPanelSize.Compact;
            row.Add(SamIcon(compact ? Icons.Default.KeyboardArrowUp : Icons.Default.KeyboardArrowDown,
                Localize(compact ? "Coach_PanelExpand" : "Coach_PanelCompact"),
                "native.sam.size", ToggleSamSize, enabled, palette));
        }
        row.Add(SamIcon(Icons.Default.Close, Localize("Coach_PanelClose"),
            "native.sam.close", CloseSam, true, palette));
        return row;
    }

    private static ComposableNode SamIcon(AndroidX.Compose.UI.Graphics.Vector.ImageVector icon, string label, string tag,
        Action command, bool enabled, AndroidVisualPalette palette)
    {
        var button = new IconButton(command, enabled: enabled)
        {
            Modifier = Modifier.TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Icon(icon, null)
        {
            Modifier = Modifier.Size(24),
            Tint = enabled ? palette.Text : palette.SecondaryText,
        });
        return button;
    }

    /// <summary>The chat log and every card that belongs under it (source CoachChatPane).</summary>
    private ComposableNode SamStream(NativeSamState state, string persona, AndroidVisualPalette palette)
    {
        var rows = AndroidSamProjection.Rows(state);
        return new LazyColumn<AndroidSamRow>(rows, row => SamRow(state, row, persona, palette))
        {
            Key = row => row.Key,
            State = _samList,
            VerticalArrangement = Arrangement.SpacedBy(12),
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .Padding(horizontal: AndroidVisualTheme.PagePadding)
                .TestTag("native.sam.stream"),
        };
    }

    private ComposableNode SamRow(NativeSamState state, AndroidSamRow row, string persona,
        AndroidVisualPalette palette)
    {
        var node = row.Kind switch
        {
            AndroidSamRowKind.Dispute => SamDispute(state, palette),
            AndroidSamRowKind.MemoryCandidate => SamMemoryCandidate(state, state.MemoryCandidate!, palette),
            AndroidSamRowKind.MemoryNotice => Caption(Localize(state.MemoryCandidateNoticeKey!), palette,
                "native.sam.memory.notice"),
            AndroidSamRowKind.Notice => SamNotice(Localize(state.NoticeKey!), palette,
                "native.sam.conversation.notice"),
            AndroidSamRowKind.Unreadable => Caption(string.Format(CultureInfo.CurrentCulture,
                Localize("Coach_UnreadableCount"), state.UnreadableCount), palette, "native.sam.unreadable"),
            AndroidSamRowKind.TranscriptLoading => SamProgress(Localize("Coach_TranscriptLoading"), palette,
                "native.sam.transcript.loading"),
            AndroidSamRowKind.Message => SamMessage(state, state.SafeEntries[row.EntryIndex], row.EntryIndex,
                row.ShowUndo, persona, palette),
            AndroidSamRowKind.Receipt => SamReceipt(state, state.LatestReceipt!, palette),
            AndroidSamRowKind.Suggestion => SamSuggestion(state, state.Suggestion!, persona, palette),
            AndroidSamRowKind.Outcome => Caption(Localize(state.NoticeKey!), palette, "native.sam.outcome"),
            AndroidSamRowKind.VocabularySet => SamVocabularySet(state, state.VocabularySet!, palette),
            AndroidSamRowKind.VocabularySetOutcome => SamVocabularySetOutcome(state, palette),
            AndroidSamRowKind.Chips => SamChips(palette),
            AndroidSamRowKind.Closed => SamNotice(Localize("Coach_ConversationClosedNotice"), palette,
                "native.sam.closed"),
            _ => SamStatus(state, persona, palette),
        };
        return node;
    }

    private static ComposableNode Caption(string text, AndroidVisualPalette palette, string? tag = null,
        bool danger = false)
    {
        var caption = Body(text, palette, secondary: !danger,
            modifier: tag is null ? null : Modifier.TestTag(tag));
        caption.FontSize = new Sp(AndroidVisualTheme.CaptionSize + 1);
        if (danger)
            caption.Color = palette.Danger;
        return caption;
    }

    private static Text Strong(string text, AndroidVisualPalette palette, bool heading = false)
    {
        var node = Body(text, palette, modifier: heading
            ? Modifier.Semantics(semantics => semantics.Heading()) : null);
        node.FontWeight = FontWeight.SemiBold;
        return node;
    }

    private static ComposableNode SamCard(AndroidVisualPalette palette, string tag, SamCardStyle style,
        IEnumerable<ComposableNode> children, int spacing = 4)
    {
        var (color, border) = style switch
        {
            SamCardStyle.Learner => (palette.Background, palette.Primary),
            SamCardStyle.Info => (palette.Elevated, palette.Border),
            SamCardStyle.Success => (palette.Elevated, palette.Success),
            SamCardStyle.Danger => (palette.DangerSurface, palette.Danger),
            _ => (palette.Card, palette.Card),
        };
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().Border(1, border, 12).TestTag(tag),
            Shape = Shape.RoundedCorners(12),
            Color = color,
        };
        var content = new Column(Arrangement.SpacedBy(spacing))
        {
            Modifier.FillMaxWidth().Padding(horizontal: 12, vertical: 10),
        };
        foreach (var child in children)
            content.Add(child);
        surface.Add(content);
        return surface;
    }

    private enum SamCardStyle { Sam, Learner, Info, Success, Danger }

    private static ComposableNode SamNotice(string message, AndroidVisualPalette palette, string tag) =>
        SamCard(palette, tag, SamCardStyle.Info, [Body(message, palette)]);

    private static ComposableNode SamProgress(string label, AndroidVisualPalette palette, string tag) =>
        new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(vertical: 4).TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            new CircularProgressIndicator { Modifier = Modifier.Size(20) },
            Body(label, palette, secondary: true),
        };

    private ComposableNode SamMessage(NativeSamState state, NativeSamEntry entry, int index,
        bool showUndo, string persona, AndroidVisualPalette palette)
    {
        var parts = new List<ComposableNode>
        {
            Caption(AndroidSamProjection.MessageMeta(entry, persona, Localize), palette)
        };
        switch (entry.Kind)
        {
            case NativeSamEntryKind.Unreadable:
                parts.Add(Body(Localize("Coach_UnreadableMessage"), palette, secondary: true));
                break;
            case NativeSamEntryKind.Unsupported:
                parts.Add(Body(Localize("Coach_UnsupportedMessage"), palette, secondary: true));
                break;
            default:
                if (!entry.Blocks.IsDefaultOrEmpty)
                    foreach (var block in entry.Blocks)
                    {
                        if (AndroidSamProjection.BlockLabel(block, Localize) is { } label)
                            parts.Add(Strong(label, palette));
                        parts.Add(Body(block.Text, palette));
                    }
                else if (entry.Text.Length > 0)
                    parts.Add(Body(entry.Text, palette));
                foreach (var line in entry.ChangeLines.IsDefault ? [] : entry.ChangeLines)
                    parts.Add(Body("• " + line, palette, secondary: true));
                break;
        }
        if (entry.Limitation is { } limitation)
            parts.Add(SamLimitation(limitation, index, palette));
        // Honesty notice: the answer was altered or a repair was withheld (source CoachRepairDisclosureNotice).
        if (AndroidSamCards.RepairKey(entry) is { } repairKey)
        {
            var repair = Localize(repairKey);
            var region = $"{Localize("Coach_Repair_RegionLabel")}: {repair}";
            var notice = Body(repair, palette, secondary: true,
                modifier: Modifier.TestTag($"native.sam.repair.{index}")
                    .Semantics(semantics => semantics.ContentDescription(region)));
            notice.FontSize = new Sp(AndroidVisualTheme.CaptionSize + 1);
            parts.Add(notice);
        }
        if (AndroidSamCards.ShowsMessageActions(entry))
            parts.AddRange(SamMessageActions(state, entry, index, palette));
        if (entry.HasEvidence)
        {
            var expanded = state.IsEvidenceExpanded(entry.Id);
            var label = Localize(expanded ? "Coach_HideEvidence" : "Coach_ViewEvidence");
            var toggle = new TextButton(() => ToggleSamEvidence(entry.Id))
            {
                Modifier = Modifier.TestTag($"native.sam.evidence.toggle.{index}")
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(label);
                        semantics.StateDescription(label);
                    }),
            };
            toggle.Add(new Text(label));
            parts.Add(toggle);
            if (expanded)
                parts.Add(SamEvidence(entry, index, palette));
        }
        if (showUndo)
            parts.Add(Action(Localize("Coach_Undo"), "native.sam.receipt.undo", UndoSam,
                !state.Busy, primary: false));
        var style = entry.Kind switch
        {
            NativeSamEntryKind.Learner => SamCardStyle.Learner,
            NativeSamEntryKind.Notice or NativeSamEntryKind.Receipt => SamCardStyle.Info,
            _ => SamCardStyle.Sam,
        };
        var body = SamCard(palette, $"native.sam.message.{index}", style, parts);
        // A proposed change renders inside the exchange that produced it, never as a dialog.
        if (AndroidSamCards.Write(state, entry, DateTime.UtcNow) is { } write)
            return new Column(Arrangement.SpacedBy(8))
            {
                Modifier.FillMaxWidth(),
                body,
                SamWriteCard(write, index, palette),
            };
        return body;
    }

    private ComposableNode SamEvidence(NativeSamEntry entry, int index, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode> { Strong(Localize("Coach_Evidence"), palette, heading: true) };
        var dateFormat = Localize("Coach_EvidenceDateFormat");
        foreach (var evidence in entry.Evidence)
        {
            rows.Add(Strong(evidence.Label, palette));
            rows.Add(Caption(string.Format(CultureInfo.CurrentCulture, Localize("Coach_EvidenceWindow"),
                evidence.WindowStartDate.ToString(dateFormat, CultureInfo.CurrentCulture),
                evidence.WindowEndDate.ToString(dateFormat, CultureInfo.CurrentCulture)), palette));
            if (evidence.Summary.Length > 0)
                rows.Add(Body(evidence.Summary, palette));
            foreach (var value in evidence.Values)
                rows.Add(Body($"{value.Label}: {AndroidSamProjection.EvidenceValue(value, Localize)}",
                    palette, secondary: true));
            if (evidence.WithheldCount is > 0)
                rows.Add(Caption(string.Format(CultureInfo.CurrentCulture,
                    Localize("Coach_EvidenceWithheld"), evidence.WithheldCount), palette));
        }
        return SamCard(palette, $"native.sam.evidence.{index}", SamCardStyle.Info, rows);
    }

    /// <summary>Typed refusal (source CoachLimitationCard, T variant without export).</summary>
    private ComposableNode SamLimitation(CoachLimitationDto limitation, int index, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Strong(Localize(limitation.Code == CoachLimitationCode.Unknown
                ? "Coach_Limitation_Unknown_Heading" : "Coach_Limitation_" + limitation.Code), palette, heading: true)
        };
        if (limitation.AffectedCount is > 0 and var count)
            rows.Add(Body($"{Localize("Coach_Limitation_AffectedItems")}: {count}", palette));
        if (limitation.Destination is { } destination && destination.Route != CoachRouteName.Unknown)
        {
            rows.Add(Strong(Localize("Coach_Route_" + destination.Route), palette));
            rows.Add(Caption(Localize("Coach_Limitation_SideEffectHeading"), palette));
            rows.Add(Body(Localize(destination.SideEffect == CoachRouteSideEffect.Unknown
                ? "Coach_RouteEffect_Unknown" : "Coach_RouteEffect_" + destination.SideEffect), palette));
        }
        var alternatives = limitation.Alternatives
            .Where(code => code is not (CoachAlternativeCode.Unknown or CoachAlternativeCode.ExportBeforeRemoving))
            .ToArray();
        if (alternatives.Length > 0)
        {
            rows.Add(Strong(Localize("Coach_Limitation_AlternativesHeading"), palette));
            foreach (var alternative in alternatives)
                rows.Add(Body("• " + Localize("Coach_Alternative_" + alternative), palette));
        }
        var rungs = limitation.HintLadder.Where(rung => rung.Kind != CoachHintKind.Unknown).ToArray();
        if (rungs.Length > 0)
        {
            rows.Add(Strong(Localize("Coach_Limitation_HintLadderHeading"), palette));
            foreach (var rung in rungs.OrderBy(rung => rung.Rung))
                rows.Add(Body("• " + Localize("Coach_Hint_" + rung.Kind), palette));
        }
        if (limitation.ShorterSession is { } shorter)
            rows.Add(Body($"{Localize("Coach_Limitation_ShorterSession")}: "
                + $"{shorter.SuggestedItemCount} / {shorter.FullItemCount}", palette));
        var label = Localize("Coach_Limitation_RefusalRegionLabel");
        return new Column
        {
            Modifier.FillMaxWidth().Semantics(semantics => semantics.ContentDescription(label)),
            SamCard(palette, $"native.sam.limitation.{index}", SamCardStyle.Info, rows),
        };
    }

    /// <summary>Source CoachSuggestionCard: one action pair, nothing changes until Accept.</summary>
    private ComposableNode SamSuggestion(NativeSamState state, PendingCoachSuggestionDto suggestion,
        string persona, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Strong(Localize("Coach_SuggestionLabel"), palette, heading: true),
            Body(suggestion.VocabularyFocus is { } focus
                    ? string.Format(CultureInfo.CurrentCulture, Localize("Coach_FocusSuggestionSummary"),
                        focus.DisplayLabel).Replace(Localize("Coach_RoleCoach"), persona)
                    : suggestion.Rationale, palette,
                modifier: Modifier.TestTag("native.sam.suggestion.rationale")),
            Caption(AndroidSamProjection.SuggestionPreview(suggestion, Localize), palette,
                "native.sam.suggestion.preview"),
        };
        if (suggestion.VocabularyFocus is { } words && words.Words.Count > 0)
        {
            rows.Add(Strong(Localize("Coach_FocusProposed"), palette));
            foreach (var word in words.Words)
                rows.Add(Body(word.DisplayText is { Length: > 0 } display
                    ? $"{word.TargetText} — {display}" : word.TargetText, palette));
        }
        if (state.ClarifyingQuestion is { } question)
            rows.Add(Body(question, palette, modifier: Modifier.TestTag("native.sam.suggestion.clarification")));
        rows.Add(SamButtonPair(Localize("Coach_Accept"), "native.sam.suggestion.accept", AcceptSamSuggestion,
            Localize("Coach_Reject"), "native.sam.suggestion.reject", RejectSamSuggestion, !state.Busy));
        return SamCard(palette, "native.sam.suggestion", SamCardStyle.Sam, rows, spacing: 6);
    }

    private static ComposableNode SamButtonPair(string primary, string primaryTag, Action primaryAction,
        string secondary, string secondaryTag, Action secondaryAction, bool enabled) =>
        new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Box { Modifier.Weight(1f), Action(primary, primaryTag, primaryAction, enabled) },
            new Box { Modifier.Weight(1f), Action(secondary, secondaryTag, secondaryAction, enabled, primary: false) },
        };

    /// <summary>Source CoachChangeReceipt: what changed and what was kept, with Undo on the latest.</summary>
    private ComposableNode SamReceipt(NativeSamState state, CoachChangeReceiptDto receipt,
        AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Strong(Localize("Coach_ReceiptLabel"), palette, heading: true),
            Body(AndroidSamProjection.ReceiptSummary(receipt, Localize), palette,
                modifier: Modifier.TestTag("native.sam.receipt.summary")),
        };
        if (receipt.VocabularyFocus.Status == CoachVocabularyFocusStatus.Cleared)
            rows.Add(Body(Localize("Coach_FocusRemoved"), palette));
        else if (receipt.VocabularyFocus is { Status: CoachVocabularyFocusStatus.Applied
                     or CoachVocabularyFocusStatus.Restored, Focus: { } focus })
        {
            rows.Add(Strong(Localize(receipt.VocabularyFocus.Status == CoachVocabularyFocusStatus.Restored
                ? "Coach_FocusRestored" : "Coach_FocusApplied"), palette));
            foreach (var word in focus.Words)
                rows.Add(Body(word.TargetText, palette));
        }
        if (AndroidSamProjection.ReceiptCanUndo(state, receipt))
            rows.Add(Action(Localize("Coach_Undo"), "native.sam.receipt.undo", UndoSam,
                !state.Busy, primary: false));
        return SamCard(palette, "native.sam.receipt", SamCardStyle.Success, rows, spacing: 6);
    }

    private ComposableNode SamChips(AndroidVisualPalette palette)
    {
        ComposableNode Chip(string title, NativeSamChip chip, string id)
        {
            var button = new OutlinedButton(() => ApplySamChip(chip))
            {
                Modifier = Modifier.Padding(end: 8, bottom: 8)
                    .HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag("native.sam.chip." + id)
                    .Semantics(semantics => semantics.ContentDescription(title)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            button.Add(new Text(title));
            return button;
        }
        string Minutes(int value) =>
            string.Format(CultureInfo.CurrentCulture, Localize("Coach_ChipMinutes"), value);
        var chips = new FlowRow
        {
            Modifier.FillMaxWidth(),
            Chip(Minutes(5), NativeSamChip.Minutes5, "5"),
            Chip(Minutes(10), NativeSamChip.Minutes10, "10"),
            Chip(Minutes(20), NativeSamChip.Minutes20, "20"),
            Chip(Localize("Coach_ChipNoAudio"), NativeSamChip.NoAudio, "noaudio"),
            Chip(Localize("Coach_ChipNoSpeech"), NativeSamChip.NoSpeech, "nospeech"),
            Chip(Localize("Coach_ChipNoTyping"), NativeSamChip.NoTyping, "notyping"),
            Chip(Localize("Coach_ChipLowEnergy"), NativeSamChip.LowEnergy, "lowenergy"),
        };
        return new Column(Arrangement.SpacedBy(6))
        {
            Modifier.FillMaxWidth().TestTag("native.sam.chips"),
            Caption(Localize("Coach_StarterPrompts"), palette),
            chips,
        };
    }

    /// <summary>Source CoachVocabularySetCard, with the English-only strings localized.</summary>
    private ComposableNode SamVocabularySet(NativeSamState state, CoachVocabularySetProposal set,
        AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Strong(set.Title, palette, heading: true),
            Caption(string.Format(CultureInfo.CurrentCulture, Localize("NativeSam_VocabularySetIntro"),
                set.Terms.Count), palette),
        };
        for (var index = 0; index < set.Terms.Count; index++)
            rows.Add(Body($"{index + 1}. {set.Terms[index].TargetTerm} — {set.Terms[index].NativeTerm}", palette));
        rows.Add(SamButtonPair(
            Localize(state.VocabularySetBusy ? "NativeSam_VocabularySetSaving" : "NativeSam_VocabularySetApprove"),
            "native.sam.vocabset.approve", ApproveSamSet,
            Localize("NativeSam_VocabularySetDecline"), "native.sam.vocabset.decline", DeclineSamSet,
            !state.VocabularySetBusy));
        return SamCard(palette, "native.sam.vocabset", SamCardStyle.Sam, rows);
    }

    private ComposableNode SamVocabularySetOutcome(NativeSamState state, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Body(Localize(state.VocabularySetMessageKey!), palette,
                modifier: Modifier.TestTag("native.sam.vocabset.outcome"))
        };
        if (state.ApprovedResourceId is not null)
            rows.Add(Action(Localize("NativeSam_VocabularySetStart"), "native.sam.vocabset.start",
                StartSamReview, !Busy.Value));
        return SamCard(palette, "native.sam.vocabset.result", SamCardStyle.Info, rows, spacing: 6);
    }

    /// <summary>Stage rows and typed state notices with Stop / Try Again / Recover (source CoachStateNotice).</summary>
    private ComposableNode SamStatus(NativeSamState state, string persona, AndroidVisualPalette palette)
    {
        var status = AndroidSamProjection.Status(state, persona, Localize)!;
        var rows = new List<ComposableNode>();
        if (status.Progress)
            rows.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                Body(status.Message, palette, secondary: true,
                    modifier: Modifier.TestTag("native.sam.status.text")),
            });
        else
        {
            var message = Body(status.Message, palette, modifier: Modifier.TestTag("native.sam.status.text"));
            if (status.Danger)
                message.Color = palette.Danger;
            rows.Add(message);
        }
        var buttons = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
        };
        foreach (var action in status.Actions)
        {
            var (label, tag, command, primary) = action switch
            {
                AndroidSamStatusAction.Stop => (Localize("Coach_Stop"), "native.sam.stop", (Action)StopSam, false),
                AndroidSamStatusAction.Retry => (Localize("Coach_TryAgain"), "native.sam.retry", RetrySam, true),
                AndroidSamStatusAction.Recover => (Localize("Coach_TurnRecover"), "native.sam.retry", RetrySam, true),
                AndroidSamStatusAction.ReloadPlan => (Localize("Coach_ReloadPlan"), "native.sam.reloadplan",
                    ReloadSamPlan, true),
                AndroidSamStatusAction.KeepChatting => (Localize("Coach_KeepChatting"), "native.sam.keepchatting",
                    DismissSamNotice, false),
                AndroidSamStatusAction.KeepPlan => (Localize("Coach_KeepPlan"), "native.sam.keepplan",
                    DismissSamNotice, false),
                _ => (Localize("Coach_StartNew"), "native.sam.startnew", NewSamConversation, true),
            };
            buttons.Add(new Box { Modifier.Weight(1f), Action(label, tag, command, true, primary) });
        }
        if (!status.Actions.IsEmpty)
            rows.Add(buttons);
        if (state.Busy || state.Status is NativeSamStatus.Opening or NativeSamStatus.Resuming)
        {
            var plain = new Column(Arrangement.SpacedBy(6))
            {
                Modifier.FillMaxWidth().Padding(vertical: 4).TestTag("native.sam.status"),
            };
            foreach (var row in rows)
                plain.Add(row);
            return plain;
        }
        return SamCard(palette, "native.sam.status",
            status.Danger ? SamCardStyle.Danger : SamCardStyle.Info, rows, spacing: 6);
    }

    /// <summary>Composer: 500-character limit with counter, Send disabled when empty or over (source CoachComposer).</summary>
    private ComposableNode SamComposer(NativeSamState state, string persona, AndroidVisualPalette palette)
    {
        var label = string.Format(CultureInfo.CurrentCulture, Localize("Coach_ComposerLabelNamed"), persona);
        var keyboard = KeyboardOptionsCompanion.Default;
        var field = new OutlinedTextField(_samDraft,
            enabled: AndroidSamProjection.ComposerEnabled(state), isError: state.DraftTooLong,
            singleLine: false, maxLines: 4)
        {
            Modifier = Modifier.Weight(1f).TestTag("native.sam.composer")
                .Semantics(semantics => semantics.ContentDescription(label)),
            Placeholder = new Text(Localize("Coach_ComposerPlaceholder")),
            KeyboardOptions = keyboard.Copy(keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text, AndroidX.Compose.ImeAction.Send,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus, keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onSend: SendSam),
        };
        var send = SamIcon(Icons.AutoMirrored.Default.Send, Localize("Coach_Send"), "native.sam.send",
            SendSam, state.CanSend, palette);
        return new Column(Arrangement.SpacedBy(2))
        {
            Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag("native.sam.composer.region"),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                field,
                send,
            },
            Caption(AndroidSamProjection.Counter(state, Localize), palette, "native.sam.counter",
                danger: state.DraftTooLong),
        };
    }

    /// <summary>Conversation shelf (source CoachConversationList), owner's rows only.</summary>
    private ComposableNode SamConversations(NativeSamState state, AndroidVisualPalette palette)
    {
        var header = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 12),
            Action(Localize("Coach_NewConversation"), "native.sam.conversations.new", NewSamConversation,
                !state.Busy),
        };
        if (state.ConversationsLoading)
            header.Add(SamProgress(Localize("Coach_ConversationsLoading"), palette,
                "native.sam.conversations.loading"));
        if (state.ConversationsErrorKey is { } error)
            header.Add(SamNotice(Localize(error), palette, "native.sam.conversations.error"));
        if (state.ConversationActionKey is { } outcome)
            header.Add(SamNotice(Localize(outcome), palette, "native.sam.conversations.outcome"));
        if (state.ConversationDialog is { } dialog)
            header.Add(SamConversationDialog(state, dialog, palette));
        var conversations = state.SafeConversations;
        if (conversations.IsEmpty && !state.ConversationsLoading && state.ConversationsErrorKey is null)
        {
            header.Add(Body(Localize("Coach_ConversationsEmpty"), palette, secondary: true,
                modifier: Modifier.TestTag("native.sam.conversations.empty")));
            header.Add(Caption(Localize("Coach_ConversationsEmptyHint"), palette));
        }
        var indexed = conversations.Select((row, index) => (Row: row, Index: index)).ToImmutableArray();
        return new Column
        {
            Modifier.FillMaxWidth().Weight(1f).TestTag("native.sam.conversations.list"),
            header,
            new LazyColumn<(NativeSamConversationRow Row, int Index)>(indexed,
                item => SamConversationRow(state, item.Row, item.Index, palette))
            {
                Key = item => "c:" + item.Row.Id,
                State = _samConversationsList,
                VerticalArrangement = Arrangement.SpacedBy(8),
                Modifier = Modifier.FillMaxWidth().Weight(1f)
                    .Padding(horizontal: AndroidVisualTheme.PagePadding),
            },
        };
    }

    private ComposableNode SamConversationRow(NativeSamState state, NativeSamConversationRow row, int index,
        AndroidVisualPalette palette)
    {
        var title = row.TitleUnreadable ? Localize("Coach_ConversationUntitled") : row.Title;
        var detail = AndroidSamProjection.ConversationDetail(row, Localize);
        var id = row.Id;
        var modifier = Modifier.FillMaxWidth()
            .Border(row.IsSelected ? 2 : 1, row.IsSelected ? palette.Primary : palette.Border, 10)
            .TestTag($"native.sam.conversation.{index}");
        modifier = !state.Busy && !state.ConversationActionBusy
            ? modifier.Clickable(() => OpenSamConversation(id)).Semantics(semantics =>
            {
                semantics.ContentDescription($"{title}, {detail}");
                semantics.Role(SemanticsRole.Button);
                semantics.Selected(row.IsSelected);
            })
            : modifier.Semantics(semantics =>
            {
                semantics.ContentDescription($"{title}, {detail}");
                semantics.Selected(row.IsSelected);
            });
        var surface = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(10),
            Color = row.IsSelected ? palette.Card : palette.Elevated,
        };
        surface.Add(new Column(Arrangement.SpacedBy(2))
        {
            Modifier.FillMaxWidth().Padding(12),
            Strong(title, palette),
            Caption(detail, palette),
        });
        return new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Box { Modifier.Weight(1f), surface },
            SamConversationMenu(state, row, index, title, palette),
        };
    }
}
