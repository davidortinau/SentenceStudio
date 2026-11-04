using System.Collections.Immutable;
using System.Globalization;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal enum AndroidSamRowKind
{
    Dispute,
    Notice,
    Unreadable,
    TranscriptLoading,
    Message,
    Receipt,
    Suggestion,
    MemoryCandidate,
    MemoryNotice,
    Outcome,
    VocabularySet,
    VocabularySetOutcome,
    Chips,
    Closed,
    Status,
}

/// <summary>One keyed row of the Sam chat stream; <see cref="EntryIndex"/> is set for messages.</summary>
internal sealed record AndroidSamRow(string Key, AndroidSamRowKind Kind,
    int EntryIndex = -1, bool ShowUndo = false);

internal enum AndroidSamStatusAction { Stop, Retry, Recover, ReloadPlan, KeepChatting, KeepPlan, StartNew }

/// <summary>Where the Sam entry sits on the current screen.</summary>
internal enum AndroidSamEntryPlacement { None, Floating, TopBar }

internal sealed record AndroidSamStatus(string Message, bool Danger, bool Progress,
    ImmutableArray<AndroidSamStatusAction> Actions);

/// <summary>
/// Platform-neutral projection of the shared <see cref="NativeSamState"/> for the
/// Compose head. It holds no Android UI types, mirrors the iOS SamView stream
/// order and status rules (source CoachChatPane / CoachStateNotice), and keeps
/// the scroll-follow rule testable on the host. Business rules stay in
/// <see cref="NativeSamPresenter"/>.
/// </summary>
internal static class AndroidSamProjection
{
    /// <summary>Material 3 compact-width boundary: phones open compact and may resize.</summary>
    internal const int ExpandedWidthDp = 600;

    /// <summary>Compact panel height as a fraction of the window (iOS detent 0.6).</summary>
    internal const float CompactHeightFraction = 0.6f;

    internal static bool IsResizable(int widthDp) => widthDp < ExpandedWidthDp;

    internal static NativeSamPanelSize DefaultSize(int widthDp) =>
        IsResizable(widthDp) ? NativeSamPanelSize.Compact : NativeSamPanelSize.Expanded;

    /// <summary>A tablet always shows the full panel; a phone follows the presenter's size.</summary>
    internal static bool UsesCompactHeight(NativeSamState state, int widthDp) =>
        IsResizable(widthDp) && state.Size == NativeSamPanelSize.Compact;

    /// <summary>
    /// Source CoachSurfaceGate: signed in, past onboarding, not syncing. Every
    /// screen of the signed-in shell qualifies (Dashboard, Resources, the screens
    /// opened over them, the quiz and every activity); sign-in, the post-login
    /// sync and onboarding do not.
    /// </summary>
    internal static bool IsShellDestination(NativeDestination destination) =>
        destination is NativeDestination.Dashboard or NativeDestination.TodaysPlan
            or NativeDestination.Resources or NativeDestination.VocabularyQuiz
            or NativeDestination.QuizSummary or NativeDestination.Activity;

    /// <summary>
    /// Source MainLayout mounts Sam on every signed-in page. The two tab roots keep
    /// the floating button; a screen opened over them (<paramref name="screenOverTab"/>:
    /// a More screen or the resource form), the quiz and every activity get the
    /// same entry in their top app bar, because a floating button there would
    /// cover the screen's bottom answer and send bars.
    /// </summary>
    internal static AndroidSamEntryPlacement EntryPlacement(NativeDestination destination,
        bool preview, bool screenOverTab, NativeSamState state)
    {
        if (!IsShellDestination(destination) || preview || !state.IsVisible || state.IsOpen)
            return AndroidSamEntryPlacement.None;
        return (destination is NativeDestination.Dashboard or NativeDestination.TodaysPlan
                or NativeDestination.Resources) && !screenOverTab
            ? AndroidSamEntryPlacement.Floating
            : AndroidSamEntryPlacement.TopBar;
    }

