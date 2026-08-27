using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// The one shared, scoped coach workspace. Both the wide overlay and the full-screen
/// <c>/coach</c> route are compositions over this single instance, so switching presentation
/// (or resizing) never loses session, draft, or canvas state.
/// </summary>
/// <remarks>
/// <para>
/// Registered <b>scoped</b> — matching <c>NavigationMemoryService</c> and
/// <c>BlazorLocalizationService</c> — so each Blazor circuit on the server holds its own instance.
/// A singleton would leak one learner's coach session into another's circuit.
/// </para>
/// <para>
/// This type owns transitions but not rendering. All pure rules live in
/// <see cref="CoachStateMachine"/>.
/// </para>
/// </remarks>
public sealed partial class CoachWorkspaceState : IDisposable
{
    /// <summary>Time box for the best-effort server-side cancel so Stop always releases the UI.</summary>
    private static readonly TimeSpan ServerCancelTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Marks a message the client holds on its own, with no server counterpart. Only learner
    /// turns are local: the server keeps no plaintext transcript, so it can never return them.
    /// </summary>
    private const string LocalMessageIdPrefix = "local:";

    private readonly ICoachApiClient _client;
    private readonly CoachConversationDirectory? _directory;
    private readonly List<CoachMessageDto> _messages = new();
    private readonly List<CoachTimelineEntry> _timeline = new();

    // Composer sends queue rather than drop. The busy guard in ExecuteAsync exists to stop a
    // control being double-fired, but applying it to typed turns silently discarded a question
    // the learner had already watched appear on screen.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _turnSequence;
    private long _artifactSequence;
    private readonly List<CoachChangeReceiptDto> _receipts = new();
    private readonly List<CoachEvidenceDto> _evidence = new();
    private readonly List<CoachRevisionDto> _revisions = new();

    private CancellationTokenSource? _runCts;
    private CoachTurnRequest? _lastTurnRequest;
    private bool _resumeRequested;
    private readonly Dictionary<string, CoachAnswerDto> _answersByMessageId = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _planResourceTitles;
    private CoachInitiator _lastInitiator = CoachInitiator.System;
    private bool _disposed;
    private readonly CoachFeatureFlags? _flags;

    public CoachWorkspaceState(ICoachApiClient client)
        : this(client, directory: null)
    {
    }

    /// <summary>
    /// Constructs the workspace with the conversation directory it should keep in step.
    /// </summary>
    /// <remarks>
    /// The directory is optional so the workspace remains constructible on its own — the
    /// session-only experience does not need a shelf, and neither do the tests that only exercise
    /// it. When one is supplied, a completed turn refreshes that conversation's row so the list
    /// order and the transcript never disagree about which thread was spoken to last.
    /// </remarks>
    public CoachWorkspaceState(
        ICoachApiClient client,
        CoachConversationDirectory? directory,
        CoachFeatureFlags? flags = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _directory = directory;
        _flags = flags;
    }

    /// <summary>Raised whenever any observable property changes. Components call StateHasChanged.</summary>
    public event Action? Changed;

    // ---------------------------------------------------------------- session

    public CoachUiState State { get; private set; } = CoachUiState.Opening;

    public CoachPresentation Presentation { get; private set; } = CoachPresentation.Unknown;

    public CoachPane Pane { get; private set; } = CoachPane.Coach;

    public string? SessionId { get; private set; }

    public bool IsOpen { get; private set; }

    public CoachAvailabilityResponse? Availability { get; private set; }

    public IReadOnlyList<CoachMessageDto> Messages => _messages;

    /// <summary>
    /// The conversation as one chronological stream: learner turns, Sam's replies, and the plan
    /// artifacts each turn produced, ordered by when they happened rather than by what they are.
    /// </summary>
    /// <remarks>
    /// Durable messages are ordered by the server's own sequence, which is the only authority on
    /// what happened first. The local counters are assigned on arrival, and arrival order is wrong
    /// the moment an older page is fetched — "load earlier" would otherwise append earlier
    /// messages to the end. Entries with no server sequence are the optimistic learner message and
    /// the current turn's artifacts, which are the newest things on screen by construction.
    /// Enforcing the order here rather than in the merge means a new code path cannot reintroduce
    /// a reversed transcript by forgetting to renumber.
    /// </remarks>
    public IReadOnlyList<CoachTimelineEntry> Timeline => _timeline
        .OrderBy(e => e.ServerSequence is null ? 1 : 0)
        .ThenBy(e => e.ServerSequence ?? 0)
        .ThenBy(e => e.TurnSequence)
        .ThenBy(e => e.Sequence)
        .ToList();

    /// <summary>The turn the pending suggestion belongs to, so it renders inside that exchange.</summary>
    public long? PendingSuggestionTurn { get; private set; }

    /// <summary>True once the learner has submitted anything in this conversation.</summary>
    public bool HasLearnerTurn => _timeline.Any(e => e.Kind == CoachTimelineKind.LearnerMessage);

    /// <summary>
    /// True when the learner has a plan the coach may edit.
    /// </summary>
    /// <remarks>
    /// The coach is available for language questions even with no plan for today. When this is
    /// false the conversation stays fully usable and only the plan-editing affordances — quick
    /// constraints, canvas edit actions, plan-change actions — are withdrawn. Sourced from
    /// availability, because CoachSessionResponse does not carry it.
    /// </remarks>
    public bool CanEditPlan { get; private set; } = true;

    /// <summary>
    /// The structured answer that belongs to a PedagogicalAnswer message, if one was returned.
    /// </summary>
    /// <remarks>
    /// The server sends BOTH a structured <c>Answer</c> and a PedagogicalAnswer message carrying
    /// its PlainText. Pairing them here lets the chat render the structured blocks in place and
    /// keeps the plain text as a genuine fallback, instead of rendering the answer twice.
    /// </remarks>
    public CoachAnswerDto? AnswerFor(CoachMessageDto message) =>
        message is not null && _answersByMessageId.TryGetValue(message.MessageId, out var answer)
            ? answer
            : null;

    /// <summary>The structured answer produced by the most recent turn, if any.</summary>
    public CoachAnswerDto? LatestAnswer { get; private set; }

    public IReadOnlyList<CoachChangeReceiptDto> Receipts => _receipts;

    public IReadOnlyList<CoachEvidenceDto> Evidence => _evidence;

    /// <summary>
    /// The learner's open correction of an earlier answer, when the server reports one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held on the workspace rather than on a message because a dispute is a property of the
    /// conversation's current state, not of the turn that produced it: it stays in force across
    /// turns until an answer satisfies it, and the notice has to keep saying so.
    /// </para>
    /// <para>
    /// Replaced wholesale from each response, including with null. A dispute the server has closed
    /// must stop being shown, and a stale notice claiming an open correction is worse than none —
    /// it tells the learner the coach is still under a constraint it has already discharged.
    /// </para>
    /// </remarks>
    public CoachDisputeDto? Dispute { get; private set; }

    /// <summary>
    /// The limitation in force from the last turn, when the coach withheld a claim it could not
    /// verify. Null when the last turn stood behind its answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaced from every turn, null included, for the same reason <see cref="Dispute"/> is: a
    /// refusal that outlives the turn that caused it tells the learner the coach is still hedging
    /// an answer it has since given plainly.
    /// </para>
    /// <para>
    /// W9 uses this for grounding refusals only, which carry a reason and at most a destination.
    /// The hint ladder, alternatives and shorter-session offer on the same DTO belong to W7's
    /// boundary refusals and are empty here — the server's projection says so, and
    /// <c>CoachLimitationWiringContractTests</c> holds it to that.
    /// </para>
    /// </remarks>
    public CoachLimitationDto? Limitation { get; private set; }

    /// <summary>
    /// True when the last turn was withheld rather than answered.
    /// </summary>
    /// <remarks>
    /// A grounding refusal is not an error: nothing failed, the coach declined to assert something
    /// it could not check. So the workspace stays out of <c>Failed</c> and does not raise
    /// <c>AlertKey</c>. It must not simply fall back to <c>Ready</c> in silence either — that is a
    /// state change the learner has to be told about — so the turn sets a polite announcement and
    /// this flag, which is what the pane keys the refusal region off.
    /// </remarks>
    public bool HasGroundingRefusal => Limitation is not null;

    /// <summary>
    /// What the grounding layer did to the answer the learner is reading, when it ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaced from every turn and every session read, null and <c>None</c> included, for the same
    /// reason the refusal is: a disclosure that outlives its answer describes a different answer.
    /// </para>
    /// <para>
    /// Null and <c>None</c> are kept apart on the wire — "not checked" versus "checked and clean" —
    /// and both render nothing, so the distinction survives for a future surface without inventing
    /// one now.
    /// </para>
    /// </remarks>
    public CoachRepairDisclosure? RepairDisclosure { get; private set; }

    /// <summary>
    /// Whether the turn that produced <see cref="RepairDisclosure"/> read anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turn-scoped, and set on the same unconditional path as <see cref="RepairDisclosure"/> so
    /// the two can never describe different turns. It is deliberately <em>not</em>
    /// <c>Evidence.Count &gt; 0</c>: the workspace evidence list is sticky for ordinary turns —
    /// a turn that cites nothing leaves the previous turn's rows standing, because the learner may
    /// still be reading them — so reading it here let a no-evidence turn inherit an older turn's
    /// rows and promise the learner evidence that belonged to a different question.
    /// </para>
    /// <para>
    /// The sticky behaviour itself is correct and stays. This is a second, narrower fact recorded
    /// beside it, not a replacement for it.
    /// </para>
    /// <para>
    /// This is the announcement's copy of the claim, and it is squared with the entry the
    /// disclosure lands on rather than left to be derived twice. The attach paths write it back
    /// from what that entry actually renders, so a learner who reads the note and a learner who
    /// hears it are promised the same thing. A restore is the case that made this necessary: the
    /// ledger rebuilds a thread with no per-turn evidence on it, and a flag left standing from the
    /// session read or from the live turn before the reload was a promise the rebuilt thread could
    /// not keep.
    /// </para>
    /// </remarks>
    public bool RepairEvidenceOnScreen { get; private set; }

