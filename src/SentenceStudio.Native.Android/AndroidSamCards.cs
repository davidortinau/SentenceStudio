using System.Collections.Immutable;
using System.Globalization;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal enum AndroidSamWriteAction { Accept, Review, Confirm, CancelConfirm, Decline, Undo, Refresh }

internal enum AndroidSamButtonStyle { Primary, Secondary, Danger }

internal sealed record AndroidSamWriteButton(AndroidSamWriteAction Action, string LabelKey,
    AndroidSamButtonStyle Style, string Suffix);

internal sealed record AndroidSamWriteNote(string Key, bool Danger = false);

/// <summary>
/// Everything one proposed-change card shows, computed only from the server's
/// state (source SamWriteCard via <see cref="NativeSamWritePresentation"/>). The
/// one-use confirmation value is never part of <see cref="NativeSamState"/>, so it
/// cannot reach this card, the screen or a log.
/// </summary>
internal sealed record AndroidSamWriteCard(
    string OperationId,
    NativeSamWriteStage Stage,
    string HeadingKey,
    string StateKey,
    string? Summary,
    IReadOnlyList<string> Lines,
    ImmutableArray<AndroidSamWriteNote> Notes,
    bool ShowsConfirmStep,
    bool ConfirmIrreversible,
    DateTime? ConfirmExpiresAtUtc,
    DateTime? UndoClosesAtUtc,
    string? ErrorKey,
    ImmutableArray<AndroidSamWriteButton> Buttons,
    bool ButtonsEnabled);

/// <summary>
/// Platform-neutral rules for the Sam cards the Compose head renders, matching the
/// iOS SamView/SamSettingsView and the source Shared/Sam + Shared/Coach components
/// (frozen reference 59c72262). Holds no Android UI types so the host tests cover it.
/// </summary>
internal static class AndroidSamCards
{
    /// <summary>
    /// Destructive button pair, used in both themes. The brand danger red (#E13B30, kept for text
    /// and outlines) is only 4.31:1 against white; #B3261E with white is 6.54:1 (WCAG AA 4.5:1).
    /// </summary>
    internal const string DangerContainerHex = "#B3261E";
    internal const string OnDangerHex = "#FFFFFF";

    /// <summary>A copy result applies only to the owner that started the copy and the live presenter.</summary>
    internal static bool ShouldApplyCopyResult(string? startOwner, string? currentOwner) =>
        !string.IsNullOrWhiteSpace(startOwner)
        && string.Equals(startOwner, currentOwner, StringComparison.Ordinal);

    /// <summary>Source CoachRepairDisclosureNotice: one closed state in, one sentence out.</summary>
    internal static string? RepairKey(NativeSamEntry entry) =>
        entry.Kind == NativeSamEntryKind.Learner || entry.Limitation is not null ? null
        : entry.Repair switch
        {
            null or CoachRepairDisclosure.None => null,
            CoachRepairDisclosure.AnswerAltered => "Coach_Repair_AnswerAltered",
            CoachRepairDisclosure.RepairSuppressedForLanguage => entry.RepairEvidenceOnScreen
                ? "Coach_Repair_SuppressedForLanguage" : "Coach_Repair_SuppressedForLanguage_NoEvidence",
            _ => entry.RepairEvidenceOnScreen ? "Coach_Repair_Unknown" : "Coach_Repair_Unknown_NoEvidence",
        };

    /// <summary>Copy and the report flag belong to a settled thing Sam said.</summary>
    internal static bool ShowsMessageActions(NativeSamEntry entry) =>
        entry.IsSettled && entry.Kind is NativeSamEntryKind.Sam or NativeSamEntryKind.Notice;

    internal static bool IsReportable(NativeSamState state, NativeSamEntry entry) =>
        state.ReportingAvailable && entry.IsReportable;

    internal static string CopyLabelKey(NativeSamState state, string messageId) =>
        state.CopiedMessageId == messageId ? "Coach_Copied"
        : state.CopyFailedMessageId == messageId ? "Coach_CopyFailed" : "Coach_Copy";