    /// <summary>
    /// A Sam vocabulary review opens the quiz from a tab root only: over an activity,
    /// the quiz, a More screen or the resource form the learner is asked to close it.
    /// </summary>
    internal static bool ReviewBlockedByScreen(NativeDestination destination, bool screenOverTab) =>
        destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan
            or NativeDestination.Resources) || screenOverTab;

    internal static bool IsEntryVisible(NativeDestination destination, bool preview,
        bool screenOverTab, NativeSamState state) =>
        EntryPlacement(destination, preview, screenOverTab, state) != AndroidSamEntryPlacement.None;

    internal static bool IsPresented(NativeDestination destination, bool preview, NativeSamState state) =>
        IsShellDestination(destination) && !preview && state.IsVisible && state.IsOpen;

    /// <summary>The coach name follows the study language (Korean study shows the ko value).</summary>
    internal static string Persona(NativeSamState state, Func<string, CultureInfo, string> text) =>
        text("Coach_RoleCoach", new CultureInfo(state.PersonaCulture));

    internal static string EntryLabel(string persona, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture, text("Coach_OpenNamed"), persona);

    /// <summary>Stream rows in the iOS/source order; keys are unique and stable per item.</summary>
    internal static ImmutableArray<AndroidSamRow> Rows(NativeSamState state)
    {
        var rows = ImmutableArray.CreateBuilder<AndroidSamRow>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(AndroidSamRow row)
        {
            var key = row.Key;
            for (var suffix = 1; !keys.Add(key); suffix++)
                key = $"{row.Key}#{suffix}";
            rows.Add(row with { Key = key });
        }

        // The learner's open correction stays at the head of the log (source CoachDisputeNotice).
        if (DisputeStatusKey(state) is not null)
            Add(new("dispute", AndroidSamRowKind.Dispute));
        if (state.NoticeKey is { } notice && notice is not ("Coach_Rejected" or "Coach_Stopped"))
            Add(new("notice", AndroidSamRowKind.Notice));
        if (state.UnreadableCount > 0)
            Add(new("unreadable", AndroidSamRowKind.Unreadable));
        var entries = state.SafeEntries;
        if (state.TranscriptLoading && entries.IsEmpty)
            Add(new("loading", AndroidSamRowKind.TranscriptLoading));
        var lastReceipt = -1;
        for (var index = 0; index < entries.Length; index++)
            if (entries[index].Kind == NativeSamEntryKind.Receipt)
                lastReceipt = index;
        for (var index = 0; index < entries.Length; index++)
            Add(new("m:" + entries[index].Id, AndroidSamRowKind.Message, index,
                ShowUndo: state.LatestReceipt is null && state.CanUndo && index == lastReceipt));
        if (state.LatestReceipt is { } receipt)
            Add(new("receipt:" + receipt.Revision.RevisionId, AndroidSamRowKind.Receipt));
        if (state.Suggestion is { } suggestion)
            Add(new("suggestion:" + suggestion.SuggestionId, AndroidSamRowKind.Suggestion));
        // Its own card after any plan suggestion: being remembered is a separate consent.
        if (state.MemoryCandidate is { } candidate)
            Add(new("memory:" + candidate.Id, AndroidSamRowKind.MemoryCandidate));
        else if (state.MemoryCandidateNoticeKey is not null)
            Add(new("memory.notice", AndroidSamRowKind.MemoryNotice));
        if (state.NoticeKey is "Coach_Rejected" or "Coach_Stopped")
            Add(new("outcome", AndroidSamRowKind.Outcome));
        if (state.VocabularySet is { } set)
            Add(new("vocabset:" + set.ProposalId, AndroidSamRowKind.VocabularySet));
        if (state.VocabularySetMessageKey is not null)
            Add(new("vocabset.outcome", AndroidSamRowKind.VocabularySetOutcome));
        if (state.ShowChips)
            Add(new("chips", AndroidSamRowKind.Chips));
        if (state.ConversationClosed)
            Add(new("closed", AndroidSamRowKind.Closed));
        if (Status(state, "", key => key) is not null)
            Add(new("status", AndroidSamRowKind.Status));
        return rows.ToImmutable();
    }

    /// <summary>Source CoachDisputeNotice: closed codes only, never the learner's words.</summary>
    internal static string? DisputeStatusKey(NativeSamState state) =>
        state.Dispute is not { } dispute || dispute.Status == CoachDisputeStatus.Unknown ? null
        : dispute.Status switch
        {
            CoachDisputeStatus.ResolvedByReRead => "Coach_Dispute_Resolved_ReRead",
            CoachDisputeStatus.ResolvedByCorrection => "Coach_Dispute_Resolved_Correction",
            CoachDisputeStatus.ResolvedByLimitation => "Coach_Dispute_Resolved_Limitation",
            CoachDisputeStatus.DismissedByLearner => "Coach_Dispute_Dismissed",
            _ => "Coach_Dispute_Open",
        };

    /// <summary>Stage rows and typed state notices (source CoachStateNotice), as on iOS.</summary>
    internal static AndroidSamStatus? Status(NativeSamState state, string persona, Func<string, string> text)
    {
        string message;
        var danger = false;
        var progress = false;
        switch (state.Status)
        {
            case NativeSamStatus.Opening:
            case NativeSamStatus.Resuming:
                progress = true;
                message = text("Coach_Resuming");
                break;
            case NativeSamStatus.Running:
            case NativeSamStatus.Applying:
            case NativeSamStatus.Undoing:
                progress = true;
                message = state.StillWorking ? text("Coach_StageStillWorking")
                    : text(state.Status switch
                    {
                        NativeSamStatus.Applying => "Coach_StageApplying",
                        NativeSamStatus.Undoing => "Coach_StageUndoing",
                        _ => "Coach_StageReading",
                    });
                break;
            case NativeSamStatus.Offline: message = text("Coach_Offline"); break;
            case NativeSamStatus.Failed:
                message = state.FailureKey is { } failure
                    ? string.Format(CultureInfo.CurrentCulture, text(failure), persona) : text("Coach_Failed");
                danger = true;
                break;
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
        var actions = ImmutableArray.CreateBuilder<AndroidSamStatusAction>();
        if (state.CanStop && state.Busy)
            actions.Add(AndroidSamStatusAction.Stop);
        if (!state.Busy && state.CanRetry)
            actions.Add(state.CanRecover ? AndroidSamStatusAction.Recover : AndroidSamStatusAction.Retry);
        if (!state.Busy && state.Status == NativeSamStatus.PlanChangedElsewhere)
        {
            actions.Add(AndroidSamStatusAction.ReloadPlan);
            actions.Add(AndroidSamStatusAction.KeepChatting);
        }
        else if (!state.Busy && state.Status is NativeSamStatus.Failed or NativeSamStatus.Incomplete)
            actions.Add(AndroidSamStatusAction.KeepPlan);
        else if (!state.Busy && state.Status == NativeSamStatus.Expired)
            actions.Add(AndroidSamStatusAction.StartNew);
        return new AndroidSamStatus(message, danger, progress, actions.ToImmutable());
    }

    /// <summary>Composer is usable only in an open, writable thread (source CoachComposer).</summary>
    internal static bool ComposerEnabled(NativeSamState state) =>
        state.IsOpen && !state.ConversationClosed && state.ConversationId is not null
        && state.Status is not (NativeSamStatus.Limited or NativeSamStatus.Unavailable);

    internal static string Counter(NativeSamState state, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture, text("Coach_InputCounter"), state.DraftLength,
            CoachConstraintLimits.MaxTurnTextLength);

    /// <summary>
    /// Whether the chat should move to the newest row after the stream changed.
    /// A new or switched thread jumps to its end; the learner's own send always
    /// follows; otherwise the list follows only when it was already at the end,
    /// so a learner who scrolled up to reread is never pulled back down.
    /// </summary>
    internal static AndroidSamFollow Follow(string? previousLastKey, string? previousConversation,
        string? lastKey, string? conversation, bool wasAtEnd, bool userScrolling, bool newestIsOwnSend)
    {
        if (lastKey is null || string.Equals(previousLastKey, lastKey, StringComparison.Ordinal)
            && string.Equals(previousConversation, conversation, StringComparison.Ordinal))
            return AndroidSamFollow.None;
        if (previousLastKey is null
            || !string.Equals(previousConversation, conversation, StringComparison.Ordinal))
            return AndroidSamFollow.Jump;
        if (newestIsOwnSend)
            return AndroidSamFollow.Animate;
        return wasAtEnd && !userScrolling ? AndroidSamFollow.Animate : AndroidSamFollow.None;
    }

    /// <summary>The key the follow rule tracks: the newest row other than the trailing status row.</summary>
    internal static string? NewestContentKey(ImmutableArray<AndroidSamRow> rows) =>
        rows.LastOrDefault(row => row.Kind != AndroidSamRowKind.Status)?.Key;

    /// <summary>True when the newest message row is the learner's pending send.</summary>
    internal static bool NewestIsOwnSend(NativeSamState state, ImmutableArray<AndroidSamRow> rows)
    {
        var last = rows.LastOrDefault(row => row.Kind == AndroidSamRowKind.Message);
        return last is not null && state.SafeEntries[last.EntryIndex] is
            { Kind: NativeSamEntryKind.Learner, IsPending: true };
    }

    internal static string MessageMeta(NativeSamEntry entry, string persona, Func<string, string> text)
    {
        var who = entry.Kind == NativeSamEntryKind.Learner ? text("Coach_RoleYou") : persona;
        var time = entry.CreatedAtUtc == default ? string.Empty
            : DateTime.SpecifyKind(entry.CreatedAtUtc, DateTimeKind.Utc).ToLocalTime()
                .ToString(text("Coach_MessageTimeFormat"), CultureInfo.CurrentCulture);
        return entry.IsPending ? $"{who} · {text("Coach_MessagePending")}"
            : entry.IsFailed ? $"{who} · {text("Coach_MessageFailed")}"
            : time.Length == 0 ? who : $"{who} · {time}";
    }

    /// <summary>A block label: custom label, nothing for a plain answer, else the typed heading.</summary>
    internal static string? BlockLabel(NativeSamAnswerBlock block, Func<string, string> text) =>
        block.Label is { Length: > 0 } custom ? custom
        : block.Kind == CoachAnswerBlockKind.Answer ? null
        : text("Coach_AnswerBlock_" + block.Kind);

    internal static string SuggestionPreview(PendingCoachSuggestionDto suggestion, Func<string, string> text)
    {
        var diff = suggestion.Preview;
        var changes = new List<string>();
        if (diff.AddedItemCount > 0) changes.Add($"{text("Coach_ChangeAdded")}: {diff.AddedItemCount}");
        if (diff.RemovedItemCount > 0) changes.Add($"{text("Coach_ChangeRemoved")}: {diff.RemovedItemCount}");
        if (diff.AdjustedItemCount > 0) changes.Add($"{text("Coach_ChangeAdjusted")}: {diff.AdjustedItemCount}");
        var kept = diff.PreservedCompletedItemCount + diff.PreservedInProgressItemCount;
        if (kept > 0) changes.Add($"{text("Coach_ChangeKept")}: {kept}");
        changes.Add(string.Format(CultureInfo.CurrentCulture, text("Coach_PlanTotalMinutes"),
            diff.EstimatedMinutesAfter));
        return string.Join(" · ", changes);
    }

    internal static string ReceiptSummary(CoachChangeReceiptDto receipt, Func<string, string> text) =>
        receipt.Revision.Source == CoachRevisionSource.Undo ? text("Coach_ReceiptUndone")
            : string.Format(CultureInfo.CurrentCulture, text("Coach_ReceiptUpdated"),
                receipt.ReplacedItemCount, receipt.PreservedCompletedItemCount,
                receipt.PreservedMinutesSpent);

    /// <summary>Undo is offered only on the latest, not-yet-undone revision.</summary>
    internal static bool ReceiptCanUndo(NativeSamState state, CoachChangeReceiptDto receipt) =>
        receipt.Revision.Source != CoachRevisionSource.Undo && state.CanUndo
        && state.UndoRevisionId == receipt.Revision.RevisionId;

    internal static string ConversationDetail(NativeSamConversationRow row, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture, text("Coach_ConversationUpdated"),
            DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc).ToLocalTime()
                .ToString(text("Coach_ConversationDateFormat"), CultureInfo.CurrentCulture))
        + " · " + string.Format(CultureInfo.CurrentCulture, text("Coach_ConversationMessageCount"),
            row.MessageCount)
        + (row.IsClosed ? " · " + text("Coach_ConversationClosedBadge") : string.Empty);

    internal static string EvidenceValue(CoachEvidenceValueDto value, Func<string, string> text) =>
        value.Unit switch
        {
            CoachEvidenceUnit.Minutes => string.Format(CultureInfo.CurrentCulture,
                text("Coach_PracticeMinutes"), Math.Round(value.Value)),
            CoachEvidenceUnit.Percent => (value.Value / 100).ToString("P0", CultureInfo.CurrentCulture),
            _ => Math.Round(value.Value, 1).ToString(CultureInfo.CurrentCulture),
        };
}

internal enum AndroidSamFollow { None, Jump, Animate }