    /// <summary>
    /// The disclosure the learner should actually be shown, after the refusal takes precedence.
    /// </summary>
    /// <remarks>
    /// A refused turn discloses nothing: there is no answer to have altered, so a repair notice
    /// beside a refusal would be describing something the learner never received. The server sends
    /// null on a refused turn, and this makes the client independently unable to show one anyway.
    /// It is also what the attach path reads, so a refused turn's message never carries one.
    /// </remarks>
    public CoachRepairDisclosure? VisibleRepairDisclosure =>
        Limitation is not null ? null : RepairDisclosure;

    public IReadOnlyList<CoachRevisionDto> Revisions => _revisions;

    public CoachConstraintSetDto? ActiveConstraints { get; private set; }

    public CoachPlanStateDto? PlanState { get; private set; }

    public PendingCoachSuggestionDto? PendingSuggestion { get; private set; }

    /// <summary>
    /// A fact Sam proposed remembering on this turn, waiting for the learner to decide.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="PendingSuggestion"/>. Accepting a plan change and
    /// agreeing to be remembered are different consents with different consequences, and folding
    /// them into one control would let a learner who wanted today's plan changed also, silently,
    /// agree to be remembered indefinitely.
    /// </remarks>
    public CoachMemoryFactDto? PendingMemoryCandidate { get; private set; }

    /// <summary>The turn the memory candidate belongs to, so it renders inside that exchange.</summary>
    public long? PendingMemoryTurn { get; private set; }

    /// <summary>Dismisses the memory candidate once the learner has decided about it.</summary>
    public void ClearMemoryCandidate()
    {
        if (PendingMemoryCandidate is null)
        {
            return;
        }

        PendingMemoryCandidate = null;
        PendingMemoryTurn = null;
        Notify();
    }

    public CoachStopReason LastStopReason { get; private set; } = CoachStopReason.Completed;

    public int ClarificationsRemaining { get; private set; } = CoachConstraintLimits.MaxClarificationsPerSession;

    public DateTime? ExpiresAtUtc { get; private set; }

    /// <summary>
    /// True when an existing session was resumed but no visible conversation survived — the usual
    /// case after a page reload, because the server holds no plaintext transcript. The UI shows a
    /// plain resumed-session summary instead of pretending the conversation was empty.
    /// </summary>
    public bool IsResumedWithoutHistory { get; private set; }

    /// <summary>
    /// True once a plan-resource lookup has been attempted for this session, so the canvas asks
    /// the learner's own plan at most once per session rather than on every canvas toggle.
    /// </summary>
    public bool PlanResourceTitlesLoaded { get; private set; }

    /// <summary>
    /// True when this circuit holds a session the learner can pick straight back up.
    /// </summary>
    /// <remarks>
    /// Deliberately local. Closing the workspace preserves the session, so the Dashboard entry
    /// can flip to "Resume coach" immediately without a round trip — the alternative was a
    /// stale availability snapshot taken before the session existed, which left the entry
    /// reading "Ask the coach" until the learner reloaded the page.
    /// </remarks>
    public bool HasResumableSession => SessionId is not null && !CoachStateMachine.IsTerminal(State);

    /// <summary>Latest applied receipt, the only one that may expose Undo. There is no redo in v1.</summary>
    public CoachChangeReceiptDto? LatestReceipt => _receipts.Count > 0 ? _receipts[^1] : null;

    // ---------------------------------------------------------------- composer

    private string _draft = string.Empty;

    /// <summary>Composer draft. Setting a too-long value moves the machine to InputTooLong.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_draft, next, StringComparison.Ordinal))
            {
                return;
            }

            _draft = next;

            if (IsDraftTooLong && State != CoachUiState.InputTooLong)
            {
                State = CoachUiState.InputTooLong;
            }
            else if (!IsDraftTooLong && State == CoachUiState.InputTooLong)
            {
                State = CoachUiState.Ready;
            }

