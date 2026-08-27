using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// The durable half of the workspace: a conversation that outlives the circuit, its message
/// ledger, and the turn operations that can be recovered after a lost response.
/// </summary>
/// <remarks>
/// <para>
/// Kept in its own file rather than folded into the session workspace because the two answer
/// different questions. The session half asks "what is on screen right now"; this half asks "what
/// does the server say happened". Where they disagree, the server wins — a client clock and a
/// client-allocated sequence are display conveniences, not evidence.
/// </para>
/// <para>
/// Everything here is inert when <see cref="IsDurableHistoryEnabled"/> is false, so the
/// session-only experience keeps working byte for byte when the flag is off.
/// </para>
/// </remarks>
public sealed partial class CoachWorkspaceState
{
    /// <summary>
    /// How many messages the first read asks for.
    /// </summary>
    /// <remarks>
    /// Fifty is roughly a long afternoon of coaching. Asking for the whole thread would make the
    /// first paint of a year-old conversation wait on a year of it.
    /// </remarks>
    public const int MessagePageSize = 50;

    /// <summary>How long a lost turn is polled for before the UI offers a retry instead.</summary>
    /// <remarks>
    /// <para>
    /// Bounded above the longest turn the server will actually run, not at the server's lease.
    /// The lease is renewed for as long as a worker is working, so it stops being an upper bound
    /// on the turn — while the model call alone is allowed up to two minutes, and the reducer,
    /// the plan write, and the ledger appends all come after it. A budget at the old lease
    /// therefore told the learner their turn had timed out at the exact moment it was still being
    /// answered, and the retry it offered would have taken the conversation over.
    /// </para>
    /// <para>
    /// It stays bounded because a spinner with no end is its own failure. Stop is available for
    /// the whole of it, and when the budget does run out the operation id is kept, so asking again
    /// resumes the same turn rather than starting a second one.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan OperationPollTimeout = TimeSpan.FromMinutes(4);

    /// <summary>Gap between polls while a durable turn is still running.</summary>
    private static readonly TimeSpan OperationPollInterval = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, int> _timelineIndexByMessageId = new(StringComparer.Ordinal);
    private string? _pendingClientTurnId;
    private string? _pendingLocalMessageId;
    private long _pendingTurnSequence;

    /// <summary>
    /// The highest server sequence already on screen when the current turn was submitted.
    /// </summary>
    /// <remarks>
    /// The turn boundary for answer pairing. Everything at or below this mark belongs to an
    /// earlier exchange, so this turn's structured answer can never be pinned onto it. A learner
    /// message marks the same boundary in the transcript, but a tapped chip does not write one,
    /// which is why the mark is recorded rather than inferred.
    /// </remarks>
    private long _answerPairingFloor;

    // ================================================================ observable state

    /// <summary>True when this workspace is backed by a durable conversation.</summary>
    /// <remarks>
    /// Set only by a conversation open that actually succeeded. A configuration flag is not
    /// evidence: the route has to have answered for this learner.
    /// </remarks>
    /// <summary>
    /// True once the canonical rows for the turn in flight have been merged from the ledger.
    /// </summary>
    /// <remarks>
    /// After that point the response body is a duplicate of what is already on screen, under
    /// different message ids, so applying its messages too is what put a second copy of Sam's
    /// answer in the transcript. The rest of the response — plan state, suggestion, receipt,
    /// memory candidate — is not in the ledger and is still applied.
    /// </remarks>
    public bool DurableLedgerIsAuthoritative { get; private set; }

    public bool IsDurableHistoryEnabled { get; private set; }

    /// <summary>The durable conversation this workspace is showing, when there is one.</summary>
    public string? ConversationId { get; private set; }

    /// <summary>The conversation's own record, as last read.</summary>
    public CoachConversationDto? Conversation { get; private set; }

    /// <summary>True when the conversation is closed and refuses new turns.</summary>
    public bool IsConversationClosed => Conversation?.IsClosed == true;

    /// <summary>
    /// The point before which history is no longer retained, when the thread has one.
    /// </summary>
    /// <remarks>
    /// Surfaced so the oldest retained message does not read as the beginning of the thread. That
    /// would be a claim the server never made.
    /// </remarks>
    public DateTime? HistoryStartsAtUtc { get; private set; }

    /// <summary>True when the retention boundary has actually been reached on screen.</summary>
    public bool IsAtHistoryBoundary { get; private set; }

    /// <summary>How many stored messages could not be read back.</summary>
    public int UnreadableMessageCount { get; private set; }

    /// <summary>True when an older page of messages exists.</summary>
    public bool HasEarlierMessages => _earlierCursor is not null;

    /// <summary>True while an older page is loading.</summary>
    public bool IsLoadingEarlier { get; private set; }

    /// <summary>True while the transcript is being read for the first time.</summary>
    public bool IsLoadingTranscript { get; private set; }

    /// <summary>
    /// The message the viewport should stay anchored to after older messages are prepended.
    /// </summary>
    /// <remarks>
    /// Prepending without an anchor throws the reader to the top of a page they did not ask to
    /// go to. The id names the message that was at the top before the fetch, so the browser can
    /// restore its position afterwards.
    /// </remarks>
    public string? ScrollAnchorMessageId { get; private set; }

