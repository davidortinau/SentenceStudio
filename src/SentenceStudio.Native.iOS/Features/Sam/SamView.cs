using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Sam;

internal sealed record SamActions(
    Action Open,
    Action Close,
    Action ToggleSize,
    Action ToggleConversations,
    Action NewConversation,
    Action<string> OpenConversation,
    Action<string> DraftChanged,
    Action Send,
    Action Retry,
    Action Stop,
    Action Dismiss,
    Action ReloadPlan,
    Action Accept,
    Action Reject,
    Action Undo,
    Action<NativeSamChip> Chip,
    Action<string> ToggleEvidence,
    Action ApproveSet,
    Action DeclineSet,
    Action StartReview);

/// <summary>
/// SwiftUI declarations for the one Sam experience. Rendered only through the
/// Microsoft.SwiftUI facade; no UIKit. Source: SentenceStudio.UI Shared/Sam and
/// Shared/Coach components (frozen reference 59c72262).
/// </summary>
internal static class SamView
{
    internal static string Persona(NativeSamState state) =>
        NativeLocalizedText.Get("Coach_RoleCoach", new CultureInfo(state.PersonaCulture));

    /// <summary>Source SamFab: the one entry, bottom trailing, labelled "Ask {name}".</summary>
    internal static SwiftView Fab(SourceUiStyle style, NativeSamState state,
        Func<string, string> text, Action open)
    {
        var appearance = style.Appearance;
        var label = string.Format(CultureInfo.CurrentCulture, text("Coach_OpenNamed"), Persona(state));
        var button = Views.Button(Views.Image("bubble.left.and.bubble.right.fill")
                    .WithFixedSymbolSize(22)
                    .WithForegroundColor(appearance.OnPrimary.Light, appearance.OnPrimary.Dark),
                open)
            .WithButtonAppearance(appearance.Primary.Light, appearance.Primary.Dark,
                appearance.Primary.Light, appearance.Primary.Dark, 28)
            .WithButtonCircle(56)
            .WithShadow("#000000", "#000000", 8, 0.2, 3)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier("native.sam.fab");
        return Views.HStack(button)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing)
            .WithPadding(0, 16, 12, 16);
    }

    /// <summary>The panel heading is the coach's name; the full label is the accessible name.</summary>
    internal static string Title(NativeSamState state, Func<string, string> text) => Persona(state);

    /// <summary>Header actions: conversations, compact/expand, close (source SamPanel header).</summary>
    internal static SwiftView[] HeaderActions(NativeSamState state, Func<string, string> text,
        SamActions actions, bool canResize)
    {
        var items = new List<SwiftView>
        {
            Views.Button(Views.Image("list.bullet"), actions.ToggleConversations)
                .WithAccessibilityLabel(text("Coach_ConversationsToggle"))
                .WithAccessibilityIdentifier("native.sam.conversations.toggle"),
            Views.Button(Views.Image("square.and.pencil"), actions.NewConversation)
                .WithAccessibilityLabel(text("Coach_NewConversation"))
                .WithAccessibilityIdentifier("native.sam.new"),
        };
        if (canResize)
            items.Add(Views.Button(Views.Image(state.Size == NativeSamPanelSize.Compact
                        ? "arrow.up.left.and.arrow.down.right" : "arrow.down.right.and.arrow.up.left"),
                    actions.ToggleSize)
                .WithAccessibilityLabel(text(state.Size == NativeSamPanelSize.Compact
                    ? "Coach_PanelExpand" : "Coach_PanelCompact"))
                .WithAccessibilityIdentifier("native.sam.size"));
        items.Add(Views.Button(Views.Image("xmark"), actions.Close)
            .WithAccessibilityLabel(text("Coach_PanelClose"))
            .WithAccessibilityIdentifier("native.sam.close"));
        foreach (var item in items)
            item.IsEnabled = !state.Busy || item.AccessibilityIdentifier == "native.sam.close";
        return items.ToArray();
    }

    /// <summary>The chat log and every card that belongs under it (source CoachChatPane).</summary>
    internal static SwiftView Stream(SourceUiStyle style, NativeSamState state,
        Func<string, string> text, SamActions actions)
    {
        var persona = Persona(state);
        var items = new List<SwiftView>();
        if (state.NoticeKey is { } noticeKey && noticeKey != "Coach_Rejected" && noticeKey != "Coach_Stopped")
            items.Add(Notice(style, text(noticeKey), "native.sam.conversation.notice"));
        if (state.UnreadableCount > 0)
            items.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    text("Coach_UnreadableCount"), state.UnreadableCount)),
                SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.sam.unreadable"));
        if (state.TranscriptLoading && state.SafeEntries.IsEmpty)
            items.Add(Views.IndeterminateProgress(text("Coach_TranscriptLoading"))
                .WithAccessibilityIdentifier("native.sam.transcript.loading"));

        var entries = state.SafeEntries;
        var lastReceiptIndex = -1;
        for (var index = 0; index < entries.Length; index++)
            if (entries[index].Kind == NativeSamEntryKind.Receipt)
                lastReceiptIndex = index;
        for (var index = 0; index < entries.Length; index++)
            items.Add(Message(style, state, entries[index], index, persona, text, actions,
                showUndo: state.LatestReceipt is null && state.CanUndo && index == lastReceiptIndex));

        if (state.LatestReceipt is { } receipt)
            items.Add(Receipt(style, state, receipt, text, actions));
        if (state.Suggestion is { } suggestion)
            items.Add(Suggestion(style, state, suggestion, persona, text, actions));
        if (state.NoticeKey is "Coach_Rejected" or "Coach_Stopped")
            items.Add(style.Text(Views.Text(text(state.NoticeKey)), SourceTextRole.Caption1,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.sam.outcome"));
        if (state.VocabularySet is { } set)
            items.Add(VocabularySet(style, state, set, text, actions));
        if (state.VocabularySetMessageKey is { } setMessage)
            items.Add(VocabularySetOutcome(style, state, text(setMessage), text, actions));
        if (state.ShowChips)
            items.Add(Chips(style, text, actions));
        if (state.ConversationClosed)
            items.Add(Notice(style, text("Coach_ConversationClosedNotice"), "native.sam.closed"));
        if (StatusRow(style, state, persona, text, actions) is { } status)
            items.Add(status);

        var stack = Views.VStack(items.ToArray()).WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.sam.stream");
        stack.Spacing = 12;
        return stack.WithPadding(12, 16, 12, 16);
    }

    private static SwiftView Message(SourceUiStyle style, NativeSamState state, NativeSamEntry entry,
        int index, string persona, Func<string, string> text, SamActions actions, bool showUndo)
    {
        var id = $"native.sam.message.{index}";
        var time = entry.CreatedAtUtc == default ? string.Empty
            : DateTime.SpecifyKind(entry.CreatedAtUtc, DateTimeKind.Utc).ToLocalTime()
                .ToString(text("Coach_MessageTimeFormat"), CultureInfo.CurrentCulture);
        var learner = entry.Kind == NativeSamEntryKind.Learner;
        var who = learner ? text("Coach_RoleYou") : persona;
        var meta = entry.IsPending ? $"{who} · {text("Coach_MessagePending")}"
            : entry.IsFailed ? $"{who} · {text("Coach_MessageFailed")}"
            : time.Length == 0 ? who : $"{who} · {time}";
        var parts = new List<SwiftView>
        {
            style.Text(Views.Text(meta), SourceTextRole.Caption1, SourceTextTone.Secondary)
        };
        switch (entry.Kind)
        {
            case NativeSamEntryKind.Unreadable:
                parts.Add(style.Text(Views.Text(text("Coach_UnreadableMessage")), SourceTextRole.Body2,
                    SourceTextTone.Secondary));
                break;
            case NativeSamEntryKind.Unsupported:
                parts.Add(style.Text(Views.Text(text("Coach_UnsupportedMessage")), SourceTextRole.Body2,
                    SourceTextTone.Secondary));
                break;
            default:
                if (!entry.Blocks.IsDefaultOrEmpty)
                    foreach (var block in entry.Blocks)
                    {
                        var label = block.Label is { Length: > 0 } custom ? custom
                            : block.Kind == CoachAnswerBlockKind.Answer ? null
                            : text("Coach_AnswerBlock_" + block.Kind);
                        if (label is not null)
                            parts.Add(style.Text(Views.Text(label), SourceTextRole.Body2Strong));
                        parts.Add(style.Text(Views.Text(block.Text), SourceTextRole.Body1));
                    }
                else if (entry.Text.Length > 0)
                    parts.Add(style.Text(Views.Text(entry.Text), SourceTextRole.Body1));
                foreach (var line in entry.ChangeLines.IsDefault ? [] : entry.ChangeLines)
                    parts.Add(style.Text(Views.Text("• " + line), SourceTextRole.Body2,
                        SourceTextTone.Secondary));
                break;
        }
        if (entry.Limitation is { } limitation)
            parts.Add(Limitation(style, limitation, index, text));
        if (entry.HasEvidence)
        {
            var expanded = state.IsEvidenceExpanded(entry.Id);
            parts.Add(style.TextLinkButton(text(expanded ? "Coach_HideEvidence" : "Coach_ViewEvidence"),
                expanded ? "chevron.up" : "chevron.down", () => actions.ToggleEvidence(entry.Id),
                $"native.sam.evidence.toggle.{index}"));
            if (expanded)
                parts.Add(Evidence(style, entry, index, text));
        }
        if (showUndo)
            parts.Add(style.OutlineButton(Views.Text(text("Coach_Undo")), actions.Undo)
                .WithAccessibilityIdentifier("native.sam.receipt.undo"));
        var body = Views.VStack(parts.ToArray()).WithFlexibleWidth();
        body.Spacing = 4;
        body.WithPadding(10, 12, 10, 12);
        if (learner)
            style.BorderedCard(body, radius: 12);
        else if (entry.Kind is NativeSamEntryKind.Notice or NativeSamEntryKind.Receipt)
            style.InfoAlert(body, compact: true);
        else
            style.Card(body, radius: 12);
        return body.WithAccessibilityIdentifier(id);
    }

    private static SwiftView Evidence(SourceUiStyle style, NativeSamEntry entry, int index,
        Func<string, string> text)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("Coach_Evidence")), SourceTextRole.Body2Strong, sourceHeading: true)
        };
        var dateFormat = text("Coach_EvidenceDateFormat");
        foreach (var evidence in entry.Evidence)
        {
            rows.Add(style.Text(Views.Text(evidence.Label), SourceTextRole.Body2Strong));
            rows.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    text("Coach_EvidenceWindow"),
                    evidence.WindowStartDate.ToString(dateFormat, CultureInfo.CurrentCulture),
                    evidence.WindowEndDate.ToString(dateFormat, CultureInfo.CurrentCulture))),
                SourceTextRole.Caption1, SourceTextTone.Secondary));
            if (evidence.Summary.Length > 0)
                rows.Add(style.Text(Views.Text(evidence.Summary), SourceTextRole.Body2));
            foreach (var value in evidence.Values)
                rows.Add(style.Text(Views.Text($"{value.Label}: {FormatValue(value, text)}"),
                    SourceTextRole.Body2, SourceTextTone.Secondary));
            if (evidence.WithheldCount is > 0)
                rows.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text("Coach_EvidenceWithheld"), evidence.WithheldCount)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary));
        }
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 4;
        return style.BorderedCard(stack.WithPadding(8, 10, 8, 10), radius: 8)
            .WithAccessibilityIdentifier($"native.sam.evidence.{index}");
    }

    private static string FormatValue(CoachEvidenceValueDto value, Func<string, string> text) =>
        value.Unit switch
        {
            CoachEvidenceUnit.Minutes => string.Format(CultureInfo.CurrentCulture,
                text("Coach_PracticeMinutes"), Math.Round(value.Value)),
            CoachEvidenceUnit.Percent => (value.Value / 100).ToString("P0", CultureInfo.CurrentCulture),
            _ => Math.Round(value.Value, 1).ToString(CultureInfo.CurrentCulture),
        };

    /// <summary>Typed refusal (source CoachLimitationCard, T variant without export).</summary>
    private static SwiftView Limitation(SourceUiStyle style, CoachLimitationDto limitation, int index,
        Func<string, string> text)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text(limitation.Code == CoachLimitationCode.Unknown
                    ? "Coach_Limitation_Unknown_Heading" : "Coach_Limitation_" + limitation.Code)),
                SourceTextRole.Body2Strong, sourceHeading: true)
        };
        if (limitation.AffectedCount is > 0 and var count)
            rows.Add(style.Text(Views.Text($"{text("Coach_Limitation_AffectedItems")}: {count}"),
                SourceTextRole.Body2));
        if (limitation.Destination is { } destination && destination.Route != CoachRouteName.Unknown)
        {
            rows.Add(style.Text(Views.Text(text("Coach_Route_" + destination.Route)), SourceTextRole.Body2Strong));
            rows.Add(style.Text(Views.Text(text("Coach_Limitation_SideEffectHeading")), SourceTextRole.Caption1,
                SourceTextTone.Secondary));
            rows.Add(style.Text(Views.Text(text(destination.SideEffect == CoachRouteSideEffect.Unknown
                    ? "Coach_RouteEffect_Unknown" : "Coach_RouteEffect_" + destination.SideEffect)),
                SourceTextRole.Body2));
        }
        var alternatives = limitation.Alternatives
            .Where(code => code is not (CoachAlternativeCode.Unknown or CoachAlternativeCode.ExportBeforeRemoving))
            .ToArray();
        if (alternatives.Length > 0)
        {
            rows.Add(style.Text(Views.Text(text("Coach_Limitation_AlternativesHeading")),
                SourceTextRole.Body2Strong));
            foreach (var alternative in alternatives)
                rows.Add(style.Text(Views.Text("• " + text("Coach_Alternative_" + alternative)),
                    SourceTextRole.Body2));
        }
        var rungs = limitation.HintLadder.Where(rung => rung.Kind != CoachHintKind.Unknown).ToArray();
        if (rungs.Length > 0)
        {
            rows.Add(style.Text(Views.Text(text("Coach_Limitation_HintLadderHeading")),
                SourceTextRole.Body2Strong));
            foreach (var rung in rungs.OrderBy(rung => rung.Rung))
                rows.Add(style.Text(Views.Text("• " + text("Coach_Hint_" + rung.Kind)), SourceTextRole.Body2));
        }
        if (limitation.ShorterSession is { } shorter)
            rows.Add(style.Text(Views.Text($"{text("Coach_Limitation_ShorterSession")}: "
                + $"{shorter.SuggestedItemCount} / {shorter.FullItemCount}"), SourceTextRole.Body2));
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 4;
        return style.InfoAlert(stack, compact: true)
            .WithAccessibilityLabel(text("Coach_Limitation_RefusalRegionLabel"))
            .WithAccessibilityIdentifier($"native.sam.limitation.{index}");
    }

    /// <summary>Source CoachSuggestionCard: one action pair, nothing changes until Accept.</summary>
    private static SwiftView Suggestion(SourceUiStyle style, NativeSamState state,
        PendingCoachSuggestionDto suggestion, string persona, Func<string, string> text, SamActions actions)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("Coach_SuggestionLabel")), SourceTextRole.Body2Strong, sourceHeading: true),
            style.Text(Views.Text(suggestion.VocabularyFocus is { } focus
                    ? string.Format(CultureInfo.CurrentCulture, text("Coach_FocusSuggestionSummary"),
                        focus.DisplayLabel).Replace(text("Coach_RoleCoach"), persona)
                    : suggestion.Rationale), SourceTextRole.Body1)
                .WithAccessibilityIdentifier("native.sam.suggestion.rationale"),
        };
        var diff = suggestion.Preview;
        var changes = new List<string>();
        if (diff.AddedItemCount > 0) changes.Add($"{text("Coach_ChangeAdded")}: {diff.AddedItemCount}");
        if (diff.RemovedItemCount > 0) changes.Add($"{text("Coach_ChangeRemoved")}: {diff.RemovedItemCount}");
        if (diff.AdjustedItemCount > 0) changes.Add($"{text("Coach_ChangeAdjusted")}: {diff.AdjustedItemCount}");
        var kept = diff.PreservedCompletedItemCount + diff.PreservedInProgressItemCount;
        if (kept > 0) changes.Add($"{text("Coach_ChangeKept")}: {kept}");
        changes.Add(string.Format(CultureInfo.CurrentCulture, text("Coach_PlanTotalMinutes"),
            diff.EstimatedMinutesAfter));
        rows.Add(style.Text(Views.Text(string.Join(" · ", changes)), SourceTextRole.Caption1,
            SourceTextTone.Secondary).WithAccessibilityIdentifier("native.sam.suggestion.preview"));
        if (suggestion.VocabularyFocus is { } words && words.Words.Count > 0)
        {
            rows.Add(style.Text(Views.Text(text("Coach_FocusProposed")), SourceTextRole.Body2Strong));
            foreach (var word in words.Words)
                rows.Add(style.Text(Views.Text(word.DisplayText is { Length: > 0 } display
                    ? $"{word.TargetText} — {display}" : word.TargetText), SourceTextRole.Body2));
        }
        if (state.ClarifyingQuestion is { } question)
            rows.Add(style.Text(Views.Text(question), SourceTextRole.Body1)
                .WithAccessibilityIdentifier("native.sam.suggestion.clarification"));
        var accept = style.PrimaryButton(text("Coach_Accept"), actions.Accept)
            .WithAccessibilityIdentifier("native.sam.suggestion.accept");
        var reject = style.SecondaryButton(text("Coach_Reject"), actions.Reject)
            .WithAccessibilityIdentifier("native.sam.suggestion.reject");
        accept.IsEnabled = reject.IsEnabled = !state.Busy;
        var buttons = Views.HStack(accept, reject);
        buttons.Spacing = 8;
        rows.Add(buttons);
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 6;
        return style.Card(stack.WithPadding(12), radius: 12)
            .WithAccessibilityIdentifier("native.sam.suggestion");
    }

    /// <summary>Source CoachChangeReceipt: what changed and what was kept, with Undo on the latest.</summary>
    private static SwiftView Receipt(SourceUiStyle style, NativeSamState state, CoachChangeReceiptDto receipt,
        Func<string, string> text, SamActions actions)
    {
        var undone = receipt.Revision.Source == CoachRevisionSource.Undo;
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("Coach_ReceiptLabel")), SourceTextRole.Body2Strong, sourceHeading: true),
            style.Text(Views.Text(undone ? text("Coach_ReceiptUndone")
                    : string.Format(CultureInfo.CurrentCulture, text("Coach_ReceiptUpdated"),
                        receipt.ReplacedItemCount, receipt.PreservedCompletedItemCount,
                        receipt.PreservedMinutesSpent)), SourceTextRole.Body1)
                .WithAccessibilityIdentifier("native.sam.receipt.summary"),
        };
        if (receipt.VocabularyFocus.Status == CoachVocabularyFocusStatus.Cleared)
            rows.Add(style.Text(Views.Text(text("Coach_FocusRemoved")), SourceTextRole.Body2));
        else if (receipt.VocabularyFocus is { Status: CoachVocabularyFocusStatus.Applied
                     or CoachVocabularyFocusStatus.Restored, Focus: { } focus })
        {
            rows.Add(style.Text(Views.Text(text(receipt.VocabularyFocus.Status == CoachVocabularyFocusStatus.Restored
                ? "Coach_FocusRestored" : "Coach_FocusApplied")), SourceTextRole.Body2Strong));
            foreach (var word in focus.Words)
                rows.Add(style.Text(Views.Text(word.TargetText), SourceTextRole.Body2));
        }
        if (!undone && state.CanUndo && state.UndoRevisionId == receipt.Revision.RevisionId)
        {
            var undo = style.OutlineButton(Views.Text(text("Coach_Undo")), actions.Undo)
                .WithAccessibilityIdentifier("native.sam.receipt.undo");
            undo.IsEnabled = !state.Busy;
            rows.Add(undo);
        }
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 6;
        return style.Alert(stack, success: true, compact: true)
            .WithAccessibilityIdentifier("native.sam.receipt");
    }

    private static SwiftView Chips(SourceUiStyle style, Func<string, string> text, SamActions actions)
    {
        SwiftView Chip(string title, NativeSamChip chip, string id) =>
            style.OutlineButton(Views.Text(title), () => actions.Chip(chip))
                .WithAccessibilityIdentifier("native.sam.chip." + id);
        string Minutes(int value) => string.Format(CultureInfo.CurrentCulture, text("Coach_ChipMinutes"), value);
        var chips = Views.ButtonGroup([
            Chip(Minutes(5), NativeSamChip.Minutes5, "5"),
            Chip(Minutes(10), NativeSamChip.Minutes10, "10"),
            Chip(Minutes(20), NativeSamChip.Minutes20, "20"),
            Chip(text("Coach_ChipNoAudio"), NativeSamChip.NoAudio, "noaudio"),
            Chip(text("Coach_ChipNoSpeech"), NativeSamChip.NoSpeech, "nospeech"),
            Chip(text("Coach_ChipNoTyping"), NativeSamChip.NoTyping, "notyping"),
            Chip(text("Coach_ChipLowEnergy"), NativeSamChip.LowEnergy, "lowenergy")],
            wraps: true);
        var stack = Views.VStack(
            style.Text(Views.Text(text("Coach_StarterPrompts")), SourceTextRole.Caption1, SourceTextTone.Secondary),
            chips).WithFlexibleWidth();
        stack.Spacing = 6;
        return stack.WithAccessibilityIdentifier("native.sam.chips");
    }

    /// <summary>Source CoachVocabularySetCard, with the English-only strings localized.</summary>
    private static SwiftView VocabularySet(SourceUiStyle style, NativeSamState state,
        CoachVocabularySetProposal set, Func<string, string> text, SamActions actions)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(set.Title), SourceTextRole.Body1Strong, sourceHeading: true),
            style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                text("NativeSam_VocabularySetIntro"), set.Terms.Count)), SourceTextRole.Caption1,
                SourceTextTone.Secondary),
        };
        for (var index = 0; index < set.Terms.Count; index++)
            rows.Add(style.Text(Views.Text($"{index + 1}. {set.Terms[index].TargetTerm} — {set.Terms[index].NativeTerm}"),
                SourceTextRole.Body2));
        var approve = style.PrimaryButton(text(state.VocabularySetBusy
                ? "NativeSam_VocabularySetSaving" : "NativeSam_VocabularySetApprove"), actions.ApproveSet)
            .WithAccessibilityIdentifier("native.sam.vocabset.approve");
        var decline = style.SecondaryButton(text("NativeSam_VocabularySetDecline"), actions.DeclineSet)
            .WithAccessibilityIdentifier("native.sam.vocabset.decline");
        approve.IsEnabled = decline.IsEnabled = !state.VocabularySetBusy;
        var buttons = Views.HStack(approve, decline);
        buttons.Spacing = 8;
        rows.Add(buttons);
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 4;
        return style.Card(stack.WithPadding(12), radius: 12)
            .WithAccessibilityIdentifier("native.sam.vocabset");
    }

    private static SwiftView VocabularySetOutcome(SourceUiStyle style, NativeSamState state, string message,
        Func<string, string> text, SamActions actions)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(message), SourceTextRole.Body2)
                .WithAccessibilityIdentifier("native.sam.vocabset.outcome")
        };
        if (state.ApprovedResourceId is not null)
            rows.Add(style.PrimaryButton(text("NativeSam_VocabularySetStart"), actions.StartReview)
                .WithAccessibilityIdentifier("native.sam.vocabset.start"));
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 6;
        return style.InfoAlert(stack, compact: true);
    }

    private static SwiftView Notice(SourceUiStyle style, string message, string id) =>
        style.InfoAlert(style.Text(Views.Text(message), SourceTextRole.Body2), compact: true)
            .WithAccessibilityIdentifier(id);

    /// <summary>Stage rows and typed state notices (source CoachStateNotice).</summary>
    private static SwiftView? StatusRow(SourceUiStyle style, NativeSamState state, string persona,
        Func<string, string> text, SamActions actions)
    {
        var rows = new List<SwiftView>();
        string? message = null;
        var danger = false;
        switch (state.Status)
        {
            case NativeSamStatus.Opening:
            case NativeSamStatus.Resuming:
                rows.Add(Views.IndeterminateProgress(text("Coach_Resuming")));
                message = text("Coach_Resuming");
                break;
            case NativeSamStatus.Running:
            case NativeSamStatus.Applying:
            case NativeSamStatus.Undoing:
                rows.Add(Views.IndeterminateProgress(text("Coach_StageReading")));
                message = state.StillWorking ? text("Coach_StageStillWorking")
                    : text(state.Status switch
                    {
                        NativeSamStatus.Applying => "Coach_StageApplying",
                        NativeSamStatus.Undoing => "Coach_StageUndoing",
                        _ => "Coach_StageReading",
                    });
                break;
            case NativeSamStatus.Offline: message = text("Coach_Offline"); break;
            case NativeSamStatus.Failed: message = text("Coach_Failed"); danger = true; break;
            case NativeSamStatus.Incomplete: message = text("Coach_Incomplete"); break;
            case NativeSamStatus.Limited:
                message = string.Format(CultureInfo.CurrentCulture, text("Coach_Limited"),
                    text("Coach_LimitResetTomorrow"));
                break;
            case NativeSamStatus.Expired: message = text("Coach_Expired"); break;
            case NativeSamStatus.Unavailable: message = text("Coach_SettingsUnavailable"); break;
            case NativeSamStatus.PlanChangedElsewhere: message = text("Coach_StatusOutOfDate"); break;
            case NativeSamStatus.InputTooLong:
                message = string.Format(CultureInfo.CurrentCulture, text("Coach_InputTooLong"),
                    CoachConstraintLimits.MaxTurnTextLength);
                break;
            case NativeSamStatus.ClarificationLimitReached: message = text("Coach_ClarificationLimit"); break;
            case NativeSamStatus.SyncRequired:
                message = string.Format(CultureInfo.CurrentCulture, text("NativeSam_SyncRequired"), persona);
                break;
            default:
                return null;
        }
        rows.Add(style.Text(Views.Text(message!), SourceTextRole.Body2,
                danger ? SourceTextTone.Danger : SourceTextTone.Body)
            .WithAccessibilityIdentifier("native.sam.status.text"));
        var buttons = new List<SwiftView>();
        if (state.CanStop && state.Busy)
            buttons.Add(style.OutlineButton(Views.Text(text("Coach_Stop")), actions.Stop)
                .WithAccessibilityIdentifier("native.sam.stop"));
        if (!state.Busy && state.CanRetry)
            buttons.Add(style.PrimaryButton(text(state.CanRecover ? "Coach_TurnRecover" : "Coach_TryAgain"),
                actions.Retry).WithAccessibilityIdentifier("native.sam.retry"));
        if (!state.Busy && state.Status == NativeSamStatus.PlanChangedElsewhere)
        {
            buttons.Add(style.PrimaryButton(text("Coach_ReloadPlan"), actions.ReloadPlan)
                .WithAccessibilityIdentifier("native.sam.reloadplan"));
            buttons.Add(style.SecondaryButton(text("Coach_KeepChatting"), actions.Dismiss)
                .WithAccessibilityIdentifier("native.sam.keepchatting"));
        }
        else if (!state.Busy && state.Status is NativeSamStatus.Failed or NativeSamStatus.Incomplete)
            buttons.Add(style.SecondaryButton(text("Coach_KeepPlan"), actions.Dismiss)
                .WithAccessibilityIdentifier("native.sam.keepplan"));
        else if (!state.Busy && state.Status == NativeSamStatus.Expired)
            buttons.Add(style.PrimaryButton(text("Coach_StartNew"), actions.NewConversation)
                .WithAccessibilityIdentifier("native.sam.startnew"));
        if (buttons.Count > 0)
        {
            var group = Views.HStack(buttons.ToArray());
            group.Spacing = 8;
            rows.Add(group);
        }
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 6;
        return (state.Busy || state.Status is NativeSamStatus.Opening or NativeSamStatus.Resuming
                ? stack.WithPadding(4)
                : danger ? style.Alert(stack, success: false, compact: true)
                : style.InfoAlert(stack, compact: true))
            .WithAccessibilityIdentifier("native.sam.status");
    }

    /// <summary>Composer: 500-character limit with counter, Send disabled when empty or over (source CoachComposer).</summary>
    internal static SwiftView Composer(SourceUiStyle style, NativeSamState state, string persona,
        Func<string, string> text, SamActions actions, out SwiftView field, out SwiftView send,
        out SwiftView counter)
    {
        field = style.TextInput(Views.TextField(text("Coach_ComposerPlaceholder"), state.Draft,
                actions.DraftChanged)
            .OnSubmit(actions.Send)
            .WithAccessibilityLabel(string.Format(CultureInfo.CurrentCulture,
                text("Coach_ComposerLabelNamed"), persona))
            .WithAccessibilityIdentifier("native.sam.composer"));
        send = Views.Button(Views.Image("arrow.up.circle.fill").WithFixedSymbolSize(30), actions.Send)
            .WithAccessibilityLabel(text("Coach_Send"))
            .WithAccessibilityIdentifier("native.sam.send");
        counter = style.Text(Views.Text(Counter(state, text)), SourceTextRole.Caption1,
                state.DraftTooLong ? SourceTextTone.Danger : SourceTextTone.Secondary)
            .WithAccessibilityIdentifier("native.sam.counter");
        var row = Views.HStack(field.WithFlexibleWidth(), send);
        row.Spacing = 8;
        var composer = Views.VStack(row, counter).WithFlexibleWidth();
        composer.Spacing = 2;
        UpdateComposer(state, field, send, counter, text);
        return style.Page(composer.WithPadding(8, 16, 8, 16))
            .WithAccessibilityIdentifier("native.sam.composer.region");
    }

    internal static string Counter(NativeSamState state, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture, text("Coach_InputCounter"), state.DraftLength,
            CoachConstraintLimits.MaxTurnTextLength);

    internal static void UpdateComposer(NativeSamState state, SwiftView field, SwiftView send,
        SwiftView counter, Func<string, string> text)
    {
        if (!string.Equals(field.Text, state.Draft, StringComparison.Ordinal))
            field.Text = state.Draft;
        var usable = state.IsOpen && !state.ConversationClosed && state.ConversationId is not null
            && state.Status is not (NativeSamStatus.Limited or NativeSamStatus.Unavailable);
        if (field.IsEnabled != usable)
            field.IsEnabled = usable;
        if (send.IsEnabled != state.CanSend)
            send.IsEnabled = state.CanSend;
        var next = Counter(state, text);
        if (counter.Text != next)
            counter.Text = next;
    }

    /// <summary>Conversation shelf (source CoachConversationList), owner's rows only.</summary>
    internal static SwiftView Conversations(SourceUiStyle style, NativeSamState state,
        Func<string, string> text, SamActions actions)
    {
        var rows = new List<SwiftView>
        {
            style.PrimaryButton(text("Coach_NewConversation"), actions.NewConversation, fullWidth: true)
                .WithAccessibilityIdentifier("native.sam.conversations.new")
        };
        if (state.ConversationsLoading)
            rows.Add(Views.IndeterminateProgress(text("Coach_ConversationsLoading")));
        if (state.ConversationsErrorKey is { } error)
            rows.Add(Notice(style, text(error), "native.sam.conversations.error"));
        var conversations = state.SafeConversations;
        if (conversations.IsEmpty && !state.ConversationsLoading && state.ConversationsErrorKey is null)
        {
            rows.Add(style.Text(Views.Text(text("Coach_ConversationsEmpty")), SourceTextRole.Body2,
                SourceTextTone.Secondary).WithAccessibilityIdentifier("native.sam.conversations.empty"));
            rows.Add(style.Text(Views.Text(text("Coach_ConversationsEmptyHint")), SourceTextRole.Caption1,
                SourceTextTone.Secondary));
        }
        var dateFormat = text("Coach_ConversationDateFormat");
        for (var index = 0; index < conversations.Length; index++)
        {
            var row = conversations[index];
            var title = row.TitleUnreadable ? text("Coach_ConversationUntitled") : row.Title;
            var detail = string.Format(CultureInfo.CurrentCulture, text("Coach_ConversationUpdated"),
                    DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc).ToLocalTime()
                        .ToString(dateFormat, CultureInfo.CurrentCulture))
                + " · " + string.Format(CultureInfo.CurrentCulture, text("Coach_ConversationMessageCount"),
                    row.MessageCount)
                + (row.IsClosed ? " · " + text("Coach_ConversationClosedBadge") : string.Empty);
            var label = Views.VStack(
                    style.Text(Views.Text(title), SourceTextRole.Body1Strong),
                    style.Text(Views.Text(detail), SourceTextRole.Caption1, SourceTextTone.Secondary))
                .WithFlexibleWidth();
            label.Spacing = 2;
            var id = row.Id;
            var button = Views.Button(label, () => actions.OpenConversation(id))
                .WithAccessibilityLabel($"{title}, {detail}")
                .WithAccessibilityIdentifier($"native.sam.conversation.{index}")
                .WithAccessibilitySelected(row.IsSelected);
            rows.Add(style.BorderedCard(button.WithPadding(10), radius: 10));
        }
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 8;
        return style.Page(Views.Scroll(stack.WithPadding(16)))
            .WithAccessibilityIdentifier("native.sam.conversations.list");
    }
}
