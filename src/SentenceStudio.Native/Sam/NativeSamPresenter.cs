using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native;

/// <summary>
/// One Sam experience for the native heads (decision 2026-09-26: Sam is one
/// system with Coach internals). Platform-neutral: it holds no UI types, so the
/// iOS SwiftUI head renders it now and the Android Compose head can bind the
/// same state later.
/// </summary>
/// <remarks>
/// Owner scoping fails closed: every command re-reads the active owner, a
/// missing owner hides Sam and makes no call, and an owner change drops every
/// conversation, message, proposal and receipt held for the previous owner
/// before anything else happens. Sam is online-only: offline is an honest state
/// and nothing is queued.
/// Source: SentenceStudio.UI CoachWorkspaceState(.Durable), SamOverlayHost,
/// CoachStateMachine, CoachPersona (frozen reference 59c72262).
/// </remarks>
public sealed partial class NativeSamPresenter(
    INativeSamClient client,
    DeviceUserScopeProvider scope,
    IPreferencesService preferences,
    IConnectivityService connectivity,
    INativeSyncReadiness sync,
    NativePendingWrites pendingWrites,
    UserProfileRepository profiles,
    ILogger<NativeSamPresenter> logger)
{
    internal const int MessagePageSize = 50;
    internal const int ConversationPageSize = 20;
    internal static TimeSpan PollInterval = TimeSpan.FromMilliseconds(1200);
    internal static TimeSpan PollTimeout = TimeSpan.FromSeconds(75);
    internal static TimeSpan StillWorkingAfter = TimeSpan.FromSeconds(8);
    internal static TimeSpan StopAfter = TimeSpan.FromSeconds(12);

    private NativeSamState _state = new();
    private string? _owner;
    private CoachAvailabilityResponse? _availability;
    private string? _sessionId;
    private string? _planVersion;
    private CoachConstraintSetDto? _constraints;
    private readonly Dictionary<string, ImmutableArray<CoachEvidenceDto>> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoachLimitationDto> _limitations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CoachRepairDisclosure Disclosure, bool EvidenceOnScreen)> _repairs =
        new(StringComparer.Ordinal);
    private ImmutableArray<NativeSamEntry> _localEntries = [];
    private CoachTurnRequest? _lastRequest;
    private string? _pendingOperationId;
    private string? _pendingIdempotencyKey;
    private CancellationTokenSource? _run;
    private long _version;

    public NativeSamState State => _state;

    public event Action? Changed;

    /// <summary>Raised after Sam applied or undid a Today's Plan revision on the server.</summary>
    public event Action? PlanChanged;

    /// <summary>Raised when the learner asks to start Vocab Review on an approved set.</summary>
    public event Action<string, string>? VocabularyReviewRequested;

    /// <summary>
    /// Reads availability for the current owner. Only <c>IsSamOverlayAvailable</c>
    /// shows the entry (source CoachSurfaceGate). A failed read hides Sam, except
    /// that an owner already known to have Sam keeps the entry while offline so
    /// the panel can say so honestly.
    /// </summary>
    public async Task RefreshAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || !client.IsConfigured)
        {
            Reset(null);
            return;
        }
        if (!connectivity.IsInternetAvailable)
        {
            if (_availability is null)
                Publish(_state with { IsVisible = false, Availability = NativeSamAvailability.Offline });
            else if (_state.IsOpen)
                Publish(_state with { Status = NativeSamStatus.Offline });
            return;
        }

        try
        {
            var availability = await client.CallAsync(owner,
                (api, token) => api.GetAvailabilityAsync(token), cancellationToken);
            if (!IsCurrent(owner))
                return;
            _availability = availability;
            var persona = await ResolvePersonaCultureAsync(owner, cancellationToken);
            if (!IsCurrent(owner))
                return;
            var visible = availability.IsSamOverlayAvailable;
            Publish(_state with
            {
                IsVisible = visible,
                Availability = visible ? NativeSamAvailability.Offered : NativeSamAvailability.NotOffered,
                IsDurableHistoryAvailable = availability.IsDurableHistoryAvailable,
                SamWriteAvailable = availability.IsSamWriteAvailable,
                Settings = _state.SafeSettings with { IsAvailable = availability.IsAvailable },
                IsOpen = visible && _state.IsOpen,
                PersonaCulture = persona,
                CanEditPlan = availability.CanEditPlan,
                Status = !visible ? NativeSamStatus.Closed
                    : availability.State == CoachAvailabilityState.LimitReached
                        && _state.IsOpen ? NativeSamStatus.Limited : _state.Status,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or CoachApiException
            or UnauthorizedAccessException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Sam availability could not be read; Sam stays hidden.");
            if (!IsCurrent(owner))
                return;
            _availability = null;
            Publish(_state with
            {
                IsVisible = false, IsOpen = false, Status = NativeSamStatus.Closed,
                Availability = NativeSamAvailability.Unreachable,
                SamWriteAvailable = false,
                Settings = _state.SafeSettings with { IsAvailable = false },
            });
        }
    }

    /// <summary>Opens the panel on the most recent open conversation, or a new one (source J1/J2).</summary>
    public async Task OpenAsync(NativeSamPanelSize size, CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || !_state.IsVisible)
            return;
        Publish(_state with { IsOpen = true, Size = size, NoticeKey = null });
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { Status = NativeSamStatus.Offline });
            return;
        }
        if (_availability?.State == CoachAvailabilityState.LimitReached)
        {
            Publish(_state with { Status = NativeSamStatus.Limited });
            if (_state.ConversationId is null)
                await ResumeMostRecentAsync(owner, cancellationToken, keepStatus: true);
            return;
        }
        if (_state.ConversationId is { } current)
        {
            await LoadConversationAsync(owner, current, cancellationToken);
            return;
        }
        await ResumeMostRecentAsync(owner, cancellationToken);
    }

    /// <summary>Sign-out or account switch: drop everything held for the previous owner now.</summary>
    public void ClearForAccountBoundary() => Reset(null);

    public void Close()
    {
        if (!_state.IsOpen)
            return;
        // Closing never ends or deletes the conversation (source J3 step 4).
        Publish(_state with { IsOpen = false, ConversationsOpen = false });
    }

    public void SetSize(NativeSamPanelSize size)
    {
        if (_state.IsOpen && _state.Size != size)
            Publish(_state with { Size = size });
    }

    public void SetDraft(string text)
    {
        text ??= string.Empty;
        if (!string.Equals(_state.Draft, text, StringComparison.Ordinal))
            Publish(_state with { Draft = text }, structural: false);
    }

    public void ToggleEvidence(string entryId)
    {
        var expanded = _state.ExpandedEvidence ?? ImmutableHashSet<string>.Empty;
        Publish(_state with
        {
            ExpandedEvidence = expanded.Contains(entryId)
                ? expanded.Remove(entryId) : expanded.Add(entryId)
        });
    }

    public void DismissNotice()
    {
        if (_state.Status is NativeSamStatus.Failed or NativeSamStatus.Incomplete
            or NativeSamStatus.PlanChangedElsewhere or NativeSamStatus.Offline
            or NativeSamStatus.SyncRequired or NativeSamStatus.InputTooLong
            or NativeSamStatus.ClarificationLimitReached)
            Publish(_state with
            {
                Status = NativeSamStatus.Ready,
                CanRetry = false, NoticeKey = null
            });
    }

    // ---------------------------------------------------------------- conversations

    public async Task ToggleConversationsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || !_state.IsOpen)
            return;
        if (_state.ConversationsOpen)
        {
            Publish(_state with { ConversationsOpen = false });
            return;
        }
        Publish(_state with { ConversationsOpen = true });
        await LoadConversationsAsync(owner, cancellationToken);
    }

    public async Task NewConversationAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || !_state.IsOpen || _state.Busy)
            return;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { Status = NativeSamStatus.Offline });
            return;
        }
        await CreateConversationAsync(owner, cancellationToken);
    }

    public async Task OpenConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || !_state.IsOpen || _state.Busy
            || string.IsNullOrWhiteSpace(conversationId))
            return;
        if (_state.SafeConversations.All(row => row.Id != conversationId))
            return; // Only a row this owner was shown can be opened.
        Publish(_state with { ConversationsOpen = false });
        await LoadConversationAsync(owner, conversationId, cancellationToken);
    }

    private async Task LoadConversationsAsync(string owner, CancellationToken cancellationToken)
    {
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { ConversationsErrorKey = "Coach_ConversationsOffline" });
            return;
        }
        Publish(_state with { ConversationsLoading = true, ConversationsErrorKey = null });
        try
        {
            var page = await client.CallAsync(owner,
                (api, token) => api.ListConversationsAsync(ConversationPageSize, null, token),
                cancellationToken);
            if (!IsCurrent(owner))
                return;
            Publish(_state with
            {
                ConversationsLoading = false,
                Conversations = Rows(page?.Items ?? []),
                ConversationsErrorKey = page is null ? "Coach_ConversationsLoadFailed" : null
            });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam conversation list could not be read.");
            if (IsCurrent(owner))
                Publish(_state with
                {
                    ConversationsLoading = false,
                    ConversationsErrorKey = ex is HttpRequestException
                        ? "Coach_ConversationsOffline" : "Coach_ConversationsLoadFailed"
                });
        }
    }

    private ImmutableArray<NativeSamConversationRow> Rows(IEnumerable<CoachConversationDto> items) =>
        items.Select(item => new NativeSamConversationRow(
                item.ConversationId,
                item.Title,
                item.TitleOrigin == CoachConversationTitleOrigin.Unreadable
                    || string.IsNullOrWhiteSpace(item.Title),
                item.UpdatedAtUtc, item.MessageCount, item.IsClosed,
                item.ConversationId == _state.ConversationId, item.StateVersion))
            .ToImmutableArray();

    private async Task ResumeMostRecentAsync(string owner, CancellationToken cancellationToken,
        bool keepStatus = false)
    {
        if (!keepStatus)
            Publish(_state with { Status = NativeSamStatus.Opening });
        try
        {
            var page = await client.CallAsync(owner,
                (api, token) => api.ListConversationsAsync(ConversationPageSize, null, token),
                cancellationToken);
            if (!IsCurrent(owner))
                return;
            if (page is null)
            {
                // Durable history off: the route answers 404 (source SAM-CONV-08).
                Publish(_state with { Status = NativeSamStatus.Unavailable });
                return;
            }
            Publish(_state with { Conversations = Rows(page.Items) });
            var recent = page.Items
                .Where(item => !item.IsClosed)
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault();
            if (recent is not null)
                await LoadConversationAsync(owner, recent.ConversationId, cancellationToken, keepStatus);
            else if (!keepStatus)
                await CreateConversationAsync(owner, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam could not resume a conversation.");
            if (IsCurrent(owner))
                Publish(_state with { Status = StatusFor(ex) });
        }
    }

    private async Task CreateConversationAsync(string owner, CancellationToken cancellationToken)
    {
        Publish(_state with { Status = NativeSamStatus.Opening, ConversationsOpen = false });
        try
        {
            var key = NewHandle();
            var conversation = await client.CallAsync(owner,
                (api, token) => api.CreateConversationAsync(
                    new StartCoachConversationRequest { IdempotencyKey = key }, token),
                cancellationToken);
            if (!IsCurrent(owner))
                return;
            AdoptConversation(conversation.ConversationId, conversation.IsClosed);
            await LoadSessionAsync(owner, cancellationToken);
            if (!IsCurrent(owner))
                return;
            // A new thread offers the report flag too (feature probe, empty list).
            await LoadReportedResponsesAsync(owner, conversation.ConversationId, cancellationToken);
            if (!IsCurrent(owner))
                return;
            Publish(_state with
            {
                Status = NativeSamStatus.Ready,
                Conversations = Rows(new[] { conversation }.Concat(
                    _state.SafeConversations
                        .Where(row => row.Id != conversation.ConversationId)
                        .Select(row => new CoachConversationDto
                        {
                            ConversationId = row.Id, Title = row.Title,
                            TitleOrigin = row.TitleUnreadable
                                ? CoachConversationTitleOrigin.Unreadable
                                : CoachConversationTitleOrigin.Generated,
                            CreatedAtUtc = row.UpdatedAtUtc, UpdatedAtUtc = row.UpdatedAtUtc,
                            HistoryStartsAtUtc = row.UpdatedAtUtc,
                            MessageCount = row.MessageCount, StateVersion = row.StateVersion,
                            HasActiveCheckpoint = false, IsClosed = row.IsClosed
                        })))
            });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam conversation could not be created.");
            if (IsCurrent(owner))
                Publish(_state with
                {
                    Status = StatusFor(ex),
                    NoticeKey = ex is HttpRequestException ? null : "Coach_ConversationCreateFailed"
                });
        }
    }

    private void AdoptConversation(string conversationId, bool closed)
    {
        var switching = _state.ConversationId != conversationId;
        if (switching)
        {
            _evidence.Clear();
            _limitations.Clear();
            _repairs.Clear();
            DiscardConfirmation();
            _localEntries = [];
            _lastRequest = null;
            _pendingOperationId = null;
            _pendingIdempotencyKey = null;
        }
        Publish(_state with
        {
            ConversationId = conversationId,
            ConversationClosed = closed,
            // A receipt belongs to the thread it was issued in; Undo stays reachable
            // through the session checkpoint's last revision.
            LatestReceipt = switching ? null : _state.LatestReceipt,
            Entries = [],
            UnreadableCount = 0,
            HasLearnerSpoken = false,
            VocabularySet = null,
            VocabularySetMessageKey = null,
            ApprovedResourceId = null,
            ExpandedEvidence = null,
            ClarifyingQuestion = null,
            CanRetry = false,
            CanRecover = false,
            NoticeKey = null,
            ActiveWriteId = switching ? null : _state.ActiveWriteId,
            ConfirmingWriteId = switching ? null : _state.ConfirmingWriteId,
            ConfirmationExpiresAtUtc = switching ? null : _state.ConfirmationExpiresAtUtc,
            WriteBusyId = switching ? null : _state.WriteBusyId,
            WriteErrorId = switching ? null : _state.WriteErrorId,
            WriteErrorKey = switching ? null : _state.WriteErrorKey,
            UnavailableWrites = switching ? null : _state.UnavailableWrites,
            MemoryCandidate = switching ? null : _state.MemoryCandidate,
            MemoryCandidateEdit = switching ? null : _state.MemoryCandidateEdit,
            MemoryCandidateNoticeKey = switching ? null : _state.MemoryCandidateNoticeKey,
            Dispute = switching ? null : _state.Dispute,
            ReportingAvailable = switching ? false : _state.ReportingAvailable,
            ReportBusy = switching ? false : _state.ReportBusy,
            Reported = switching ? null : _state.Reported,
            ReportingMessageId = switching ? null : _state.ReportingMessageId,
            ReportReason = switching ? null : _state.ReportReason,
            ReportFailedId = switching ? null : _state.ReportFailedId,
            CopiedMessageId = null,
            CopyFailedMessageId = null,
            Conversations = _state.SafeConversations
                .Select(row => row with { IsSelected = row.Id == conversationId })
                .ToImmutableArray()
        });
    }

    private async Task LoadConversationAsync(string owner, string conversationId,
        CancellationToken cancellationToken, bool keepStatus = false)
    {
        var closed = _state.SafeConversations
            .FirstOrDefault(row => row.Id == conversationId)?.IsClosed ?? _state.ConversationClosed;
        AdoptConversation(conversationId, closed);
        if (!keepStatus)
            Publish(_state with { Status = NativeSamStatus.Resuming });
        try
        {
            await LoadSessionAsync(owner, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            // A network or owner-token failure never leaves the panel "Resuming".
            logger.LogWarning(ex, "Sam could not resume the conversation checkpoint.");
            if (IsCurrent(owner))
                Publish(_state with { Status = StatusFor(ex) });
            return;
        }
        if (!IsCurrent(owner) || _state.ConversationId != conversationId)
            return;
        await LoadTranscriptAsync(owner, conversationId, cancellationToken);
        if (!IsCurrent(owner) || _state.ConversationId != conversationId)
            return;
        await LoadVocabularyStateAsync(owner, conversationId, cancellationToken);
        if (!IsCurrent(owner) || _state.ConversationId != conversationId)
            return;
        await LoadReportedResponsesAsync(owner, conversationId, cancellationToken);
        if (IsCurrent(owner) && !keepStatus
            && _state.Status is NativeSamStatus.Resuming or NativeSamStatus.Opening)
            Publish(_state with { Status = NativeSamStatus.Ready });
    }

    /// <summary>
    /// The 24-hour checkpoint under the thread: session id for plan decisions,
    /// the plan version for conflict checks, the pending suggestion and undo.
    /// Its failure (for example no plan today) leaves the transcript readable.
    /// </summary>
    private async Task LoadSessionAsync(string owner, CancellationToken cancellationToken)
    {
        try
        {
            var session = await client.CallAsync(owner,
                (api, token) => api.StartSessionAsync(new StartCoachSessionRequest { Resume = true }, token),
                cancellationToken);
            if (!IsCurrent(owner))
                return;
            ApplySession(session);
        }
        catch (Exception ex) when (ex is CoachApiException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Sam session checkpoint unavailable: {Problem}.",
                (ex as CoachApiException)?.ProblemType);
            _sessionId = null;
            _planVersion = null;
            if (IsCurrent(owner))
                Publish(_state with { Suggestion = null, CanUndo = false, UndoRevisionId = null });
        }
    }

    private void ApplySession(CoachSessionResponse session)
    {
        _sessionId = session.SessionId;
        _planVersion = session.PlanState.PlanVersion;
        _constraints = session.ActiveConstraints;
        Publish(_state with
        {
            Suggestion = session.PendingSuggestion,
            CanUndo = session.PlanState.CanUndo && session.PlanState.LastRevision is not null,
            UndoRevisionId = session.PlanState.LastRevision?.RevisionId,
        });
    }

    private async Task LoadTranscriptAsync(string owner, string conversationId,
        CancellationToken cancellationToken)
    {
        Publish(_state with { TranscriptLoading = true });
        try
        {
            var page = await client.CallAsync(owner,
                (api, token) => api.GetConversationMessagesAsync(conversationId, MessagePageSize, null, token),
                cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            if (page is null)
            {
                DropConversation(conversationId);
                return;
            }
            var entries = page.Items.OrderBy(item => item.Sequence).Select(MapHistory).ToList();
            // Optimistic learner rows not yet in the canonical page stay visible in order.
            foreach (var local in _localEntries)
                if (!entries.Any(entry => entry.Kind == NativeSamEntryKind.Learner
                    && entry.Text == local.Text && entry.CreatedAtUtc >= local.CreatedAtUtc.AddMinutes(-2)))
                    entries.Add(local);
            _localEntries = _localEntries.Where(local => entries.Contains(local)).ToImmutableArray();
            Publish(_state with
            {
                TranscriptLoading = false,
                Entries = entries.ToImmutableArray(),
                UnreadableCount = page.UnreadableCount,
                HasLearnerSpoken = entries.Any(entry => entry.Kind == NativeSamEntryKind.Learner),
            });
            RecomputeActiveWrite();
        }
        catch (CoachApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            if (IsCurrent(owner))
                DropConversation(conversationId);
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam transcript could not be read.");
            if (IsCurrent(owner))
                Publish(_state with { TranscriptLoading = false, Status = StatusFor(ex) });
        }
    }

    /// <summary>
    /// A conversation that is gone or not this owner's leaves nothing on screen
    /// but the notice: transcript, cards and its shelf row all go (source
    /// DropUnavailableConversation).
    /// </summary>
    private void DropConversation(string conversationId)
    {
        _evidence.Clear();
        _limitations.Clear();
        _repairs.Clear();
        DiscardConfirmation();
        _localEntries = [];
        Publish(_state with
        {
            ConversationId = null,
            Entries = [],
            TranscriptLoading = false,
            Suggestion = null,
            VocabularySet = null,
            ApprovedResourceId = null,
            ActiveWriteId = null,
            ConfirmingWriteId = null,
            ConfirmationExpiresAtUtc = null,
            WriteBusyId = null,
            MemoryCandidate = null,
            MemoryCandidateEdit = null,
            Dispute = null,
            ReportingMessageId = null,
            NoticeKey = "Coach_ConversationGone",
            Status = NativeSamStatus.Ready,
            // A turn or decision in flight for the dropped thread ends here, so
            // New conversation stays reachable.
            Busy = false,
            StillWorking = false,
            CanStop = false,
            CanRetry = false,
            CanRecover = false,
            Conversations = _state.SafeConversations
                .Where(row => row.Id != conversationId).ToImmutableArray()
        });
    }

    private NativeSamEntry MapHistory(CoachHistoryMessageDto item)
    {
        var message = item.Message;
        if (!item.IsReadable)
            return Entry(message, NativeSamEntryKind.Unreadable, string.Empty, item.Sequence);
        if (message.Role == CoachMessageRole.Learner)
            return Entry(message, NativeSamEntryKind.Learner, message.Text, item.Sequence);
        var entry = MapCoachHistory(item, message);
        // A proposed change renders inside the exchange that produced it (source SamWriteCard).
        return item.WriteOperation is { } write ? entry with { Write = write } : entry;
    }

    private NativeSamEntry MapCoachHistory(CoachHistoryMessageDto item, CoachMessageDto message)
    {
        return message.Kind switch
        {
            CoachMessageKind.Receipt => Entry(message, NativeSamEntryKind.Receipt,
                item.Receipt?.Summary ?? message.Text, item.Sequence,
                changeLines: item.Receipt?.ChangeLines),
            CoachMessageKind.Notice => Entry(message, NativeSamEntryKind.Notice,
                message.Text, item.Sequence),
            CoachMessageKind.Suggestion => Entry(message, NativeSamEntryKind.Sam,
                item.Suggestion?.Rationale ?? message.Text, item.Sequence,
                changeLines: item.Suggestion?.ChangeLines),
            CoachMessageKind.Unrecognized => Entry(message, NativeSamEntryKind.Unsupported,
                string.Empty, item.Sequence),
            _ => Entry(message, NativeSamEntryKind.Sam,
                item.Answer?.PlainText ?? message.Text, item.Sequence, item.Answer),
        };
    }

    private NativeSamEntry Entry(CoachMessageDto message, NativeSamEntryKind kind, string text,
        long sequence, CoachAnswerDto? answer = null, IReadOnlyList<string>? changeLines = null) =>
        new(message.MessageId, kind, text, message.CreatedAtUtc, sequence,
            answer is null ? [] : answer.Blocks
                .Select(block => new NativeSamAnswerBlock(block.Kind, block.Label,
                    string.Concat(block.Spans.Select(span => span.Text)),
                    block.Spans.FirstOrDefault(span => span.Language == CoachLanguageRole.Target)?.LanguageTag))
                .Where(block => block.Text.Length > 0)
                .ToImmutableArray(),
            _evidence.TryGetValue(message.MessageId, out var evidence) ? evidence : [],
            _limitations.GetValueOrDefault(message.MessageId),
            changeLines?.ToImmutableArray() ?? [],
            MessageKind: message.Kind,
            Repair: _repairs.TryGetValue(message.MessageId, out var repair) ? repair.Disclosure : null,
            RepairEvidenceOnScreen: _repairs.TryGetValue(message.MessageId, out var shown) && shown.EvidenceOnScreen);

    // ---------------------------------------------------------------- owner + plumbing

    private bool TryOwner(out string owner)
    {
        if (scope.TryGetUserProfileId(out owner)
            && !string.IsNullOrWhiteSpace(owner)
            && string.Equals(preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal))
        {
            if (!string.Equals(_owner, owner, StringComparison.Ordinal))
                Reset(owner);
            return true;
        }
        owner = string.Empty;
        if (_owner is not null)
            Reset(null);
        return false;
    }

    private bool IsCurrent(string owner) =>
        TryOwner(out var current) && string.Equals(current, owner, StringComparison.Ordinal);

    /// <summary>Account boundary: nothing of a previous owner survives (source CoachAccountBoundary).</summary>
    private void Reset(string? owner)
    {
        _run?.Cancel();
        _run = null;
        _owner = owner;
        _availability = null;
        _sessionId = null;
        _planVersion = null;
        _constraints = null;
        _evidence.Clear();
        _limitations.Clear();
        _repairs.Clear();
        DiscardConfirmation();
        _localEntries = [];
        _lastRequest = null;
        _pendingOperationId = null;
        _pendingIdempotencyKey = null;
        Publish(new NativeSamState());
    }

    private async Task<string> ResolvePersonaCultureAsync(string owner, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await profiles.GetByIdAsync(owner, cancellationToken);
            return PersonaCulture(profile?.TargetLanguage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sam persona language could not be read; using the default name.");
            return "en";
        }
    }

    /// <summary>
    /// The coach name follows the language being studied, not the UI language:
    /// Korean study shows the Korean resource value, everything else the default
    /// (source CoachPersonaLanguages).
    /// </summary>
    public static string PersonaCulture(string? studyLanguage)
    {
        var primary = (studyLanguage ?? string.Empty).Split(',', ';')[0].Trim();
        if (primary.Length == 0)
            return "en";
        var separator = primary.IndexOfAny(['-', '_']);
        var subtag = separator > 0 ? primary[..separator] : primary;
        return subtag.ToLowerInvariant() is "korean" or "ko" or "kor" || subtag == "한국어"
            ? "ko" : "en";
    }

    private void Publish(NativeSamState next, bool structural = true)
    {
        _state = next with { Version = structural ? ++_version : _version };
        Changed?.Invoke();
    }

    private static string NewHandle() => Guid.NewGuid().ToString("N");

    private static bool IsRecoverable(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ex is HttpRequestException or CoachApiException or UnauthorizedAccessException
            or TaskCanceledException;

    private static NativeSamStatus StatusFor(Exception ex) => ex switch
    {
        HttpRequestException => NativeSamStatus.Offline,
        TaskCanceledException => NativeSamStatus.Incomplete,
        CoachApiException api => FromProblem(api),
        UnauthorizedAccessException => NativeSamStatus.Unavailable,
        _ => NativeSamStatus.Failed,
    };
}