    /// <summary>Source CoachReportControl: five closed reasons, no free text.</summary>
    internal static readonly ImmutableArray<(CoachResponseReportReason Reason, string Key, string Id)> ReportReasons =
    [
        (CoachResponseReportReason.DidNotAnswer, "Coach_ReportReasonDidNotAnswer", "didnotanswer"),
        (CoachResponseReportReason.IncorrectOrMisleading, "Coach_ReportReasonIncorrect", "incorrect"),
        (CoachResponseReportReason.ExpectedAppAction, "Coach_ReportReasonExpectedAction", "expectedaction"),
        (CoachResponseReportReason.Confusing, "Coach_ReportReasonConfusing", "confusing"),
        (CoachResponseReportReason.Other, "Coach_ReportReasonOther", "other"),
    ];

    /// <summary>A proposed change renders inside the exchange that produced it, never as a dialog.</summary>
    internal static AndroidSamWriteCard? Write(NativeSamState state, NativeSamEntry entry, DateTime utcNow)
    {
        if (entry.Write is not { } write || !state.WriteSurfaceEnabled)
            return null;
        var id = write.OperationId;
        var unavailable = state.IsWriteUnavailable(id);
        var stage = NativeSamWritePresentation.Stage(write, state.ActiveWriteId == id,
            state.ConfirmingWriteId == id, utcNow, unavailable);
        var busy = state.WriteBusyId == id;
        var irreversible = NativeSamWritePresentation.ShowsIrreversibleWarning(write, stage);
        var notes = ImmutableArray.CreateBuilder<AndroidSamWriteNote>();
        DateTime? undoCloses = null;
        switch (stage)
        {
            case NativeSamWriteStage.Proposed:
                notes.Add(new("Coach_WriteNothingChangedYet"));
                if (irreversible)
                    notes.Add(new("Coach_WriteIrreversibleNotice", Danger: true));
                else if (NativeSamWritePresentation.ShowsReversibleNote(write, stage))
                    notes.Add(new("Coach_WriteReversibleNotice"));
                if (write.RequiresConfirmation)
                    notes.Add(new("Coach_WriteProtectedNotice"));
                break;
            case NativeSamWriteStage.ConfirmationRequired:
                break;
            case NativeSamWriteStage.Applied:
                if (write.Receipt is { UndoExpiresAtUtc: { } closes }
                    && NativeSamWritePresentation.ShowsUndo(write, stage, utcNow, unavailable))
                    undoCloses = closes;
                break;
            default:
                if (NativeSamWritePresentation.HintKey(stage) is { } hint)
                    notes.Add(new(hint));
                break;
        }
        var buttons = ImmutableArray.CreateBuilder<AndroidSamWriteButton>();
        string Busy(string key) => busy ? "Coach_WriteBusy" : key;
        if (NativeSamWritePresentation.ShowsAccept(write, stage))
            buttons.Add(new(AndroidSamWriteAction.Accept, Busy("Coach_WriteAccept"),
                AndroidSamButtonStyle.Primary, "accept"));
        if (NativeSamWritePresentation.ShowsReview(write, stage))
            buttons.Add(new(AndroidSamWriteAction.Review, Busy("Coach_WriteReview"),
                AndroidSamButtonStyle.Primary, "review"));
        if (stage == NativeSamWriteStage.ConfirmationRequired)
        {
            buttons.Add(new(AndroidSamWriteAction.Confirm, Busy("Coach_WriteConfirm"),
                AndroidSamButtonStyle.Danger, "confirm"));
            buttons.Add(new(AndroidSamWriteAction.CancelConfirm, "Coach_WriteCancelConfirm",
                AndroidSamButtonStyle.Secondary, "cancelconfirm"));
        }
        if (NativeSamWritePresentation.ShowsDecline(stage) && stage != NativeSamWriteStage.ConfirmationRequired)
            buttons.Add(new(AndroidSamWriteAction.Decline, "Coach_WriteDecline",
                AndroidSamButtonStyle.Secondary, "decline"));
        if (NativeSamWritePresentation.ShowsUndo(write, stage, utcNow, unavailable))
            buttons.Add(new(AndroidSamWriteAction.Undo, Busy("Coach_WriteUndo"),
                AndroidSamButtonStyle.Secondary, "undo"));
        if (NativeSamWritePresentation.ShowsRefresh(stage))
            buttons.Add(new(AndroidSamWriteAction.Refresh, "Coach_WriteRefreshAction",
                AndroidSamButtonStyle.Secondary, "refresh"));
        var confirming = stage == NativeSamWriteStage.ConfirmationRequired;
        return new AndroidSamWriteCard(id, stage,
            NativeSamWritePresentation.HeadingKey(write.ChangeKind),
            NativeSamWritePresentation.StateKey(stage),
            NativeSamWritePresentation.Summary(write) is { Length: > 0 } summary ? summary : null,
            NativeSamWritePresentation.Lines(write),
            notes.ToImmutable(),
            ShowsConfirmStep: confirming,
            ConfirmIrreversible: confirming && irreversible,
            ConfirmExpiresAtUtc: confirming ? state.ConfirmationExpiresAtUtc : null,
            UndoClosesAtUtc: undoCloses,
            ErrorKey: state.WriteErrorId == id ? state.WriteErrorKey : null,
            buttons.ToImmutable(),
            ButtonsEnabled: !busy && state.WriteBusyId is null && !state.Busy);
    }