            Notify();
        }
    }

    public int DraftLength => _draft.Length;

    public int MaxDraftLength => CoachConstraintLimits.MaxTurnTextLength;

    public bool IsDraftTooLong => _draft.Length > CoachConstraintLimits.MaxTurnTextLength;

    // ---------------------------------------------------------------- canvas

    /// <summary>Canvas visibility. Closed at open; auto-opens once per new suggestion or revision.</summary>
    public bool IsCanvasOpen { get; private set; }

    /// <summary>Count of plan changes that landed while the canvas was closed. Drives the Plan badge.</summary>
    public int PlanBadgeCount { get; private set; }

    private string? _lastAutoOpenKey;

    // ---------------------------------------------------------------- a11y

    /// <summary>
    /// Resource key for the single polite live region. Never set at the same time as
    /// <see cref="AlertKey"/>. The key is localized by the rendering component, so this service
    /// stays free of culture state.
    /// </summary>
    public string? PoliteAnnouncementKey { get; private set; }

    /// <summary>
    /// Resource key describing the active failure. The visible failure card in CoachStateNotice
    /// is the single role="alert" container and renders its own copy; this property records which
    /// failure is active and guarantees the polite region stays silent for it.
    /// </summary>
    public string? AlertKey { get; private set; }

    /// <summary>DOM id the UI should focus after the next render, consumed exactly once.</summary>
    public string? PendingFocusElementId { get; private set; }

    /// <summary>DOM id of the control that opened the workspace, so focus can be restored on close.</summary>
    public string? InvokerElementId { get; private set; }

    // ---------------------------------------------------------------- run

    /// <summary>When the in-flight turn started, for the still-working and Stop affordances.</summary>
    public DateTimeOffset? RunStartedAt { get; private set; }

    /// <summary>True when the learner stopped a run; the server result is discarded on arrival.</summary>
    public bool LastRunAbandoned { get; private set; }

    public bool IsBusy => CoachStateMachine.IsBusy(State);

    public bool CanSubmit => CoachStateMachine.CanSubmit(State) && !IsDraftTooLong;

    // ---------------------------------------------------------------- destructive actions

    /// <summary>
    /// The destructive action awaiting explicit confirmation, if any. Both compositions read this
    /// one value so the wide overlay and the full-screen route can never disagree about whether a
    /// confirmation is showing.
    /// </summary>
    public CoachConfirmation PendingConfirmation { get; private set; }

    /// <summary>
    /// Asks for confirmation before ending the session. Deleting coach history is irreversible,
    /// so the DELETE never fires straight from a menu item.
    /// </summary>
    public void RequestEndSessionConfirmation()
    {
        if (SessionId is null || PendingConfirmation == CoachConfirmation.EndSession)
        {
            return;
        }

        PendingConfirmation = CoachConfirmation.EndSession;
        Notify();
    }

    /// <summary>Dismisses the confirmation without performing the action.</summary>
    public void DismissConfirmation()
    {
        if (PendingConfirmation == CoachConfirmation.None)
        {
            return;
        }

        PendingConfirmation = CoachConfirmation.None;
        Notify();
    }

    /// <summary>
    /// Performs the confirmed destructive action. Does nothing when no confirmation is pending,
    /// so a stray call cannot delete anything.
    /// </summary>
    public async Task ConfirmPendingAsync(CancellationToken cancellationToken = default)
    {
        if (PendingConfirmation != CoachConfirmation.EndSession)
        {
            return;
        }

        PendingConfirmation = CoachConfirmation.None;
        await DeleteSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    // ================================================================ lifecycle

    /// <summary>
    /// Reads availability. Returns a disabled response when the feature is off, the learner is
    /// outside the cohort, or the API is unreachable, so the entry point simply stays hidden.
    /// </summary>
    public async Task<CoachAvailabilityResponse> RefreshAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Availability = await _client.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Availability must never break the Dashboard. No entry point is the safe answer.
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = false,
                State = CoachAvailabilityState.Disabled
            };
        }

        CanEditPlan = Availability.CanEditPlan;

        // Publish the per-feature flags so the two directories gate on the same answer this read
        // produced, rather than each spending another availability call to learn it.
        _flags?.Apply(Availability);

        Notify();
        return Availability;
    }

    /// <summary>Records the chosen composition. Resizing does not re-run this: the overlay degrades in place.</summary>
    public void SetPresentation(CoachPresentation presentation)
    {
        if (Presentation == presentation)
        {
            return;
        }

        Presentation = presentation;
        Notify();
    }

    public void SetPane(CoachPane pane)
    {
        if (Pane == pane)
        {
            return;
        }

        Pane = pane;
        if (pane == CoachPane.Plan)
        {
            PlanBadgeCount = 0;
        }

        Notify();
    }

    /// <summary>
    /// Opens (or resumes) the workspace. Safe to call again for the same session id — it will not
    /// restart a session that is already loaded.
    /// </summary>
    public async Task OpenAsync(
        CoachPresentation presentation,
        string? sessionId = null,
        string? invokerElementId = null,
        CancellationToken cancellationToken = default)
    {
        Presentation = presentation;
        InvokerElementId = invokerElementId ?? InvokerElementId;

        // Durable history changes what opening means: the thing being opened is a conversation
        // that outlives the 24-hour checkpoint, and every turn afterwards has to go to the
        // conversation routes. Deciding that here rather than at each entry point is what stops a
        // learner from starting in one mode and sending in the other, which is how a durable
        // conversation ends up with a row and no messages.
        if (_directory is not null)
        {
            // Re-entering without an id means "make sure the workspace is open", which is what a
            // re-render or a second navigation does. Legacy read that as the session already in
            // hand; durable mode has to read it as the conversation already in hand, or every
            // such call would quietly start another thread. Starting a genuinely new one goes
            // through Reset first, which clears this.
            var durableId = sessionId ?? (IsOpen ? ConversationId : null);

            if (await OpenConversationAsync(
                    presentation,
                    durableId,
                    invokerElementId,
                    cancellationToken,
                    createWhenMissing: true).ConfigureAwait(false))
            {
                return;
            }
        }

        if (IsOpen && SessionId is not null && (sessionId is null || sessionId == SessionId))
        {
            Notify();
            return;
        }

        IsOpen = true;
        _resumeRequested = sessionId is not null;

        // A deep link opens the workspace without the Dashboard entry ever running, so
        // availability — and with it CanEditPlan — would otherwise still be at its default and
        // the plan affordances would appear for a learner who has no plan.
        if (Availability is null)
        {
            await RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }

        State = sessionId is null ? CoachUiState.Opening : CoachUiState.Resuming;
        ClearTransientAnnouncements();
        Notify();

        try
        {
            CoachSessionResponse? session = null;

            if (sessionId is not null)
            {
                session = await _client.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }

            if (session is null)
            {
                // The resume target is gone, so this is a brand-new conversation: an empty
                // message list is expected here and needs no explanation.
                _resumeRequested = false;
                State = CoachUiState.LoadingEvidence;
                Notify();
                session = await _client.StartSessionAsync(new StartCoachSessionRequest { Resume = true }, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                PoliteAnnouncementKey = "Coach_AnnounceResumed";
            }

            ApplySession(session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException ex)
        {
            State = CoachStateMachine.FromProblem(ex);
            Notify();
        }
        catch (HttpRequestException)
        {
            State = CoachUiState.Offline;
            Notify();
        }
    }

    /// <summary>Closes the workspace without ending the session. Applied changes are kept.</summary>
    /// <remarks>
    /// The one-use confirmation is dropped here. Closing is a deliberate exit from the step that
    /// minted it — the prompt the learner was answering is no longer on screen — and a value that
    /// outlived its prompt would be a credential in hand for a decision nobody is being shown.
    /// Everything else about the session is preserved so it can be resumed.
    /// </remarks>
    public void Close()
    {
        IsOpen = false;
        PendingConfirmation = CoachConfirmation.None;
        DiscardConfirmation();
        // Closing preserves the session for resume; it only drops the local run. If a run was in
        // flight the server's own timeout releases the slot.
        AbandonRun();
        ClearTransientAnnouncements();

        // Set AFTER clearing, so the focus target survives. The workspace is going away, so
        // focus must return to the control that opened it rather than falling to <body>.
        PendingFocusElementId = InvokerElementId;
        Notify();
    }

    /// <summary>Resets everything, including the session identity. Used after deletion or expiry.</summary>
    public void Reset()
    {
        AbandonRun();
        _messages.Clear();
        _timeline.Clear();
        PendingSuggestionTurn = null;
        _receipts.Clear();
        _evidence.Clear();
        _revisions.Clear();
        SessionId = null;
        ActiveConstraints = null;
        PlanState = null;
        PendingSuggestion = null;
        PendingMemoryCandidate = null;
        PendingMemoryTurn = null;
        ExpiresAtUtc = null;
        _draft = string.Empty;
        _lastAutoOpenKey = null;
        _lastTurnRequest = null;
        _resumeRequested = false;
        _answersByMessageId.Clear();
        LatestAnswer = null;
        _planResourceTitles = null;
        PlanResourceTitlesLoaded = false;
        IsResumedWithoutHistory = false;
        IsCanvasOpen = false;
        PlanBadgeCount = 0;
        Pane = CoachPane.Coach;
        LastRunAbandoned = false;
        LastStopReason = CoachStopReason.Completed;
        ClarificationsRemaining = CoachConstraintLimits.MaxClarificationsPerSession;
        PendingConfirmation = CoachConfirmation.None;
        State = CoachUiState.Opening;
        IsOpen = false;
        ResetDurable();
        ResetReports();
        ClearTransientAnnouncements();
        Notify();
    }

    /// <summary>
    /// Clears everything <see cref="Reset"/> clears, plus everything that is an answer <em>about a
    /// learner</em>. Used when the signed-in account changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Reset"/> is "start a new conversation" and deliberately keeps what the server
    /// already told us about this learner — availability, whether they have a plan the coach may
    /// edit, which composition is on screen, which control to return focus to. Every one of those
    /// is wrong the moment somebody else signs in, and two of them are load-bearing: availability
    /// decides whether approval controls may be drawn at all, and <see cref="CanEditPlan"/>
    /// decides whether plan affordances appear for a learner who may have no plan.
    /// </para>
    /// <para>
    /// So this is the account-boundary clear and <see cref="Reset"/> is the conversation clear.
    /// Keeping them apart matters: collapsing them would make "new conversation" throw away an
    /// availability answer it just paid for, and collapsing them the other way is the leak.
    /// </para>
    /// </remarks>
    public void ResetForAccountBoundary()
    {
        Reset();

        Availability = null;
        CanEditPlan = true;
        Presentation = CoachPresentation.Unknown;
        InvokerElementId = null;
        PendingFocusElementId = null;
        _lastInitiator = CoachInitiator.System;
        _turnSequence = 0;
        _artifactSequence = 0;

        Notify();
    }

    // ================================================================ canvas

    public void ToggleCanvas()
    {
        IsCanvasOpen = !IsCanvasOpen;
        if (IsCanvasOpen)
        {
            PlanBadgeCount = 0;
        }

        Notify();
    }

    public void OpenCanvas()
    {
        if (IsCanvasOpen)
        {
            return;
        }

        IsCanvasOpen = true;
        PlanBadgeCount = 0;
        Notify();
    }

    public void CloseCanvas()
    {
        if (!IsCanvasOpen)
        {
            return;
        }

        IsCanvasOpen = false;
        Notify();
    }

    // ================================================================ turns

    /// <summary>Submits the composer draft as a free-text turn.</summary>
    /// <remarks>
    /// <para>
    /// The learner's own words are added to the conversation here, before the request is awaited,
    /// so the question is on screen the instant it is sent. The server never echoes learner text
    /// back — it keeps no plaintext transcript — so if the client did not keep it, a learner would
    /// watch their own question vanish and only the answer appear.
    /// </para>
    /// <para>
    /// Typed turns QUEUE behind an in-flight run rather than being dropped by the busy guard.
    /// Dropping them showed the learner a question that had already been cleared from the
    /// composer and would never be sent — the question looked asked and was silently lost.
    /// </para>
    /// </remarks>
    public async Task SendDraftAsync(CancellationToken cancellationToken = default)
    {
        if (IsDraftTooLong || string.IsNullOrWhiteSpace(_draft))
        {
            return;
        }

        // Captured before the draft is cleared: the composer must not empty until the text is
        // safely part of the conversation.
        var text = _draft;

        var request = new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text,
            PendingSuggestionId = PendingSuggestion?.SuggestionId,
            ExpectedPlanVersion = PlanState?.PlanVersion,
            ClientTurnId = Guid.NewGuid().ToString("N")
        };

        // The text is captured above, so clearing the box now cannot lose it. Cleared before the
        // append so the single notification that follows renders one consistent state: empty
        // composer, question on screen.
        _draft = string.Empty;

        // The turn is claimed now, in submission order, so a slow first response still lands
        // beside its own question rather than after a later one.
        var turn = AppendLearnerMessage(text, request.ClientTurnId);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunTurnAsync(request, CoachInitiator.Composer, cancellationToken, turn: turn)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Adds the learner's own turn to the visible conversation, idempotently, and claims the
    /// turn sequence every artifact of this exchange will share.
    /// </summary>
    /// <remarks>
    /// Local only. This is display state for the active circuit; it is never sent anywhere and is
    /// gone on reload, which is what keeps the privacy design intact.
    /// </remarks>
    private long AppendLearnerMessage(string text, string clientTurnId)
    {
        var messageId = LocalMessageIdPrefix + clientTurnId;

        var existing = _timeline.FirstOrDefault(e =>
            e.Kind == CoachTimelineKind.LearnerMessage
            && string.Equals(e.Message?.MessageId, messageId, StringComparison.Ordinal));

        if (existing is not null)
        {
            // Defence in depth. No public path reaches this today — a retry replays the request
            // without coming back through here, and every fresh send mints a new ClientTurnId —
            // but appending the same turn twice is the one mistake this method must never make.
            return existing.TurnSequence;
        }

        var message = new CoachMessageDto
        {
            MessageId = messageId,
            Role = CoachMessageRole.Learner,
            Kind = CoachMessageKind.Text,
            Text = text,
            CreatedAtUtc = DateTime.UtcNow
        };

        _messages.Add(message);

        var turn = ++_turnSequence;
        _timeline.Add(new CoachTimelineEntry
        {
            TurnSequence = turn,
            Sequence = ++_artifactSequence,
            Kind = CoachTimelineKind.LearnerMessage,
            Timestamp = DateTimeOffset.Now,
            Message = message
        });

        Notify();
        return turn;
    }

    /// <summary>Places a server artifact inside the turn that produced it.</summary>
    /// <remarks>
    /// A turn of zero means the artifact has no learner question in front of it — a resumed
    /// session, or a tapped control. It still claims its own turn so it keeps its place in the
    /// stream instead of collapsing into whichever exchange happens to be last.
    /// </remarks>
    private void AppendTimelineEntry(
        long turn,
        CoachTimelineKind kind,
        DateTimeOffset timestamp,
        CoachMessageDto? message = null,
        CoachAnswerDto? answer = null,
        CoachChangeReceiptDto? receipt = null,
        string? noticeReasonCode = null) =>
        _timeline.Add(new CoachTimelineEntry
        {
            TurnSequence = turn == 0 ? ++_turnSequence : turn,
            Sequence = ++_artifactSequence,
            Kind = kind,
            Timestamp = timestamp,
            Message = message,
            Answer = answer,
            Receipt = receipt,
            NoticeReasonCode = noticeReasonCode
        });

    /// <summary>Submits a tapped chip.</summary>
    public Task SendChipAsync(string chipId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chipId);

        var request = new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Chip,
            ChipId = chipId,
            PendingSuggestionId = PendingSuggestion?.SuggestionId,
            ExpectedPlanVersion = PlanState?.PlanVersion,
            ClientTurnId = Guid.NewGuid().ToString("N")
        };

        return RunTurnAsync(request, CoachInitiator.Chip, cancellationToken);
    }

    /// <summary>
    /// Submits a structured direct constraint change. A direct request is intent: the server
    /// applies it immediately after deterministic validation, so the UI expects a receipt back and
    /// never renders an optimistic plan.
    /// </summary>
    public Task ApplyConstraintAsync(CoachConstraintDeltaDto delta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var request = new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.ConstraintAction,
            ConstraintAction = delta,
            ExpectedPlanVersion = PlanState?.PlanVersion,
            ClientTurnId = Guid.NewGuid().ToString("N")
        };

        return RunTurnAsync(request, CoachInitiator.Chip, cancellationToken, applying: true);
    }

    /// <summary>Deterministic tapped acceptance of the pending suggestion.</summary>
    public async Task AcceptSuggestionAsync(CancellationToken cancellationToken = default)
    {
        if (SessionId is null || PendingSuggestion is null)
        {
            return;
        }

        var sessionId = SessionId;
        var suggestionId = PendingSuggestion.SuggestionId;
        var request = new CoachSuggestionDecisionRequest
        {
            ExpectedPlanVersion = PlanState?.PlanVersion,
            ClientTurnId = Guid.NewGuid().ToString("N")
        };

        await ExecuteAsync(
            CoachUiState.Applying,
            CoachInitiator.SuggestionButton,
            async token => await _client.AcceptSuggestionAsync(sessionId, suggestionId, request, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deterministic rejection. Never writes; the card resolves to a visible "not added" note.</summary>
    public async Task RejectSuggestionAsync(CancellationToken cancellationToken = default)
    {
        if (SessionId is null || PendingSuggestion is null)
        {
            return;
        }

        var sessionId = SessionId;
        var suggestionId = PendingSuggestion.SuggestionId;
        var request = new CoachSuggestionDecisionRequest
        {
            ExpectedPlanVersion = PlanState?.PlanVersion,
            ClientTurnId = Guid.NewGuid().ToString("N")
        };

        await ExecuteAsync(
            CoachUiState.Running,
            CoachInitiator.SuggestionButton,
            async token => await _client.RejectSuggestionAsync(sessionId, suggestionId, request, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Undoes the latest applied revision. Only the latest revision is undoable; there is no redo.</summary>
    public async Task UndoAsync(string? revisionId = null, CancellationToken cancellationToken = default)
    {
        if (SessionId is null)
        {
            return;
        }

        var sessionId = SessionId;
        var request = new CoachUndoRequest
        {
            RevisionId = revisionId ?? LatestReceipt?.Revision.RevisionId,
            ExpectedPlanVersion = PlanState?.PlanVersion,
            ClientTurnId = Guid.NewGuid().ToString("N")
        };

        await ExecuteAsync(
            CoachUiState.Undoing,
            CoachInitiator.UndoButton,
            async token => await _client.UndoAsync(sessionId, request, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-submits the last turn after a failure.</summary>
    public Task RetryLastAsync(CancellationToken cancellationToken = default)
    {
        if (_lastTurnRequest is null)
        {
            return Task.CompletedTask;
        }

        return RunTurnAsync(_lastTurnRequest, _lastInitiator, cancellationToken);
    }

    /// <summary>
    /// Dismisses a failure and returns to input without retrying. Today's Plan is unchanged.
    /// </summary>
    public void KeepCurrentPlan()
    {
        if (State is CoachUiState.Failed or CoachUiState.Incomplete or CoachUiState.PlanChangedElsewhere)
        {
            State = CoachUiState.Ready;
            AlertKey = null;
            Notify();
        }
    }

    /// <summary>
    /// Stops the in-flight run. Tells the server first so the run stops holding the learner's
    /// single concurrency slot, then abandons the run locally: the client stops rendering it and
    /// discards the server result if it still arrives.
    /// </summary>
    /// <remarks>
    /// The server call is best effort and time-boxed. Stop must always release the UI, so a slow
    /// or failing cancel endpoint never leaves the learner stuck watching a spinner.
    /// </remarks>
    public async Task CancelRunAsync(CancellationToken cancellationToken = default)
    {
        if (_runCts is null)
        {
            return;
        }

        var sessionId = SessionId;

        // Durable mode cancels the operation, not the checkpoint. The record of the cancel lives
        // with the turn, so it is honored even when the run is executing on another replica and
        // this client's request never reaches the machine doing the work.
        if (IsDurableHistoryEnabled && ConversationId is { } conversationId && PendingOperationId is { } operationId)
        {
            try
            {
                using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                operationTimeout.CancelAfter(ServerCancelTimeout);
                var cancelled = await _client
                    .CancelConversationTurnAsync(conversationId, operationId, operationTimeout.Token)
                    .ConfigureAwait(false);

                if (cancelled is not null)
                {
                    LastOperationState = cancelled.State;
                    IsCancelRequested = cancelled.CancelRequested;
                }
            }
            catch (Exception)
            {
                // Cancel is advisory here too. Stop must always release the UI.
            }

            AbandonRun();
            return;
        }

        if (sessionId is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ServerCancelTimeout);
                await _client.CancelSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancel is advisory. The run is abandoned locally regardless, and the server's
                // own timeout will release the slot if this call never landed.
            }
        }

        AbandonRun();
    }

    /// <summary>
    /// Drops the in-flight run locally without contacting the server. Used by Stop after the
    /// server call, and by Close/Reset/Dispose where the caller is tearing the workspace down.
    /// </summary>
    private void AbandonRun()
    {
        if (_runCts is null)
        {
            return;
        }

        LastRunAbandoned = true;
        LastStopReason = CoachStopReason.Cancelled;
        try
        {
            _runCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed.
        }

        _runCts.Dispose();
        _runCts = null;
        RunStartedAt = null;

        if (IsBusy)
        {
            State = CoachUiState.Ready;
        }

        // The stop must be visibly resolved, not just silently return to Ready.
        AlertKey = null;
        PoliteAnnouncementKey = "Coach_Stopped";
        Notify();
    }

    /// <summary>Deletes coach history for this session. Today's Plan and progress are untouched.</summary>
    public async Task DeleteSessionAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = SessionId;
        if (sessionId is null)
        {
            return;
        }

        try
        {
            await _client.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException ex)
        {
            State = CoachStateMachine.FromProblem(ex);
            Notify();
            return;
        }
        catch (HttpRequestException)
        {
            State = CoachUiState.Offline;
            Notify();
            return;
        }

        Reset();
        State = CoachUiState.SessionDeleted;
        Notify();
    }

    // ================================================================ plan resource titles

    /// <summary>
    /// Supplies plan-item resource titles from the learner's OWN current plan.
    /// </summary>
    /// <remarks>
    /// The server deliberately sends <c>ResourceTitle = null</c> on every coach plan item: joining
    /// resources server-side is a read that could surface embargoed content, so the projection
    /// refuses to do it. The client may fill the gap only from plan data it already holds for this
    /// learner. When that data is absent, the title stays absent — it is never invented, and never
    /// guessed from the activity type.
    /// </remarks>
    /// <param name="titles">Plan item id to resource title. Null or empty records "no data".</param>
    public void SetPlanResourceTitles(IReadOnlyDictionary<string, string>? titles)
    {
        PlanResourceTitlesLoaded = true;

        _planResourceTitles = titles is null || titles.Count == 0
            ? null
            : titles
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        Notify();
    }

    /// <summary>
    /// Resource title for a coach plan item, or null when the learner's own plan has no matching
    /// item. Callers must render nothing when this is null rather than substituting a placeholder.
    /// </summary>
    public string? ResourceTitleFor(string? planItemId)
    {
        if (string.IsNullOrWhiteSpace(planItemId) || _planResourceTitles is null)
        {
            return null;
        }

        return _planResourceTitles.TryGetValue(planItemId, out var title) ? title : null;
    }

    // ================================================================ a11y plumbing

    /// <summary>
    /// Asks for focus to land on one element after the next render.
    /// </summary>
    /// <remarks>
    /// Routed through the workspace rather than called directly by a component so it lands
    /// <em>after</em> the render that creates the target. A control that opens a region and
    /// focuses it in the same handler focuses an element that does not exist yet, which silently
    /// does nothing — and "silently does nothing" is exactly the class of defect this channel is
    /// being used to close.
    /// </remarks>
    public void RequestFocus(string elementId)
    {
        if (string.IsNullOrEmpty(elementId))
        {
            return;
        }

        PendingFocusElementId = elementId;
        Notify();
    }

    /// <summary>Reads and clears the pending focus target.</summary>
    public string? ConsumePendingFocus()
    {
        var id = PendingFocusElementId;
        PendingFocusElementId = null;
        return id;
    }

    /// <summary>Clears the polite announcement after it has been read.</summary>
    public void ClearAnnouncement()
    {
        if (PoliteAnnouncementKey is null)
        {
            return;
        }

        PoliteAnnouncementKey = null;
        Notify();
    }

    // ================================================================ internals

    private async Task RunTurnAsync(
        CoachTurnRequest request,
        CoachInitiator initiator,
        CancellationToken cancellationToken,
        bool applying = false,
        long turn = 0)
    {
        // Durable mode runs against the conversation, not the checkpoint: the conversation is what
        // survives the 24-hour session and what a poll can find again after a lost response.
        if (IsDurableHistoryEnabled && ConversationId is { } durableConversationId)
        {
            _lastTurnRequest = request;
            _lastInitiator = initiator;

            await ExecuteAsync(
                applying ? CoachUiState.Applying : CoachUiState.Running,
                initiator,
                token => SubmitDurableTurnAsync(durableConversationId, request, turn, token),
                cancellationToken,
                turn).ConfigureAwait(false);
            return;
        }

        if (SessionId is null)
        {
            return;
        }

        var sessionId = SessionId;
        _lastTurnRequest = request;
        _lastInitiator = initiator;

        await ExecuteAsync(
            applying ? CoachUiState.Applying : CoachUiState.Running,
            initiator,
            async token => await _client.SubmitTurnAsync(sessionId, request, token).ConfigureAwait(false),
            cancellationToken,
            turn).ConfigureAwait(false);
    }

    /// <summary>
    /// One run at a time. Every constraint affordance is disabled while a run is in flight; the UI
    /// never queues turns.
    /// </summary>
    private async Task ExecuteAsync(
        CoachUiState runningState,
        CoachInitiator initiator,
        Func<CancellationToken, Task<CoachTurnResponse?>> operation,
        CancellationToken cancellationToken,
        long turn = 0)
    {
        if (IsBusy)
        {
            return;
        }

        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _runCts.Token;
        var runId = _runCts;

        LastRunAbandoned = false;
        State = runningState;
        RunStartedAt = DateTimeOffset.UtcNow;
        ClearTransientAnnouncements();
        Notify();

        CoachTurnResponse? response;
        try
        {
            response = await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Abandoned by the learner (or the component went away). CancelRun already set state.
            return;
        }
        catch (CoachDurableTurnFailedException)
        {
            if (!ReferenceEquals(runId, _runCts))
            {
                return;
            }

            // The server answered, and its answer was that the turn failed. The learner's message
            // is already in the ledger carrying a failed status, so this is a state they can act
            // on — retry — rather than an error that loses what they typed.
            FinishRun();
            State = CoachUiState.Failed;
            ApplyOutcomePolicy(initiator, succeeded: false);
            Notify();
            return;
        }
        catch (CoachApiException ex)
        {
            if (!ReferenceEquals(runId, _runCts))
            {
                return;
            }

            FinishRun();

            // The server no longer knows this suggestion, so the client's copy of the plan is
            // stale in ways the exception does not describe. Clearing the two fields it names
            // would leave plan state, constraints, revisions, receipts and the transcript at
            // values the server has already moved past. Read the authoritative state back
            // instead, in place, so the card and everything around it agree (Phase 2 defect #4).
            if (ex.ProblemType == CoachProblemTypes.SuggestionNotFound
                && !await RefreshAuthoritativeStateAsync(cancellationToken).ConfigureAwait(false))
            {
                // The read back said the session itself is gone or not ours, and has already put
                // the workspace into that state. "Carry on, the suggestion resolved" would be a
                // less true answer than the one already on screen.
                ApplyOutcomePolicy(initiator, succeeded: false);
                Notify();
                return;
            }

            State = CoachStateMachine.FromProblem(ex);
            ApplyOutcomePolicy(initiator, succeeded: false);
            Notify();
            return;
        }
        catch (HttpRequestException)
        {
            if (!ReferenceEquals(runId, _runCts))
            {
                return;
            }

            FinishRun();
            State = CoachUiState.Offline;
            ApplyOutcomePolicy(initiator, succeeded: false);
            Notify();
            return;
        }

        // A stopped run's late result is discarded.
        if (!ReferenceEquals(runId, _runCts))
        {
            return;
        }

        FinishRun();

        if (response is null)
        {
            // A durable turn that settled without a response body: the ledger already carries the
            // messages, and inventing a turn response to apply would be inventing state. That cuts
            // both ways — a result of null says nothing about the open suggestion either, and the
            // server is explicit that refusing a change never withdraws an offer the learner has
            // not answered. So the card is not cleared on a guess; the session is read back and
            // whatever it says stands (Phase 2 defect #3).
            if (!await RefreshAuthoritativeStateAsync(cancellationToken).ConfigureAwait(false))
            {
                ApplyOutcomePolicy(initiator, succeeded: false);
                Notify();
                return;
            }

            if (State is not (CoachUiState.Expired or CoachUiState.Limited or CoachUiState.Failed))
            {
                State = PendingSuggestion is null
                    ? CoachUiState.Ready
                    : CoachUiState.SuggestionPending;
            }

            ApplyOutcomePolicy(initiator, succeeded: true);
            Notify();
            return;
        }

        ApplyTurn(response, initiator, turn, DurableLedgerIsAuthoritative);
    }

    /// <summary>
    /// Attaches a turn's structured answer to the canonical message it belongs to.
    /// </summary>
    /// <remarks>
    /// The ledger row for Sam's reply does not always carry the structured answer, and the
    /// response body does not carry the canonical message id. Pairing the newest canonical coach
    /// message that has no answer yet is what keeps the blocks rendering in place instead of
    /// falling back to the duplicated plain text.
    /// </remarks>
    /// <summary>
    /// Attaches one turn's evidence to the message that made the claim it supports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last conversational message of the turn wins, because that is the one the learner is
    /// reading when they ask "how do you know". Which message that is, on a thread the ledger
    /// owns, is <see cref="FindTurnAnchorIndex"/>'s question and not this method's.
    /// </para>
    /// <para>
    /// Silently does nothing when the turn produced no message of its own — a resumed session
    /// reading state back, for instance. Evidence with no claim to sit under stays in the plan
    /// canvas, where it is attributed to the plan rather than to a sentence nobody said.
    /// </para>
    /// </remarks>
    private void AttachEvidenceToTurn(long turnSequence, IReadOnlyList<CoachEvidenceDto> evidence)
    {
        if (evidence.Count == 0)
        {
            return;
        }

        if (FindTurnAnchorIndex(turnSequence) is var index && index < 0)
        {
            return;
        }

        _timeline[index] = _timeline[index].WithEvidence(evidence);
    }

    /// <summary>
    /// Finds the message on screen that the turn that just ran hung its artifacts on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not a turn-counter comparison.</b> A locally appended entry wears the
    /// counter it was appended with, so comparing <see cref="CoachTimelineEntry.TurnSequence"/> is
    /// exact in session-only mode. A durable entry does not wear it. <c>ReindexDurable</c>
    /// renumbers every entry to its position in the read order, because arrival order is the wrong
    /// order the moment an older page is fetched, so the canonical row for the turn that just ran
    /// carries an ordinal and never the client's counter. Comparing against the counter therefore
    /// matched nothing at all in ledger-authoritative mode, and a live durable turn rendered
    /// neither the evidence it cited nor the note about what was done to its answer until a
    /// transcript reload rebuilt the thread from scratch.
    /// </para>
    /// <para>
    /// The row's own provenance answers it instead. A merge that ran while a turn was in flight is
    /// the one moment the client knows which turn a canonical row came from, and it records that
    /// on the entry as <see cref="CoachTimelineEntry.ArrivedOnTurn"/> — a stamp renumbering does
    /// not touch, because where an entry is read and where it came from are different questions.
    /// A transcript load and a page of older history leave it null, so neither can be mistaken for
    /// a turn. List position alone is never enough: on a turn that added no answer of its own,
    /// "newest" is the previous answer, and hanging this turn's evidence on a question the learner
    /// already had answered is the stale-pointer defect one rung up.
    /// </para>
    /// <para>
    /// Deliberately the same three skips and the same turn boundary as the answer pairing, so the
    /// answer, its evidence and the note about it land on one message rather than three.
    /// </para>
    /// <returns>
    /// The index of the anchor, or -1 when the turn produced no message of its own. That is a real
    /// outcome, not a failure: a chip tap that only refreshed state, a refusal that produced a
    /// notice, a resumed session reading itself back. Nothing attaches, and the artifacts stay in
    /// the plan canvas where they are attributed to the plan rather than to a sentence nobody said.
    /// </returns>
    /// </remarks>
    private int FindTurnAnchorIndex(long turnSequence)
    {
        for (var i = _timeline.Count - 1; i >= 0; i--)
        {
            var entry = _timeline[i];

            // The turn boundary. The learner's question opens the exchange these artifacts belong
            // to, so the search stops there rather than walking back into an earlier one.
            if (entry.Kind == CoachTimelineKind.LearnerMessage)
            {
                return -1;
            }

            if (entry.Kind != CoachTimelineKind.CoachMessage)
            {
                continue;
            }

            // A notice is the server saying it did not act, a receipt is a record of a change and
            // a suggestion is a plan artifact. None of the three is what the learner asked, so
            // none of them may wear the answer's evidence or a note describing the answer.
            if (entry.Message is
                {
                    Kind: CoachMessageKind.Notice
                        or CoachMessageKind.Receipt
                        or CoachMessageKind.Suggestion
                })
            {
                continue;
            }

            if (entry.ServerSequence is not null)
            {
                // A canonical row. It belongs to this turn only if it arrived while this turn was
                // running - the ordinal it wears was reassigned by the merge and says nothing
                // about provenance. Anything else at this position is an earlier exchange, and
                // there is nothing of this turn's further back, so the search ends either way.
                // Taking it on position alone is how this turn's evidence would end up pinned to a
                // question the learner already had answered.
                return entry.ArrivedOnTurn == turnSequence ? i : -1;
            }

            if (entry.TurnSequence == turnSequence)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Attaches one turn's repair disclosure to the message that carries the answer it describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the same shape as <see cref="AttachEvidenceToTurn"/> and for the same reason:
    /// a note about an answer belongs under that answer. The disclosure used to render once at the
    /// head of the log from a workspace-wide field, which put "part of this answer was adjusted"
    /// several screens above the answer in question — and after the pane auto-scrolls to the
    /// newest message, off screen entirely.
    /// </para>
    /// <para>
    /// The two silent states never attach. <c>None</c> is checked-and-clean and null is
    /// not-checked; storing either would make every renderer re-derive that they say nothing.
    /// </para>
    /// <para>
    /// Callers pass <see cref="VisibleRepairDisclosure"/>, so a refused turn attaches nothing:
    /// there is no answer to describe. The anchor is resolved by
    /// <see cref="FindTurnAnchorIndex"/>, the same search the evidence used a moment earlier, so
    /// the note and the rows it may point at are on one message by construction.
    /// </para>
    /// <para>
    /// <b>The claim is clamped to what the entry actually renders.</b> On the live path the
    /// caller's flag and the entry agree by construction — evidence is attached to the same entry,
    /// through the same search, immediately before this runs — so the clamp changes nothing today.
    /// It is here because the two are separately derived, and the one failure mode worth ruling
    /// out permanently is a disclosure that promises evidence the message beside it does not
    /// carry. The workspace flag is squared with the same value so the copy and the announcement
    /// cannot diverge.
    /// </para>
    /// </remarks>
    private void AttachRepairDisclosureToTurn(
        long turnSequence,
        CoachRepairDisclosure? disclosure,
        bool evidenceOnScreen)
    {
        if (disclosure is not { } state || state == CoachRepairDisclosure.None)
        {
            return;
        }

        if (FindTurnAnchorIndex(turnSequence) is var index && index < 0)
        {
            return;
        }

        var entry = _timeline[index];
        var claimed = evidenceOnScreen && entry.Evidence.Count > 0;

        _timeline[index] = entry.WithRepairDisclosure(state, claimed);
        RepairEvidenceOnScreen = claimed;
    }

    /// <summary>
    /// Re-attaches a restored disclosure to the newest answer on screen, after a transcript load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session read restores the disclosure for the latest completed turn only, and it arrives
    /// before the transcript does — so at <c>ApplySession</c> time there is no message to attach it
    /// to. Running again once the ledger rows are on screen is what keeps a reloaded thread saying
    /// the same thing about its newest answer as it did before the reload.
    /// </para>
    /// <para>
    /// When the thread has no answer on screen — the ordinary non-durable resume, where the server
    /// keeps no plaintext transcript — nothing attaches and nothing renders. That is the honest
    /// outcome: a note about an answer the learner cannot see is a note about nothing, and the old
    /// pane-level banner rendered it anyway.
    /// </para>
    /// <para>
    /// <b>The evidence claim is read off the entry, never off the workspace.</b> This is the defect
    /// Jayne reproduced. The restored disclosure used to carry the workspace-level
    /// <see cref="RepairEvidenceOnScreen"/>, and that flag can be true on this path for two
    /// reasons that both lie. A session read may answer with an evidence list that belongs to no
    /// particular turn, and a live turn that really did read something leaves the flag standing
    /// while <c>ClearTranscript</c> throws away the entries that carried the rows. Either way the
    /// ledger rebuilds the thread without per-turn evidence — durable history has no member to
    /// carry it — so the note rendered "have a look at the evidence" over an answer with no
    /// evidence beside it, and marked itself <c>data-coach-repair-evidence="true"</c> while doing
    /// it.
    /// </para>
    /// <para>
    /// What the entry itself holds is the one answer that cannot be wrong, because it is the same
    /// list the renderer draws the rows from. It is empty today for every restored turn, which is
    /// the truthful reading rather than a cautious one, and it becomes true on its own the day a
    /// history row starts carrying the evidence for the answer it belongs to.
    /// </para>
    /// <para>
    /// The workspace flag is corrected to match, so the visible copy and the announcement can
    /// never promise different things about the same answer.
    /// </para>
    /// </remarks>
    private void AttachRestoredRepairDisclosure()
    {
        if (VisibleRepairDisclosure is not { } state || state == CoachRepairDisclosure.None)
        {
            return;
        }

        for (var i = _timeline.Count - 1; i >= 0; i--)
        {
            var entry = _timeline[i];

            if (entry.Kind != CoachTimelineKind.CoachMessage)
            {
                continue;
            }

            if (entry.Message is { Kind: CoachMessageKind.Notice or CoachMessageKind.Receipt })
            {
                continue;
            }

            // Beside this answer, on this entry, in the same list the pane renders from.
            var evidenceOnScreen = entry.Evidence.Count > 0;

            _timeline[i] = entry.WithRepairDisclosure(state, evidenceOnScreen);
            RepairEvidenceOnScreen = evidenceOnScreen;
            return;
        }

        // No answer to sit under, so the note renders nowhere. It promises nothing either: an
        // announcement pointing at evidence for an answer the learner cannot see is the same
        // failure one channel over.
        RepairEvidenceOnScreen = false;
    }

    private void PairAnswerWithCanonicalMessage(CoachAnswerDto? answer)
    {        if (answer is null)
        {
            return;
        }

        LatestAnswer = answer;

        for (var i = _timeline.Count - 1; i >= 0; i--)
        {
            var entry = _timeline[i];

            // The turn boundary. The learner's question opens the exchange this answer belongs to,
            // so the search stops there rather than walking back into a previous exchange and
            // pinning this answer onto an older message that never got one (Phase 2 defect #1).
            if (entry.Kind == CoachTimelineKind.LearnerMessage)
            {
                break;
            }

            if (entry.ServerSequence is not { } sequence)
            {
                continue;
            }

            // The same boundary expressed in the server's own ordering, which is what a chip tap
            // has instead of a learner message: everything at or below the mark this turn started
            // from was already on screen before the turn ran.
            if (sequence <= _answerPairingFloor)
            {
                break;
            }

            if (entry.Kind != CoachTimelineKind.CoachMessage
                || entry.Answer is not null
                || entry.Message is not { } message)
            {
                continue;
            }

            // A notice is the server saying it did not act, and a receipt or a suggestion card is
            // a plan artifact. None of them are what the learner asked, so none of them may wear
            // this answer — that is how the newest answer ended up inside an earlier refusal
            // bubble (Phase 2 E2E defect #1).
            if (message.Kind is CoachMessageKind.Notice
                or CoachMessageKind.Receipt
                or CoachMessageKind.Suggestion)
            {
                continue;
            }

            _answersByMessageId[message.MessageId] = answer;
            _timeline[i] = entry.WithAnswer(answer);
            return;
        }
    }

    /// <summary>
    /// Re-reads the server's own view of this session and conversation.
    /// </summary>
    /// <returns>
    /// False when the server answered that this session is gone or is not this learner's, in which
    /// case the workspace has already been moved to the state that names it and the caller must not
    /// overwrite that with an outcome of its own. True in every other case, including a read that
    /// could not be completed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Used where a turn ended without telling the client what changed: a suggestion the server no
    /// longer recognises, or an operation that settled with no result body. Guessing at those
    /// points is what produced Phase 2 defects #3 and #4 — a card cleared that the server had
    /// deliberately kept open, and plan state, constraints and revisions left standing at values
    /// the server had already moved past.
    /// </para>
    /// <para>
    /// <b>What each read actually refreshes.</b> The session read refreshes plan state,
    /// constraints, revisions, the pending suggestion, the clarification and run allowances and the
    /// session status. It does <i>not</i> refresh receipts: the session contract carries no receipt
    /// collection, so there is nothing to read them from. In durable mode the ledger read is what
    /// brings receipts back up to date, as the receipt rows of the transcript. In session-only mode
    /// the locally accumulated receipts are left exactly as they were, because the only other
    /// option would be to drop a record of writes the server never contradicted.
    /// </para>
    /// <para>
    /// A read that cannot be completed — cancelled, offline, or failed for a reason that says
    /// nothing about ownership — leaves the last known state alone rather than inventing a newer
    /// one.
    /// </para>
    /// </remarks>
    private async Task<bool> RefreshAuthoritativeStateAsync(CancellationToken cancellationToken)
    {
        if (SessionId is not { } sessionId)
        {
            return true;
        }

        try
        {
            if (await _client.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is { } session)
            {
                ApplySession(session);
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (CoachApiException ex)
        {
            // The session is gone, or it was never this learner's — the server answers both the
            // same way on purpose. Everything still on screen describes that session: the pending
            // suggestion card, the plan, the receipts. Swallowing this leaves one learner looking
            // at another's plan, or at a session the server has already discarded, with no sign
            // anything is wrong. Drop the view and show the state the problem type already names.
            //
            // Deliberately not a sign-out: the learner's identity is not in question here, only
            // this session's, and tearing down auth would put a still-valid learner through a
            // sign-in that returns them to a workspace that opens a fresh session anyway.
            if (ex.ProblemType is CoachProblemTypes.SessionNotFound
                or CoachProblemTypes.SessionExpired
                or CoachProblemTypes.Unavailable)
            {
                DropStaleAuthoritativeView();
                State = CoachStateMachine.FromProblem(ex);
                Notify();
                return false;
            }

            return true;
        }
        catch (HttpRequestException)
        {
            return true;
        }

        if (IsDurableHistoryEnabled && ConversationId is { } conversationId)
        {
            // Already swallows its own transport failures.
            await ReconcileFromLedgerAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Clears everything the denied session was the source of, without closing the workspace.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="Reset"/> on purpose. The learner is still here and still signed in;
    /// what is gone is one session's data. The transcript is cleared with the rest because its
    /// messages are that session's too, and the empty workspace then renders the expired or
    /// unavailable notice that tells the learner what to do next.
    /// </remarks>
    private void DropStaleAuthoritativeView()
    {
        SessionId = null;
        PendingSuggestion = null;
        PendingSuggestionTurn = null;
        PendingMemoryCandidate = null;
        PendingMemoryTurn = null;
        PlanState = null;
        ActiveConstraints = null;
        ExpiresAtUtc = null;
        LatestAnswer = null;
        _receipts.Clear();
        _revisions.Clear();
        _evidence.Clear();
        _messages.Clear();
        _timeline.Clear();
        _answersByMessageId.Clear();
        PlanBadgeCount = 0;
        PendingConfirmation = CoachConfirmation.None;
    }

    private void FinishRun()
    {
        _runCts?.Dispose();
        _runCts = null;
        RunStartedAt = null;
    }

    private void ApplySession(CoachSessionResponse session)
    {
        // The server keeps no plaintext transcript — learner words live only inside the encrypted
        // AgentSession — so a session read always answers Messages=[] and Evidence=[]. The client
        // owns the visible conversation. Two rules follow:
        //   1. Within a live circuit, keep what the learner can already see. Clearing it on every
        //      resume/refresh call would blank the conversation they are still reading.
        //   2. After a real reload there is nothing to keep, and nothing may be invented. The UI
        //      says so plainly instead of implying the history is simply empty.
        var isSameSession = string.Equals(SessionId, session.SessionId, StringComparison.Ordinal);

        SessionId = session.SessionId;
        ActiveConstraints = session.ActiveConstraints;
        PlanState = session.PlanState;
        PendingSuggestion = session.PendingSuggestion;

        // The placement only means something while a card is on screen. An authoritative read
        // that no longer carries the offer has to drop the anchor with it, or a later card
        // inherits a position from a suggestion the server already closed.
        if (PendingSuggestion is null)
        {
            PendingSuggestionTurn = null;
        }

        ClarificationsRemaining = session.ClarificationsRemaining;
        ExpiresAtUtc = session.ExpiresAtUtc;

        if (session.Messages.Count > 0)
        {
            // Server history is authoritative, but it can never contain the learner's own words:
            // no plaintext is persisted. Keep the local turns from this same session so a reopen
            // does not silently drop the questions the learner can see right now.
            var localLearnerMessages = isSameSession
                ? _messages
                    .Where(m => m.MessageId.StartsWith(LocalMessageIdPrefix, StringComparison.Ordinal))
                    .ToList()
                : [];

            _messages.Clear();
            _messages.AddRange(session.Messages);
            _messages.AddRange(localLearnerMessages.Where(local =>
                !_messages.Any(m => m.Role == CoachMessageRole.Learner
                                    && string.Equals(m.Text, local.Text, StringComparison.Ordinal))));
            _messages.Sort((a, b) => a.CreatedAtUtc.CompareTo(b.CreatedAtUtc));
        }
        else if (!isSameSession)
        {
            // A different session's history must not leak into this one.
            _messages.Clear();
            _timeline.Clear();
            PendingSuggestionTurn = null;
            _answersByMessageId.Clear();
            LatestAnswer = null;
        }

        // Session load and resume. The correction survives a reload because the server reports it
        // from the stored outcome, so a learner who closes the app mid-dispute comes back to the
        // same constraint rather than to a coach that has quietly forgotten.
        Dispute = session.Dispute;

        // The refusal survives a reload. The server restores it from the stored outcome of the
        // latest completed turn and only that one — a one-row lookback, so a learner who was
        // refused and then asked something ordinary is not handed the older refusal back. An
        // outcome it cannot read fails closed to null rather than searching further back.
        // The evidence rows are not restored with it, so the card states the withheld count and
        // reason from the limitation's own fields in that case.
        Limitation = session.Limitation;

        // Restored latest-only alongside the refusal, and cleared when the latest turn discloses
        // nothing.
        RepairDisclosure = session.RepairDisclosure;

        // False, and unconditionally so. This used to read session.Evidence.Count > 0, on the
        // assumption that a session read reports the evidence for the latest turn. It does not,
        // and the contract says as much where the restored refusal is documented: the projection
        // restores the stored outcome, "but not the evidence the refusal was judged against —
        // that lived on the turn". The two facts come from different places. The disclosure comes
        // from the stored outcome of the newest completed turn; the evidence list is whatever the
        // request in hand happened to read, which for a session GET is nothing. Nothing on
        // CoachEvidenceDto names a message or a turn, so a non-empty list cannot be shown to
        // belong to the answer the disclosure describes even when one arrives.
        //
        // A claim that cannot be proven has to fail to the quiet side: an unearned true here sent
        // a reloaded learner looking for evidence beside an answer that has none, which is the
        // defect this closes. Set even when the disclosure is null, so a later turn can never pair
        // a fresh disclosure with a stale reading of this one. The restore path re-derives the
        // real answer from the entry it attaches to, in AttachRestoredRepairDisclosure.
        RepairEvidenceOnScreen = false;

        if (session.Evidence.Count > 0)
        {
            _evidence.Clear();
            _evidence.AddRange(session.Evidence);
        }
        else if (!isSameSession)
        {
            _evidence.Clear();
        }

        // Revisions are server-owned and are returned in full, so plan continuity survives a
        // reload even when the conversation does not.
        _revisions.Clear();
        _revisions.AddRange(session.Revisions);

        // Only a resume can legitimately have no visible history. A brand-new session starts
        // empty by definition and needs no explanation. Durable mode never needs it at all: the
        // transcript came back from the ledger, so there is no hidden history to apologize for.
        IsResumedWithoutHistory = !IsDurableHistoryEnabled && _resumeRequested && _messages.Count == 0;

        State = session.Status switch
        {
            CoachSessionStatus.Expired => CoachUiState.Expired,
            CoachSessionStatus.Limited => CoachUiState.Limited,
            CoachSessionStatus.Failed => CoachUiState.Failed,
            CoachSessionStatus.AwaitingClarification => CoachUiState.Clarification,
            CoachSessionStatus.SuggestionPending => CoachUiState.SuggestionPending,
            _ => CoachUiState.Ready
        };

        MaybeAutoOpenCanvas();
        Notify();
    }

    private void ApplyTurn(
        CoachTurnResponse turn,
        CoachInitiator initiator,
        long turnSequence = 0,
        bool ledgerIsAuthoritative = false)
    {
        // Every artifact of this response belongs to the exchange that asked for it, so a slow
        // reply lands beside its own question rather than after a later one.
        var placement = turnSequence == 0 ? ++_turnSequence : turnSequence;

        ActiveConstraints = turn.ActiveConstraints;
        PlanState = turn.PlanState;
        PendingSuggestion = turn.PendingSuggestion;
        PendingMemoryCandidate = turn.MemoryCandidate;
        PendingMemoryTurn = turn.MemoryCandidate is null ? null : placement;
        ClarificationsRemaining = turn.ClarificationsRemaining;
        ExpiresAtUtc = turn.ExpiresAtUtc;
        LastStopReason = turn.StopReason;

        // Attach this turn's structured answer to its PedagogicalAnswer message, so the chat can
        // render blocks in place rather than the duplicated plain text.
        var answerAttached = false;

        // The reason code this turn's notices carry, read from the operation's own fields and never
        // from what Sam wrote. This is the identical call the server makes when it writes a notice
        // into the ledger, so a session-only notice and the durable row for the same outcome carry
        // the same code and render the same marker (Phase 2 defect #2). A turn that produced a
        // change receipt is not a no-change turn even when it stopped badly.
        var noticeReasonCode = CoachNoticeReasonCodes.ForNotice(
            turn.StopReason,
            turn.ChangeReceipt is not null);

        // In durable mode the canonical rows are already on screen, carrying the server's own
        // message ids, sequences and timestamps. The response body describes the same exchange
        // under different ids, so nothing here can recognise it as a duplicate - it simply
        // appends a second Sam answer. The ledger wins; only the structured answer is carried
        // across, because a ledger row does not always bring one.
        if (ledgerIsAuthoritative)
        {
            PairAnswerWithCanonicalMessage(turn.Answer);
        }

        foreach (var message in ledgerIsAuthoritative ? Array.Empty<CoachMessageDto>() : (IEnumerable<CoachMessageDto>)turn.Messages)
        {
            if (_messages.Any(m => string.Equals(m.MessageId, message.MessageId, StringComparison.Ordinal)))
            {
                continue;
            }

            // Today the server never echoes learner turns. If that ever changes, reconcile against
            // the copy already on screen instead of showing the learner's question twice.
            if (message.Role == CoachMessageRole.Learner
                && _messages.Any(m => m.Role == CoachMessageRole.Learner
                                      && m.MessageId.StartsWith(LocalMessageIdPrefix, StringComparison.Ordinal)
                                      && string.Equals(m.Text, message.Text, StringComparison.Ordinal)))
            {
                continue;
            }

            _messages.Add(message);

            CoachAnswerDto? paired = null;

            if (!answerAttached
                && message.Kind == CoachMessageKind.PedagogicalAnswer
                && turn.Answer is { } structured)
            {
                _answersByMessageId[message.MessageId] = structured;
                paired = structured;
                answerAttached = true;
            }

            AppendTimelineEntry(
                placement,
                CoachTimelineEntry.KindFor(message),
                // The server's own stamp when it sent one; otherwise this arrival. Either way it
                // is the moment the artifact came into being, not the moment it was rendered.
                message.CreatedAtUtc == default
                    ? DateTimeOffset.Now
                    : new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAtUtc, DateTimeKind.Utc)).ToLocalTime(),
                message,
                paired,
                // Only a notice carries a reason code: it is the artifact that stands in for the
                // change that did not happen.
                noticeReasonCode: message.Kind == CoachMessageKind.Notice ? noticeReasonCode : null);
        }

        if (turn.Answer is not null)
        {
            LatestAnswer = turn.Answer;
        }

        // Replaced from every turn, null included: a resolved dispute has to stop being shown.
        Dispute = turn.Dispute;

        // Same rule, same reason: a refusal that outlives its turn keeps hedging an answer the
        // coach has since given plainly. Unconditional, so the null case clears it.
        // The announcement is raised in ApplyOutcomePolicy, which runs after this and resets both
        // announcement channels — setting it here would be silently discarded.
        Limitation = turn.Limitation;

        // Unconditional for the same reason: a stale "part of this was adjusted" is a claim about
        // whichever answer is on screen now.
        RepairDisclosure = turn.RepairDisclosure;

        // On the same unconditional path, and that is the point. This is what THIS turn read, and
        // it is what the disclosure copy is allowed to promise. Reading the workspace evidence
        // list instead let a no-evidence turn inherit the previous turn's rows and tell the learner
        // to go and look at them — evidence for a question they had already been answered. The
        // sticky list stays sticky for ordinary turns, deliberately; this fact is recorded beside
        // it rather than in place of it.
        RepairEvidenceOnScreen = turn.Evidence.Count > 0;

        if (turn.Limitation is not null)
        {
            // A refusal is judged against exactly what this turn read, so the workspace list is
            // replaced from the turn even when the turn read nothing. Keeping the previous turn's
            // rows here is the LVG-W9-8 defect: the refusal region would sit above evidence from a
            // question the learner already got an answer to, and the card would say "the evidence
            // below shows what it looked at" over rows it never looked at. An empty read is an
            // honest answer and has to be able to render as one.
            _evidence.Clear();
            _evidence.AddRange(turn.Evidence);

            if (turn.Evidence.Count > 0)
            {
                AttachEvidenceToTurn(placement, turn.Evidence);
            }
        }
        else if (turn.Evidence.Count > 0)
        {
            // Unchanged for ordinary turns: a turn that cites nothing leaves the last citation
            // standing, because the learner may still be reading it.
            _evidence.Clear();
            _evidence.AddRange(turn.Evidence);

            // The evidence also belongs to the message that cited it, so the conversation can
            // offer a disclosure under the claim itself. Without this the only copy lived in this
            // workspace-wide list, and every one of Sam's messages advertised the newest turn's
            // evidence — including the ones that had cited nothing.
            AttachEvidenceToTurn(placement, turn.Evidence);
        }

        // After the evidence, so the note and the rows it may point at land on the same message.
        // Reads VisibleRepairDisclosure, so a refused turn attaches nothing at all — refusal
        // precedence is one rule enforced in one place rather than re-derived per renderer.
        AttachRepairDisclosureToTurn(placement, VisibleRepairDisclosure, RepairEvidenceOnScreen);

        if (turn.PlanState.LastRevision is { } revision
            && !_revisions.Any(r => string.Equals(r.RevisionId, revision.RevisionId, StringComparison.Ordinal)))
        {
            _revisions.Add(revision);
        }

        if (turn.ChangeReceipt is { } receipt
            && !_receipts.Any(r => string.Equals(r.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal)))
        {
            _receipts.Add(receipt);

            // The receipt belongs to the turn that produced it, so it renders inside that
            // exchange rather than after whatever message happens to be last.
            AppendTimelineEntry(placement, CoachTimelineKind.Receipt, DateTimeOffset.Now, receipt: receipt);
        }

        // A pending suggestion is part of the turn that offered it, so it is anchored there.
        PendingSuggestionTurn = turn.PendingSuggestion is null ? null : placement;

        // A proposed change belongs to its own exchange too. In durable mode the ledger rows have
        // already carried it in, so stamping it a second time from the response body would show
        // one proposal as two cards under two identities.
        if (!ledgerIsAuthoritative)
        {
            AttachTurnWrite(turn.WriteOperation, placement);
        }

        RecomputeActiveWrite();

        // Once new turns exist there is visible history again, so the resumed-session summary
        // stops applying.
        if (_messages.Count > 0)
        {
            IsResumedWithoutHistory = false;
        }

        State = CoachStateMachine.FromTurn(turn);

        var succeeded = State is CoachUiState.PlanUpdated or CoachUiState.Undone
            or CoachUiState.Ready or CoachUiState.Clarification or CoachUiState.SuggestionPending
            or CoachUiState.ClarificationLimitReached;

        MaybeAutoOpenCanvas();
        ApplyOutcomePolicy(initiator, succeeded);
        Notify();
    }

    /// <summary>
    /// Auto-opens the canvas once per new suggestion or applied revision. When the canvas is
    /// deliberately closed the key is still recorded, so it does not reopen for the same change.
    /// </summary>
    private void MaybeAutoOpenCanvas()
    {
        var key = PendingSuggestion?.SuggestionId ?? LatestReceipt?.Revision.RevisionId;

        if (!CoachStateMachine.ShouldAutoOpenCanvas(_lastAutoOpenKey, key))
        {
            return;
        }

        _lastAutoOpenKey = key;

        if (IsCanvasOpen)
        {
            return;
        }

        if (Presentation == CoachPresentation.FullScreen)
        {
            // Mobile never force-switches panes after a write. The badge plus one polite
            // announcement carries the change instead.
            PlanBadgeCount++;
        }
        else
        {
            IsCanvasOpen = true;
        }
    }

    private void ApplyOutcomePolicy(CoachInitiator initiator, bool succeeded)
    {
        var policy = CoachStateMachine.OutcomePolicy(initiator, succeeded);

        // Exactly one channel per event: never both the polite region and the alert.
        PoliteAnnouncementKey = null;
        AlertKey = null;

        var receiptId = LatestReceipt?.ReceiptId;

        if (!succeeded)
        {
            AlertKey = CoachStateMachine.AnnouncementKey(State);
            PendingFocusElementId = policy.MoveFocusToReceipt ? CoachElementIds.Alert : null;
            return;
        }

        if (policy.MoveFocusToReceipt && receiptId is not null)
        {
            PendingFocusElementId = CoachElementIds.Receipt(receiptId);
            return;
        }

        if (policy.AnnouncePolitely)
        {
            PoliteAnnouncementKey = CoachStateMachine.AnnouncementKey(State);
        }

        // A withheld answer is the one outcome the generic policy cannot describe. The turn
        // succeeded and the state machine lands on Ready, so without this the learner gets a
        // shorter answer, no explanation, and silence from the live region — which is the whole
        // defect the refusal exists to fix. Polite, never an alert: nothing failed. Only when the
        // run did not already have something louder to say.
        if (Limitation is not null && AlertKey is null)
        {
            // Split by what is actually on screen. Announcing "the evidence is on screen" when the
            // read returned nothing sends a screen-reader user looking for a panel that is not
            // there, which is a worse failure than saying less.
            PoliteAnnouncementKey = _evidence.Count > 0
                ? "Coach_Announce_ClaimWithheld"
                : "Coach_Announce_ClaimWithheldNoEvidence";
        }
        else if (AlertKey is null && RepairAnnouncementKey() is { } repairKey)
        {
            // A repaired answer is the other outcome the generic policy cannot describe: the turn
            // succeeded, the state machine lands on Ready, and the learner reads an answer that is
            // not quite what the coach wrote. Saying nothing is the silent-repair defect. Polite,
            // never an alert — the answer is still an answer.
            PoliteAnnouncementKey = repairKey;
        }
    }

    /// <summary>
    /// The announcement for a disclosed repair, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>None</c> and null are silent: checked-and-clean and not-checked are not news, and
    /// announcing either would make the live region fire on every ordinary turn until it was
    /// ignored. <c>Unknown</c> is announced, because a state this build cannot name may be the one
    /// that means the answer was rewritten — the announcement says only that this version cannot
    /// describe what happened, never that anything changed.
    /// </para>
    /// <para>
    /// The two states that mention the evidence split on whether this turn produced any, exactly
    /// as the visible copy does. The same pairing the refusal already uses, and for the same
    /// reason: telling a screen-reader user the evidence is on screen when the turn read nothing
    /// sends them looking for a panel that is not there. A learner who reads the note and a
    /// learner who hears it must be promised the same thing in every cell of the matrix.
    /// </para>
    /// </remarks>
    private string? RepairAnnouncementKey() => VisibleRepairDisclosure switch
    {
        null => null,
        CoachRepairDisclosure.None => null,
        CoachRepairDisclosure.AnswerAltered => "Coach_Announce_AnswerAltered",
        CoachRepairDisclosure.RepairSuppressedForLanguage => RepairEvidenceOnScreen
            ? "Coach_Announce_RepairSuppressed"
            : "Coach_Announce_RepairSuppressedNoEvidence",
        _ => RepairEvidenceOnScreen
            ? "Coach_Announce_RepairUnknown"
            : "Coach_Announce_RepairUnknownNoEvidence"
    };

    private void ClearTransientAnnouncements()    {
        PoliteAnnouncementKey = null;
        AlertKey = null;
        PendingFocusElementId = null;
    }

    private void Notify() => Changed?.Invoke();

    /// <summary>
    /// Releases the workspace. The scoped lifetime ends here, so nothing may be left holding a
    /// one-use confirmation.
    /// </summary>
    /// <remarks>
    /// Dropping the confirmation on dispose is the last of the four places it is released, and the
    /// only one that is not a decision the learner made. A circuit that ends while a protected
    /// change is being confirmed — a closed tab, a dropped connection, a navigation away — must
    /// not leave the secret reachable from a state object that outlives the prompt it was minted
    /// for. It is cheap, and the alternative is a credential whose lifetime is the garbage
    /// collector's business rather than ours.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DiscardConfirmation();
        _runCts?.Dispose();
        _runCts = null;
        Changed = null;
    }
}