    /// <summary>The operation id of the turn in flight, kept so a lost response is recoverable.</summary>
    public string? PendingOperationId { get; private set; }

    /// <summary>The state of the last durable turn operation the client saw.</summary>
    public CoachTurnOperationState? LastOperationState { get; private set; }

    /// <summary>True when the server has recorded a cancel for the operation in flight.</summary>
    public bool IsCancelRequested { get; private set; }

    /// <summary>
    /// True when a turn failed in a way the learner can retry without retyping.
    /// </summary>
    /// <remarks>
    /// The learner's own words stay on screen either way. Deleting what somebody typed because
    /// the model fell over is the one recovery that is worse than the failure.
    /// </remarks>
    public bool HasRecoverableTurn { get; private set; }

    /// <summary>A resource key naming the last durable conflict, or null.</summary>
    public string? ConversationNoticeKey { get; private set; }

    /// <summary>
    /// The id a link or query parameter should carry to come back to what is open now.
    /// </summary>
    /// <remarks>
    /// In durable mode that is the conversation, because the conversation is what survives the
    /// checkpoint; a URL holding a session id would resume into an empty thread once the session
    /// expired. Legacy mode has only the session, so it keeps naming that.
    /// </remarks>
    public string? EntryId => ConversationId ?? SessionId;

    private string? _earlierCursor;

    // ================================================================ open / resume

