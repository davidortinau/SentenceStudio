using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Native;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose cards inside the Sam stream and shelf that mirror the iOS SamView:
/// proposed changes (SamWriteCard), Copy and report (CoachCopyButton,
/// CoachReportControl), the memory candidate (CoachMemoryCandidateCard), the
/// dispute notice (CoachDisputeNotice) and conversation management
/// (CoachConversationList). Facade-only Compose; ids match native.sam.*.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    /// <summary>A compact button for wrapping rows; primary, outlined or danger.</summary>
    private static ComposableNode SamButton(string label, string tag, Action command, bool enabled,
        AndroidSamButtonStyle style, AndroidVisualPalette palette, bool? selected = null)
    {
        var modifier = Modifier.Padding(end: 8, bottom: 4)
            .HeightIn(min: AndroidVisualTheme.ControlHeight)
            .TestTag(tag)
            .Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                if (selected is { } value)
                    semantics.Selected(value);
            });
        switch (style)
        {
            case AndroidSamButtonStyle.Secondary:
            {
                var outlined = new OutlinedButton(onClick: command, enabled: enabled)
                {
                    Modifier = modifier,
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                outlined.Add(new Text(label));
                return outlined;
            }
            case AndroidSamButtonStyle.Danger:
                return new Composed(composer =>
                {
                    var danger = new ComposeButton(onClick: command, enabled: enabled)
                    {
                        Modifier = modifier,
                        Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                        Colors = composer.ButtonColors(
                            containerColor: palette.DangerContainer,
                            contentColor: palette.OnDanger,
                            disabledContainerColor: palette.Border,
                            disabledContentColor: palette.SecondaryText),
                    };
                    danger.Add(new Text(label));
                    return danger;
                });
            default:
            {
                var primary = new ComposeButton(onClick: command, enabled: enabled)
                {
                    Modifier = modifier,
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                primary.Add(new Text(label));
                return primary;
            }
        }
    }

    private static ComposableNode SamButtonRow(IEnumerable<ComposableNode> buttons)
    {
        var row = new FlowRow { Modifier.FillMaxWidth() };
        foreach (var button in buttons)
            row.Add(button);
        return row;
    }

    private static ComposableNode SamLink(string label, string tag, Action command, bool enabled = true)
    {
        var button = new TextButton(command, enabled)
        {
            Modifier = Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Text(label));
        return button;
    }

    // ------------------------------------------------------------ dispute

    /// <summary>Source CoachDisputeNotice: closed codes only, never the learner's words.</summary>
    private ComposableNode SamDispute(NativeSamState state, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode> { Caption(Localize("Coach_Dispute_Heading"), palette) };
        if (state.Dispute is { } dispute && AndroidSamCards.DisputeSignalKey(dispute) is { } signal)
            rows.Add(Body(Localize(signal), palette));
        rows.Add(Body(Localize(AndroidSamProjection.DisputeStatusKey(state)!), palette,
            modifier: Modifier.TestTag("native.sam.dispute.state")));
        return SamCard(palette, "native.sam.dispute", SamCardStyle.Info, rows);
    }

    // ------------------------------------------------------------ Copy and report

    /// <summary>Copy and the report flag share one row; the report panel opens below it.</summary>
    private IEnumerable<ComposableNode> SamMessageActions(NativeSamState state, NativeSamEntry entry, int index,
        AndroidVisualPalette palette)
    {
        var id = entry.Id;
        var row = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            SamLink(Localize(AndroidSamCards.CopyLabelKey(state, id)), $"native.sam.copy.{index}",
                () => CopySamMessage(id)),
        };
        var reportable = AndroidSamCards.IsReportable(state, entry);
        if (reportable && state.IsReported(id))
            row.Add(Caption(Localize("Coach_ReportDone"), palette, $"native.sam.reported.{index}"));
        else if (reportable && state.ReportingMessageId != id)
            row.Add(SamLink(Localize("Coach_ReportResponse"), $"native.sam.report.{index}",
                () => OpenSamReport(id), !state.Busy && !state.ReportBusy));
        yield return row;
        if (state.ReportFailedId == id && state.ReportingMessageId != id)
            yield return Caption(Localize("Coach_ReportFailed"), palette, $"native.sam.report.failed.{index}",
                danger: true);
        if (reportable && state.ReportingMessageId == id)
            yield return SamReportPanel(state, index, palette);
    }

    /// <summary>Source CoachReportControl: five closed reasons, no free text, Report and Cancel.</summary>
    private ComposableNode SamReportPanel(NativeSamState state, int index, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Strong(string.Format(CultureInfo.CurrentCulture, Localize("Coach_ReportPrompt"), SamPersona(state)),
                palette, heading: true),
        };
        var reasons = new Column
        {
            Modifier.FillMaxWidth().Semantics(semantics => semantics.SelectableGroup()),
        };
        foreach (var (reason, key, reasonId) in AndroidSamCards.ReportReasons)
        {
            var selected = state.ReportReason == reason;
            var label = Localize(key);
            var enabled = !state.ReportBusy;
            Action select = () => SelectSamReportReason(reason);
            // One radio per reason: the row is the single selectable node (merged semantics); the
            // inner RadioButton is only the visual indicator and its tap is the same selection.
            var option = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                // Disabled: no selection action, but descendants still merge so the disabled radio
                // state is announced on the single row node.
                (enabled ? Modifier.FillMaxWidth().Selectable(selected, select)
                    : Modifier.FillMaxWidth().Semantics(true, label, SemanticsRole.RadioButton))
                    .HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag($"native.sam.report.reason.{reasonId}")
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(label);
                        semantics.Role(SemanticsRole.RadioButton);
                        semantics.Selected(selected);
                    }),
                new AndroidX.Compose.RadioButton(selected, select, enabled),
                Body(label, palette),
            };
            reasons.Add(option);
        }
        rows.Add(reasons);
        if (state.ReportFailedId is { } failed && failed == state.ReportingMessageId)
            rows.Add(Caption(Localize("Coach_ReportFailed"), palette, $"native.sam.report.failed.{index}",
                danger: true));
        rows.Add(SamButtonRow([
            SamButton(Localize(state.ReportBusy ? "Coach_ReportSubmitting" : "Coach_ReportSubmit"),
                "native.sam.report.submit", SubmitSamReport, state.ReportReason is not null && !state.ReportBusy,
                AndroidSamButtonStyle.Primary, palette),
            SamButton(Localize("Coach_ReportCancel"), "native.sam.report.cancel", CancelSamReport,
                !state.ReportBusy, AndroidSamButtonStyle.Secondary, palette),
        ]));
        return SamCard(palette, "native.sam.report.panel", SamCardStyle.Learner, rows, spacing: 6);
    }

    // ------------------------------------------------------------ proposed change

    /// <summary>Source SamWriteCard: one element that moves through its states in place.</summary>
    private ComposableNode SamWriteCard(AndroidSamWriteCard card, int index, AndroidVisualPalette palette)
    {
        var prefix = $"native.sam.write.{index}";
        var heading = Strong(Localize(card.HeadingKey), palette);
        heading.Modifier = Modifier.Weight(1f).Semantics(semantics => semantics.Heading());
        var rows = new List<ComposableNode>
        {
            new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                heading,
                Caption(Localize(card.StateKey), palette, prefix + ".state"),
            },
            Body(card.Summary ?? Localize("Coach_WriteUnreadableHint"), palette,
                modifier: Modifier.TestTag(prefix + ".summary")),
        };
        foreach (var line in card.Lines)
            rows.Add(Caption("• " + line, palette));
        foreach (var note in card.Notes)
            rows.Add(Caption(Localize(note.Key), palette, danger: note.Danger));
        if (card.ShowsConfirmStep)
        {
            var step = new List<ComposableNode>
            {
                Strong(Localize("Coach_WriteConfirmTitle"), palette, heading: true),
                Caption(Localize("Coach_WriteConfirmIntro"), palette),
            };
            if (card.ConfirmIrreversible)
                step.Add(Caption(Localize("Coach_WriteIrreversibleNotice"), palette, danger: true));
            if (card.ConfirmExpiresAtUtc is { } closes)
                step.Add(Caption(string.Format(CultureInfo.CurrentCulture, Localize("Coach_WriteConfirmExpiresAt"),
                    AndroidSamCards.LocalTime(closes)), palette));
            rows.Add(SamCard(palette, prefix + ".confirmstep", SamCardStyle.Learner, step));
        }
        if (card.UndoClosesAtUtc is { } undoCloses)
            rows.Add(Caption(string.Format(CultureInfo.CurrentCulture, Localize("Coach_WriteUndoWindow"),
                AndroidSamCards.LocalTime(undoCloses)), palette));
        if (card.ErrorKey is { } error)
            rows.Add(Caption(Localize(error), palette, prefix + ".error", danger: true));
        if (!card.Buttons.IsEmpty)
        {
            var operationId = card.OperationId;
            rows.Add(SamButtonRow(card.Buttons.Select(button => SamButton(Localize(button.LabelKey),
                $"{prefix}.{button.Suffix}", () => RunSamWrite(button.Action, operationId),
                card.ButtonsEnabled, button.Style, palette))));
        }
        return SamCard(palette, prefix,
            card.Stage == NativeSamWriteStage.Applied ? SamCardStyle.Success : SamCardStyle.Sam, rows, spacing: 6);
    }

    // ------------------------------------------------------------ memory candidate

    /// <summary>Source CoachMemoryCandidateCard: Save, Edit, Only this chat, Not now.</summary>
    private ComposableNode SamMemoryCandidate(NativeSamState state, CoachMemoryFactDto fact,
        AndroidVisualPalette palette)
    {
        var busy = state.MemoryCandidateBusy;
        var rows = new List<ComposableNode>
        {
            Strong(Localize("Coach_MemoryCandidateTitle"), palette, heading: true),
            Body(fact.DisplayText, palette, modifier: Modifier.TestTag("native.sam.memory.value")),
            Caption($"{Localize("Coach_MemoryScopeLabel")}: {AndroidSamCards.MemoryScope(fact, Localize)}", palette),
        };
        if (state.MemoryCandidateEdit is not null)
            rows.Add(SamEditor(_samCandidateEdit, Localize("Coach_MemoryEditLabel"), "native.sam.memory.editfield",
                text => _sam?.SetMemoryCandidateEdit(text), typed => SamButtonRow([
                    SamButton(Localize("Coach_MemorySaveEdited"), "native.sam.memory.saveedited",
                        SaveEditedSamMemoryCandidate, AndroidSamCards.CanSaveEdit(typed, busy),
                        AndroidSamButtonStyle.Primary, palette),
                    SamButton(Localize("Coach_Cancel"), "native.sam.memory.canceledit",
                        CancelSamMemoryCandidateEdit, !busy, AndroidSamButtonStyle.Secondary, palette),
                ])));
        else
        {
            var buttons = new List<ComposableNode>
            {
                SamButton(Localize("Coach_MemorySave"), "native.sam.memory.save", SaveSamMemoryCandidate, !busy,
                    AndroidSamButtonStyle.Primary, palette),
            };
            if (AndroidSamCards.IsEditable(fact))
                buttons.Add(SamButton(Localize("Coach_MemoryEdit"), "native.sam.memory.edit",
                    BeginSamMemoryCandidateEdit, !busy, AndroidSamButtonStyle.Secondary, palette));
            buttons.Add(SamButton(Localize("Coach_MemoryOnlyThisChat"), "native.sam.memory.onlythischat",
                DeclineSamMemoryCandidate, !busy, AndroidSamButtonStyle.Secondary, palette));
            buttons.Add(SamButton(Localize("Coach_MemoryNotNow"), "native.sam.memory.notnow",
                DeclineSamMemoryCandidate, !busy, AndroidSamButtonStyle.Secondary, palette));
            rows.Add(SamButtonRow(buttons));
        }
        if (state.MemoryCandidateNoticeKey is { } notice)
            rows.Add(Caption(Localize(notice), palette, "native.sam.memory.notice"));
        rows.Add(Caption(Localize("Coach_MemoryCandidateExplainer"), palette));
        return SamCard(palette, "native.sam.memory.candidate", SamCardStyle.Sam, rows, spacing: 6);
    }

    /// <summary>
    /// A text field bound to a <see cref="SamTextEdit"/>: typing recomposes only this
    /// scope (the field and the controls that depend on its text), and each committed
    /// edit reaches the presenter.
    /// </summary>
    private static ComposableNode SamEditor(SamTextEdit edit, string label, string tag, Action<string> push,
        Func<string, ComposableNode> controls) => new Composed(composer =>
    {
        var typed = edit.Value.Value.Text ?? string.Empty;
        composer.LaunchedEffect(typed, _ =>
        {
            if (edit.Key is not null && !string.Equals(typed, edit.Synced, StringComparison.Ordinal))
            {
                edit.Synced = typed;
                push(typed);
            }
            return Task.CompletedTask;
        });
        return new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth(),
            new OutlinedTextField(edit.Value, enabled: true, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(tag)
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Label = new Text(label),
            },
            controls(typed),
        };
    });

    // ------------------------------------------------------------ conversation management

    /// <summary>Row actions: Rename, Close or Reopen, Delete (source CoachConversationList).</summary>
    private ComposableNode SamConversationMenu(NativeSamState state, NativeSamConversationRow row, int index,
        string title, AndroidVisualPalette palette)
    {
        var id = row.Id;
        var label = $"{Localize("Coach_ConversationActions")}, {title}";
        var enabled = !state.ConversationActionBusy && !state.Busy;
        var button = new IconButton(() => ToggleSamConversationMenu(id), enabled: enabled)
        {
            Modifier = Modifier.TestTag($"native.sam.conversation.{index}.actions")
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Icon(Icons.Default.MoreVert, null)
        {
            Modifier = Modifier.Size(24),
            Tint = enabled ? palette.Text : palette.SecondaryText,
        });
        var menu = new DropdownMenu(expanded: _samConversationMenu == id,
            onDismissRequest: () => ToggleSamConversationMenu(null));
        menu.Add(new DropdownMenuItem(text: new Text(Localize("Coach_ConversationRename")),
            onClick: () => BeginSamConversationDialog(NativeSamConversationDialogKind.Rename, id))
        {
            Modifier = Modifier.TestTag($"native.sam.conversation.{index}.rename"),
        });
        menu.Add(row.IsClosed
            ? new DropdownMenuItem(text: new Text(Localize("Coach_ConversationReopen")),
                onClick: () => ReopenSamConversation(id))
            {
                Modifier = Modifier.TestTag($"native.sam.conversation.{index}.reopen"),
            }
            : new DropdownMenuItem(text: new Text(Localize("Coach_ConversationClose")),
                onClick: () => BeginSamConversationDialog(NativeSamConversationDialogKind.Close, id))
            {
                Modifier = Modifier.TestTag($"native.sam.conversation.{index}.close"),
            });
        menu.Add(new DropdownMenuItem(text: new Text(Localize("Coach_ConversationDelete")),
            onClick: () => BeginSamConversationDialog(NativeSamConversationDialogKind.Delete, id))
        {
            Modifier = Modifier.TestTag($"native.sam.conversation.{index}.delete"),
        });
        return new Box { button, menu };
    }

    /// <summary>
    /// Rename / Close / Delete confirmation, inline at the head of the shelf as the source
    /// renders it, with its promise that Today's Plan and progress are untouched.
    /// </summary>
    private ComposableNode SamConversationDialog(NativeSamState state, NativeSamConversationDialog dialog,
        AndroidVisualPalette palette)
    {
        var busy = state.ConversationActionBusy;
        var rows = new List<ComposableNode>();
        ComposableNode Buttons(string confirmLabel, bool confirmEnabled, AndroidSamButtonStyle style) =>
            SamButtonRow([
                SamButton(Localize("Coach_Cancel"), "native.sam.conversations.dialog.cancel",
                    CancelSamConversationDialog, !busy, AndroidSamButtonStyle.Secondary, palette),
                SamButton(confirmLabel, "native.sam.conversations.dialog.confirm", ConfirmSamConversationDialog,
                    confirmEnabled, style, palette),
            ]);
        switch (dialog.Kind)
        {
            case NativeSamConversationDialogKind.Rename:
                rows.Add(Strong(Localize("Coach_ConversationRenameTitle"), palette, heading: true));
                rows.Add(SamEditor(_samRenameEdit, Localize("Coach_ConversationRenameLabel"),
                    "native.sam.conversations.rename.field", text => _sam?.SetRenameText(text),
                    typed => Buttons(Localize("Coach_ConversationRenameSave"),
                        AndroidSamCards.CanRename(typed, busy), AndroidSamButtonStyle.Primary)));
                break;
            case NativeSamConversationDialogKind.Close:
                rows.Add(Strong(Localize("Coach_ConversationCloseTitle"), palette, heading: true));
                rows.Add(Body(Localize("Coach_ConversationCloseBody"), palette));
                rows.Add(Buttons(Localize("Coach_ConversationClose"), !busy, AndroidSamButtonStyle.Primary));
                break;
            default:
                rows.Add(Strong(Localize("Coach_ConversationDeleteTitle"), palette, heading: true));
                rows.Add(Body(Localize("Coach_ConversationDeleteBody"), palette));
                rows.Add(Caption(Localize("Coach_ConversationDeleteImpact"), palette));
                rows.Add(Buttons(Localize("Coach_ConversationDeleteConfirm"), !busy, AndroidSamButtonStyle.Danger));
                break;
        }
        return SamCard(palette, "native.sam.conversations.dialog", SamCardStyle.Sam, rows, spacing: 8);
    }
}