    internal static string LocalTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    internal static string LocalDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    internal static string MemoryScope(CoachMemoryFactDto fact, Func<string, string> text) =>
        fact.Scope == CoachMemoryScope.Global ? text("Coach_MemoryScopeGlobal")
        : string.IsNullOrWhiteSpace(fact.TargetLanguageCode) ? text("Coach_MemoryScopeLanguage")
        : string.Format(CultureInfo.CurrentCulture, text("Coach_MemoryScopeLanguageNamed"), fact.TargetLanguageCode);

    /// <summary>Scope, provenance, saved and last-used line (source CoachMemoryPanel row).</summary>
    internal static string MemoryMeta(CoachMemoryFactDto fact, Func<string, string> text) =>
        $"{text("Coach_MemoryScopeLabel")}: {MemoryScope(fact, text)} · "
        + $"{text("Coach_MemoryProvenanceLabel")}: "
        + text(fact.Provenance == CoachMemoryProvenance.UserConfirmed
            ? "Coach_MemoryProvenanceConfirmed" : "Coach_MemoryProvenanceStated")
        + $" · {text("Coach_MemorySavedAtLabel")}: {LocalDate(fact.ConfirmedAtUtc ?? fact.CreatedAtUtc)}"
        + (fact.LastUsedAtUtc is { } used ? $" · {text("Coach_MemoryLastUsedLabel")}: {LocalDate(used)}" : "");

    /// <summary>Only a study goal is editable (source CoachMemoryCandidateCard / CoachMemoryPanel).</summary>
    internal static bool IsEditable(CoachMemoryFactDto fact) => fact.Kind == CoachMemoryKind.PersistentStudyGoal;

    internal static bool CanSaveEdit(string text, bool busy) => !busy && !string.IsNullOrWhiteSpace(text);

    /// <summary>Server title cap (CoachHistoryLimits, title max 120).</summary>
    internal const int MaxConversationTitleLength = 120;

    internal static bool CanRename(string text, bool busy) =>
        !busy && !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= MaxConversationTitleLength;

    internal static string? DisputeSignalKey(CoachDisputeDto dispute) => dispute.Signal switch
    {
        CoachDisputeSignal.MeantSomethingElse => "Coach_Dispute_Signal_MeantSomethingElse",
        CoachDisputeSignal.NotWhatIAsked => "Coach_Dispute_Signal_NotWhatIAsked",
        CoachDisputeSignal.WrongClaim => "Coach_Dispute_Signal_WrongClaim",
        CoachDisputeSignal.DifferentCohort => "Coach_Dispute_Signal_DifferentCohort",
        _ => null,
    };

    /// <summary>The text field a Sam edit seeds from, keyed so typing is never overwritten.</summary>
    internal static (string? Key, string Text) RenameSeed(NativeSamState state) =>
        state.ConversationDialog is { Kind: NativeSamConversationDialogKind.Rename } dialog
            ? ("rename:" + dialog.ConversationId, dialog.RenameText) : (null, string.Empty);

    internal static (string? Key, string Text) CandidateSeed(NativeSamState state) =>
        state.MemoryCandidate is { } fact && state.MemoryCandidateEdit is { } edit
            ? ("candidate:" + fact.Id, edit) : (null, string.Empty);

    internal static (string? Key, string Text) MemorySeed(NativeSamState state) =>
        state.SafeMemory.EditingId is { } id ? ("memory:" + id, state.SafeMemory.EditText) : (null, string.Empty);
}