    /// <summary>
    /// Opens the workspace on a durable conversation, creating one when no id is given.
    /// </summary>
    /// <returns>
    /// False when durable history is not available here, in which case the caller must fall back
    /// to <see cref="OpenAsync"/> and the session-only experience.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Resume means <em>this</em> conversation. Reopening after a close reads the id it was given
    /// and nothing else: silently substituting "the most recent thread" is the failure where a
    /// learner taps a conversation from a list and lands in a different one.
    /// </para>
    /// <para>
    /// The 24-hour session is still started underneath, because plan state, constraints and the
    /// clarification budget are checkpoint state, not conversation content. The conversation
    /// supplies the transcript; the checkpoint supplies the plan.
    /// </para>
    /// </remarks>
    /// <param name="createWhenMissing">
    /// When true, an id that names no readable conversation starts a new one instead of reporting
    /// that it is gone. The URL entry point sets this because a stale or legacy link is not the
    /// learner asking for a specific thread; the conversation list never does, because there the
    /// id came from a row the learner just tapped and substituting a different thread would be
    /// the exact surprise this design forbids.
    /// </param>
    public async Task<bool> OpenConversationAsync(
        CoachPresentation presentation,
        string? conversationId = null,
        string? invokerElementId = null,
        CancellationToken cancellationToken = default,
        bool createWhenMissing = false)
    {
        if (_directory is null)
        {
            return false;
        }

        Presentation = presentation;
        InvokerElementId = invokerElementId ?? InvokerElementId;

        if (IsOpen && ConversationId is not null && conversationId is not null
            && string.Equals(ConversationId, conversationId, StringComparison.Ordinal))
        {
            Notify();
            return true;
        }

        // A deep link opens the workspace without the Dashboard entry ever running, so
        // availability - and with it CanEditPlan - would otherwise still be at its default and the
        // plan affordances would appear for a learner who has no plan.
        if (Availability is null)
        {
            await RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }

        var availability = await _directory.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (availability != CoachDurableHistoryAvailability.Available)
        {
            IsDurableHistoryEnabled = false;
            return false;
        }

        IsOpen = true;
        IsDurableHistoryEnabled = true;
        State = conversationId is null ? CoachUiState.Opening : CoachUiState.Resuming;
        ClearTransientAnnouncements();
        ConversationNoticeKey = null;
        Notify();

        CoachConversationDto? conversation;

        if (conversationId is null)
        {
            conversation = await _directory.CreateAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            conversation = _directory.Find(conversationId)
                ?? await _directory.ReloadOneAsync(conversationId, cancellationToken).ConfigureAwait(false);

            if (conversation is null && createWhenMissing)
            {
                // A link that no longer resolves. Legacy resume has always treated a missing
                // target as "then this is a new conversation", and a learner arriving from a
                // bookmark is better served by a usable thread than by an error about an id they
                // never saw.
                conversation = await _directory.CreateAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (conversation is null)
            {
                // The id names a conversation that is gone, or that was never this learner's. The
                // server answers both the same way on purpose, and so does the UI: an id is not an
                // authorization, and telling the two apart would say whether it exists.
                ConversationNoticeKey = "Coach_ConversationGone";
                State = CoachUiState.Ready;
                Notify();
                return true;
            }
        }

        if (conversation is null)
        {
            IsDurableHistoryEnabled = _directory.IsDurableHistoryAvailable;
            State = CoachUiState.Ready;
            Notify();
            return IsDurableHistoryEnabled;
        }

        AdoptConversation(conversation);
        _directory.Select(conversation.ConversationId);

        // The checkpoint underneath. Its failure is not fatal to reading the thread, so a plan
        // that cannot be loaded still leaves the transcript readable.
        try
        {
            var session = await _client
                .StartSessionAsync(new StartCoachSessionRequest { Resume = true }, cancellationToken)
                .ConfigureAwait(false);
            ApplySession(session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException)
        {
            State = CoachUiState.Ready;
        }
        catch (HttpRequestException)
        {
            State = CoachUiState.Offline;
        }

        await LoadTranscriptAsync(cancellationToken).ConfigureAwait(false);

        // After the transcript, so the flag control settles onto messages that are already on
        // screen rather than appearing a frame later. Its own failure is silent and only costs
        // the control, so it never delays or fails the thread.
        await LoadReportedResponsesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Opens the overlay on the thread the learner was last in, starting one only when there is
    /// none to return to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overlay has no address of its own — no query string, no route parameter — so after a
    /// page reload the only id it could resume from is the one held in circuit memory, and that
    /// circuit is gone. Reading <see cref="CoachWorkspaceState.ConversationId"/> alone therefore
    /// answered "none" on every reload and opened a new conversation each time, stranding the
    /// previous thread and any proposal still pending inside it.
    /// </para>
    /// <para>
    /// Resolution order is deliberate: the conversation already in hand wins, because a re-open
    /// inside a live circuit must land where the learner left off even if a newer thread was
    /// created elsewhere; otherwise the directory's most recent open thread; otherwise nothing,
    /// which <see cref="OpenAsync"/> reads as "start one".
    /// </para>
    /// <para>
    /// This does not change what resuming a <em>named</em> conversation means. A caller that holds
    /// an id still goes through <see cref="OpenConversationAsync"/> and still gets that exact
    /// thread; this only supplies an id to a caller that has none.
    /// </para>
    /// </remarks>
    public async Task ResumeMostRecentAsync(
        CoachPresentation presentation,
        string? invokerElementId = null,
        CancellationToken cancellationToken = default)
    {
        var resumeId = ConversationId;

        if (resumeId is null && _directory is not null)
        {
            var availability = await _directory.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (availability == CoachDurableHistoryAvailability.Available)
            {
                resumeId = _directory.MostRecentResumableId;
            }
        }

        await OpenAsync(presentation, resumeId, invokerElementId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the newest page of the conversation, replacing whatever the timeline held.
    /// </summary>
    public async Task LoadTranscriptAsync(CancellationToken cancellationToken = default)
    {
        if (ConversationId is not { } conversationId)
        {
            return;
        }

        IsLoadingTranscript = true;
        Notify();

        try
        {
            var page = await _client
                .GetConversationMessagesAsync(conversationId, MessagePageSize, before: null, cancellationToken)
                .ConfigureAwait(false);

            if (page is null)
            {
                // The conversation is gone, was never readable, or was never this learner's — the
                // route answers all three the same way and so does this. What must NOT survive is
                // what was on screen a moment ago: the notice would otherwise render above a full
                // transcript of the thread it is saying is unavailable, complete with its
                // proposal cards and their approval controls. Clearing first is what makes the
                // notice true.
                DropUnavailableConversation(conversationId, "Coach_ConversationGone");
                return;
            }

            ClearTranscript();
            MergeDurableMessages(page.Items, prepend: false);
            _earlierCursor = page.PreviousCursor;
            UnreadableMessageCount = page.UnreadableCount;
            UpdateHistoryBoundary();

            // The session read restored the latest turn's disclosure before this page existed, so
            // it had no answer to sit under. Now it does. Without this a reload silently drops a
            // "part of this answer was adjusted" note from a thread that still shows the answer.
            AttachRestoredRepairDisclosure();

            if (State == CoachUiState.Resuming)
            {
                State = CoachUiState.Ready;
                PoliteAnnouncementKey = "Coach_AnnounceResumed";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException ex)
        {
            State = CoachStateMachine.FromProblem(ex);

            // A refusal is the same claim as a not-found: this thread is not readable by whoever
            // is asking. Leaving the transcript up under it is the same defect, so an unauthorized
            // or forbidden answer clears exactly as hard.
            if (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.NotFound)
            {
                DropUnavailableConversation(conversationId, "Coach_ConversationGone");
            }
        }
        catch (HttpRequestException)
        {
            State = CoachUiState.Offline;
        }
        finally
        {
            IsLoadingTranscript = false;
            Notify();
        }
    }

    /// <summary>
    /// Takes a conversation off the screen entirely, leaving only the notice explaining it.
    /// </summary>
    /// <remarks>
    /// Everything derived from the thread goes at once: the messages, the proposal cards and any
    /// confirmation in hand for them, the paging cursor, the retention boundary, and the row in
    /// the shelf along with its title and its selection. A title is content too — "Refund request
    /// for my landlord" names a conversation as surely as its transcript does — so the shelf entry
    /// is dropped rather than greyed out.
    /// </remarks>
    private void DropUnavailableConversation(string conversationId, string noticeKey)
    {
        ClearTranscript();

        _earlierCursor = null;
        UnreadableMessageCount = 0;
        HistoryStartsAtUtc = null;
        IsAtHistoryBoundary = false;
        ScrollAnchorMessageId = null;
        ClearPendingOperation();

        ConversationNoticeKey = noticeKey;
        _directory?.Remove(conversationId);
        ConversationId = null;
        Conversation = null;
        IsDurableHistoryEnabled = _directory?.IsDurableHistoryAvailable ?? false;
    }

    /// <summary>
    /// Prepends the previous page of messages, keeping the reader where they were.
    /// </summary>
    /// <remarks>
    /// A rejected cursor re-reads the newest page rather than surfacing an error. Cursors expire
    /// for ordinary reasons and "invalid cursor" is not something a learner can act on.
    /// </remarks>
    public async Task LoadEarlierMessagesAsync(CancellationToken cancellationToken = default)
    {
        if (ConversationId is not { } conversationId || _earlierCursor is not { } cursor || IsLoadingEarlier)
        {
            return;
        }

        IsLoadingEarlier = true;

        // Captured before the fetch: this is the message the viewport must still be looking at
        // once older ones are inserted above it.
        ScrollAnchorMessageId = FirstDurableMessageId();
        Notify();

        try
        {
            var page = await _client
                .GetConversationMessagesAsync(conversationId, MessagePageSize, cursor, cancellationToken)
                .ConfigureAwait(false);

            if (page is null)
            {
                _earlierCursor = null;
                return;
            }

            MergeDurableMessages(page.Items, prepend: true);
            _earlierCursor = page.PreviousCursor;
            UnreadableMessageCount += page.UnreadableCount;
            UpdateHistoryBoundary();
            PoliteAnnouncementKey = "Coach_EarlierLoaded";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException ex) when (ex.ProblemType == CoachProblemTypes.InvalidCursor)
        {
            _earlierCursor = null;
            IsLoadingEarlier = false;
            ScrollAnchorMessageId = null;
            await LoadTranscriptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CoachApiException)
        {
            ConversationNoticeKey = "Coach_ConversationsLoadFailed";
        }
        catch (HttpRequestException)
        {
            State = CoachUiState.Offline;
        }
        finally
        {
            IsLoadingEarlier = false;
            Notify();
        }
    }

    /// <summary>Consumes the scroll anchor. Read once, by the component that restores position.</summary>
    public string? ConsumeScrollAnchor()
    {
        var anchor = ScrollAnchorMessageId;
        ScrollAnchorMessageId = null;
        return anchor;
    }

    // ================================================================ turn submission

    /// <summary>
    /// Submits one durable turn and waits for the ledger to settle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation id and the idempotency key are minted here, <em>before</em> the request goes
    /// out, and kept on the workspace. That is what makes a lost response recoverable: the client
    /// can poll for an operation whose reply it never saw, and a retry replays the stored result
    /// instead of running the turn a second time.
    /// </para>
    /// <para>
    /// A retry of the same client turn reuses both handles. Minting new ones would turn every
    /// retry into a second turn, which is exactly the duplicate this design exists to prevent.
    /// </para>
    /// <para>
    /// Reusing them also means a retry sent while the first attempt is still running is answered
    /// with a conflict rather than a result, because the server holds the conversation for the
    /// worker that is still working on it. That conflict is not a failure of this turn — it is the
    /// server saying the turn is alive — so it falls through to polling the operation this client
    /// already owns. Surfacing it as an error would tell the learner their message failed at the
    /// exact moment it was being answered.
    /// </para>
    /// </remarks>
    private async Task<CoachTurnResponse?> SubmitDurableTurnAsync(
        string conversationId,
        CoachTurnRequest request,
        long turnSequence,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_pendingClientTurnId, request.ClientTurnId, StringComparison.Ordinal)
            || PendingOperationId is null)
        {
            PendingOperationId = CoachConversationDirectory.NewHandle();
            _pendingIdempotencyKey = CoachConversationDirectory.NewHandle();
            _pendingClientTurnId = request.ClientTurnId;
        }

        _pendingTurnSequence = turnSequence;
        _pendingLocalMessageId = LocalMessageIdPrefix + request.ClientTurnId;

        // Recorded before the turn's own rows can arrive, so the mark describes the transcript as
        // it was when the learner asked.
        _answerPairingFloor = HighestServerSequence();

        HasRecoverableTurn = false;
        IsCancelRequested = false;

        // Per turn, not per conversation: a turn whose canonical rows never arrived still has to
        // be able to fall back to its own response body.
        DurableLedgerIsAuthoritative = false;

        CoachTurnOperationDto operation;

        try
        {
            operation = await _client.SubmitConversationTurnAsync(
                conversationId,
                new CoachConversationTurnRequest
                {
                    Turn = request,
                    OperationId = PendingOperationId,
                    IdempotencyKey = _pendingIdempotencyKey!
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CoachApiException ex) when (ex.ProblemType == CoachProblemTypes.RunInProgress)
        {
            if (await ResumePendingOperationAsync(conversationId, cancellationToken).ConfigureAwait(false)
                is not { } running)
            {
                // The conversation is busy with something that is not this turn, or this turn has
                // gone. Either way there is nothing here to wait for, and the conflict is the
                // honest answer.
                throw;
            }

            operation = running;
        }

        return await SettleOperationAsync(conversationId, operation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads back the operation this client already owns, when it is still running.
    /// </summary>
    /// <remarks>
    /// Returns null when the server does not know the operation, or when it has already finished,
    /// because in both cases the caller's conflict is about some other turn holding the
    /// conversation and pretending otherwise would hang the UI on a turn that is not ours.
    /// </remarks>
    private async Task<CoachTurnOperationDto?> ResumePendingOperationAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (PendingOperationId is not { } operationId)
        {
            return null;
        }

        var operation = await _client
            .GetConversationOperationAsync(conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        return operation?.State is CoachTurnOperationState.Pending or CoachTurnOperationState.Running
            ? operation
            : null;
    }

    private string? _pendingIdempotencyKey;

    /// <summary>
    /// Polls a durable operation until it reaches a terminal state, then returns its result.
    /// </summary>
    private async Task<CoachTurnResponse?> SettleOperationAsync(
        string conversationId,
        CoachTurnOperationDto operation,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + OperationPollTimeout;
        var current = operation;
        var mergedFromLedger = false;

        while (true)
        {
            LastOperationState = current.State;
            IsCancelRequested = current.CancelRequested;

            if (current.Messages.Count > 0)
            {
                MergeDurableMessages(current.Messages, prepend: false, turnSequence: _pendingTurnSequence);
                mergedFromLedger = true;
                DurableLedgerIsAuthoritative = true;
            }

            switch (current.State)
            {
                case CoachTurnOperationState.Completed when current.Result is { } result:
                    // The ledger is the transcript, not the response body. An operation that
                    // completed without carrying its messages leaves the local copy built from
                    // the reply alone, which is the one shape that can drift from what a reload
                    // would show — so read the canonical page back first. This runs before the
                    // pending handles are dropped, because the merge needs them to know which
                    // local message the canonical row replaces.
                    // A partial carry is the same problem as no carry. The observed failure was an
                    // operation that returned only Sam's reply: the canonical learner row was never
                    // seen, so the optimistic copy of the learner's own message went on standing in
                    // for a row that already existed, and sorted below Sam's answer because a local
                    // entry has no server sequence to sort by. A still-pending local id after the
                    // merge is exactly that condition.
                    if (!mergedFromLedger || _pendingLocalMessageId is not null)
                    {
                        await ReconcileFromLedgerAsync(conversationId, cancellationToken).ConfigureAwait(false);
                    }

                    ClearPendingOperation();
                    await RefreshConversationRowAsync(conversationId, cancellationToken).ConfigureAwait(false);
                    return result;

                case CoachTurnOperationState.Completed:
                    // Completed with no result body: the ledger already carries the messages, so
                    // there is nothing left to apply beyond what was merged above.
                    ClearPendingOperation();
                    await RefreshConversationRowAsync(conversationId, cancellationToken).ConfigureAwait(false);
                    return null;

                case CoachTurnOperationState.Cancelled:
                    ClearPendingOperation();
                    throw new OperationCanceledException(cancellationToken);

                case CoachTurnOperationState.Failed:
                    MarkPendingTurnFailed();
                    throw new CoachDurableTurnFailedException();
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // Still running after the poll budget. The turn is not lost — the operation id is
                // kept and the learner is offered a way to ask again rather than a spinner with
                // no end.
                HasRecoverableTurn = true;
                throw new CoachApiException(
                    System.Net.HttpStatusCode.RequestTimeout,
                    CoachProblemTypes.Timeout,
                    title: null,
                    detail: null);
            }

            await Task.Delay(OperationPollInterval, cancellationToken).ConfigureAwait(false);

            var polled = await _client
                .GetConversationOperationAsync(conversationId, current.OperationId, cancellationToken)
                .ConfigureAwait(false);

            if (polled is null)
            {
                // The operation is not there. Treat as unrecoverable rather than as success: the
                // zero value of the state enum is Failed for the same reason.
                MarkPendingTurnFailed();
                throw new CoachApiException(
                    System.Net.HttpStatusCode.NotFound,
                    CoachProblemTypes.ConversationNotFound,
                    title: null,
                    detail: null);
            }

            current = polled;
        }
    }

    /// <summary>
    /// Asks the server what happened to a turn whose response was never received.
    /// </summary>
    /// <remarks>
    /// The same operation id is used, so this can never produce a second turn. A poll that finds
    /// the turn already completed simply merges its messages: the ledger, not the response, is
    /// what the transcript is built from.
    /// </remarks>
    public async Task PollPendingOperationAsync(CancellationToken cancellationToken = default)
    {
        if (ConversationId is not { } conversationId || PendingOperationId is not { } operationId)
        {
            return;
        }

        try
        {
            var operation = await _client
                .GetConversationOperationAsync(conversationId, operationId, cancellationToken)
                .ConfigureAwait(false);

            if (operation is null)
            {
                HasRecoverableTurn = true;
                Notify();
                return;
            }

            LastOperationState = operation.State;
            IsCancelRequested = operation.CancelRequested;

            if (operation.Messages.Count > 0)
            {
                MergeDurableMessages(operation.Messages, prepend: false, turnSequence: _pendingTurnSequence);
            }

            switch (operation.State)
            {
                case CoachTurnOperationState.Completed:
                    ClearPendingOperation();
                    if (operation.Result is { } result)
                    {
                        ApplyTurn(result, _lastInitiator, _pendingTurnSequence);
                    }
                    else
                    {
                        State = CoachUiState.Ready;
                    }

                    await RefreshConversationRowAsync(conversationId, cancellationToken).ConfigureAwait(false);
                    break;

                case CoachTurnOperationState.Cancelled:
                    ClearPendingOperation();
                    State = CoachUiState.Ready;
                    break;

                case CoachTurnOperationState.Failed:
                    MarkPendingTurnFailed();
                    State = CoachUiState.Failed;
                    break;

                default:
                    // Pending or running. Leave the pending handles in place so the learner can
                    // ask again without resending the turn.
                    HasRecoverableTurn = true;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException)
        {
            HasRecoverableTurn = true;
        }
        catch (HttpRequestException)
        {
            State = CoachUiState.Offline;
        }
        finally
        {
            Notify();
        }
    }

    /// <summary>
    /// Retries the failed turn using the handles it already has, so the server replays rather
    /// than re-runs.
    /// </summary>
    public async Task RetryDurableTurnAsync(CancellationToken cancellationToken = default)
    {
        if (ConversationId is not { } conversationId
            || _lastTurnRequest is not { } request
            || PendingOperationId is null)
        {
            return;
        }

        HasRecoverableTurn = false;
        MarkPendingTurnStatus(CoachTimelineStatus.Pending);

        await ExecuteAsync(
            CoachUiState.Running,
            _lastInitiator,
            token => SubmitDurableTurnAsync(conversationId, request, _pendingTurnSequence, token),
            cancellationToken,
            _pendingTurnSequence).ConfigureAwait(false);
    }

    // ================================================================ merge

    /// <summary>
    /// Folds server messages into the timeline, matching on identity rather than on position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things happen here, and all three are the point. A local learner message is
    /// <em>replaced</em> by its canonical counterpart, so the optimistic copy never becomes a
    /// duplicate. A message already present is updated in place, so a re-read does not grow the
    /// thread. Anything new is appended, or prepended when it belongs to an older page.
    /// </para>
    /// <para>
    /// The server's stamp replaces the client's capture time on every entry that has one, because
    /// a client clock is not evidence and two devices reading the same thread must agree.
    /// </para>
    /// </remarks>
    private void MergeDurableMessages(
        IReadOnlyList<CoachHistoryMessageDto> durable,
        bool prepend,
        long turnSequence = 0)
    {
        if (durable.Count == 0)
        {
            return;
        }

        // Chronological within the page. The sequence is the only trustworthy order — timestamps
        // can tie to the millisecond and a tie is not a coin flip.
        var ordered = durable.OrderBy(m => m.Sequence).ToList();
        var inserted = new List<CoachTimelineEntry>();

        foreach (var item in ordered)
        {
            var id = item.Message.MessageId;

            var existingIndex = _timeline.FindIndex(e =>
                string.Equals(e.MessageId, id, StringComparison.Ordinal));

            if (existingIndex >= 0)
            {
                _timeline[existingIndex] = _timeline[existingIndex].Reconciled(item);
                continue;
            }

            // The optimistic copy of this learner turn, if it is still standing in for the real
            // one. Matched on the client turn handle, never on text: a learner who says "yes"
            // twice said it twice.
            if (item.Message.Role == CoachMessageRole.Learner && _pendingLocalMessageId is { } localId)
            {
                var localIndex = _timeline.FindIndex(e =>
                    e.Kind == CoachTimelineKind.LearnerMessage
                    && string.Equals(e.MessageId ?? e.Message?.MessageId, localId, StringComparison.Ordinal));

                if (localIndex >= 0)
                {
                    _timeline[localIndex] = _timeline[localIndex].Reconciled(item);
                    _messages.RemoveAll(m => string.Equals(m.MessageId, localId, StringComparison.Ordinal));
                    _messages.Add(item.Message);
                    _pendingLocalMessageId = null;
                    continue;
                }
            }

            var entry = BuildDurableEntry(item, turnSequence);
            inserted.Add(entry);
            _messages.Add(item.Message);

            if (item.Answer is { } answer)
            {
                _answersByMessageId[id] = answer;
                LatestAnswer = answer;
            }
        }

        if (inserted.Count > 0)
        {
            if (prepend)
            {
                _timeline.InsertRange(0, inserted);
            }
            else
            {
                _timeline.AddRange(inserted);
            }
        }

        ReindexDurable();
        DeduplicateWriteCards();
        RecomputeActiveWrite();
    }

    /// <summary>
    /// Leaves exactly one card per proposal on the merged timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A proposal is anchored to the last thing Sam said on the turn that produced it, and the
    /// anchor is resolved <em>per page</em>. A turn whose messages straddle a page boundary
    /// therefore anchors the same proposal twice — once in each page — and the two pages are
    /// merged into one timeline here. Both copies carry the same operation id, so both satisfy
    /// the "is this the actionable one" test and the learner is shown the same decision twice,
    /// with two live Accept buttons. Approving through one of them leaves the other still
    /// offering to run a change that has already run.
    /// </para>
    /// <para>
    /// The surviving copy is the latest anchor, which is the one a single unsplit page would have
    /// produced, so paging older history in does not make a card jump up the thread. A durable
    /// anchor always beats a locally-attached one, because the ledger is the authority on where a
    /// proposal belongs and the local copy only exists until the ledger answers.
    /// </para>
    /// <para>
    /// The duplicate is stripped of its proposal rather than removed: the message it was riding on
    /// is a real message the learner sent or received, and deleting it to solve a card problem
    /// would rewrite the transcript.
    /// </para>
    /// </remarks>
    private void DeduplicateWriteCards()
    {
        Dictionary<string, int>? survivors = null;

        for (var i = 0; i < _timeline.Count; i++)
        {
            if (_timeline[i].WriteOperation is not { OperationId.Length: > 0 } write)
            {
                continue;
            }

            survivors ??= new Dictionary<string, int>(StringComparer.Ordinal);

            if (!survivors.TryGetValue(write.OperationId, out var incumbent))
            {
                survivors[write.OperationId] = i;
                continue;
            }

            var incumbentIsDurable = _timeline[incumbent].ServerSequence is not null;
            var candidateIsDurable = _timeline[i].ServerSequence is not null;

            // Same provenance: the later anchor wins. Different: the durable one does.
            if (candidateIsDurable == incumbentIsDurable || candidateIsDurable)
            {
                survivors[write.OperationId] = i;
            }
        }

        if (survivors is null || survivors.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _timeline.Count; i++)
        {
            if (_timeline[i].WriteOperation is not { OperationId.Length: > 0 } write)
            {
                continue;
            }

            if (survivors.TryGetValue(write.OperationId, out var keep) && keep != i)
            {
                _timeline[i] = _timeline[i].WithWriteOperation(null);
            }
        }
    }

    private CoachTimelineEntry BuildDurableEntry(CoachHistoryMessageDto item, long turnSequence)
    {
        var kind = !item.IsReadable
            ? CoachTimelineKind.UnreadableMessage
            : CoachTimelineEntry.KindFor(item.Message);

        return new CoachTimelineEntry
        {
            // Durable entries order by the server sequence, so the client turn counter only has to
            // be plausible, not authoritative.
            TurnSequence = turnSequence == 0 ? ++_turnSequence : turnSequence,

            // Only a merge that happened while a turn was running knows which turn a row belongs
            // to. A transcript load and a page of older history pass zero, and leave this null.
            ArrivedOnTurn = turnSequence == 0 ? null : turnSequence,
            Sequence = ++_artifactSequence,
            Kind = kind,
            Timestamp = CoachTimelineEntry.ServerTime(item.Message.CreatedAtUtc),
            Message = item.Message,
            Answer = item.Answer,
            MessageId = item.Message.MessageId,
            ServerSequence = item.Sequence,
            Status = CoachTimelineStatus.Settled,
            HistoryReceipt = item.Receipt,
            HistorySuggestion = item.Suggestion,
            NoticeReasonCode = item.NoticeReasonCode,
            WriteOperation = item.WriteOperation
        };
    }

    /// <summary>
    /// Re-sorts the durable part of the timeline by server sequence and rebuilds the id index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries without a server sequence — the optimistic learner message, and anything the
    /// session half produced — keep their relative position at the end. They are the newest thing
    /// on screen by construction.
    /// </para>
    /// <para>
    /// Every entry is renumbered afterwards, because the timeline is presented in arrival order
    /// and arrival order is wrong as soon as an older page is fetched. The turn counters are
    /// advanced past the last ordinal so the next turn's artifacts still land at the end.
    /// </para>
    /// </remarks>
    private void ReindexDurable()
    {
        var ordered = _timeline
            .Where(e => e.ServerSequence is not null)
            .OrderBy(e => e.ServerSequence!.Value)
            .Concat(_timeline.Where(e => e.ServerSequence is null))
            .ToList();

        _timeline.Clear();
        _timelineIndexByMessageId.Clear();

        long ordinal = 0;

        foreach (var entry in ordered)
        {
            var renumbered = entry.Renumbered(++ordinal);
            _timeline.Add(renumbered);

            if (renumbered.MessageId is { } id)
            {
                _timelineIndexByMessageId[id] = _timeline.Count - 1;
            }
        }

        _turnSequence = Math.Max(_turnSequence, ordinal);
        _artifactSequence = Math.Max(_artifactSequence, ordinal);
    }

    private string? FirstDurableMessageId() => _timeline
        .Where(e => e.ServerSequence is not null)
        .OrderBy(e => e.ServerSequence!.Value)
        .Select(e => e.MessageId)
        .FirstOrDefault();

    private void UpdateHistoryBoundary() =>
        IsAtHistoryBoundary = HistoryStartsAtUtc is not null && _earlierCursor is null;

    /// <summary>The newest server ordinal currently on screen, or zero when there is none.</summary>
    private long HighestServerSequence()
    {
        var highest = 0L;

        foreach (var entry in _timeline)
        {
            if (entry.ServerSequence is { } sequence && sequence > highest)
            {
                highest = sequence;
            }
        }

        return highest;
    }

    // ================================================================ housekeeping

    private void AdoptConversation(CoachConversationDto conversation)
    {
        Conversation = conversation;
        ConversationId = conversation.ConversationId;
        HistoryStartsAtUtc = conversation.HistoryStartsAtUtc;
    }

    /// <summary>
    /// Re-reads the newest page of the conversation and merges it over whatever is on screen.
    /// </summary>
    /// <remarks>
    /// Merging rather than replacing keeps a message the learner is still reading from jumping,
    /// and the merge is keyed by the server's message id, so a canonical row always wins over the
    /// local copy it replaces.
    /// </remarks>
    private async Task ReconcileFromLedgerAsync(string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            var page = await _client
                .GetConversationMessagesAsync(conversationId, MessagePageSize, before: null, cancellationToken)
                .ConfigureAwait(false);

            if (page is null)
            {
                return;
            }

            MergeDurableMessages(page.Items, prepend: false, turnSequence: _pendingTurnSequence);
            DurableLedgerIsAuthoritative = true;
            UnreadableMessageCount = page.UnreadableCount;
            UpdateHistoryBoundary();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException)
        {
            // The turn itself succeeded. Failing the run because the read-back stumbled would
            // discard a completed answer over a bookkeeping call.
        }
        catch (HttpRequestException)
        {
        }
    }

    private async Task RefreshConversationRowAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (_directory is null)
        {
            return;
        }

        var refreshed = await _directory.ReloadOneAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (refreshed is not null)
        {
            AdoptConversation(refreshed);
        }
    }

    private void ClearPendingOperation()
    {
        PendingOperationId = null;
        _pendingIdempotencyKey = null;
        _pendingClientTurnId = null;
        _pendingLocalMessageId = null;
        HasRecoverableTurn = false;
        IsCancelRequested = false;
    }

    private void MarkPendingTurnFailed()
    {
        MarkPendingTurnStatus(CoachTimelineStatus.Failed);
        HasRecoverableTurn = true;
    }

    private void MarkPendingTurnStatus(CoachTimelineStatus status)
    {
        if (_pendingLocalMessageId is not { } localId)
        {
            return;
        }

        var index = _timeline.FindIndex(e =>
            e.Kind == CoachTimelineKind.LearnerMessage
            && string.Equals(e.MessageId ?? e.Message?.MessageId, localId, StringComparison.Ordinal));

        if (index >= 0)
        {
            _timeline[index] = _timeline[index].WithStatus(status);
        }
    }

    private void ClearTranscript()
    {
        _timeline.Clear();
        _messages.Clear();
        _timelineIndexByMessageId.Clear();
        _answersByMessageId.Clear();
        LatestAnswer = null;
        PendingSuggestionTurn = null;
        ResetWrites();
    }

    /// <summary>Clears every durable field. Called from <see cref="Reset"/>.</summary>
    private void ResetDurable()
    {
        ConversationId = null;
        Conversation = null;
        IsDurableHistoryEnabled = false;
        HistoryStartsAtUtc = null;
        IsAtHistoryBoundary = false;
        UnreadableMessageCount = 0;
        _earlierCursor = null;
        IsLoadingEarlier = false;
        IsLoadingTranscript = false;
        ScrollAnchorMessageId = null;
        LastOperationState = null;
        ConversationNoticeKey = null;
        _pendingTurnSequence = 0;
        _timelineIndexByMessageId.Clear();
        ClearPendingOperation();
        ResetWrites();
    }

    /// <summary>
    /// Announces a completed action politely, without interrupting whatever is being read.
    /// </summary>
    /// <remarks>
    /// Routed through the workspace so there stays exactly one live region. A second component
    /// announcing on its own is how two regions end up talking over each other, and a screen
    /// reader user hears neither. The alert channel is cleared here for the same reason: an alert
    /// is an interruption, a confirmation is not, and they are never both live.
    /// </remarks>
    public void Announce(string resourceKey)
    {
        if (string.IsNullOrEmpty(resourceKey))
        {
            return;
        }

        AlertKey = null;
        PoliteAnnouncementKey = resourceKey;
        Notify();
    }

    /// <summary>Dismisses the conversation notice so it does not survive the next render.</summary>
    public void ClearConversationNotice()
    {
        if (ConversationNoticeKey is null)
        {
            return;
        }

        ConversationNoticeKey = null;
        Notify();
    }
}

/// <summary>
/// A durable turn that the server reports as failed.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a <see cref="CoachApiException"/>. The transport succeeded: the
/// server answered, and its answer was that the turn failed. Routing it through a problem type
/// would land it on the legacy tool-failure mapping, which means "the run stopped before
/// completing and nothing was written" — a different, softer claim than the one the ledger is
/// making.
/// </para>
/// <para>
/// The distinction matters to the learner, not just to the code: a failed durable turn keeps
/// their message and offers a retry, so the state has to be the one whose copy says the turn
/// failed and can be tried again.
/// </para>
/// </remarks>
internal sealed class CoachDurableTurnFailedException : Exception
{
    public CoachDurableTurnFailedException()
        : base("The durable turn operation reported a failed state.")
    {
    }
}
