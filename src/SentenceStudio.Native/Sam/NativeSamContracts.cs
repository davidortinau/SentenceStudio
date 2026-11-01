using System.Collections.Immutable;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native;

/// <summary>
/// The single owner-bound route to the coach API. Every call names the owner it
/// is made for; the adapter refuses the call (and any late result) when that
/// owner is not the current authenticated device owner. There is no ambient or
/// "current user" call: an empty owner never reaches the network.
/// </summary>
public interface INativeSamClient
{
    /// <summary>False when the head has no API endpoint; Sam stays hidden.</summary>
    bool IsConfigured { get; }

    Task<T> CallAsync<T>(
        string owner,
        Func<ICoachApiClient, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableNativeSamClient : INativeSamClient
{
    public bool IsConfigured => false;

    public Task<T> CallAsync<T>(string owner,
        Func<ICoachApiClient, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Sam has no configured API endpoint.");
}

/// <summary>Sam panel size. Source: SamOverlayVisualState Compact / Expanded.</summary>
public enum NativeSamPanelSize { Compact, Expanded }

/// <summary>
/// Learner-visible status. Mirrors the source CoachUiState members that the
/// native panel renders, plus <see cref="SyncRequired"/> for the native
/// pre-turn synchronization preflight (coach-product-policy-v1 section 10).
/// </summary>
public enum NativeSamStatus
{
    Closed = 0,
    Opening,
    Resuming,
    Ready,
    Running,
    Applying,
    Undoing,
    Clarification,
    ClarificationLimitReached,
    PlanChangedElsewhere,
    Incomplete,
    Offline,
    Limited,
    InputTooLong,
    Failed,
    Expired,
    Unavailable,
    SyncRequired,
}

public enum NativeSamEntryKind { Learner, Sam, Notice, Receipt, Unreadable, Unsupported }

public sealed record NativeSamAnswerBlock(
    CoachAnswerBlockKind Kind, string? Label, string Text, string? LanguageTag);

public sealed record NativeSamEntry(
    string Id,
    NativeSamEntryKind Kind,
    string Text,
    DateTime CreatedAtUtc,
    long Sequence,
    ImmutableArray<NativeSamAnswerBlock> Blocks,
    ImmutableArray<CoachEvidenceDto> Evidence,
    CoachLimitationDto? Limitation,
    ImmutableArray<string> ChangeLines,
    bool IsPending = false,
    bool IsFailed = false,
    CoachMessageKind MessageKind = CoachMessageKind.Text,
    CoachWriteOperationDto? Write = null,
    CoachRepairDisclosure? Repair = null,
    bool RepairEvidenceOnScreen = false)
{
    public bool HasEvidence => !Evidence.IsDefaultOrEmpty;

    /// <summary>A settled server message (not an optimistic or failed local echo).</summary>
    public bool IsSettled => !IsPending && !IsFailed && !Id.StartsWith("local:", StringComparison.Ordinal);

    /// <summary>
    /// Source CoachChatPane.IsReportable: a durable, settled thing Sam said, of a
    /// reportable kind (receipts and no-change markers are bookkeeping, not answers).
    /// </summary>
    public bool IsReportable =>
        IsSettled && Kind is NativeSamEntryKind.Sam or NativeSamEntryKind.Notice
        && CoachResponseReportability.IsReportableKind(MessageKind);

    /// <summary>Source CoachTimelineEntry.ReadableText: what Copy puts on the clipboard.</summary>
    public string ReadableText => Blocks.IsDefaultOrEmpty
        ? Text
        : string.Join("\n\n", Blocks.Select(block => block.Label is { Length: > 0 } label
            ? label + "\n" + block.Text : block.Text));
}

/// <summary>A conversation-shelf action that needs a confirmation step (source CoachConversationList dialogs).</summary>
public enum NativeSamConversationDialogKind { Rename, Close, Delete }

public sealed record NativeSamConversationDialog(NativeSamConversationDialogKind Kind, string ConversationId,
    string Title, string RenameText = "");

/// <summary>Source CoachMemoryAvailability: the panel disappears unless the route group answers.</summary>
public enum NativeSamMemoryAvailability { Unknown, Available, Unavailable }

public enum NativeSamMemoryTab { Active, Candidates }

/// <summary>Settings "What Sam remembers" (source CoachMemoryPanel + CoachMemoryDirectory).</summary>
public sealed record NativeSamMemoryState(
    NativeSamMemoryAvailability Availability = NativeSamMemoryAvailability.Unknown,
    ImmutableArray<CoachMemoryFactDto> Active = default,
    ImmutableArray<CoachMemoryFactDto> Candidates = default,
    NativeSamMemoryTab Tab = NativeSamMemoryTab.Active,
    bool Busy = false,
    string? NoticeKey = null,
    string? EditingId = null,
    string EditText = "",
    bool ConfirmingForgetAll = false)
{
    public ImmutableArray<CoachMemoryFactDto> SafeActive => Active.IsDefault ? [] : Active;
    public ImmutableArray<CoachMemoryFactDto> SafeCandidates => Candidates.IsDefault ? [] : Candidates;
    public bool IsPaused => Availability == NativeSamMemoryAvailability.Available && SafeActive.IsEmpty;
}

/// <summary>Settings "Learning Coach" section (source Settings.razor coach card).</summary>
public sealed record NativeSamSettingsState(
    bool IsAvailable = false,
    bool ConfirmingDelete = false,
    bool Deleting = false,
    string? NoticeKey = null);

public sealed record NativeSamConversationRow(
    string Id, string Title, bool TitleUnreadable, DateTime UpdatedAtUtc,
    long MessageCount, bool IsClosed, bool IsSelected, long StateVersion = 0);

public sealed record NativeSamState(
    bool IsVisible = false,
    bool IsOpen = false,
    NativeSamPanelSize Size = NativeSamPanelSize.Compact,
    NativeSamStatus Status = NativeSamStatus.Closed,
    string PersonaCulture = "en",
    string? ConversationId = null,
    bool ConversationClosed = false,
    ImmutableArray<NativeSamEntry> Entries = default,
    bool TranscriptLoading = false,
    int UnreadableCount = 0,
    ImmutableArray<NativeSamConversationRow> Conversations = default,
    bool ConversationsOpen = false,
    bool ConversationsLoading = false,
    string? ConversationsErrorKey = null,
    string Draft = "",
    bool CanEditPlan = false,
    bool HasLearnerSpoken = false,
    PendingCoachSuggestionDto? Suggestion = null,
    string? ClarifyingQuestion = null,
    CoachChangeReceiptDto? LatestReceipt = null,
    bool CanUndo = false,
    string? UndoRevisionId = null,
    CoachVocabularySetProposal? VocabularySet = null,
    bool VocabularySetBusy = false,
    string? VocabularySetMessageKey = null,
    string? ApprovedResourceId = null,
    ImmutableHashSet<string>? ExpandedEvidence = null,
    string? NoticeKey = null,
    string? FailureKey = null,
    bool StillWorking = false,
    bool CanStop = false,
    bool CanRetry = false,
    bool CanRecover = false,
    bool Busy = false,
    bool IsDurableHistoryAvailable = false,
    bool SamWriteAvailable = false,
    string? ActiveWriteId = null,
    string? ConfirmingWriteId = null,
    DateTime? ConfirmationExpiresAtUtc = null,
    string? WriteBusyId = null,
    string? WriteErrorId = null,
    string? WriteErrorKey = null,
    ImmutableHashSet<string>? UnavailableWrites = null,
    CoachMemoryFactDto? MemoryCandidate = null,
    string? MemoryCandidateEdit = null,
    bool MemoryCandidateBusy = false,
    string? MemoryCandidateNoticeKey = null,
    CoachDisputeDto? Dispute = null,
    bool ReportingAvailable = false,
    ImmutableHashSet<string>? Reported = null,
    string? ReportingMessageId = null,
    CoachResponseReportReason? ReportReason = null,
    bool ReportBusy = false,
    string? ReportFailedId = null,
    string? CopiedMessageId = null,
    string? CopyFailedMessageId = null,
    NativeSamConversationDialog? ConversationDialog = null,
    bool ConversationActionBusy = false,
    string? ConversationActionKey = null,
    NativeSamMemoryState? Memory = null,
    NativeSamSettingsState? Settings = null,
    long Version = 0)
{
    /// <summary>Source IsWriteSurfaceEnabled: write tools on, durable history on, a thread open.</summary>
    public bool WriteSurfaceEnabled => SamWriteAvailable && IsDurableHistoryAvailable && ConversationId is not null;

    public NativeSamMemoryState SafeMemory => Memory ?? new NativeSamMemoryState();

    public NativeSamSettingsState SafeSettings => Settings ?? new NativeSamSettingsState();

    public bool IsWriteUnavailable(string? operationId) =>
        operationId is not null && UnavailableWrites?.Contains(operationId) == true;

    public bool IsReported(string? messageId) =>
        messageId is { Length: > 0 } && Reported?.Contains(messageId) == true;

    public ImmutableArray<NativeSamEntry> SafeEntries =>
        Entries.IsDefault ? [] : Entries;

    public ImmutableArray<NativeSamConversationRow> SafeConversations =>
        Conversations.IsDefault ? [] : Conversations;

    public bool IsEvidenceExpanded(string entryId) =>
        ExpandedEvidence?.Contains(entryId) == true;

    public int DraftLength => Draft.Length;

    public bool DraftTooLong => Draft.Length > CoachConstraintLimits.MaxTurnTextLength;

    /// <summary>Send is offered only for a non-empty, in-limit draft in a usable open thread.</summary>
    public bool CanSend =>
        IsOpen && !Busy && !ConversationClosed
        && !string.IsNullOrWhiteSpace(Draft) && !DraftTooLong
        && Status is not (NativeSamStatus.Limited or NativeSamStatus.Unavailable
            or NativeSamStatus.Opening or NativeSamStatus.Resuming);

    /// <summary>Source CoachChatPane ShowChips: empty conversation and a plan to edit.</summary>
    public bool ShowChips =>
        IsOpen && CanEditPlan && !HasLearnerSpoken && !ConversationClosed
        && ConversationId is not null && Suggestion is null
        && Status is NativeSamStatus.Ready;
}
