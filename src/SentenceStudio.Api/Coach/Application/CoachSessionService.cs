using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Evidence;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;
using SentenceStudio.Application.Practice;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The application-owned coach reducer. See <see cref="ICoachSessionService"/>.
/// </summary>
public sealed class CoachSessionService : ICoachSessionService
{
    private const string AcceptLabel = "Yes, update it";

    /// <summary>Plan-side notices. Application-owned, and free of counts the diff already carries.</summary>
    private const string AlreadyPendingNotice = "There is already a suggestion waiting for an answer.";

    private const string NoPlanNotice =
        "There is no plan for today yet, so there is nothing to adjust. " +
        "Start today\u2019s plan first, and I can change it from there.";

    private const string InvalidConstraintNotice =
        "I could not make that change to Today\u2019s Plan, so it is unchanged.";

    private const string NoFeasiblePlanNotice =
        "I could not build a plan that fits that change, so Today\u2019s Plan is unchanged.";

    private const string IneffectiveSuggestionNotice =
        "I could not find a change that would help today, so Today\u2019s Plan is unchanged.";

    private const string UnverifiedChangeNotice =
        "I could not verify that change. Today\u2019s Plan is unchanged.";

    /// <summary>Receipt text for an accepted suggestion, identical on the tapped and typed paths.</summary>
    private const string AppliedSuggestionMessage = "Applied the suggested change to Today\u2019s Plan.";

    /// <summary>Notice text for a rejected suggestion, identical on the tapped and typed paths.</summary>
    private const string RejectedSuggestionMessage = "Today\u2019s Plan is unchanged.";
    private const string RejectLabel = "Not now";

    private readonly IUserScopeProvider _userScope;
    private readonly IPlanDateContext _dateContext;
    private readonly IPlanService _planService;
    private readonly ICoachSessionStore _sessions;
    private readonly ICoachUsageStore _usage;
    private readonly ICoachAvailabilityPolicy _availability;
    private readonly ICoachBudgetService _budget;
    private readonly ICoachAgentFactory _agentFactory;
    private readonly ILearningCoach _coach;
    private readonly CoachConstraintMapper _mapper;
    private readonly CoachPlanProjection _projection;
    private readonly CoachExplicitAcceptanceClassifier _acceptance;
    private readonly CoachSuggestionValidator _suggestionValidator;
    private readonly CoachAnswerProjection _answers;
    private readonly ICoachLanguageResolver _languages;
    private readonly CoachWriteAuthority _writeAuthority;
    private readonly CoachIntentValidator _intentValidator;
    private readonly CoachDueItemLeakValidator _leakValidator;
    private readonly CoachVocabularyFocusService _focus;
    private readonly ICoachValidationDataSource _validationData;
    private readonly CoachRunRegistry _runs;
    private readonly CoachTurnIdempotencyStore _idempotency;
    private readonly CoachTelemetry _telemetry;
    private readonly IOptionsMonitor<CoachOptions> _options;

    /// <summary>
    /// The durable ledger, when this host has one. Read-only here: the conversation service owns
    /// every write, so the compatibility routes can show history without becoming a second writer.
    /// </summary>
    private readonly ICoachMessageStore? _history;

    /// <summary>
    /// The conversation ledger, when durable history is on. Optional for the same reason
    /// <see cref="_history"/> is: a host with history off constructs this service without one.
    /// </summary>
    private readonly ICoachConversationStore? _conversations;
    private readonly Memory.CoachMemoryTurnCoordinator? _memory;
    private readonly ILogger<CoachSessionService> _logger;

    /// <summary>
    /// Optional. Present whenever the write tools are wired; absent in the read-only tests that
    /// construct this service by hand.
    /// </summary>
    private readonly Operations.CoachWriteTurnScope? _writeTurn;

    /// <summary>
    /// The write ledger, when the write tools are wired. Read-only from here: this service asks
    /// it what a turn proposed so the answer can travel with the turn, and never asks it to do
    /// anything. Approving a change is a separate learner request on a route no turn can reach.
    /// </summary>
    private readonly Operations.CoachWriteOperationService? _writeLedger;

    /// <summary>
    /// The opportunity ledger, when this host has one. Never consulted for a decision — it is
    /// written to after the turn result is already computed and its outcome is discarded.
    /// </summary>
    private readonly Opportunities.ICoachOpportunityRecorder _opportunities;

    /// <summary>
    /// The referent-loss predicate, when this host has one.
    /// </summary>
    private readonly Opportunities.Detection.CoachUnboundAnswerDetector? _unboundAnswers;

    /// <summary>
    /// The practice history query. Used by the deterministic latest-study route to call
    /// the same application query as the tool path, without going through the model.
    /// </summary>
    private readonly IPracticeHistoryQueries? _practiceHistory;

    /// <summary>
    /// Every scoped read this turn made. Optional so a host or a test that wires no seam still
    /// builds; absent, the turn simply has no evidence to show, which is the truthful answer for a
    /// turn nobody was watching.
    /// </summary>
    private readonly Tools.Observation.ICoachTurnObservationBuffer? _observations;

    /// <summary>
    /// The grounding ladder. Optional so every existing construction of this service keeps
    /// compiling; a host without it behaves exactly as one configured Off.
    /// </summary>
    private readonly Validation.Claims.CoachTurnGroundingEvaluator? _grounding;

    /// <summary>
    /// The correction-state coordinator. Optional, like the grounding ladder: a host that has not
    /// registered it behaves exactly as one with the flag off.
    /// </summary>
    private readonly CoachDisputeCoordinator? _disputes;

    /// <summary>
    /// The durable turn operations, for reading the last completed turn's stored outcome.
    /// </summary>
    /// <remarks>
    /// Optional, like every other durable dependency here: a host without history has no stored
    /// outcome to restore from, which is the same answer as a conversation whose last turn carried
    /// no limitation.
    /// </remarks>
    private readonly Persistence.History.ICoachTurnOperationStore? _operations;

    /// <summary>
    /// The dispute in force for the turn being processed. Turn-scoped, like
    /// <see cref="_turnViolation"/>, because a dispute belongs to one conversation and carrying one
    /// into another turn in the same request scope is the cross-carry this feature must not do.
    /// </summary>
    private Persistence.History.CoachTurnDisputeState? _turnDispute;

    /// <inheritdoc />
    public Persistence.History.CoachTurnDisputeState? CurrentTurnDispute => _turnDispute;

    /// <summary>
    /// What the grounding layer did to the turn being processed.
    /// </summary>
    /// <remarks>
    /// Turn-scoped for the same reason the dispute is: this service is registered scoped and one
    /// request carries exactly one turn. The conversation service reads it at the outcome write so
    /// the durable row records the same judgement the metric already counted.
    /// </remarks>
    private Validation.Claims.CoachGroundingTurnSummary? _turnGrounding;

    /// <summary>
    /// What this turn must disclose about the grounding layer's handling of its answer.
    /// </summary>
    /// <remarks>
    /// Turn-scoped, and set only once the refusal decision is known: the same summary produces a
    /// disclosure on a turn that ships and nothing on one that refuses, so the value cannot be
    /// derived before that branch.
    /// </remarks>
    private CoachRepairDisclosure? _turnRepairDisclosure;

    /// <inheritdoc />
    public Validation.Claims.CoachGroundingTurnSummary? CurrentTurnGrounding => _turnGrounding;

    /// <summary>
    /// The validation rule that refused the current turn, when one did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turn-scoped mutable state, and safe because this service is registered scoped and one
    /// request carries exactly one turn. It exists so the ledger can tell an answer-leak refusal
    /// apart from a malformed-intent refusal without threading an observation object through
    /// every reduce path — two failures that look identical on the wire (both are
    /// <c>ValidationFailed</c>) and mean completely different things to a reviewer.
    /// </para>
    /// <para>
    /// Cleared at the start of every turn, so a value can never leak from one turn into the next.
    /// </para>
    /// </remarks>
    private CoachViolationKind? _turnViolation;

    /// <summary>
    /// True when the current turn was refused by the answer-shape projection (as opposed to the
    /// intent validator or grounding layer). Turn-scoped; cleared alongside
    /// <see cref="_turnViolation"/>.
    /// </summary>
    private bool _answerShapeRefused;

    /// <summary>
    /// The client's advertised capabilities for the turn in flight. Turn-scoped, like
    /// <see cref="_turnViolation"/>, and for the same reason: it must never be read on a turn other
    /// than the one that sent it.
    /// </summary>
    private CoachClientCapabilityHandshake? _turnHandshake;

    public CoachSessionService(
        IUserScopeProvider userScope,
        IPlanDateContext dateContext,
        IPlanService planService,
        ICoachSessionStore sessions,
        ICoachUsageStore usage,
        ICoachAvailabilityPolicy availability,
        ICoachBudgetService budget,
        ICoachAgentFactory agentFactory,
        ILearningCoach coach,
        CoachConstraintMapper mapper,
        CoachPlanProjection projection,
        CoachExplicitAcceptanceClassifier acceptance,
        CoachSuggestionValidator suggestionValidator,
        CoachAnswerProjection answers,
        ICoachLanguageResolver languages,
        CoachWriteAuthority writeAuthority,
        CoachIntentValidator intentValidator,
        CoachDueItemLeakValidator leakValidator,
        CoachVocabularyFocusService focus,
        ICoachValidationDataSource validationData,
        CoachRunRegistry runs,
        CoachTurnIdempotencyStore idempotency,
        CoachTelemetry telemetry,
        IOptionsMonitor<CoachOptions> options,
        ILogger<CoachSessionService> logger,
        ICoachMessageStore? history = null,
        ICoachConversationStore? conversations = null,
        Memory.CoachMemoryTurnCoordinator? memory = null,
        Operations.CoachWriteTurnScope? writeTurn = null,
        Operations.CoachWriteOperationService? writeLedger = null,
        Opportunities.ICoachOpportunityRecorder? opportunities = null,
        Opportunities.Detection.CoachUnboundAnswerDetector? unboundAnswers = null,
        IPracticeHistoryQueries? practiceHistory = null,
        Tools.Observation.ICoachTurnObservationBuffer? observations = null,
        Validation.Claims.CoachTurnGroundingEvaluator? grounding = null,
        CoachDisputeCoordinator? disputes = null,
        Persistence.History.ICoachTurnOperationStore? operations = null)
    {
        _observations = observations;
        _grounding = grounding;
        _disputes = disputes;
        _operations = operations;
        _writeTurn = writeTurn;
        _writeLedger = writeLedger;
        _opportunities = opportunities ?? Opportunities.NullCoachOpportunityRecorder.Instance;
        _unboundAnswers = unboundAnswers;
        _practiceHistory = practiceHistory;
        _userScope = userScope;
        _dateContext = dateContext;
        _planService = planService;
        _sessions = sessions;
        _usage = usage;
        _availability = availability;
        _budget = budget;
        _agentFactory = agentFactory;
        _coach = coach;
        _mapper = mapper;
        _projection = projection;
        _acceptance = acceptance;
        _suggestionValidator = suggestionValidator;
        _answers = answers;
        _languages = languages;
        _writeAuthority = writeAuthority;
        _intentValidator = intentValidator;
        _leakValidator = leakValidator;
        _focus = focus;
        _validationData = validationData;
        _runs = runs;
        _idempotency = idempotency;
        _telemetry = telemetry;
        _options = options;
        _logger = logger;
        _history = history;
        _conversations = conversations;
        _memory = memory;
    }

    // ---------------------------------------------------------------- availability

    public async Task<CoachOperationResult<CoachAvailabilityResponse>> GetAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        // A missing user_profile_id claim is an authentication problem, not a coach one, so it
        // surfaces as the API's usual 401 rather than a coach-shaped answer.
        var userProfileId = RequireUserProfileId();

        var decision = _availability.Evaluate(userProfileId);
        if (!decision.IsAllowed)
        {
            // A disabled feature and a learner outside the cohort are both "no entry point".
            return Unavailable<CoachAvailabilityResponse>("The coach is not available.");
        }

        var today = _dateContext.UserLocalDate;
        var budget = await _budget.GetSnapshotAsync(userProfileId, today, cancellationToken).ConfigureAwait(false);

        // No plan is no longer a reason to hide the coach. A learner can ask what a word means
        // before they have generated one; only the plan-editing half is unavailable. Reading the
        // snapshot never creates a plan, so opening the coach still costs nothing.
        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var canEditPlan = plan.Items.Count > 0;

        var resumable = await _sessions.LoadResumableAsync(userProfileId, cancellationToken).ConfigureAwait(false);
        var active = resumable.IsUsable ? resumable.Session : null;

        var state = budget.RunsRemainingToday <= 0 || budget.RunsRemainingThisWeek <= 0
            ? CoachAvailabilityState.LimitReached
            : active is not null
                ? CoachAvailabilityState.ResumeAvailable
                : CoachAvailabilityState.Available;

        return CoachOperationResult<CoachAvailabilityResponse>.Ok(new CoachAvailabilityResponse
        {
            IsAvailable = state != CoachAvailabilityState.LimitReached,
            State = state,
            EntryPointLabel = state == CoachAvailabilityState.ResumeAvailable ? "Resume coach" : "Learning coach",
            CanEditPlan = canEditPlan,
            ActiveSessionId = active?.Id,
            ActiveSessionStatus = active?.Status,
            ActiveSessionExpiresAtUtc = active?.ExpiresAt,
            RunsRemainingToday = budget.RunsRemainingToday,
            RunsRemainingThisWeek = budget.RunsRemainingThisWeek,

            // Both flags need the option *and* the services. Reporting a feature from its flag
            // alone would promise a surface that answers 404 on a host where the services were
            // never registered, and the client would have no way to find that out except by
            // failing. History needs both of its stores because the surface is useless with
            // either half missing: conversations without messages, or messages with nothing to
            // list them under. The two features are independent of each other by construction.
            IsDurableHistoryAvailable =
                _options.CurrentValue.IsDurableHistoryEnabled && _conversations is not null && _history is not null,
            IsMemoryAvailable = _memory is { IsEnabled: true },
            IsSamOverlayAvailable = _options.CurrentValue.IsSamOverlayEnabled,

            // The write surface needs its own flag, the read tools it builds on, the overlay it
            // renders in, and the ledger that records a proposal. Any one of them missing means a
            // proposal either cannot be produced or cannot be approved, and a client told
            // otherwise would draw controls that answer 404.
            IsSamWriteAvailable =
                _options.CurrentValue.IsSamWriteToolsEnabled
                && _options.CurrentValue.IsSamReadToolsEnabled
                && _options.CurrentValue.IsSamOverlayEnabled
                && _writeLedger is not null
        });
    }

    // ---------------------------------------------------------------- session lifecycle

    public async Task<CoachOperationResult<CoachSessionResponse>> StartSessionAsync(
        StartCoachSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new StartCoachSessionRequest();

        var gate = await RequireAvailableAsync<CoachSessionResponse>(cancellationToken).ConfigureAwait(false);
        if (gate.Denied is { } denied)
        {
            return denied;
        }

        var userProfileId = gate.UserProfileId!;
        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (request.Resume)
        {
            var resumable = await _sessions.LoadResumableAsync(userProfileId, cancellationToken).ConfigureAwait(false);
            if (resumable.IsUsable)
            {
                // Resuming the checkpoint has to resume the conversation behind it, or a client on
                // the compatibility route would keep talking to a thread with no ledger.
                await EnsureConversationAsync(userProfileId, resumable.Session!.Id, cancellationToken)
                    .ConfigureAwait(false);

                return CoachOperationResult<CoachSessionResponse>.Ok(
                    await BuildSessionResponseAsync(userProfileId, resumable.Session!, plan, cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        var constraints = CoachConstraintMapper.Default(plan.TotalEstimatedMinutes);
        var created = await _sessions.CreateAsync(
            userProfileId,
            new CreateCoachSessionRequest
            {
                AgentImplementation = _coach.Implementation.ToString().ToLowerInvariant(),
                AgentName = CoachInstructions.AgentName,
                ActiveConstraints = constraints
            },
            cancellationToken).ConfigureAwait(false);

        await EnsureConversationAsync(userProfileId, created.Id, cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachSessionResponse>.Ok(
            await BuildSessionResponseAsync(userProfileId, created, plan, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Gives a session started through the compatibility route the durable conversation that the
    /// new routes expect, using the session id as the conversation id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One identifier serves both shapes, so an old client posting to <c>/sessions/{id}</c> and a
    /// new client reading <c>/conversations/{id}/messages</c> are looking at one thread. Without
    /// this, a session started the old way would accumulate no history at all and the alias would
    /// be a quiet data-loss path for the release the two shapes overlap.
    /// </para>
    /// <para>
    /// Creation is best-effort and idempotent: an existing conversation is left exactly as it is,
    /// including its title, and a failure to create one must not stop a learner starting a
    /// session. History is additive here, never a precondition.
    /// </para>
    /// </remarks>
    private async Task EnsureConversationAsync(
        string userProfileId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (_conversations is null || !_options.CurrentValue.IsDurableHistoryEnabled)
        {
            return;
        }

        var owner = CoachOwner.ForUser(userProfileId);

        var existing = await _conversations.GetAsync(owner, sessionId, cancellationToken).ConfigureAwait(false);
        if (existing.Status == CoachHistoryStatus.Success)
        {
            return;
        }

        var created = await _conversations.CreateAsync(
            owner,
            new CreateCoachConversationRequest(
                CoachHistoryTitles.Fallback(_dateContext.UserLocalDate),
                CoachConversationTitleSource.Generated,
                null,
                sessionId),
            cancellationToken).ConfigureAwait(false);

        if (created.Status != CoachHistoryStatus.Success)
        {
            // Status only. A conversation that could not be created leaves the session working and
            // the ledger empty, which is the pre-history behaviour rather than a broken one.
            _logger.LogWarning(
                "[Coach] Could not open a durable conversation for a compatibility session: {Status}.",
                created.Status);
        }
    }

    /// <summary>
    /// Loads the checkpoint with this id, or creates one when it is missing, expired, or written
    /// under an incompatible agent configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Durable history needs a checkpoint whose id it chose, so the 24-hour session and the
    /// permanent conversation share one identifier and neither needs a lookup table. That is why
    /// this exists rather than <see cref="StartSessionAsync"/>: the public start route lets the
    /// store name the session, and a caller that must control identity cannot use it.
    /// </para>
    /// <para>
    /// An unusable row is deleted before the replacement is created. Reusing the id is the point
    /// — the conversation keeps its ledger and the client keeps its link — and an expired or
    /// version-mismatched checkpoint holds nothing worth preserving, because everything the
    /// learner is entitled to read back is already in the message ledger.
    /// </para>
    /// </remarks>
    public async Task<CoachCheckpointState> EnsureCheckpointAsync(
        string checkpointId,
        CoachCheckpointCoverage? required = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        var userProfileId = RequireUserProfileId();

        var load = await _sessions.LoadAsync(userProfileId, checkpointId, cancellationToken).ConfigureAwait(false);
        if (load.IsUsable && Covers(load.Session!, required))
        {
            // A usable row is not the same thing as usable agent memory, and this is the case that
            // cost a live conversation its context. Memory rotation clears the checkpoint *in
            // place*: CoachMemoryCheckpointRotator calls ICoachSessionStore.ClearAgentCheckpointsAsync,
            // which nulls ProtectedAgentSession and deliberately leaves the row, the constraints,
            // and the coverage stamp exactly as they were. The row therefore still loads, still
            // matches the configuration identity, and still covers the ledger — while holding
            // nothing to resume from.
            //
            // Reporting that as a live checkpoint sent the next turn to the model with a fresh
            // AgentSession *and* an empty prior-message list, so the agent answered an anaphoric
            // follow-up with no idea what it referred to. The rotator's contract is that "the
            // ledger is canonical and the next turn reconstructs from it"; the reconstruct signal
            // is this flag, so it has to mean "there is no agent memory to resume", not merely
            // "the row had to be replaced".
            var resumable = !string.IsNullOrWhiteSpace(load.AgentSessionJson);

            if (!resumable)
            {
                // Shape only: which conversation, and that it is resuming with no agent memory.
                // No learner text, no message content, no memory value. This line is what turns
                // "the coach forgot the conversation" from a model complaint into a one-look
                // diagnosis, because a rotated checkpoint is otherwise indistinguishable from a
                // healthy one at every layer the operator can see.
                _logger.LogInformation(
                    "[Coach] Session {SessionId}: the checkpoint is live but holds no agent session; " +
                    "the turn will be rebuilt from the message ledger.",
                    checkpointId);
            }

            return new CoachCheckpointState(load.Session!, load.AgentSessionJson, Rebuilt: !resumable, load.Status);
        }

        if (load.Status != CoachSessionLoadStatus.NotFound)
        {
            await _sessions.DeleteAsync(userProfileId, checkpointId, cancellationToken).ConfigureAwait(false);
        }

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var created = await _sessions.CreateAsync(
            userProfileId,
            new CreateCoachSessionRequest
            {
                SessionId = checkpointId,
                AgentImplementation = _coach.Implementation.ToString().ToLowerInvariant(),
                AgentName = CoachInstructions.AgentName,
                ActiveConstraints = CoachConstraintMapper.Default(plan.TotalEstimatedMinutes)
            },
            cancellationToken).ConfigureAwait(false);

        return new CoachCheckpointState(created, AgentSessionJson: null, Rebuilt: true, load.Status);
    }

    /// <summary>
    /// True when a loaded checkpoint is trustworthy for <paramref name="required"/> coverage.
    /// </summary>
    /// <remarks>
    /// The session store already rejects an expired or config-mismatched row. This adds the case
    /// it cannot see: a checkpoint whose memory trails the ledger, because a crash landed between
    /// the ledger append and the checkpoint update, or another replica appended after this one was
    /// written. Trusting it would let the coach answer without having seen its own last turn.
    /// </remarks>
    private static bool Covers(CoachSession session, CoachCheckpointCoverage? required)
    {
        if (required is null)
        {
            return true;
        }

        var coverage = CoachActiveStateEnvelope.TryRead(session.ActiveConstraintsJson)?.Checkpoint;

        return coverage is not null
            && coverage.Matches(required)
            && coverage.CoveredSequence >= required.CoveredSequence;
    }

    public async Task StampCheckpointAsync(
        string checkpointId,
        CoachCheckpointCoverage coverage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentNullException.ThrowIfNull(coverage);

        var userProfileId = RequireUserProfileId();

        var load = await _sessions.LoadAsync(userProfileId, checkpointId, cancellationToken).ConfigureAwait(false);
        if (!load.IsUsable)
        {
            // Nothing to stamp. The next turn rebuilds, which is the safe direction to fail in.
            return;
        }

        var previous = CoachActiveStateEnvelope.TryRead(load.Session!.ActiveConstraintsJson);
        if (previous is null)
        {
            return;
        }

        await _sessions.UpdateAsync(
            userProfileId,
            checkpointId,
            new CoachSessionUpdate
            {
                ActiveStateJson = CoachNormalizedJson.Serialize(previous with { Checkpoint = coverage })
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the plan revision a durable turn operation produced, if it produced one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced a search for revisions created since the operation started. That search was
    /// wrong in both directions. Two conversations revising the same plan inside the window would
    /// each find the other's revision and report the wrong change; a retry outside the window
    /// would find nothing and report "no change" for a change that had already been committed.
    /// </para>
    /// <para>
    /// The correlation is now carried by the revision row itself, so the answer is exact and does
    /// not depend on clocks, session scope, or how long recovery took to run.
    /// </para>
    /// </remarks>
    public async Task<CoachPlanRevision?> GetRevisionByOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var userProfileId = RequireUserProfileId();
        return await _sessions.GetRevisionByOperationAsync(userProfileId, operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The configuration identity a checkpoint built right now would carry.</summary>
    public CoachCheckpointCoverage CheckpointIdentity(string conversationId, long coveredSequence) =>
        new()
        {
            ConversationId = conversationId,
            CoveredSequence = coveredSequence,
            AgentConfigVersion = _options.CurrentValue.AgentConfigVersion,
            PromptVersion = CoachPolicyFingerprint.Prompt,
            ToolPolicyVersion = CoachPolicyFingerprint.ToolPolicy,
            ModelPolicyVersion = _coach.Implementation.ToString().ToLowerInvariant()
        };

    public async Task<CoachOperationResult<CoachSessionResponse>> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var gate = await RequireAvailableAsync<CoachSessionResponse>(cancellationToken).ConfigureAwait(false);
        if (gate.Denied is { } denied)
        {
            return denied;
        }

        var userProfileId = gate.UserProfileId!;
        var load = await _sessions.LoadAsync(userProfileId, sessionId, cancellationToken).ConfigureAwait(false);
        if (!load.IsUsable)
        {
            return NotFoundFor<CoachSessionResponse>(load.Status);
        }

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        return CoachOperationResult<CoachSessionResponse>.Ok(
            await BuildSessionResponseAsync(userProfileId, load.Session!, plan, cancellationToken).ConfigureAwait(false));
    }

    public async Task<CoachOperationResult<bool>> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var userProfileId = RequireUserProfileId();

        // Deletion deliberately ignores the feature flag: a learner must always be able to
        // erase conversation state, even after the coach has been switched off for them.
        _runs.Cancel(userProfileId, sessionId);
        _idempotency.Clear(userProfileId, sessionId);

        var deleted = await _sessions.DeleteAsync(userProfileId, sessionId, cancellationToken).ConfigureAwait(false);
        return deleted
            ? CoachOperationResult<bool>.Ok(true)
            : CoachOperationResult<bool>.Problem(
                CoachOperationStatus.SessionNotFound, CoachProblemTypes.SessionNotFound, "No coach session with that id.");
    }

    public Task<CoachOperationResult<bool>> CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var userProfileId = RequireUserProfileId();

        // Cancelling is safe to answer without a store read: the key is scoped to the owner,
        // so another learner's run is unreachable.
        var cancelled = _runs.Cancel(userProfileId, sessionId);
        return Task.FromResult(CoachOperationResult<bool>.Ok(cancelled));
    }

    // ---------------------------------------------------------------- turns

    public Task<CoachOperationResult<CoachTurnResponse>> SubmitTurnAsync(
        string sessionId,
        CoachTurnRequest request,
        CancellationToken cancellationToken = default) =>
        SubmitTurnAsync(sessionId, request, CoachTurnExecutionContext.Default, cancellationToken);

    public async Task<CoachOperationResult<CoachTurnResponse>> SubmitTurnAsync(
        string sessionId,
        CoachTurnRequest request,
        CoachTurnExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        request ??= new CoachTurnRequest { InputKind = CoachTurnInputKind.Text };
        context ??= CoachTurnExecutionContext.Default;

        // Turn-scoped. Cleared here so a refusal observed on a previous turn in the same request
        // scope can never be attributed to this one.
        _turnViolation = null;
        _answerShapeRefused = false;
        _turnHandshake = request.ClientCapabilities;
        _turnDispute = OpenOrCarryDispute(request, context);

        var gate = await RequireAvailableAsync<CoachTurnResponse>(cancellationToken).ConfigureAwait(false);
        if (gate.Denied is { } denied)
        {
            return denied;
        }

        var userProfileId = gate.UserProfileId!;

        if (!context.BypassProcessIdempotency
            && _idempotency.TryGet(userProfileId, sessionId, request.ClientTurnId, out var replay))
        {
            return CoachOperationResult<CoachTurnResponse>.Ok(replay);
        }

        var load = await _sessions.LoadAsync(userProfileId, sessionId, cancellationToken).ConfigureAwait(false);
        if (!load.IsUsable)
        {
            return NotFoundFor<CoachTurnResponse>(load.Status);
        }

        var session = load.Session!;

        // A turn identity the server issues to itself may not arrive on a request. Checked before
        // the scope is entered, because entering is what binds the identity to the conversation
        // and a bound reserved value is already the harm: turn identities are unique per
        // conversation, so a request naming a reversal's identity would occupy the slot that
        // reversal needs and leave the learner's Undo failing on a row somebody else wrote.
        // Refused rather than quietly replaced, so a client cannot half-succeed with a turn
        // identity that is not the one its replay key names.
        if (Operations.CoachWriteTurnScope.IsReservedTurnId(request.ClientTurnId?.Trim()))
        {
            _logger.LogWarning("[Coach] A turn was refused: the client supplied a reserved turn identity.");
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "That turn identifier is reserved. Send a different one, or none.");
        }

        // A write proposal has to name the conversation it belongs to, and the model must not be
        // the one naming it. This is the single choke point every turn passes through, so the
        // conversation is recorded here, from the routed session id rather than from anything the
        // model or the request body supplied. A tool that runs outside this window finds the scope
        // unset and refuses.
        _writeTurn?.Enter(sessionId, TrimTurnId(request.ClientTurnId));

        var validation = ValidateTurnInput(request);
        if (validation is not null)
        {
            return validation;
        }

        // A structured UI constraint action is deterministic: the learner set the value in the
        // UI, so it is a direct request and never reaches the model.
        if (request.InputKind == CoachTurnInputKind.ConstraintAction)
        {
            return await ApplyDirectConstraintActionAsync(userProfileId, session, request, cancellationToken)
                .ConfigureAwait(false);
        }

        // Clear typed yes/no about the open suggestion is decided here, before any model work.
        //
        // The model used to run first and the application then re-checked its answer. That made
        // an unmistakable "Yes, update it" depend on the model echoing the suggestion id in the
        // exact intent shape; when it did not, the turn was refused as a validation failure and
        // the learner's plain yes did nothing. The classifier is already the authority for this
        // decision, so it runs first: the model is not consulted, no run is charged, and no
        // tokens are spent on a question the server can answer on its own.
        var shortcut = await TryHandleTypedDecisionAsync(userProfileId, session, request, cancellationToken)
            .ConfigureAwait(false);
        if (shortcut is not null)
        {
            return shortcut;
        }

        // Deterministic latest-study route: the learner is asking when they last practiced.
        // Runs before the model, budget, and run-lease — no tokens, no run charged.
        var latestStudy = CoachLatestStudyClassifier.Classify(request.Text);
        if (latestStudy is not null && _practiceHistory is not null)
        {
            return await HandleLatestStudyAsync(
                userProfileId, session, request, latestStudy, cancellationToken).ConfigureAwait(false);
        }

        if (_runs.IsRunning(userProfileId, sessionId))
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.RunInProgress, CoachProblemTypes.RunInProgress,
                "A coach run for this session is still in progress.");
        }

        if (!_agentFactory.IsModelAvailable)
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.ModelUnavailable, CoachProblemTypes.ToolFailure,
                "The coach model is not configured on this server.");
        }

        var lease = await _budget
            .TryStartRunAsync(userProfileId, _dateContext.UserLocalDate, cancellationToken)
            .ConfigureAwait(false);

        if (!lease.Acquired)
        {
            _telemetry.RecordRunDenied(_coach.Implementation, lease.DeniedReason ?? CoachStopReason.RateLimit);

            var refusal = lease.DeniedReason == CoachStopReason.ConcurrencyLimit
                ? CoachOperationResult<CoachTurnResponse>.Problem(
                    CoachOperationStatus.RunInProgress, CoachProblemTypes.RunInProgress,
                    "A coach run for this learner is still in progress.")
                : CoachOperationResult<CoachTurnResponse>.Problem(
                    CoachOperationStatus.RateLimited, CoachProblemTypes.RateLimited,
                    "The coach run limit for this period has been reached.");

            // This is the authoritative early-return boundary for a denied run, and the only
            // place a run limit is observable: the turn never reaches ObserveTurnOutcomeAsync,
            // so without this a learner hitting the cap every day is invisible to the rollup.
            // The problem result above is already built, so the observation cannot influence what
            // the learner is told, and the concurrency denial deliberately records nothing —
            // another run holding the slot is not a gap in what the coach can do.
            await ObserveRunDeniedAsync(lease.DeniedReason, cancellationToken).ConfigureAwait(false);

            return refusal;
        }

        await using var runLease = lease.Lease!;
        using var registration = _runs.Register(userProfileId, sessionId, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var agentResult = await RunAgentAsync(session, load.AgentSessionJson, request, context, registration.Token)
            .ConfigureAwait(false);
        stopwatch.Stop();

        // A malformed AgentSession signals back before any model call. Propagate to the
        // conversation layer which owns the ledger rebuild.
        if (agentResult.RequiresRebuild)
        {
            return CoachOperationResult<CoachTurnResponse>.NeedsRebuild();
        }

        await runLease.RecordUsageAsync(agentResult.Usage, CancellationToken.None).ConfigureAwait(false);
        if (agentResult.Usage.InputTokens > 0 || agentResult.Usage.OutputTokens > 0)
        {
            await _usage.RecordRunAsync(
                userProfileId,
                _dateContext.UserLocalDate,
                agentResult.Usage.InputTokens,
                agentResult.Usage.OutputTokens,
                agentResult.Usage.EstimatedCostUsd,
                CancellationToken.None).ConfigureAwait(false);
        }

        // Stage boundary: the model has answered, nothing has been applied. A cancel recorded on
        // another replica is only actionable here, and acting on it here is what keeps a cancelled
        // turn free of plan effects instead of merely fast.
        if (context.IsCancelRequested is { } cancelProbe
            && await cancelProbe(cancellationToken).ConfigureAwait(false))
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.RunCancelled,
                CoachProblemTypes.RunCancelled,
                "That turn was cancelled.");
        }

        var result = await ReduceAgentResultAsync(userProfileId, session, request, agentResult, cancellationToken)
            .ConfigureAwait(false);

        _telemetry.RecordRunCompleted(
            activity: null,
            _coach.Implementation,
            result.Value?.Status ?? CoachTurnStatus.Failed,
            result.Value?.StopReason ?? CoachStopReason.Failed,
            stopwatch.Elapsed,
            modelIterations: 1,
            toolCalls: 0,
            agentResult.Usage);

        if (result.IsOk && result.Value is not null && !context.BypassProcessIdempotency)
        {
            _idempotency.Store(userProfileId, sessionId, request.ClientTurnId, result.Value);
        }

        // The opportunity ledger, last. Everything the learner will see has already been decided
        // and stored; this observes it and cannot change it. The recorder never throws by
        // contract, and the awaited call is deliberately not guarded by a branch on its outcome
        // — there is no outcome to branch on.
        await ObserveTurnOutcomeAsync(userProfileId, session, request, agentResult, result, cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Records at most one ledger row describing what this turn could not do for the learner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Response-neutral by construction.</b> Called after <c>_idempotency.Store</c>, so the
    /// response bytes are already fixed and already replayable; it takes the result by value and
    /// returns nothing; and every failure inside it is swallowed by the recorder. A test asserts
    /// the turn response is byte-identical with capture on and off.
    /// </para>
    /// <para>
    /// The detector runs first and its five authoritative conjuncts decide precedence: a learner
    /// whose clear answer bound to nothing is a more specific — and more actionable — statement
    /// than "the turn asked another question".
    /// </para>
    /// </remarks>
    private async Task ObserveTurnOutcomeAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachAgentTurnResult agentResult,
        CoachOperationResult<CoachTurnResponse> result,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = result.Value;
            var stopReason = response?.StopReason ?? CoachStopReason.Failed;

            Opportunities.Detection.CoachReferentLoss? referentLoss = null;

            if (_unboundAnswers is not null
                && !string.IsNullOrWhiteSpace(userProfileId)
                && CoachOwner.TryCreate(userProfileId, null, out var owner))
            {
                // The model's declared intent for this turn. It is what lets a completed turn be
                // told apart from ordinary tutoring: a completed turn that declared a settings
                // change and produced nothing is a dropped answer, a completed pedagogical answer
                // is the coach doing its job. See CoachActionIntent.
                var declaredIntent = agentResult.Intent?.Kind;

                // Asked only when a proposal could plausibly be open. The write ledger read is an
                // indexed existence check, and the detector's cheap conjuncts run before it.
                var hasOpenProposal = _writeLedger is not null
                    && _unboundAnswers.IsUnboundDecisiveAnswer(
                        request.InputKind, request.Text, session.PendingSuggestionId,
                        hasOpenWriteProposal: false,
                        hasChangeReceipt: response?.ChangeReceipt is not null,
                        hasWriteOperation: response?.WriteOperation is not null,
                        stopReason,
                        declaredIntent)
                    && await _writeLedger.HasOpenProposalAsync(session.Id, cancellationToken)
                        .ConfigureAwait(false);

                referentLoss = await _unboundAnswers.DetectAsync(
                    owner,
                    session.Id,
                    request.InputKind,
                    request.Text,
                    session.PendingSuggestionId,
                    hasOpenProposal,
                    response?.ChangeReceipt is not null,
                    response?.WriteOperation is not null,
                    stopReason,
                    declaredIntent,
                    cancellationToken).ConfigureAwait(false);

                // Enum values and booleans only — no identifiers, no message text. This is the
                // line that was missing when the ledger came back empty after a reproduced
                // failure: without it, telling "the detector declined" apart from "the detector
                // never ran" needed a debugger, and the shape that decided it was unknowable
                // after the fact because the declared intent is not persisted anywhere.
                if (referentLoss is null)
                {
                    _logger.LogDebug(
                        "[Coach] Turn outcome observed, no referent loss. StopReason={StopReason} "
                        + "Intent={Intent} Receipt={HasReceipt} WriteOperation={HasWriteOperation} "
                        + "PendingSuggestion={HasPendingSuggestion} OpenProposal={HasOpenProposal}",
                        stopReason,
                        declaredIntent,
                        response?.ChangeReceipt is not null,
                        response?.WriteOperation is not null,
                        !string.IsNullOrWhiteSpace(session.PendingSuggestionId),
                        hasOpenProposal);
                }
            }

            var signal = Opportunities.Mapping.CoachTurnOutcomeOpportunityMapper.Map(
                referentLoss,
                stopReason,                agentResult.Intent?.Kind,
                _turnViolation,
                session.Id,
                TrimTurnId(request.ClientTurnId),
                turnOperationId: null,
                modelOutputUnreadable: agentResult.Outcome
                    is CoachAgentOutcome.InvalidOutput or CoachAgentOutcome.OutputLimitReached,
                answerShapeRefused: _answerShapeRefused);

            if (signal is { } value)
            {
                await _opportunities.RecordAsync(value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Belt and braces, and every exception including OperationCanceledException. The
            // recorder already swallows everything, so reaching here means the detector or the
            // ledger read failed — neither of which the learner should ever find out about,
            // because their turn already succeeded and its response is already stored.
            //
            // Cancellation is caught for that reason and no other: the learner's own operation
            // observed the token upstream, so preserving its semantics is already done. A token
            // cancelled during observation must not retroactively fail a turn that succeeded.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] A turn outcome could not be observed; the turn is unaffected. " +
                "Category={FailureCategory} InnerDepth={InnerDepth}",
                facts.Category,
                facts.InnerDepth);
        }
    }

    /// <summary>
    /// Records the one content-free row a denied run produces, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response is already built when this runs.</b> The caller constructs its problem
    /// result first and returns it immediately after, so this cannot change what the learner is
    /// told and cannot make them wait for anything but one indexed upsert on a private scope.
    /// </para>
    /// <para>
    /// <b>Aggregate-only, always.</b> A learner reaching the daily cap is a capacity fact, not a
    /// conversation worth reading: the row carries a kind, a capability code, and a stop reason,
    /// and the recorder strips every pointer regardless. "How many learners hit the cap, how
    /// often" is the entire signal.
    /// </para>
    /// <para>
    /// <b>A concurrency denial records nothing.</b> Another run already holding the slot is the
    /// budget working, not a gap in what the coach can do — the same reasoning
    /// <c>CoachTurnOutcomeOpportunityMapper</c> applies to
    /// <see cref="CoachStopReason.ConcurrencyLimit"/> at the turn boundary. Recording it here
    /// would contradict that mapping and inflate the rollup with normal contention.
    /// </para>
    /// <para>
    /// Identity is not passed in: the recorder resolves the trusted owner itself and no-ops when
    /// there is none, which is the same fail-closed rule every other call site relies on.
    /// </para>
    /// </remarks>
    private async Task ObserveRunDeniedAsync(
        CoachStopReason? deniedReason,
        CancellationToken cancellationToken)
    {
        if (deniedReason == CoachStopReason.ConcurrencyLimit)
        {
            return;
        }

        try
        {
            await _opportunities.RecordAsync(
                new Opportunities.CoachOpportunitySignal(
                    Opportunities.CoachOpportunityKind.CapacityOrBudgetRefusal,
                    Opportunities.CoachOpportunityCapabilityCodes.DailyRunLimit,
                    Opportunities.CoachOpportunitySurface.TurnOutcome,
                    Opportunities.CoachOpportunityDisposition.AggregateOnly,
                    StopReason: CoachStopReason.RateLimit),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Including OperationCanceledException. The learner's refusal is already decided and
            // is about to be returned; letting a cancelled or failed observation replace it would
            // turn a clear "you have reached your limit" into an unexplained error.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] A denied run could not be observed; the response is unaffected. " +
                "Category={FailureCategory} InnerDepth={InnerDepth}",
                facts.Category,
                facts.InnerDepth);
        }
    }

    public async Task<CoachOperationResult<CoachTurnResponse>> AcceptSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new CoachSuggestionDecisionRequest();

        var gate = await RequireAvailableAsync<CoachTurnResponse>(cancellationToken).ConfigureAwait(false);
        if (gate.Denied is { } denied)
        {
            return denied;
        }

        var userProfileId = gate.UserProfileId!;
        if (_idempotency.TryGet(userProfileId, sessionId, request.ClientTurnId, out var replay))
        {
            return CoachOperationResult<CoachTurnResponse>.Ok(replay);
        }

        var load = await _sessions.LoadAsync(userProfileId, sessionId, cancellationToken).ConfigureAwait(false);
        if (!load.IsUsable)
        {
            return NotFoundFor<CoachTurnResponse>(load.Status);
        }

        return await AcceptPendingCoreAsync(
            gate.UserProfileId!,
            load.Session!,
            suggestionId,
            request.ExpectedPlanVersion,
            request.ClientTurnId,
            cancellationToken).ConfigureAwait(false);
    }

    // ─── Deterministic latest-study route ───────────────────────────────────

    /// <summary>
    /// Handles a latest-study question or correction without calling the model.
    /// Calls the same <see cref="IPracticeHistoryQueries.GetLastPracticeUtcAsync"/> application
    /// query as the tool path, composes a deterministic answer with proper evidence scope,
    /// and completes the durable turn pipeline.
    /// </summary>
    private async Task<CoachOperationResult<CoachTurnResponse>> HandleLatestStudyAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachLatestStudyClassifier.LatestStudyMatch match,
        CancellationToken cancellationToken)
    {
        var isCorrection = match.Kind == CoachLatestStudyClassifier.LatestStudyMatchKind.Correction;

        // Call the same application query the tool uses
        DateTime? lastUtc;
        try
        {
            lastUtc = await _practiceHistory!.GetLastPracticeUtcAsync(userProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Coach] Session {SessionId}: latest-study data read failed.", session.Id);
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.Failed, CoachProblemTypes.ToolFailure,
                "Unable to read practice history.");
        }

        // Convert to user-local date, same as PracticeHistorySummaryTool
        DateOnly? lastLocal = lastUtc.HasValue ? _dateContext.ToUserLocal(lastUtc.Value) : null;
        int? daysSince = lastLocal.HasValue
            ? _dateContext.UserLocalDate.DayNumber - lastLocal.Value.DayNumber
            : null;

        // Resolve language profile for the answer
        var langProfile = await _languages.ResolveAsync(cancellationToken).ConfigureAwait(false);

        // Compose the deterministic answer text
        var answerText = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            lastLocal, daysSince, isCorrection, langProfile.DisplayLanguageTag);

        // Build a valid CoachAnswerDto
        var answer = new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.StudyStrategy,
            Blocks =
            [
                new CoachAnswerBlockDto
                {
                    Kind = CoachAnswerBlockKind.Answer,
                    Label = null,
                    Spans =
                    [
                        new CoachAnswerSpanDto
                        {
                            Text = answerText,
                            Language = CoachLanguageRole.Display,
                            LanguageTag = langProfile.DisplayLanguageTag
                        }
                    ]
                }
            ],
            PlainText = answerText,
            TargetLanguageTag = langProfile.TargetLanguageTag,
            DisplayLanguageTag = langProfile.DisplayLanguageTag,
            EndsWithRecallQuestion = false
        };

        // Build evidence scope matching the tool path
        var scope = new Tools.CoachResultScope
        {
            Coverage = Tools.CoachScopeCoverage.DerivedProjection,
            Order = Tools.CoachScopeOrder.NotApplicable,
            OrderHonored = true,
            TieBreak = Tools.CoachScopeTieBreak.NotApplicable,
            Filters = Tools.CoachScopeFilters.OwnerScoped,
            MinimumEvidence = Tools.CoachScopeMinimumEvidence.None,
            AsOfUtc = _dateContext.UtcNow,
            ReturnedCount = lastLocal.HasValue ? 1 : 0,
            MatchedCount = lastLocal.HasValue ? 1 : 0,
            EligiblePopulationCount = lastLocal.HasValue ? 1 : 0,
            WithheldCount = 0,
            WithheldReason = Tools.CoachScopeWithheldReason.None,
            Truncated = false,
            DefinitionCode = Tools.CoachScopeDefinition.LatestPracticeSummary,
            ClockBasis = Tools.CoachScopeClockBasis.LearnerLocalDay,
            ReferenceMode = Tools.CoachScopeReferenceMode.AsOfInstant
        };

        // Build evidence DTO from the scope
        var evidence = new List<CoachEvidenceDto>
        {
            new()
            {
                Kind = CoachEvidenceKind.PracticeBalance,
                Label = "Practice balance",
                Summary = lastLocal.HasValue
                    ? $"Last practice: {lastLocal.Value:yyyy-MM-dd}"
                    : "No practice records found",
                WindowStartDate = lastLocal ?? _dateContext.UserLocalDate,
                WindowEndDate = _dateContext.UserLocalDate,
                Coverage = CoachEvidenceCoverage.DerivedProjection,
                Order = CoachEvidenceOrder.NotApplicable,
                DefinitionCode = CoachDefinitionCode.LatestPracticeSummary,
                AsOfUtc = _dateContext.UtcNow,
                Values = lastLocal.HasValue && daysSince.HasValue
                    ? [new CoachEvidenceValueDto
                    {
                        Code = CoachEvidenceValueCode.RowsRead,
                        Label = "Rows read",
                        Value = 1,
                        Unit = CoachEvidenceUnit.Items
                    }]
                    : []
            }
        };

        // Complete the durable turn pipeline
        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

        await RecordTurnOutcomeAsync(
            userProfileId, session, CoachSessionStatus.Active,
            stopReason: null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[Coach] Session {SessionId}: deterministic latest-study route; " +
            "kind={MatchKind}, hasData={HasData}.",
            session.Id, match.Kind, lastLocal.HasValue);

        var response = await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Completed, CoachStopReason.Completed,
            CoachSessionStatus.Active,
            messages: [CoachMessage(CoachMessageKind.PedagogicalAnswer, answer.PlainText)],
            pendingSuggestion: await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
            receipt: null,
            evidence: evidence,
            clarifyingQuestion: null,
            cancellationToken,
            answer: answer).ConfigureAwait(false);

        _idempotency.Store(userProfileId, session.Id, request.ClientTurnId, response);

        return CoachOperationResult<CoachTurnResponse>.Ok(response);
    }

    /// <summary>
    /// Answers a clear typed yes/no about the open suggestion without calling the model.
    /// Returns null when the turn is not one of those, so it continues to the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions must all hold before a typed word is allowed to decide anything: the
    /// session must hold a pending suggestion, the client must name that exact suggestion, and
    /// <see cref="CoachExplicitAcceptanceClassifier"/> must read the learner's own words as an
    /// unmistakable yes or no. A mismatched id, a missing id, or anything the classifier has
    /// not been taught falls through to the model, which can then ask for clarification. Under
    /// no circumstance does an unclear answer write.
    /// </para>
    /// <para>
    /// Only free text is handled here. A chip is already a structured tap and the client posts
    /// it to the tapped accept/reject routes.
    /// </para>
    /// <para>
    /// <b>Budget:</b> this path charges no run and records no tokens. The daily and weekly caps
    /// exist to bound model cost, and nothing here calls a model. Charging a run would also
    /// make typing "yes" cost more than tapping Accept for the same learner action, and could
    /// strand a pending suggestion the learner is no longer allowed to answer once they hit
    /// the cap.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<CoachTurnResponse>?> TryHandleTypedDecisionAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InputKind != CoachTurnInputKind.Text
            || string.IsNullOrWhiteSpace(session.PendingSuggestionId)
            || !string.Equals(request.PendingSuggestionId, session.PendingSuggestionId, StringComparison.Ordinal))
        {
            return null;
        }

        switch (_acceptance.Classify(request.Text))
        {
            case CoachExplicitAcceptance.Affirmative:
                return await AcceptPendingCoreAsync(
                    userProfileId,
                    session,
                    session.PendingSuggestionId!,
                    request.ExpectedPlanVersion,
                    request.ClientTurnId,
                    cancellationToken).ConfigureAwait(false);

            case CoachExplicitAcceptance.Negative:
                return await RejectPendingCoreAsync(
                    userProfileId,
                    session,
                    session.PendingSuggestionId!,
                    request.ClientTurnId,
                    cancellationToken).ConfigureAwait(false);

            default:
                // Ambiguous or mixed signals: the model gets the turn and can ask one focused
                // question. The suggestion stays open either way.
                return null;
        }
    }

    /// <summary>
    /// The one deterministic acceptance path. A tapped Accept and a clear typed "yes" both
    /// land here, so they cannot drift: same stored delta, same ownership and preview checks,
    /// same stale-plan handling, same revision, same receipt, same idempotency key.
    /// </summary>
    private async Task<CoachOperationResult<CoachTurnResponse>> AcceptPendingCoreAsync(
        string userProfileId,
        CoachSession session,
        string suggestionId,
        string? expectedPlanVersion,
        string? clientTurnId,
        CancellationToken cancellationToken)
    {
        // Applies the delta the server stored when it made the suggestion — not one supplied
        // by the client and not one re-derived by the model on this turn. The focus selection is
        // stored with it, so acceptance replays the exact words the preview showed instead of
        // resolving again against a queue that may have moved.
        var stored = CoachPendingSuggestionEnvelope.TryRead(await _sessions
            .GetPendingSuggestionPayloadAsync(userProfileId, session.Id, suggestionId, cancellationToken)
            .ConfigureAwait(false));

        var delta = stored?.Delta;

        if (delta is null)
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.SuggestionNotFound, CoachProblemTypes.SuggestionNotFound,
                "That suggestion is no longer pending.");
        }

        var applied = await ApplyDeltaAsync(
            userProfileId,
            session,
            delta,
            CoachRevisionSource.AcceptedSuggestion,
            CoachIntentKind.AcceptPendingSuggestion,
            expectedPlanVersion,
            clientTurnId,
            AppliedSuggestionMessage,
            cancellationToken,
            requireOwnedPreview: true,
            focusSelection: stored?.FocusSelection).ConfigureAwait(false);

        // A refusal that is still a well-formed turn (an unowned preview, an answer-leak
        // refusal) comes back as an Ok result carrying a Rejected turn, so IsOk alone cannot
        // say the suggestion was answered. Only a completed turn clears it; a stale plan
        // version, a failed validation, or a refused preview leaves the offer exactly where it
        // was so the learner can retry against the fresh plan version.
        if (!applied.IsOk || applied.Value is not { Status: CoachTurnStatus.Completed })
        {
            return applied;
        }

        await _sessions.ClearPendingSuggestionAsync(userProfileId, session.Id, cancellationToken)
            .ConfigureAwait(false);
        _telemetry.RecordSuggestionOutcome(CoachAcceptanceState.Accepted);
        _idempotency.Store(userProfileId, session.Id, clientTurnId, applied.Value!);

        return applied;
    }

    public async Task<CoachOperationResult<CoachTurnResponse>> RejectSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new CoachSuggestionDecisionRequest();

        var gate = await RequireAvailableAsync<CoachTurnResponse>(cancellationToken).ConfigureAwait(false);
        if (gate.Denied is { } denied)
        {
            return denied;
        }

        var userProfileId = gate.UserProfileId!;
        if (_idempotency.TryGet(userProfileId, sessionId, request.ClientTurnId, out var replay))
        {
            return CoachOperationResult<CoachTurnResponse>.Ok(replay);
        }

        var load = await _sessions.LoadAsync(userProfileId, sessionId, cancellationToken).ConfigureAwait(false);
        if (!load.IsUsable)
        {
            return NotFoundFor<CoachTurnResponse>(load.Status);
        }

        return await RejectPendingCoreAsync(
            gate.UserProfileId!,
            load.Session!,
            suggestionId,
            request.ClientTurnId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one deterministic rejection path, shared by a tapped Not-now and a clear typed
    /// "no". Clears the pending suggestion and writes nothing.
    /// </summary>
    private async Task<CoachOperationResult<CoachTurnResponse>> RejectPendingCoreAsync(
        string userProfileId,
        CoachSession session,
        string suggestionId,
        string? clientTurnId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(session.PendingSuggestionId, suggestionId, StringComparison.Ordinal))
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.SuggestionNotFound, CoachProblemTypes.SuggestionNotFound,
                "That suggestion is no longer pending.");
        }

        await _sessions.ClearPendingSuggestionAsync(userProfileId, session.Id, cancellationToken)
            .ConfigureAwait(false);

        // Explicit, because clearing an offer only resets the status when the session was on
        // SuggestionPending — and an ambiguous answer may have moved it to AwaitingClarification
        // while the offer stayed open.
        await RecordTurnOutcomeAsync(
            userProfileId, session, CoachSessionStatus.Active, stopReason: null, cancellationToken)
            .ConfigureAwait(false);

        _telemetry.RecordSuggestionOutcome(CoachAcceptanceState.Rejected);

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var response = await BuildTurnResponseAsync(
            userProfileId,
            session,
            plan,
            CoachTurnStatus.Completed,
            CoachStopReason.Completed,
            CoachSessionStatus.Active,
            messages: [CoachMessage(CoachMessageKind.Notice, RejectedSuggestionMessage)],
            pendingSuggestion: null,
            receipt: null,
            evidence: [],
            clarifyingQuestion: null,
            cancellationToken).ConfigureAwait(false);

        _idempotency.Store(userProfileId, session.Id, clientTurnId, response);
        return CoachOperationResult<CoachTurnResponse>.Ok(response);
    }

    // ---------------------------------------------------------------- undo

    public async Task<CoachOperationResult<CoachTurnResponse>> UndoAsync(
        string sessionId,
        CoachUndoRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new CoachUndoRequest();

        var gate = await RequireAvailableAsync<CoachTurnResponse>(cancellationToken).ConfigureAwait(false);
        if (gate.Denied is { } denied)
        {
            return denied;
        }

        var userProfileId = gate.UserProfileId!;
        if (_idempotency.TryGet(userProfileId, sessionId, request.ClientTurnId, out var replay))
        {
            return CoachOperationResult<CoachTurnResponse>.Ok(replay);
        }

        var load = await _sessions.LoadAsync(userProfileId, sessionId, cancellationToken).ConfigureAwait(false);
        if (!load.IsUsable)
        {
            return NotFoundFor<CoachTurnResponse>(load.Status);
        }

        var session = load.Session!;
        var revisions = await _sessions.GetRevisionsAsync(userProfileId, sessionId, cancellationToken)
            .ConfigureAwait(false);

        // Undo only ever walks back one applied revision. There is no redo: re-applying is a
        // new, explicit request.
        var target = revisions
            .Where(r => !r.IsUndone && r.Source != CoachRevisionSource.Undo)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault(r => string.IsNullOrWhiteSpace(request.RevisionId)
                                 || string.Equals(r.Id, request.RevisionId, StringComparison.Ordinal));

        if (target is null)
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.NothingToUndo, CoachProblemTypes.NothingToUndo,
                "There is no coach change to undo.");
        }

        var envelope = CoachNormalizedJson.Deserialize<CoachRevisionSnapshotEnvelope>(target.BeforePlanSnapshotJson);
        if (envelope?.Restore is null)
        {
            _logger.LogWarning(
                "[Coach] Revision {RevisionNumber} has no restore snapshot; refusing to undo.", target.RevisionNumber);
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.NothingToUndo, CoachProblemTypes.NothingToUndo,
                "There is no coach change to undo.");
        }

        var revision = await _planService.UndoCoachRevisionAsync(
            new CoachPlanUndoRequest
            {
                TargetSnapshot = envelope.Restore,
                ExpectedPlanVersion = request.ExpectedPlanVersion,
                OperationKey = request.ClientTurnId,
                SessionId = sessionId,
                RevisionId = target.Id
            },
            cancellationToken).ConfigureAwait(false);

        var failure = MapRevisionFailure(revision);
        if (failure is not null)
        {
            _telemetry.RecordPlanRevision(CoachRevisionSource.Undo, success: false, 0, 0);
            return failure;
        }

        var deltaForAudit = CoachNormalizedJson.Deserialize<CoachConstraintDeltaDto>(target.AcceptedConstraintDeltaJson)
            ?? new CoachConstraintDeltaDto();

        // Undo restores the plan AND the constraint set that produced it. Leaving the session
        // on the post-apply constraints is what let a later suggestion merge against minutes
        // the learner had already undone: its preview showed a 5-minute plan while its
        // normalized delta disclosed only "audio allowed", so accepting would silently
        // re-apply a constraint nobody had agreed to on that turn.
        var constraintsBeforeUndo = ActiveConstraints(session);
        var focusBeforeUndo = ActiveFocusSelection(session);
        var restore = ReadStoredRestore(target);

        if (restore.Kind == CoachRestoreKind.Unreadable)
        {
            await RecordTurnOutcomeAsync(
                userProfileId, session, StatusWithPending(session),
                CoachStopReason.ValidationFailed, cancellationToken).ConfigureAwait(false);

            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.InvalidConstraint, CoachProblemTypes.PlanValidationFailed,
                "That change cannot be undone on this server.");
        }

        // The selection is restored from the same side of the same revision as the constraints, so
        // the code, the projection, and the identifiers cannot disagree afterwards. A row that
        // predates the artifact keeps the focus in force: guessing which words a past plan used
        // would be worse than leaving it alone.
        var restoredConstraints = restore.Constraints;
        var restoredFocus = restore.Kind == CoachRestoreKind.Legacy
            ? focusBeforeUndo
            : CoachActiveStateEnvelope.TryRead(session.ActiveConstraintsJson)?.Rehydrate(restore.Focus)
              ?? restore.Focus;

        // The restored constraint set came from the audit, which carries no words. Re-attach the
        // ones this session remembers so the learner sees the set they are being returned to.
        if (restoredConstraints?.VocabularyFocus is not null)
        {
            restoredConstraints = WithFocusWords(restoredConstraints, restoredFocus);
        }

        await RecordTurnOutcomeAsync(
            userProfileId, session,
            CoachSessionStatus.Active,
            stopReason: null,
            cancellationToken,
            activeConstraints: restoredConstraints,
            focusSelection: restoredFocus).ConfigureAwait(false);

        var undoRecord = await AppendRevisionAsync(
            userProfileId, session, revision, deltaForAudit,
            CoachRevisionSource.Undo, CoachIntentKind.NoChange,
            beforeConstraints: constraintsBeforeUndo,
            afterConstraints: restoredConstraints ?? constraintsBeforeUndo,
            cancellationToken,
            beforeFocus: focusBeforeUndo,
            afterFocus: restoredFocus).ConfigureAwait(false);

        if (undoRecord is not null)
        {
            await _sessions.MarkRevisionUndoneAsync(userProfileId, target.Id, undoRecord.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        _telemetry.RecordPlanRevision(
            CoachRevisionSource.Undo,
            revision.IsApplied,
            revision.PreservedCompletedCount,
            revision.PreservedInProgressCount);

        // The state the learner is now in, computed here rather than re-read, so the response does
        // not depend on the session entity having been refreshed.
        var constraintsAfterUndo = restoredConstraints ?? constraintsBeforeUndo;

        var receipt = BuildReceipt(
            undoRecord,
            revision,
            deltaForAudit,
            "Restored the previous remaining items. Completed work and logged minutes were unchanged.",
            canUndo: false,
            focusChange: new CoachVocabularyFocusChangeDto
            {
                Status = constraintsAfterUndo.VocabularyFocus is null
                    ? constraintsBeforeUndo.VocabularyFocus is null
                        ? CoachVocabularyFocusStatus.Unchanged
                        : CoachVocabularyFocusStatus.Cleared
                    : CoachVocabularyFocusStatus.Restored,
                Focus = constraintsAfterUndo.VocabularyFocus
            });

        var response = await BuildTurnResponseAsync(
            userProfileId,
            session,
            revision.After ?? await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false),
            CoachTurnStatus.Completed,
            CoachStopReason.Completed,
            CoachSessionStatus.Active,
            messages: [CoachMessage(CoachMessageKind.Receipt, "Restored the previous remaining items.")],
            pendingSuggestion: null,
            receipt: receipt,
            evidence: [],
            clarifyingQuestion: null,
            cancellationToken: cancellationToken,
            constraintsOverride: constraintsAfterUndo).ConfigureAwait(false);

        _idempotency.Store(userProfileId, sessionId, request.ClientTurnId, response);
        return CoachOperationResult<CoachTurnResponse>.Ok(response);
    }

    // ---------------------------------------------------------------- reduction

    private async Task<CoachAgentTurnResult> RunAgentAsync(
        CoachSession session,
        string? agentSessionJson,
        CoachTurnRequest request,
        CoachTurnExecutionContext context,
        CancellationToken cancellationToken)
    {
        var constraints = CoachActiveStateEnvelope.TryRead(session.ActiveConstraintsJson)?.Constraints
            ?? CoachConstraintMapper.Default(15);

        // The model is told that a focus is in force and what kind it is. It is never told which
        // words were chosen: those are the learner's own vocabulary, and the whole point of this
        // design is that the model does not select or see them. Handing the projection to the
        // agent would put a due word in front of it by the back door.
        if (constraints.VocabularyFocus is { } focus)
        {
            constraints = new CoachConstraintSetDto
            {
                AvailableMinutes = constraints.AvailableMinutes,
                AudioAllowed = constraints.AudioAllowed,
                SpeechAllowed = constraints.SpeechAllowed,
                TypingAllowed = constraints.TypingAllowed,
                SkillEmphasis = constraints.SkillEmphasis,
                GoalTag = constraints.GoalTag,
                GoalHorizonDays = constraints.GoalHorizonDays,
                EnergyLevel = constraints.EnergyLevel,
                VocabularyFocus = new CoachVocabularyFocusDto
                {
                    FocusCode = focus.FocusCode,
                    DisplayLabel = focus.DisplayLabel,
                    EligibleCount = 0,
                    SelectedCount = 0,
                    Words = Array.Empty<CoachVocabularyFocusWordDto>()
                }
            };
        }

        var pendingDelta = string.IsNullOrEmpty(session.PendingSuggestionDeltaJson)
            ? null
            : CoachNormalizedJson.Deserialize<CoachConstraintDeltaDto>(session.PendingSuggestionDeltaJson);

        var options = _options.CurrentValue;

        // Saved preferences are selected fresh on every turn, never cached into the checkpoint's
        // own reasoning. Selection is driven by trusted application state — the owner from the
        // authenticated scope, the target language from the profile, a closed category from plan
        // state — and the learner's text is consulted only to drop kinds their current message has
        // already overridden. A memory outage returns null and the turn proceeds without it.
        var memoryBlock = _memory is null
            ? null
            : await _memory.BuildContextBlockAsync(
                session.UserProfileId,
                (await _languages.ResolveAsync(cancellationToken).ConfigureAwait(false)).TargetLanguageTag,
                constraints,
                session.PendingSuggestionId,
                LearnerTextFor(request),
                cancellationToken).ConfigureAwait(false);

        return await _coach.RunTurnAsync(
            new CoachAgentTurnRequest
            {
                SessionId = session.Id,
                // Decrypted by the store on load; re-encrypted by the store on save. The
                // plaintext exists only for the lifetime of this call.
                AgentSessionJson = agentSessionJson,
                // Only populated when the checkpoint could not be reused and the turn is being
                // rebuilt from the ledger. Role-tagged conversation data, never instructions.
                PriorMessages = context.PriorMessages,
                LearnerText = LearnerTextFor(request),
                MemoryBlock = memoryBlock,
                ActiveConstraints = constraints,
                PendingSuggestionId = session.PendingSuggestionId,
                PendingSuggestionDelta = pendingDelta,
                ClarificationsRemaining =
                    Math.Max(0, options.MaxClarificationsPerSession - session.ClarificationCount),
                UserLocalDate = _dateContext.UserLocalDate
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> ReduceAgentResultAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachAgentTurnResult agentResult,
        CancellationToken cancellationToken)
    {
        // The leak gate runs first, before the conversation state is persisted and before any
        // text can reach the learner. A leak is terminal: the coach does not re-prompt, does not
        // surface the message, and does not write the plan.
        //
        // It scans exactly the model-authored strings this turn will surface, which is now a
        // short list. Receipts and suggestion rationale are application-owned, and a pedagogical
        // answer surfaces its validated blocks, so on every other intent the model's CoachMessage
        // is discarded — scanning it could only refuse a turn over text no learner ever sees.
        if (agentResult.Outcome == CoachAgentOutcome.Completed && agentResult.Intent is not null)
        {
            var surfaced = SurfacedModelText(agentResult.Intent);
            if (surfaced.Count > 0)
            {
                var leak = await ValidateNoAnswerLeakAsync(
                    userProfileId, session, surfaced, cancellationToken).ConfigureAwait(false);
                if (leak is not null)
                {
                    return leak;
                }
            }
        }

        // Persist the resumable conversation state even when the turn did not complete, so a
        // retry continues the same conversation.
        if (agentResult.AgentSessionJson is not null)
        {
            await _sessions.UpdateAsync(
                userProfileId, session.Id,
                new CoachSessionUpdate { AgentSessionJson = agentResult.AgentSessionJson, TurnIncrement = 1 },
                cancellationToken).ConfigureAwait(false);
        }

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (agentResult.Outcome != CoachAgentOutcome.Completed || agentResult.Intent is null)
        {
            return await IncompleteAsync(userProfileId, session, plan, agentResult, cancellationToken)
                .ConfigureAwait(false);
        }

        var intent = agentResult.Intent;

        // Shape first, then grounding. Both end in the same refusal, but they are separate
        // questions and a reviewer reading the ledger should be able to tell "the model answered
        // in a shape we cannot use" from "the model cited reads it never made". The grounding
        // check runs here because this is the first point at which the agent has finished its
        // tool calls, so the turn's record of what it read is complete.
        var intentValidation = CoachValidationResult.From(
            _intentValidator.ValidateIntent(intent).Violations
                .Concat(ValidateGrounding(intent).Violations));

        if (!intentValidation.IsValid)
        {
            // Recorded for the ledger before the response is built. The first violation is the
            // one that describes the refusal; the rest are usually consequences of it.
            _turnViolation = intentValidation.Violations.Count > 0
                ? intentValidation.Violations[0].Kind
                : CoachViolationKind.IntentShape;

            _logger.LogWarning(
                "[Coach] Session {SessionId}: the intent failed {ViolationCount} validation rule(s); " +
                "codes: [{ViolationCodes}]. Nothing was written.",
                session.Id,
                intentValidation.Violations.Count,
                string.Join(", ", intentValidation.Violations.Select(v => $"{v.Kind}:{v.Code}")));

            // A malformed model answer is the model's problem, not the learner's. An open
            // suggestion survives it: dropping the offer here is how a plain "yes" that the
            // model failed to echo correctly used to lose the learner their pending change.
            var stillPending = await LoadPendingAsync(userProfileId, session, cancellationToken)
                .ConfigureAwait(false);

            await RecordTurnOutcomeAsync(
                userProfileId, session, StatusWithPending(session),
                CoachStopReason.ValidationFailed, cancellationToken).ConfigureAwait(false);

            return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
                userProfileId, session, plan,
                CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed,
                StatusWithPending(session),
                messages: [CoachMessage(CoachMessageKind.Notice, CoachDeterministicCopy.ValidationFailedNotice(intent.Kind))],
                pendingSuggestion: stillPending, receipt: null, evidence: [], clarifyingQuestion: null,
                cancellationToken).ConfigureAwait(false));
        }

        var reduced = intent.Kind switch
        {
            CoachIntentKind.DirectConstraintChange =>
                await ReduceDirectAsync(userProfileId, session, request, intent, cancellationToken).ConfigureAwait(false),
            CoachIntentKind.SuggestConstraintChange =>
                await ReduceSuggestionAsync(userProfileId, session, request, intent, cancellationToken).ConfigureAwait(false),

            CoachIntentKind.AcceptPendingSuggestion =>
                await ReduceTypedAcceptanceAsync(userProfileId, session, request, intent, cancellationToken).ConfigureAwait(false),

            CoachIntentKind.RejectPendingSuggestion =>
                await ReduceTypedRejectionAsync(userProfileId, session, request, intent, cancellationToken).ConfigureAwait(false),

            CoachIntentKind.AskClarification =>
                await ReduceClarificationAsync(userProfileId, session, intent, cancellationToken).ConfigureAwait(false),

            CoachIntentKind.PedagogicalAnswer =>
                await ReduceAnswerAsync(userProfileId, session, request, intent, cancellationToken).ConfigureAwait(false),

            // NoChange and OffTopic never write.
            _ => CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
                userProfileId, session, plan,
                CoachTurnStatus.Completed, CoachStopReason.Completed,
                await ClearedStatusAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
                messages: [CoachMessage(CoachMessageKind.Text, intent.CoachMessage)],
                pendingSuggestion: await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
                receipt: null,
                evidence: BuildEvidence(),
                clarifyingQuestion: null,
                cancellationToken).ConfigureAwait(false))
        };

        var withMemory = await AttachMemoryCandidateAsync(
            userProfileId, session, request, intent, reduced, cancellationToken).ConfigureAwait(false);

        return await AttachWriteOperationAsync(session, withMemory, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches the change this turn proposed, so the client can render its card without asking
    /// the model what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attached at the single exit point for the same reason the memory candidate is: a proposal
    /// is orthogonal to every reducer, and a turn that answers a grammar question can propose a
    /// vocabulary entry in the same breath.
    /// </para>
    /// <para>
    /// The ledger is the authority for what a proposal is and what state it is in. Sam's reply is
    /// prose and may be wrong about both — it may say a word "has been added" when the ledger has
    /// only recorded a request — so the card is built from this and never from the text.
    /// </para>
    /// <para>
    /// A failure to read is not a turn failure. The learner still gets their answer; the card is
    /// simply absent, and the conversation reload that follows any navigation rebuilds it from
    /// durable history. Losing an answer because a card could not be described would be the worse
    /// trade.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<CoachTurnResponse>> AttachWriteOperationAsync(
        CoachSession session,
        CoachOperationResult<CoachTurnResponse> reduced,
        CancellationToken cancellationToken)
    {
        if (_writeLedger is null || _writeTurn?.TurnId is not { Length: > 0 } turnId
            || !reduced.IsOk || reduced.Value is null)
        {
            return reduced;
        }

        try
        {
            var proposal = await _writeLedger
                .GetLatestForTurnAsync(session.Id, turnId, cancellationToken)
                .ConfigureAwait(false);

            return proposal is null
                ? reduced
                : CoachOperationResult<CoachTurnResponse>.Ok(reduced.Value.WithWriteOperation(proposal));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[Coach] The change proposed on session {SessionId} could not be described: {Failure}.",
                session.Id,
                Telemetry.CoachExceptionSanitizer.Describe(ex));
            return reduced;
        }
    }

    /// <summary>
    /// Screens any memory proposal from this turn and attaches the resulting candidate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attached at the single exit point rather than threaded through seven reducers, because a
    /// memory proposal is orthogonal to every one of them: it can ride along with an answer, with
    /// a plan suggestion, or with nothing at all. Ordering in the response is answer, then plan
    /// suggestion, then this — the learner's question is what they asked for, and the memory offer
    /// is the server asking them for something.
    /// </para>
    /// <para>
    /// A candidate is inert. It does not activate, does not enter any prompt, and writes no plan,
    /// setting, or review state. Approving it is a separate learner action on the memory routes,
    /// deliberately not the same gesture as accepting a plan suggestion.
    /// </para>
    /// <para>
    /// Only successful turns can propose. A rejected, failed, or cancelled turn produced no
    /// learner-visible answer, and offering to remember something on the back of a turn that did
    /// not work would attach a permanent decision to a broken interaction.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<CoachTurnResponse>> AttachMemoryCandidateAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachTurnIntent intent,
        CoachOperationResult<CoachTurnResponse> reduced,
        CancellationToken cancellationToken)
    {
        if (_memory is null
            || intent.MemoryProposal is null
            || !reduced.IsOk
            || reduced.Value is null
            || reduced.Value.Status != CoachTurnStatus.Completed)
        {
            return reduced;
        }

        var languages = await _languages.ResolveAsync(cancellationToken).ConfigureAwait(false);

        var candidate = await _memory.TryRecordCandidateAsync(
            userProfileId,
            intent.MemoryProposal,
            LearnerTextFor(request),
            languages.TargetLanguageTag,
            // The durable conversation shares the session's identifier, so deleting the
            // conversation later also reaches the candidates it produced.
            sourceConversationId: session.Id,
            sourceMessageId: null,
            cancellationToken).ConfigureAwait(false);

        return candidate is null
            ? reduced
            : CoachOperationResult<CoachTurnResponse>.Ok(reduced.Value.WithMemoryCandidate(candidate));
    }

    /// <summary>
    /// Runs the answer-leak gate over the two model-authored strings a learner can see.
    /// Returns null when the answer is clean, or the terminal rejection response on a hit.
    /// </summary>
    /// <remarks>
    /// The embargoed values are read here, inside validation. They are never placed in agent
    /// context, so the model cannot have been told which words it must avoid; the check is
    /// therefore a real test of the answer rather than of the prompt.
    /// </remarks>
    /// <summary>
    /// Runs the ownership check over a server-built preview. Returns null when every named
    /// resource is owned, or the violations when one is not.
    /// </summary>
    private async Task<CoachValidationResult?> ValidateOwnedPreviewAsync(
        PlanPreviewResult preview,
        CancellationToken cancellationToken)
    {
        var owned = await _validationData.GetOwnedResourceIdsAsync(cancellationToken).ConfigureAwait(false);

        // PreviewId is the snapshot version: a content hash the server derived itself, so it
        // identifies exactly the plan these resource ids came from.
        var result = _intentValidator.ValidateOwnedPreview(
            preview.PreviewId,
            preview.Snapshot!.Items.Select(i => i.ResourceId),
            owned);

        return result.IsValid ? null : result;
    }

    /// <summary>The terminal answer for a preview that names a resource the learner does not own.</summary>
    private async Task<CoachOperationResult<CoachTurnResponse>> RejectUnownedPreviewAsync(
        string userProfileId,
        CoachSession session,
        PlanSnapshot plan,
        CoachValidationResult ownership,
        CancellationToken cancellationToken,
        CoachAnswerDto? answer = null)
    {
        _logger.LogError(
            "[Coach] Session {SessionId}: a preview failed the ownership check with {ViolationCount} violation(s); nothing was written.",
            session.Id, ownership.Violations.Count);

        // A mixed turn still delivers its answer: the plan half failing is not a reason to
        // withhold the explanation the learner asked for.
        if (answer is not null)
        {
            return await AnswerWithPlanNoticeAsync(
                userProfileId, session, answer, UnverifiedChangeNotice, StatusWithPending(session),
                cancellationToken).ConfigureAwait(false);
        }

        // Refusing a change never silently withdraws an offer the learner has not answered.
        var stillPending = await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed,
            stillPending is null ? CoachSessionStatus.Active : CoachSessionStatus.SuggestionPending,
            messages: [CoachMessage(CoachMessageKind.Notice, CoachDeterministicCopy.ValidationFailedNeutral)],
            pendingSuggestion: null, receipt: null, evidence: [], clarifyingQuestion: null,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The model-authored strings a turn of this kind actually shows the learner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listed explicitly, and it fails closed: a kind is exempt only because this method names it
    /// and the matching reducer branch is known to discard the model's prose. Anything else — a
    /// kind added later, or one that survived validation — falls to the catch-all and is scanned.
    /// The cost of scanning text that turns out to be discarded is a rare false refusal; the cost
    /// of skipping text that turns out to be surfaced is a leak.
    /// </para>
    /// <para>
    /// The exempt branches: <c>ReduceDirectAsync</c> and <c>AcceptPendingCoreAsync</c> surface
    /// deterministic receipts built from the validated delta, <c>ReduceSuggestionAsync</c>
    /// surfaces deterministic rationale, <c>RejectPendingCoreAsync</c> surfaces an
    /// application-owned literal, and <c>ReduceAnswerAsync</c> surfaces its validated blocks.
    /// NoChange and OffTopic reach the default reducer branch, which shows <c>CoachMessage</c>
    /// verbatim, and AskClarification shows <c>ClarifyingQuestion</c> verbatim.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string?> SurfacedModelText(CoachTurnIntent intent) => intent.Kind switch
    {
        CoachIntentKind.DirectConstraintChange => [],
        CoachIntentKind.SuggestConstraintChange => [],
        CoachIntentKind.AcceptPendingSuggestion => [],
        CoachIntentKind.RejectPendingSuggestion => [],
        CoachIntentKind.PedagogicalAnswer => [],
        _ => [intent.CoachMessage, intent.ClarifyingQuestion]
    };

    private async Task<CoachOperationResult<CoachTurnResponse>?> ValidateNoAnswerLeakAsync(
        string userProfileId,
        CoachSession session,
        IReadOnlyList<string?> surfaced,
        CancellationToken cancellationToken)
    {
        var embargoed = await _validationData
            .GetEmbargoedItemsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (embargoed.Count == 0)
        {
            return null;
        }

        var leak = _leakValidator.ValidateMany(surfaced, embargoed);

        if (leak.IsValid)
        {
            return null;
        }

        _turnViolation = leak.Violations.Count > 0
            ? leak.Violations[0].Kind
            : CoachViolationKind.AnswerLeak;

        _logger.LogWarning(
            "[Coach] Session {SessionId}: the answer repeated {ViolationCount} embargoed item(s); the turn was refused and nothing was written.",
            session.Id, leak.Violations.Count);

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var stillPending = await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed,
            stillPending is null ? CoachSessionStatus.Active : CoachSessionStatus.SuggestionPending,
            messages: [CoachMessage(CoachMessageKind.Notice, CoachDeterministicCopy.ValidationFailedNeutral)],
            pendingSuggestion: null, receipt: null, evidence: [], clarifyingQuestion: null,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The terminal answer for a plan action when there is no plan for today.
    /// </summary>
    /// <remarks>
    /// Explains rather than errors, and writes nothing. Generating a plan is the learner's
    /// deliberate act on the Today's Plan surface; a coach turn must not create one as a side
    /// effect of being asked to shorten it.
    /// </remarks>
    private async Task<CoachOperationResult<CoachTurnResponse>?> NoPlanToEditAsync(
        string userProfileId,
        CoachSession session,
        CancellationToken cancellationToken)
    {
        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (plan.Items.Count > 0)
        {
            return null;
        }

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Completed, CoachStopReason.Completed,
            await ClearedStatusAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
            messages:
            [
                CoachMessage(
                    CoachMessageKind.Notice,
                    NoPlanNotice + " I can still answer language questions now.")
            ],
            pendingSuggestion: await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
            receipt: null, evidence: [], clarifyingQuestion: null,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The one no-write exit for a turn whose plan half could not proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mixed turn asks a language question <b>and</b> requests a plan change. Every reason the
    /// plan half can stop — an offer already open, no plan to edit, an invalid delta, an
    /// infeasible preview, an unowned resource, a change that would not help — used to return
    /// through a path that had no idea an answer existed, so the learner lost the answer they
    /// asked for because of the half they did not.
    /// </para>
    /// <para>
    /// The answer comes first and the notice second, because the answer is what the learner
    /// asked for and the notice explains what did not happen. Any open suggestion is preserved
    /// untouched, and nothing here writes.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<CoachTurnResponse>> AnswerWithPlanNoticeAsync(
        string userProfileId,
        CoachSession session,
        CoachAnswerDto? answer,
        string notice,
        CoachSessionStatus status,
        CancellationToken cancellationToken)
    {
        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var pending = await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false);

        var messages = answer is null
            ? new[] { CoachMessage(CoachMessageKind.Notice, notice) }
            :
            [
                CoachMessage(CoachMessageKind.PedagogicalAnswer, answer.PlainText),
                CoachMessage(CoachMessageKind.Notice, notice)
            ];

        await RecordTurnOutcomeAsync(userProfileId, session, status, stopReason: null, cancellationToken)
            .ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Completed, CoachStopReason.Completed, status,
            messages: messages,
            pendingSuggestion: pending,
            receipt: null,
            evidence: [],
            clarifyingQuestion: null,
            cancellationToken: cancellationToken,
            answer: answer).ConfigureAwait(false));
    }

    /// <summary>
    /// Turns a plan change the message was not allowed to apply into an offer the learner can
    /// accept explicitly.
    /// </summary>
    /// <remarks>
    /// Reuses the suggestion branch rather than repeating it, so a downgraded change gets the
    /// same effectiveness check, the same merged preview, and the same stored delta as one the
    /// coach proposed itself. The learner still gets their change — it just costs them a tap.
    /// </remarks>
    private Task<CoachOperationResult<CoachTurnResponse>> OfferInsteadOfApplyingAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnIntent intent,
        CancellationToken cancellationToken) =>
        ReduceSuggestionAsync(
            userProfileId,
            session,
            // The learner text is not re-read here: the delta has already been validated, and
            // the suggestion branch only needs it to exempt words the learner typed.
            new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = null },
            intent,
            cancellationToken);

    /// <summary>
    /// Answers a language-learning question. Writes nothing about Today's Plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only state this branch may persist is the encrypted conversation and the run's token
    /// usage, both of which the turn pipeline has already written. It does not preview a plan,
    /// apply one, record a revision, move the active constraints, or touch a pending suggestion.
    /// An offer that was open before the question is still open after it, so a learner can ask
    /// what a word means without losing the change they were deciding about.
    /// </para>
    /// <para>
    /// A malformed or oversized answer is refused here, before it is scanned, shown, or stored.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<CoachTurnResponse>> ReduceAnswerAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachTurnIntent intent,
        CancellationToken cancellationToken)
    {
        var answer = await BuildAnswerAsync(userProfileId, session, intent, cancellationToken)
            .ConfigureAwait(false);

        if (answer.Refusal is not null)
        {
            return answer.Refusal;
        }

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var pending = await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Completed, CoachStopReason.Completed,
            await ClearedStatusAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
            messages: [CoachMessage(CoachMessageKind.PedagogicalAnswer, answer.Answer!.PlainText)],
            pendingSuggestion: pending,
            receipt: null,
            evidence: [],
            clarifyingQuestion: null,
            cancellationToken: cancellationToken,
            answer: answer.Answer).ConfigureAwait(false));
    }

    /// <summary>The outcome of validating and scanning one pedagogical answer.</summary>
    /// <summary>
    /// Bounds a client-supplied turn id to something safe to store as a correlation field.
    /// </summary>
    /// <remarks>
    /// On the conversation route this is the server's own durable operation id. On the bare
    /// session route it is whatever the caller sent, so it is length-bounded before it is kept.
    /// It correlates audit rows and nothing more; no approval decision reads it.
    /// </remarks>
    private static string? TrimTurnId(string? clientTurnId)
    {
        if (string.IsNullOrWhiteSpace(clientTurnId))
        {
            return null;
        }

        var trimmed = clientTurnId.Trim();
        return trimmed.Length <= Operations.CoachWriteLimits.IdMaxLength
            ? trimmed
            : trimmed[..Operations.CoachWriteLimits.IdMaxLength];
    }

    private readonly record struct AnswerOutcome(
        CoachAnswerDto? Answer,
        CoachOperationResult<CoachTurnResponse>? Refusal);

    /// <summary>
    /// Validates an answer's shape and resolves its language tags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An answer is <b>not</b> matched against the learner's due vocabulary. The embargo set is
    /// exactly the review queue — target term, gloss, lemma, and the curated example sentences of
    /// words that are due — and no part of it can reach the model. The read-only tools return
    /// counts, bands, tags, and resource metadata only; <see cref="ICoachValidationDataSource"/> is
    /// injected into this service alone, after the model has answered, and never into a tool or the
    /// agent factory. The start-up embargo scanner enforces that no tool shape may even name a term,
    /// a gloss, or an example.
    /// </para>
    /// <para>
    /// So when an explanation and a due row share a word, the model did not read the row: it wrote
    /// an ordinary English or Korean word out of its own language knowledge, and the queue happened
    /// to contain the same word. Refusing on that basis blocks no exfiltration path, because none
    /// exists. It only makes tutoring fail at random — the more of a language a learner is actively
    /// reviewing, the less of it the coach may explain. That is backwards.
    /// </para>
    /// <para>
    /// Being due for review is also not a hidden assessment. It is a scheduling fact the learner can
    /// see in their own app. Refusing an explicit request for the answer to a graded question stays
    /// an instruction-level rule; this build has no trusted active-assessment state and does not
    /// invent one.
    /// </para>
    /// <para>
    /// Everything that guards a real channel is untouched: block and span bounds, language-tag
    /// resolution, the plan-path leak scan over model-authored plan text, deterministic receipts,
    /// and a reducer that writes nothing on an answer turn.
    /// </para>
    /// </remarks>
    private async Task<AnswerOutcome> BuildAnswerAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnIntent intent,
        CancellationToken cancellationToken)
    {
        var languages = await _languages.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var projection = _answers.Project(intent.PedagogicalAnswer, languages);

        if (!projection.IsValid)
        {
            _logger.LogWarning(
                "[Coach] Session {SessionId}: the answer failed {ErrorCount} shape rule(s); "
                + "nothing was shown or stored. Rules: {ShapeErrors}",
                session.Id, projection.Errors.Count,
                string.Join(" | ", projection.Errors));

            _turnViolation = ClassifyAnswerShapeViolation(projection.Errors);
            _answerShapeRefused = true;

            return new AnswerOutcome(null, await RefuseAnswerAsync(
                userProfileId, session, cancellationToken,
                limitationOverride: Validation.Claims.CoachRefusalLimitationProjection.ProjectShape(
                    _dateContext.UtcNow))
                .ConfigureAwait(false));
        }

        // No due-queue scan. See the remark on this method for why matching an explanation
        // against the review list is a coincidence filter, not a confidentiality control.
        //
        // The grounding ladder runs here and nowhere else. This is the one point on the real turn
        // path where all four inputs exist at once — the answer is composed, the evidence is built
        // from the turn's own scopes, the agent has finished its tool calls so the trace is
        // complete, and the manifest is a startup constant — and it is still before anything is
        // stored or returned. Both answer-producing branches reach it, because both call this
        // method.
        return await ApplyGroundingAsync(session, projection.Answer!, userProfileId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the grounding ladder over a composed answer, and turns its verdict into a turn outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off returns before anything happens.</b> Not "scans and discards" — the evaluator is not
    /// called at all, so a host at <see cref="Validation.Claims.CoachGroundingStage.Off"/> behaves exactly like the
    /// build that had no ladder. Promoting to Observe then measures a real difference rather than
    /// the difference between two things that both already ran.
    /// </para>
    /// <para>
    /// <b>A refusal reuses the existing path.</b> <see cref="RefuseAnswerAsync"/> already records
    /// <see cref="CoachStopReason.ValidationFailed"/>, keeps an open suggestion, and writes nothing.
    /// An answer refused for dishonesty and an answer refused for a malformed shape are the same
    /// event from the learner's side and should leave the same trail; inventing a second refusal
    /// path would have produced a turn that refused without recording why.
    /// </para>
    /// <para>
    /// <b>Refusal cannot unwind a write, and does not need to.</b> The branches that produce an
    /// answer never write — the same invariant <c>BuildTurnResponseAsync</c> relies on for answers
    /// and receipts being mutually exclusive — so withholding here withholds text and nothing else.
    /// </para>
    /// </remarks>
    private async Task<AnswerOutcome> ApplyGroundingAsync(
        CoachSession session,
        CoachAnswerDto answer,
        string userProfileId,
        CancellationToken cancellationToken)
    {
        var stage = _options.CurrentValue.Grounding.Stage;

        if (_grounding is null || stage == Validation.Claims.CoachGroundingStage.Off)
        {
            return new AnswerOutcome(answer, null);
        }

        var result = _grounding.Evaluate(
            stage,
            answer,
            BuildEvidence(),
            _observations,
            ProposedCapabilities,
            CapabilityStage,
            _turnHandshake,
            _turnDispute);

        // Recorded before the refusal branch, so a refused turn still persists why it refused. A
        // summary written only on the success path would make the report table blind to exactly the
        // turns an operator most wants to read.
        _turnGrounding = result.Grounding;
        _turnRepairDisclosure = Validation.Claims.CoachRefusalLimitationProjection.ProjectDisclosure(
            result.Grounding, result.Refused);

        if (!result.Refused)
        {
            // Resolved only on a turn that ships, and against the context the rules actually
            // judged. The first cut resolved before this branch, reasoning that a compliant
            // re-read is compliant whatever else went wrong — but the learner receives nothing on
            // a refused turn, so a dispute closed there would release the constraint without the
            // learner ever seeing the corrected answer their pushback earned. A refusal resolves
            // nothing.
            if (result.Context is { } judged)
            {
                ResolveDispute(judged);
            }

            return new AnswerOutcome(result.Answer, null);
        }

        _logger.LogWarning(
            "[Coach] Session {SessionId}: the grounding layer refused an answer at {Stage} over "
            + "{FindingCount} unrepairable finding(s); nothing was shown or stored.",
            session.Id, stage, result.Record?.Findings.Count ?? 0);

        _turnViolation = CoachViolationKind.EvidenceWindow;

        return new AnswerOutcome(
            null,
            await RefuseAnswerAsync(userProfileId, session, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The capabilities this turn's intent proposes to use.
    /// </summary>
    /// <remarks>
    /// <b>Empty today, and honestly so.</b> <c>CoachTurnIntent</c> has no capability-proposal
    /// member; Sam cannot yet propose an action, so there is nothing truthful to put here. Plan
    /// §5.6 anticipates this — <c>SideEffectNotDisclosed</c> is specified as "registered before
    /// anything can trigger it" — and the alternative, recovering a capability name by matching
    /// words in the answer against the manifest, is the exact failure B5 forbids: it would make an
    /// honesty rule depend on how the model phrased itself. The three capability rules are wired,
    /// reachable, and inert until the action-card work gives the intent something to declare.
    /// </remarks>
    private static readonly IReadOnlyList<string> ProposedCapabilities = Array.Empty<string>();

    /// <summary>
    /// The promoted capability stage for this turn.
    /// </summary>
    /// <remarks>
    /// <b>Off, because nothing binds it yet.</b> Plan §10.1 lists <c>Coach:Capabilities:Stage</c>
    /// beside <c>Coach:Grounding:Stage</c>, but only the grounding key is in scope for this
    /// workstream and the capability key binds to nothing today. Off is the fail-safe reading: it
    /// resolves every capability to its least permissive availability, so an honesty rule that
    /// consults it can under-grant but never over-grant.
    /// </remarks>
    private static CoachCapabilityStage CapabilityStage => CoachCapabilityStage.Off;

    /// <summary>
    /// Classifies an answer-shape projection failure as <see cref="CoachViolationKind.LengthLimit"/>
    /// when any error is a total-character or span-character overrun, otherwise
    /// <see cref="CoachViolationKind.IntentShape"/>.
    /// </summary>
    /// <remarks>
    /// The error strings are operator-authored constants produced by
    /// <see cref="CoachAnswerProjection"/>. They never contain learner or model content, so prefix
    /// matching is safe and stable. The two prefixes are the exact format strings from the
    /// projection — changes there must be mirrored here.
    /// </remarks>
    private static CoachViolationKind ClassifyAnswerShapeViolation(IReadOnlyList<string> errors)
    {
        const string spanOverrunPrefix = "A piece of text is longer than";
        const string totalOverrunPrefix = "The answer is longer than";

        foreach (var error in errors)
        {
            if (error.StartsWith(spanOverrunPrefix, StringComparison.Ordinal)
                || error.StartsWith(totalOverrunPrefix, StringComparison.Ordinal))
            {
                return CoachViolationKind.LengthLimit;
            }
        }

        return CoachViolationKind.IntentShape;
    }

    /// <summary>The terminal answer for a refused explanation. Writes nothing, keeps any offer.</summary>
    /// <param name="userProfileId">The learner whose turn was refused.</param>
    /// <param name="session">The current session.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="limitationOverride">
    /// When non-null, uses this limitation instead of the grounding-refusal projector. The shape-
    /// projection path passes <see cref="CoachRefusalLimitationProjection.ProjectShape"/> here
    /// because grounding did not run.
    /// </param>
    private async Task<CoachOperationResult<CoachTurnResponse>> RefuseAnswerAsync(
        string userProfileId,
        CoachSession session,
        CancellationToken cancellationToken,
        CoachLimitationDto? limitationOverride = null)
    {
        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var pending = await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false);

        await RecordTurnOutcomeAsync(
            userProfileId, session, StatusWithPending(session),
            CoachStopReason.ValidationFailed, cancellationToken).ConfigureAwait(false);

        // The evidence the turn actually produced, not an empty list. A refusal that carries
        // nothing tells the learner only that something went wrong; the same refusal beside the
        // real coverage, counts and withheld reason tells them what Sam did look at. It is also
        // what the report path reads, so emptying it here would have made every refused turn
        // unreviewable. Content-free by construction — the W3 projection cannot carry a term.
        var evidence = BuildEvidence();

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed, StatusWithPending(session),

            // No learner-visible message. The refusal used to ship a hardcoded English sentence
            // straight past the client's resource file, so a learner reading the app in Korean got
            // English. The reason is now a closed code on Limitation and the client writes the
            // sentence; deterministic server copy stays in logs and operator surfaces.
            messages: [],
            pendingSuggestion: pending, receipt: null, evidence: evidence, clarifyingQuestion: null,
            cancellationToken,
            limitation: limitationOverride ?? Validation.Claims.CoachRefusalLimitationProjection.Project(
                evidence, _dateContext.UtcNow)).ConfigureAwait(false));
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> ReduceDirectAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachTurnIntent intent,
        CancellationToken cancellationToken)
    {
        var mapped = _mapper.FromIntent(intent.ConstraintDelta);
        if (!mapped.IsValid)
        {
            return await InvalidConstraintAsync(userProfileId, session, mapped.Errors, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!mapped.HasChange)
        {
            var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
            return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
                userProfileId, session, plan,
                CoachTurnStatus.Completed, CoachStopReason.Completed, CoachSessionStatus.Active,
                // Still a claim about Today's Plan, so the application makes it.
                messages: [CoachMessage(CoachMessageKind.Notice, CoachDeterministicCopy.NoChange)],
                pendingSuggestion: null, receipt: null, evidence: BuildEvidence(), clarifyingQuestion: null,
                cancellationToken).ConfigureAwait(false));
        }

        // A typed direct write needs the whole message to be a plan command. Once the coach
        // also answers language questions, "what's the difference between X and Y? also make
        // today shorter" is a question with a request attached — the plan half is offered, not
        // applied. The model calling it a direct change is not enough on its own.
        if (request.InputKind == CoachTurnInputKind.Text)
        {
            var denial = _writeAuthority.Evaluate(request.Text);
            if (denial != CoachWriteAuthority.Denial.None)
            {
                _logger.LogInformation(
                    "[Coach] Session {SessionId}: a typed direct change was downgraded to a suggestion ({Denial}).",
                    session.Id, denial);

                return await OfferInsteadOfApplyingAsync(
                    userProfileId, session, intent, cancellationToken).ConfigureAwait(false);
            }
        }

        // Model-derived: the plan is previewed and ownership-checked before the write. The
        // receipt sentence is the application's, not the model's — a narrated change is where
        // invented totals and item counts get in front of the learner.
        // A semantic focus is always offered, never applied, however imperative the sentence was.
        //
        // "Make it 20 minutes" names the exact value the server will store, so applying it holds no
        // surprise. "Focus on active verbs" names a category and leaves the server to pick which
        // ten of the learner's words satisfy it — a choice the learner has not seen and did not
        // make. Writing that immediately hands them a plan built from a set they never agreed to.
        // So this one field breaks the direct-write rule and takes the Accept / Not now path, where
        // the concrete set is shown first.
        //
        // Clearing is exempt: removing a focus involves no server choice, so an exclusive,
        // unambiguous clear stays direct. A question or a mixed message still cannot write at all —
        // the write-authority check above has already run.
        if (!string.IsNullOrWhiteSpace(mapped.Delta!.VocabularyFocusDescription))
        {
            _logger.LogInformation(
                "[Coach] Session {SessionId}: a vocabulary focus was offered rather than applied; " +
                "the learner named a category, so the selected set needs their agreement.",
                session.Id);

            return await OfferInsteadOfApplyingAsync(
                userProfileId, session, intent, cancellationToken).ConfigureAwait(false);
        }

        // A clear, or an exact-constraint change: resolved here, after the whole intent has been
        // validated, and never before.
        var focus = await ResolveFocusAsync(
            userProfileId, session, mapped.Delta!, cancellationToken).ConfigureAwait(false);

        if (focus.Refusal is not null)
        {
            return focus.Refusal;
        }

        return await ApplyDeltaAsync(
            userProfileId, session, mapped.Delta!,
            CoachRevisionSource.DirectRequest, CoachIntentKind.DirectConstraintChange,
            request.ExpectedPlanVersion, request.ClientTurnId,
            CoachDeterministicCopy.AppliedDirectChange, cancellationToken, requireOwnedPreview: true,
            focusSelection: focus.Selection)
            .ConfigureAwait(false);
    }

    /// <summary>The outcome of resolving the focus a delta asks for, if it asks for one.</summary>
    private readonly record struct FocusResolution(
        CoachFocusSelection? Selection,
        CoachOperationResult<CoachTurnResponse>? Refusal);

    /// <summary>
    /// Resolves the vocabulary focus a validated delta describes.
    /// </summary>
    /// <remarks>
    /// Every failure is a no-write turn that says exactly what went wrong. There is no fallback:
    /// quietly turning an unrecognized focus into "some vocabulary", or a focus with three matches
    /// into a set padded with unrelated words, hands the learner a plan that does not do what they
    /// asked while looking like it does.
    /// </remarks>
    private async Task<FocusResolution> ResolveFocusAsync(
        string userProfileId,
        CoachSession session,
        CoachConstraintDeltaDto delta,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(delta.VocabularyFocusDescription))
        {
            return new FocusResolution(null, null);
        }

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await _focus
            .ResolveAsync(delta.VocabularyFocusDescription, plan.Version, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.IsSuccess)
        {
            return new FocusResolution(outcome.Selection, null);
        }

        var refusal = outcome.Failure == CoachFocusFailure.Unrecognized
            ? await AskClarificationAsync(
                userProfileId, session,
                CoachDeterministicCopy.UnrecognizedFocusQuestion,
                cancellationToken).ConfigureAwait(false)
            : await FocusUnavailableAsync(
                userProfileId, session, plan, outcome, cancellationToken).ConfigureAwait(false);

        return new FocusResolution(null, refusal);
    }

    /// <summary>A focus the registry understood but the learner's vocabulary cannot satisfy.</summary>
    private async Task<CoachOperationResult<CoachTurnResponse>> FocusUnavailableAsync(
        string userProfileId,
        CoachSession session,
        PlanSnapshot plan,
        CoachFocusOutcome outcome,
        CancellationToken cancellationToken)
    {
        await RecordTurnOutcomeAsync(
            userProfileId, session, StatusWithPending(session),
            CoachStopReason.ValidationFailed, cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed,
            StatusWithPending(session),
            messages: [CoachMessage(
                CoachMessageKind.Notice,
                CoachDeterministicCopy.FocusUnavailable(outcome.Failure!.Value, outcome.MatchedCount))],
            pendingSuggestion: await LoadPendingAsync(userProfileId, session, cancellationToken)
                .ConfigureAwait(false),
            receipt: null, evidence: [], clarifyingQuestion: null,
            cancellationToken).ConfigureAwait(false));
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> ReduceSuggestionAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachTurnIntent intent,
        CancellationToken cancellationToken)
    {
        // A mixed turn — a language question plus a plan request — is answered and previewed in
        // one reply. The answer is delivered now; the plan change still waits for an explicit
        // acceptance, so the learner gets what they asked about without a write they did not
        // authorise.
        CoachAnswerDto? answer = null;
        if (intent.PedagogicalAnswer is not null)
        {
            var built = await BuildAnswerAsync(userProfileId, session, intent, cancellationToken)
                .ConfigureAwait(false);

            if (built.Refusal is not null)
            {
                return built.Refusal;
            }

            answer = built.Answer;
        }

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

        // At most one pending suggestion. A second proposal while one is open is dropped,
        // because accepting "yes" would otherwise be ambiguous about which one it answered.
        if (!string.IsNullOrWhiteSpace(session.PendingSuggestionId))
        {
            return await AnswerWithPlanNoticeAsync(
                userProfileId, session, answer, AlreadyPendingNotice,
                CoachSessionStatus.SuggestionPending, cancellationToken).ConfigureAwait(false);
        }

        // Nothing to propose a change to. The answer half of a mixed turn has already been
        // delivered above, so the learner still gets what they asked about.
        if (plan.Items.Count == 0)
        {
            return await AnswerWithPlanNoticeAsync(
                userProfileId, session, answer, NoPlanNotice, StatusWithPending(session), cancellationToken)
                .ConfigureAwait(false);
        }

        var mapped = _mapper.FromIntent(intent.ConstraintDelta);
        if (!mapped.IsValid || !mapped.HasChange)
        {
            // A learner who asked a question and requested an impossible change still gets the
            // answer. A bare problem response would throw it away over the plan half.
            if (answer is not null)
            {
                return await AnswerWithPlanNoticeAsync(
                    userProfileId, session, answer, InvalidConstraintNotice, StatusWithPending(session),
                    cancellationToken).ConfigureAwait(false);
            }

            return await InvalidConstraintAsync(
                userProfileId, session,
                mapped.Errors.Count > 0 ? mapped.Errors : new[] { "The suggestion changed nothing." },
                cancellationToken).ConfigureAwait(false);
        }

        var current = ActiveConstraints(session);

        // Resolved after intent validation and before the preview, so the previewed plan and the
        // eventual apply share one frozen set of identifiers.
        var focus = await ResolveFocusAsync(
            userProfileId, session, mapped.Delta!, cancellationToken).ConfigureAwait(false);

        if (focus.Refusal is not null)
        {
            return answer is not null
                ? await AnswerWithPlanNoticeAsync(
                    userProfileId, session, answer, InvalidConstraintNotice, StatusWithPending(session),
                    cancellationToken).ConfigureAwait(false)
                : focus.Refusal;
        }

        var suggestionFocus = mapped.Delta!.ClearVocabularyFocus
            ? null
            : focus.Selection ?? ActiveFocusSelection(session);

        var proposed = _mapper.Apply(
            current, mapped.Delta!,
            focus.Selection is not null
                ? CoachVocabularyFocusService.Project(focus.Selection)
                : current.VocabularyFocus);

        // Preview only. This is a pure read: it never seeds resources and never writes.
        var preview = await _planService
            .PreviewPlanAsync(
                _mapper.ToPlanConstraints(proposed), suggestionFocus?.VocabularyWordIds, cancellationToken)
            .ConfigureAwait(false);

        if (!preview.IsSuccess || preview.Snapshot is null)
        {
            if (answer is not null)
            {
                return await AnswerWithPlanNoticeAsync(
                    userProfileId, session, answer,
                    preview.Outcome == PlanPreviewOutcome.InvalidConstraints
                        ? InvalidConstraintNotice
                        : NoFeasiblePlanNotice,
                    StatusWithPending(session), cancellationToken).ConfigureAwait(false);
            }

            return await InvalidConstraintAsync(
                userProfileId, session,
                preview.ValidationErrors.Count > 0
                    ? preview.ValidationErrors
                    : new[] { "No plan satisfies that change." },
                cancellationToken,
                preview.Outcome == PlanPreviewOutcome.InvalidConstraints
                    ? CoachOperationStatus.InvalidConstraint
                    : CoachOperationStatus.NoFeasiblePlan).ConfigureAwait(false);
        }

        var ownership = await ValidateOwnedPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
        if (ownership is not null)
        {
            return await RejectUnownedPreviewAsync(
                userProfileId, session, plan, ownership, cancellationToken, answer).ConfigureAwait(false);
        }

        // The raw preview is the planner's whole remainder. Merge it the way the apply path
        // would so the learner sees the plan they would actually get: completed and started
        // work preserved, only untouched remaining work replaced.
        var proposedPlanConstraints = _mapper.ToPlanConstraints(proposed);
        var merged = PlanRevisionPreview.Merge(plan, preview.Snapshot);

        // The model wrote the rationale, so the plan — not the rationale — decides whether the
        // suggestion is worth showing.
        var verdict = _suggestionValidator.Validate(plan, merged, proposedPlanConstraints);
        if (!verdict.IsEffective)
        {
            _logger.LogInformation(
                "[Coach] Session {SessionId}: dropped a suggestion ({Rejection}). {Detail}",
                session.Id, verdict.Rejection, verdict.Detail);

            // With an answer this is a completed turn that answered a question and proposed
            // nothing; without one it stays the terminal refusal it already was.
            if (answer is not null)
            {
                return await AnswerWithPlanNoticeAsync(
                    userProfileId, session, answer, IneffectiveSuggestionNotice, StatusWithPending(session),
                    cancellationToken).ConfigureAwait(false);
            }

            return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
                userProfileId, session, plan,
                CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed, CoachSessionStatus.Active,
                messages: [CoachMessage(CoachMessageKind.Notice, IneffectiveSuggestionNotice)],
                pendingSuggestion: null, receipt: null, evidence: [], clarifyingQuestion: null,
                cancellationToken).ConfigureAwait(false));
        }

        // The registry has already read the learner's wording; nothing downstream needs it, so it
        // stops here rather than living in an unencrypted column or on the wire.
        var storedDelta = mapped.Delta!.WithoutRawFocusText();

        var suggestionId = Guid.NewGuid().ToString("N");
        await _sessions.SetPendingSuggestionPayloadAsync(
                userProfileId, session.Id, suggestionId,
                CoachNormalizedJson.Serialize(new CoachPendingSuggestionEnvelope
                {
                    Delta = storedDelta,
                    FocusSelection = suggestionFocus
                }),
                cancellationToken)
            .ConfigureAwait(false);

        await RecordTurnOutcomeAsync(
            userProfileId, session,
            CoachSessionStatus.SuggestionPending,
            stopReason: null,
            cancellationToken).ConfigureAwait(false);

        _telemetry.RecordConstraintChange(mapped.Delta!.ChangedFields);

        // The rationale describes the delta the server validated, not the sentence the model
        // wrote about it. Counts and minutes live in Preview, which the server derived.
        // Only when this offer actually changes the focus. Echoing the focus already in force
        // would make an unrelated suggestion look like a vocabulary change.
        var offeredFocus = mapped.Delta!.ChangedFields.Contains(CoachConstraintField.VocabularyFocus)
                           && !mapped.Delta.ClearVocabularyFocus
            ? CoachVocabularyFocusService.Project(focus.Selection)
            : null;

        var rationale = CoachDeterministicCopy.SuggestionRationale(storedDelta, offeredFocus);

        var pending = new PendingCoachSuggestionDto
        {
            SuggestionId = suggestionId,
            // The same redacted change the server stores, so the offer a client renders and the
            // offer it later re-reads are identical, and neither carries the learner's wording.
            Delta = storedDelta,
            Rationale = rationale,
            VocabularyFocus = offeredFocus,
            Preview = _projection.ToDiff(plan, merged, isPreview: true),
            Evidence = BuildEvidence(),
            AcceptLabel = AcceptLabel,
            RejectLabel = RejectLabel,
            CreatedAtUtc = _dateContext.UtcNow,
            ExpiresAtUtc = session.ExpiresAt
        };

        var messages = answer is null
            ? new[] { CoachMessage(CoachMessageKind.Suggestion, rationale, suggestionId) }
            :
            [
                CoachMessage(CoachMessageKind.PedagogicalAnswer, answer.PlainText),
                CoachMessage(CoachMessageKind.Suggestion, rationale, suggestionId)
            ];

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Completed, CoachStopReason.Completed, CoachSessionStatus.SuggestionPending,
            messages: messages,
            pendingSuggestion: pending, receipt: null, evidence: pending.Evidence, clarifyingQuestion: null,
            cancellationToken: cancellationToken,
            answer: answer).ConfigureAwait(false));
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> ReduceTypedAcceptanceAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachTurnIntent intent,
        CancellationToken cancellationToken)
    {
        var pendingId = session.PendingSuggestionId;

        // Two independent gates: the session must actually hold the named suggestion, and the
        // learner's own words must be an unmistakable yes. The model's classification alone
        // is never authorisation to write.
        var namesCurrentSuggestion =
            !string.IsNullOrWhiteSpace(pendingId)
            && string.Equals(intent.PendingSuggestionId, pendingId, StringComparison.Ordinal);

        var typed = _acceptance.Classify(request.Text);

        if (!namesCurrentSuggestion || typed != CoachExplicitAcceptance.Affirmative)
        {
            _telemetry.RecordSuggestionOutcome(CoachAcceptanceState.Ambiguous);
            return await AskClarificationAsync(
                userProfileId, session,
                "Should I update Today's Plan with that change now?",
                cancellationToken).ConfigureAwait(false);
        }

        // Same deterministic core as a tap and as the pre-model shortcut. Reaching this point
        // means the classifier already read the learner's words as a clear yes, so the model
        // only confirmed a decision the application had made anyway.
        return await AcceptPendingCoreAsync(
            userProfileId,
            session,
            pendingId!,
            request.ExpectedPlanVersion,
            request.ClientTurnId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> ReduceTypedRejectionAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CoachTurnIntent intent,
        CancellationToken cancellationToken)
    {
        var typed = _acceptance.Classify(request.Text);
        if (typed != CoachExplicitAcceptance.Negative)
        {
            _telemetry.RecordSuggestionOutcome(CoachAcceptanceState.Ambiguous);
            return await AskClarificationAsync(
                userProfileId, session,
                "Should I update Today's Plan with that change now?",
                cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(session.PendingSuggestionId))
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.SuggestionNotFound, CoachProblemTypes.SuggestionNotFound,
                "That suggestion is no longer pending.");
        }

        return await RejectPendingCoreAsync(
            userProfileId,
            session,
            session.PendingSuggestionId!,
            request.ClientTurnId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> ReduceClarificationAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnIntent intent,
        CancellationToken cancellationToken)
    {
        var limit = _options.CurrentValue.MaxClarificationsPerSession;
        if (session.ClarificationCount >= limit)
        {
            var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

            await RecordTurnOutcomeAsync(
                userProfileId, session, StatusWithPending(session),
                CoachStopReason.ClarificationRequested, cancellationToken).ConfigureAwait(false);

            return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
                userProfileId, session, plan,
                CoachTurnStatus.Incomplete, CoachStopReason.ClarificationRequested,
                StatusWithPending(session),
                messages: [CoachMessage(CoachMessageKind.Notice,
                    CoachDeterministicCopy.ValidationFailedNotice(intent.Kind))],
                pendingSuggestion: await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
                receipt: null, evidence: [], clarifyingQuestion: null,
                cancellationToken).ConfigureAwait(false));
        }

        return await AskClarificationAsync(userProfileId, session, intent.ClarifyingQuestion, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> AskClarificationAsync(
        string userProfileId,
        CoachSession session,
        string? question,
        CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(question)
            ? "Should I update Today's Plan with that change now?"
            : question;

        // The session is waiting for the learner to clarify, and that is what the status says —
        // even when a suggestion is still open. The open offer is carried by
        // PendingSuggestionId and its stored delta, which this does not touch, so the two facts
        // are recorded independently instead of one overwriting the other.
        await RecordTurnOutcomeAsync(
            userProfileId, session,
            CoachSessionStatus.AwaitingClarification,
            CoachStopReason.ClarificationRequested,
            cancellationToken,
            clarificationIncrement: 1).ConfigureAwait(false);

        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Incomplete, CoachStopReason.ClarificationRequested,
            CoachSessionStatus.AwaitingClarification,
            messages: [CoachMessage(CoachMessageKind.Clarification, text)],
            pendingSuggestion: await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
            receipt: null, evidence: [], clarifyingQuestion: text,
            cancellationToken).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- writes

    private async Task<CoachOperationResult<CoachTurnResponse>> ApplyDirectConstraintActionAsync(
        string userProfileId,
        CoachSession session,
        CoachTurnRequest request,
        CancellationToken cancellationToken)
    {
        var mapped = _mapper.FromClient(request.ConstraintAction);
        if (!mapped.IsValid)
        {
            return await InvalidConstraintAsync(userProfileId, session, mapped.Errors, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!mapped.HasChange)
        {
            var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
            return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
                userProfileId, session, plan,
                CoachTurnStatus.Completed, CoachStopReason.Completed, CoachSessionStatus.Active,
                messages: [CoachMessage(CoachMessageKind.Notice, "Today's Plan is unchanged.")],
                pendingSuggestion: null, receipt: null, evidence: [], clarifyingQuestion: null,
                cancellationToken).ConfigureAwait(false));
        }

        var result = await ApplyDeltaAsync(
            userProfileId, session, mapped.Delta!,
            CoachRevisionSource.DirectRequest, CoachIntentKind.DirectConstraintChange,
            request.ExpectedPlanVersion, request.ClientTurnId,
            "Today's Plan now matches your change.", cancellationToken).ConfigureAwait(false);

        if (result.IsOk && result.Value is not null)
        {
            _idempotency.Store(userProfileId, session.Id, request.ClientTurnId, result.Value);
        }

        return result;
    }

    /// <param name="requireOwnedPreview">
    /// True for a change the model derived (a direct request it read from learner text, or a
    /// suggestion the learner accepted). The server previews the plan and checks resource
    /// ownership before it writes. A structured constraint action from the UI is already
    /// deterministic input, so it uses plan validation only and skips the model-output check.
    /// </param>
    private async Task<CoachOperationResult<CoachTurnResponse>> ApplyDeltaAsync(
        string userProfileId,
        CoachSession session,
        CoachConstraintDeltaDto delta,
        CoachRevisionSource source,
        CoachIntentKind intentKind,
        string? expectedPlanVersion,
        string? clientTurnId,
        string coachMessage,
        CancellationToken cancellationToken,
        bool requireOwnedPreview = false,
        CoachFocusSelection? focusSelection = null)
    {
        var noPlan = await NoPlanToEditAsync(userProfileId, session, cancellationToken).ConfigureAwait(false);
        if (noPlan is not null)
        {
            return noPlan;
        }

        var current = ActiveConstraints(session);

        // Captured before anything writes. RecordTurnOutcomeAsync mutates the same tracked entity,
        // so reading this back later would stamp the before side of the audit with the after
        // selection and make every Undo restore the state it was undoing.
        var focusBeforeChange = ActiveFocusSelection(session);

        // Clearing drops the focus; a change that resolved one carries it; anything else keeps the
        // exact set already in force rather than resolving a fresh one.
        var effectiveFocus = delta.ClearVocabularyFocus
            ? null
            : focusSelection ?? focusBeforeChange;

        var focusIds = effectiveFocus?.VocabularyWordIds;

        // Display comes from the freshly resolved focus when this change set one, and otherwise
        // from the constraint set already stored — which is where the projected words live, so an
        // unrelated change does not blank them.
        var focusDisplay = focusSelection is not null
            ? CoachVocabularyFocusService.Project(focusSelection)
            : current.VocabularyFocus;

        var proposed = _mapper.Apply(current, delta, focusDisplay);

        if (requireOwnedPreview)
        {
            var preview = await _planService
                .PreviewPlanAsync(_mapper.ToPlanConstraints(proposed), focusIds, cancellationToken)
                .ConfigureAwait(false);

            if (!preview.IsSuccess || preview.Snapshot is null)
            {
                return await InvalidConstraintAsync(
                    userProfileId, session,
                    preview.ValidationErrors.Count > 0
                        ? preview.ValidationErrors
                        : new[] { "No plan satisfies that change." },
                    cancellationToken,
                    preview.Outcome == PlanPreviewOutcome.InvalidConstraints
                        ? CoachOperationStatus.InvalidConstraint
                        : CoachOperationStatus.NoFeasiblePlan).ConfigureAwait(false);
            }

            var ownership = await ValidateOwnedPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
            if (ownership is not null)
            {
                var currentPlan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
                return await RejectUnownedPreviewAsync(
                    userProfileId, session, currentPlan, ownership, cancellationToken).ConfigureAwait(false);
            }
        }

        var revision = await _planService.ApplyCoachConstraintsAsync(
            new CoachPlanRevisionRequest
            {
                Constraints = _mapper.ToPlanConstraints(proposed),
                ExpectedPlanVersion = expectedPlanVersion,
                OperationKey = clientTurnId,
                SessionId = session.Id,
                ClientTurnId = clientTurnId,
                // Exactly the identifiers the preview used. The plan written is the plan shown.
                FocusVocabularyWordIds = focusIds
            },
            cancellationToken).ConfigureAwait(false);

        var failure = MapRevisionFailure(revision);
        if (failure is not null)
        {
            _telemetry.RecordPlanRevision(source, success: false, 0, 0);
            return failure;
        }

        // The constraint set moves forward even for a no-op plan change: the learner's stated
        // situation is still what the next turn should reason from.
        await RecordTurnOutcomeAsync(
            userProfileId, session,
            CoachSessionStatus.Active,
            stopReason: null,
            cancellationToken,
            activeConstraints: proposed,
            focusSelection: effectiveFocus).ConfigureAwait(false);

        _telemetry.RecordConstraintChange(delta.ChangedFields);
        _telemetry.RecordPlanRevision(
            source,
            revision.IsApplied,
            revision.PreservedCompletedCount,
            revision.PreservedInProgressCount);

        CoachChangeReceiptDto? receipt = null;
        if (revision.IsApplied)
        {
            var record = await AppendRevisionAsync(
                userProfileId, session, revision, delta, source, intentKind,
                beforeConstraints: current, afterConstraints: proposed, cancellationToken,
                beforeFocus: focusBeforeChange, afterFocus: effectiveFocus,
                operationId: clientTurnId).ConfigureAwait(false);

            receipt = BuildReceipt(
                record, revision, delta, CoachConstraintMapper.Summarize(delta), canUndo: true,
                focusChange: new CoachVocabularyFocusChangeDto
                {
                    Status = !delta.ChangedFields.Contains(CoachConstraintField.VocabularyFocus)
                        ? CoachVocabularyFocusStatus.Unchanged
                        : delta.ClearVocabularyFocus
                            ? CoachVocabularyFocusStatus.Cleared
                            : CoachVocabularyFocusStatus.Applied,
                    // The state after this write, never an earlier one.
                    Focus = delta.ClearVocabularyFocus ? null : proposed.VocabularyFocus
                });
        }

        var after = revision.After ?? await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId,
            session,
            after,
            CoachTurnStatus.Completed,
            CoachStopReason.Completed,
            CoachSessionStatus.Active,
            messages: [CoachMessage(
                revision.IsApplied ? CoachMessageKind.Receipt : CoachMessageKind.Notice,
                FocusReceiptMessage(delta, focusDisplay) ?? coachMessage)],
            pendingSuggestion: null,
            receipt: receipt,
            evidence: [],
            clarifyingQuestion: null,
            constraintsOverride: proposed,
            cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes the revision audit, stamping each side of the change with the constraint set that
    /// actually produced that plan.
    /// </summary>
    /// <remarks>
    /// <paramref name="beforeConstraints"/> and <paramref name="afterConstraints"/> are passed
    /// in rather than read from the session, because by the time this runs the session row has
    /// already moved to the new set. Reading it here stamped both sides with the post-apply
    /// constraints, which left the audit unable to answer the one question Undo needs: what was
    /// in force before.
    /// </remarks>
    private async Task<CoachPlanRevision?> AppendRevisionAsync(
        string userProfileId,
        CoachSession session,
        PlanRevisionResult revision,
        CoachConstraintDeltaDto delta,
        CoachRevisionSource source,
        CoachIntentKind intentKind,
        CoachConstraintSetDto beforeConstraints,
        CoachConstraintSetDto afterConstraints,
        CancellationToken cancellationToken,
        CoachFocusSelection? beforeFocus = null,
        CoachFocusSelection? afterFocus = null,
        string? operationId = null)
    {
        var before = revision.Before ?? PlanSnapshot.Empty(_dateContext.UserLocalDate);
        var after = revision.After ?? before;

        // The audit is permanent, so the constraint projection it stores is stripped of the
        // focus words. The identifiers ride in the envelope's own selection member, which is
        // what an Undo needs and all it needs.
        var beforeState = _projection.ToPlanState(before, WithoutFocusWords(beforeConstraints));
        var afterState = _projection.ToPlanState(after, WithoutFocusWords(afterConstraints));

        return await _sessions.AppendRevisionAsync(
            userProfileId,
            session.Id,
            new CoachPlanRevisionInput
            {
                Source = source,
                IntentKind = intentKind,
                AcceptedDelta = delta.WithoutRawFocusText(),
                BeforePlanVersion = before.Version,
                AfterPlanVersion = after.Version,
                BeforePlan = beforeState,
                AfterPlan = afterState,
                BeforePlanAuditJson = CoachNormalizedJson.Serialize(new CoachRevisionSnapshotEnvelope
                {
                    Version = CoachRevisionSnapshotEnvelope.CurrentVersion,
                    State = beforeState, Restore = before, FocusSelection = beforeFocus?.WithoutWords()
                }),
                AfterPlanAuditJson = CoachNormalizedJson.Serialize(new CoachRevisionSnapshotEnvelope
                {
                    Version = CoachRevisionSnapshotEnvelope.CurrentVersion,
                    State = afterState, Restore = after, FocusSelection = afterFocus?.WithoutWords()
                }),
                PreservedCompletedCount = revision.PreservedCompletedCount,
                PreservedInProgressCount = revision.PreservedInProgressCount,
                // Stamped with the turn that caused it, so a crash between this write and the
                // receipt append can find exactly this row again instead of searching by time.
                OperationId = operationId
            },
            cancellationToken).ConfigureAwait(false);
    }

    private CoachChangeReceiptDto? BuildReceipt(
        CoachPlanRevision? record,
        PlanRevisionResult revision,
        CoachConstraintDeltaDto delta,
        string summary,
        bool canUndo,
        CoachVocabularyFocusChangeDto? focusChange = null)
    {
        if (record is null || revision.Before is null || revision.After is null)
        {
            return null;
        }

        return new CoachChangeReceiptDto
        {
            VocabularyFocus = focusChange ?? CoachVocabularyFocusChangeDto.Unchanged(null),
            ReceiptId = record.Id,
            Revision = ToRevisionDto(record, delta, summary, canUndo),
            Summary = summary,
            AppliedDelta = delta,
            Diff = _projection.ToDiff(revision.Before, revision.After, isPreview: false),
            ReplacedItemCount = revision.ReplacedItemCount,
            PreservedCompletedItemCount = revision.PreservedCompletedCount,
            PreservedInProgressItemCount = revision.PreservedInProgressCount,
            PreservedMinutesSpent = revision.PreservedMinutesSpent,
            CanUndo = canUndo,
            UndoLabel = "Undo"
        };
    }

    /// <summary>
    /// The refusal the last completed turn ended on, so a reload does not lose it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The latest completed turn governs, and only the latest.</b> The lookback is one row, which
    /// makes "do not scan backwards past a later null looking for an older refusal" a property of
    /// the query rather than a rule inside a loop somebody could edit. A learner who was refused and
    /// then asked something ordinary is no longer being refused; surfacing the older limitation
    /// would tell them the coach is still withholding an answer it has since given.
    /// </para>
    /// <para>
    /// <b>Unreadable fails closed to null.</b> An outcome this build cannot decrypt or cannot parse
    /// yields no limitation rather than a search for an older one. Falling back would let a payload
    /// failure resurrect state the learner had already moved past, and a stale refusal is worse than
    /// no refusal: it is a claim about the present.
    /// </para>
    /// <para>
    /// <b>Owner and conversation scoped by the store's own owned set</b>, so this is not a filter a
    /// caller could forget. The session id is the conversation id on the durable path.
    /// </para>
    /// <para>
    /// The submit path is untouched: a turn reports its own limitation directly on the turn
    /// response, and this only answers the question a reload asks.
    /// </para>
    /// </remarks>
    private async Task<CoachTurnResponse?> LoadRestorableLimitationAsync(
        string userProfileId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (_operations is null
            || string.IsNullOrWhiteSpace(conversationId)
            || !CoachOwner.TryCreate(userProfileId, null, out var owner))
        {
            return null;
        }

        try
        {
            // One row. Newest completed first, so this is the turn that governs and there is no
            // older row in hand to fall back to.
            var recent = await _operations
                .GetRecentOutcomesAsync(owner, conversationId, limit: 1, cancellationToken)
                .ConfigureAwait(false);

            if (recent.Count == 0)
            {
                return null;
            }

            var stored = History.CoachConversationService.ReadOutcome(
                recent[0].Payload, recent[0].SchemaVersion);

            // The stored turn itself, so the limitation and the repair disclosure come from the
            // same row and cannot disagree about which turn they describe. Null covers all three
            // cases: unreadable payload, unknown version, or a turn that carried neither. A reload
            // cannot tell them apart, and should not.
            return stored?.Answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "[Coach] The stored limitation could not be read and was treated as absent. {Failure}",
                CoachExceptionSanitizer.Describe(ex));

            return null;
        }
    }

    // ---------------------------------------------------------------- responses

    private async Task<CoachSessionResponse> BuildSessionResponseAsync(
        string userProfileId,
        CoachSession session,
        PlanSnapshot plan,
        CancellationToken cancellationToken)
    {
        var constraints = ActiveConstraints(session);

        // One read for both restorable facts, so they always describe the same turn.
        var restorable = await LoadRestorableLimitationAsync(
            userProfileId, session.Id, cancellationToken).ConfigureAwait(false);

        var revisions = await _sessions.GetRevisionsAsync(userProfileId, session.Id, cancellationToken)
            .ConfigureAwait(false);

        var revisionDtos = revisions.Select(ToRevisionDto).ToList();
        var last = revisionDtos.LastOrDefault();
        var canUndo = revisions.Any(r => !r.IsUndone && r.Source != CoachRevisionSource.Undo);
        var budget = await _budget.GetSnapshotAsync(userProfileId, _dateContext.UserLocalDate, cancellationToken)
            .ConfigureAwait(false);

        return new CoachSessionResponse
        {
            SessionId = session.Id,
            Status = session.Status,
            // With durable history off, the server keeps no readable transcript: learner words
            // live only inside the encrypted AgentSession and the client owns what is shown.
            // With it on, the session id is also the conversation id, so the compatibility route
            // can answer with real history instead of an empty list that reads as "nothing
            // happened" the moment a 24-hour checkpoint rolls over.
            Messages = await VisibleMessagesAsync(userProfileId, session.Id, cancellationToken)
                .ConfigureAwait(false),
            ActiveConstraints = constraints,
            PlanState = _projection.ToPlanState(plan, constraints, last, canUndo),
            PendingSuggestion = await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
            // Whatever this request actually read, which for a plain session GET is nothing. The
            // empty list is now derived rather than asserted: a hardcoded empty said "evidence is
            // not implemented here" and would have stayed empty if this path ever ran inside a turn.
            Evidence = BuildEvidence(),
            Dispute = CoachDisputeProjection.Project(_turnDispute),
            Limitation = restorable?.Limitation,
            RepairDisclosure = restorable?.RepairDisclosure,
            Revisions = revisionDtos,
            ClarificationsRemaining =
                Math.Max(0, _options.CurrentValue.MaxClarificationsPerSession - session.ClarificationCount),
            RunsRemainingToday = budget.RunsRemainingToday,
            CreatedAtUtc = session.CreatedAt,
            ExpiresAtUtc = session.ExpiresAt
        };
    }

    /// <summary>
    /// The learner-visible messages for a conversation, oldest first, or an empty list when
    /// durable history is off or unreadable.
    /// </summary>
    private async Task<IReadOnlyList<CoachMessageDto>> VisibleMessagesAsync(
        string userProfileId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (_history is null || !_options.CurrentValue.IsDurableHistoryEnabled)
        {
            return Array.Empty<CoachMessageDto>();
        }

        var owner = CoachOwner.ForUser(userProfileId);
        var page = await _history.GetLatestAsync(owner, conversationId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (page.Status != CoachHistoryStatus.Success)
        {
            // A conversation that does not exist yet is the normal case for a session created
            // before history was switched on. An empty list is the truthful answer, not an error.
            return Array.Empty<CoachMessageDto>();
        }

        return page.Items
            .Select(CoachHistoryProjection.ToSessionMessage)
            .Where(static m => m is not null)
            .Select(static m => m!)
            .ToList();
    }

    /// <summary>
    /// The correction in force for this turn: the one already open, or one this message opens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Runs before the agent does.</b> The dispute has to exist before the model answers,
    /// because the turn that carries the correction is itself the turn the correction constrains.
    /// Opening it afterwards would let the coach repeat the disputed claim once for free — which is
    /// exactly the moment a learner is least willing to forgive it.
    /// </para>
    /// <para>
    /// <b>Off is a total bypass.</b> Not "classify and discard": the classifier is never called, so
    /// a host with the flag off does the same work it did before this existed.
    /// </para>
    /// <para>
    /// <b>An already-open dispute is carried, never re-opened.</b> Re-classifying while one is open
    /// would re-anchor it to a newer message every time the learner pushed back again, and the
    /// original claim — the one that has to change — would slide out from under the constraint.
    /// </para>
    /// </remarks>
    private Persistence.History.CoachTurnDisputeState? OpenOrCarryDispute(
        CoachTurnRequest request,
        CoachTurnExecutionContext context)
    {
        if (_disputes is null || !_disputes.IsEnabled)
        {
            return null;
        }

        if (context.ActiveDispute is { } carried && carried.IsOpen)
        {
            return carried;
        }

        if (context.PriorCoachMessageId is not { Length: > 0 } priorMessageId)
        {
            // No earlier coach answer in this conversation, so there is nothing to correct. This is
            // also the state a host without durable history is permanently in.
            return null;
        }

        var opened = _disputes.TryOpen(
            request.Text, priorMessageId, context.PriorTrace, _dateContext.UtcNow);

        if (opened is not null)
        {
            // Codes and a bounded count. The learner's sentence is not on this line and is not on
            // the record it produces.
            _logger.LogInformation(
                "[Coach] Session correction opened: signal={Signal} definitions={DefinitionCount}",
                opened.Signal,
                opened.DisputedDefinitionCodes.Count);
        }

        return opened;
    }

    /// <summary>
    /// Closes the dispute this answer satisfied, or leaves it standing.
    /// </summary>
    /// <remarks>
    /// Judged by the same rule that would otherwise have refused the answer, so there is no second
    /// definition of "good enough" that could drift from the first. Called after the ladder has
    /// run, on the context the ladder used.
    /// </remarks>
    private void ResolveDispute(Validation.Claims.CoachClaimRuleContext context)
    {
        if (_disputes is null || _turnDispute is not { IsOpen: true } open)
        {
            return;
        }

        var resolved = _disputes.Resolve(open, context, _dateContext.UtcNow);

        if (!ReferenceEquals(resolved, open))
        {
            _logger.LogInformation(
                "[Coach] Session correction resolved: {Resolution}", resolved.Resolution);
        }

        _turnDispute = resolved;
    }

    private async Task<CoachTurnResponse> BuildTurnResponseAsync(
        string userProfileId,
        CoachSession session,
        PlanSnapshot plan,
        CoachTurnStatus status,
        CoachStopReason stopReason,
        CoachSessionStatus sessionStatus,
        IReadOnlyList<CoachMessageDto> messages,
        PendingCoachSuggestionDto? pendingSuggestion,
        CoachChangeReceiptDto? receipt,
        IReadOnlyList<CoachEvidenceDto> evidence,
        string? clarifyingQuestion,
        CancellationToken cancellationToken,
        CoachConstraintSetDto? constraintsOverride = null,
        CoachAnswerDto? answer = null,
        CoachLimitationDto? limitation = null)
    {
        var constraints = constraintsOverride ?? ActiveConstraints(session);
        var revisions = await _sessions.GetRevisionsAsync(userProfileId, session.Id, cancellationToken)
            .ConfigureAwait(false);

        var last = revisions.Count == 0 ? null : ToRevisionDto(revisions[^1]);
        var canUndo = revisions.Any(r => !r.IsUndone && r.Source != CoachRevisionSource.Undo);
        var budget = await _budget.GetSnapshotAsync(userProfileId, _dateContext.UserLocalDate, cancellationToken)
            .ConfigureAwait(false);

        return new CoachTurnResponse
        {
            SessionId = session.Id,
            TurnId = Guid.NewGuid().ToString("N"),
            Status = status,
            StopReason = stopReason,
            SessionStatus = sessionStatus,
            Messages = messages,
            ActiveConstraints = constraints,
            PlanState = _projection.ToPlanState(plan, constraints, last, canUndo),
            PendingSuggestion = pendingSuggestion,
            ChangeReceipt = receipt,
            // An answer and a receipt are mutually exclusive by construction: the branches that
            // produce an answer never write, and the branches that write never answer.
            Answer = answer,
            Evidence = evidence,

            // On every response, not only the answering ones. A learner whose correction is still
            // open must see it is still open even on a turn that produced a notice or a receipt,
            // or the constraint becomes invisible the moment the conversation moves sideways.
            Dispute = CoachDisputeProjection.Project(_turnDispute),
            Limitation = limitation,

            // Never both. A refusal carries the limitation and no disclosure, because the learner
            // received no answer for a disclosure to be about.
            RepairDisclosure = limitation is null ? _turnRepairDisclosure : null,
            ClarifyingQuestion = clarifyingQuestion,
            ClarificationsRemaining =
                Math.Max(0, _options.CurrentValue.MaxClarificationsPerSession - session.ClarificationCount),
            RunsRemainingToday = budget.RunsRemainingToday,
            ExpiresAtUtc = session.ExpiresAt
        };
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> IncompleteAsync(
        string userProfileId,
        CoachSession session,
        PlanSnapshot plan,
        CoachAgentTurnResult agentResult,
        CancellationToken cancellationToken)
    {
        if (agentResult.Outcome == CoachAgentOutcome.ModelUnavailable)
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.ModelUnavailable, CoachProblemTypes.ToolFailure,
                "The coach model is not configured on this server.");
        }

        var stopReason = agentResult.Outcome switch
        {
            CoachAgentOutcome.Timeout => CoachStopReason.Timeout,
            CoachAgentOutcome.Cancelled => CoachStopReason.Cancelled,
            CoachAgentOutcome.InvalidOutput => CoachStopReason.ValidationFailed,
            CoachAgentOutcome.OutputLimitReached => CoachStopReason.OutputTokenLimit,
            _ => CoachStopReason.Failed
        };

        var message = stopReason switch
        {
            CoachStopReason.Timeout => CoachDeterministicCopy.IncompleteNeutral,
            CoachStopReason.Cancelled => CoachDeterministicCopy.IncompleteNeutral,
            CoachStopReason.ValidationFailed => CoachDeterministicCopy.ValidationFailedNeutral,
            CoachStopReason.OutputTokenLimit => CoachDeterministicCopy.IncompleteNeutral,
            _ => CoachDeterministicCopy.IncompleteNeutral
        };

        // Record why the turn ended. Without this the session row keeps a null StopReason, so
        // an incomplete turn leaves no trace in stored state — which is exactly why the first
        // live occurrence of this failure could not be diagnosed after the fact.
        await _sessions.UpdateAsync(
            userProfileId, session.Id,
            new CoachSessionUpdate { StopReason = stopReason },
            cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnResponse>.Ok(await BuildTurnResponseAsync(
            userProfileId, session, plan,
            CoachTurnStatus.Incomplete, stopReason, session.Status,
            messages: [CoachMessage(CoachMessageKind.Notice, message)],
            pendingSuggestion: await LoadPendingAsync(userProfileId, session, cancellationToken).ConfigureAwait(false),
            receipt: null, evidence: [], clarifyingQuestion: null,
            cancellationToken).ConfigureAwait(false));
    }

    private async Task<CoachOperationResult<CoachTurnResponse>> InvalidConstraintAsync(
        string userProfileId,
        CoachSession session,
        IReadOnlyList<string> errors,
        CancellationToken cancellationToken,
        CoachOperationStatus status = CoachOperationStatus.InvalidConstraint)
    {
        _logger.LogInformation(
            "[Coach] Session {SessionId}: rejected a constraint change with {ErrorCount} problem(s).",
            session.Id, errors.Count);

        await Task.CompletedTask.ConfigureAwait(false);
        _ = userProfileId;
        _ = cancellationToken;

        return CoachOperationResult<CoachTurnResponse>.Problem(
            status,
            status == CoachOperationStatus.NoFeasiblePlan
                ? CoachProblemTypes.PlanValidationFailed
                : CoachProblemTypes.InvalidConstraint,
            errors.Count == 0 ? "That change is not allowed." : string.Join(" ", errors));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(string? UserProfileId, CoachOperationResult<T>? Denied)> RequireAvailableAsync<T>(
        CancellationToken cancellationToken)
    {
        var userProfileId = RequireUserProfileId();

        var decision = _availability.Evaluate(userProfileId);
        if (!decision.IsAllowed)
        {
            return (null, Unavailable<T>("The coach is not available."));
        }

        // Availability is evaluated before any session or model work, so a disabled coach
        // never touches the store and never resolves a chat client.
        await Task.CompletedTask.ConfigureAwait(false);
        _ = cancellationToken;
        return (userProfileId, null);
    }

    /// <summary>
    /// Resolves the trusted user profile id, or throws so the endpoint answers with the same
    /// 401 every other authenticated route in this API returns for a missing claim.
    /// </summary>
    private string RequireUserProfileId() =>
        _userScope.TryGetUserProfileId(out var userProfileId)
            ? userProfileId
            : throw new UnauthorizedAccessException("The request has no user profile scope.");

    /// <summary>
    /// The constraint set that produced the plan a revision replaced, read from the revision's
    /// own audit envelope. Returns null when the stored row predates constraint stamping.
    /// </summary>
    /// <remarks>
    /// Rows written before the audit stamped the two sides separately carry the post-apply set
    /// on <b>both</b> sides, so their before-constraints are simply not recoverable. They are
    /// detectable — an applied revision always changes at least one field, so identical sides
    /// can only mean a legacy row — and the safe answer there is to leave the session's
    /// constraints alone rather than restore a value known to be wrong.
    /// </remarks>
    /// <summary>What a revision's stored audit could tell us about the state before it.</summary>
    private enum CoachRestoreKind
    {
        /// <summary>The envelope is current and its before side is authoritative.</summary>
        Restored = 0,

        /// <summary>The row predates constraint stamping. Keep what is in force.</summary>
        Legacy,

        /// <summary>The row claims a schema this build cannot read. Refuse.</summary>
        Unreadable
    }

    private readonly record struct CoachRestore(
        CoachRestoreKind Kind,
        CoachConstraintSetDto? Constraints,
        CoachFocusSelection? Focus,
        int Version);

    /// <summary>
    /// Reads the constraint set and focus selection in force before a revision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The version is what makes this safe. The previous implementation inferred "this row predates
    /// stamping" from before and after being equal, which is also true of a perfectly current
    /// revision whose constraints did not change — only the plan did. Such a revision was read as
    /// legacy, so Undo restored nothing and reported success: the learner undid a change and kept
    /// its constraints. Equality is now only consulted for a row that genuinely carries no version.
    /// </para>
    /// <para>
    /// A row from a newer schema is refused rather than guessed at. Returning "nothing to restore"
    /// for something we cannot read is a success-shaped no-op, which is the failure mode this whole
    /// method exists to avoid.
    /// </para>
    /// </remarks>
    private CoachRestore ReadStoredRestore(CoachPlanRevision revision)
    {
        CoachRevisionSnapshotEnvelope? beforeEnvelope;
        CoachRevisionSnapshotEnvelope? afterEnvelope;

        try
        {
            beforeEnvelope = CoachNormalizedJson
                .Deserialize<CoachRevisionSnapshotEnvelope>(revision.BeforePlanSnapshotJson);
            afterEnvelope = CoachNormalizedJson
                .Deserialize<CoachRevisionSnapshotEnvelope>(revision.AfterPlanSnapshotJson);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "[Coach] Revision {RevisionNumber}: the stored audit could not be read; the undo was " +
                "refused rather than reported as a no-op.",
                revision.RevisionNumber);

            return new CoachRestore(CoachRestoreKind.Unreadable, null, null, -1);
        }

        var before = beforeEnvelope?.State?.AppliedConstraints;
        if (beforeEnvelope is null || before is null)
        {
            return new CoachRestore(CoachRestoreKind.Legacy, null, null, CoachRevisionSnapshotEnvelope.LegacyVersion);
        }

        if (beforeEnvelope.Version > CoachRevisionSnapshotEnvelope.CurrentVersion)
        {
            _logger.LogWarning(
                "[Coach] Revision {RevisionNumber} carries audit schema version {Version}; this build " +
                "reads up to {SupportedVersion}. The undo was refused.",
                revision.RevisionNumber, beforeEnvelope.Version, CoachRevisionSnapshotEnvelope.CurrentVersion);

            return new CoachRestore(CoachRestoreKind.Unreadable, null, null, beforeEnvelope.Version);
        }

        if (beforeEnvelope.Version == CoachRevisionSnapshotEnvelope.LegacyVersion)
        {
            var after = afterEnvelope?.State?.AppliedConstraints;
            if (after is not null && ConstraintsMatch(before, after))
            {
                _logger.LogWarning(
                    "[Coach] Revision {RevisionNumber} predates constraint stamping; leaving the active " +
                    "constraint set unchanged rather than restoring an unrecoverable value.",
                    revision.RevisionNumber);

                return new CoachRestore(
                    CoachRestoreKind.Legacy, null, null, CoachRevisionSnapshotEnvelope.LegacyVersion);
            }
        }

        return new CoachRestore(
            CoachRestoreKind.Restored, before, beforeEnvelope.FocusSelection, beforeEnvelope.Version);
    }

    /// <summary>
    /// The receipt sentence when a change touched the focus. Counts and label come from the
    /// resolved selection; the model contributes nothing to it.
    /// </summary>
    private static string? FocusReceiptMessage(
        CoachConstraintDeltaDto delta, CoachVocabularyFocusDto? focus)
    {
        if (!delta.ChangedFields.Contains(CoachConstraintField.VocabularyFocus))
        {
            return null;
        }

        return delta.ClearVocabularyFocus || focus is null
            ? CoachDeterministicCopy.FocusCleared
            : CoachDeterministicCopy.FocusApplied(focus.SelectedCount, focus.DisplayLabel);
    }

    /// <summary>The same constraint set with the focus words re-attached from a selection.</summary>
    private static CoachConstraintSetDto WithFocusWords(
        CoachConstraintSetDto constraints, CoachFocusSelection? selection) =>
        constraints.VocabularyFocus is not { } focus || selection?.Words is not { Count: > 0 } words
            ? constraints
            : new CoachConstraintSetDto
            {
                AvailableMinutes = constraints.AvailableMinutes,
                AudioAllowed = constraints.AudioAllowed,
                SpeechAllowed = constraints.SpeechAllowed,
                TypingAllowed = constraints.TypingAllowed,
                SkillEmphasis = constraints.SkillEmphasis,
                GoalTag = constraints.GoalTag,
                GoalHorizonDays = constraints.GoalHorizonDays,
                EnergyLevel = constraints.EnergyLevel,
                VocabularyFocus = new CoachVocabularyFocusDto
                {
                    FocusCode = focus.FocusCode,
                    DisplayLabel = focus.DisplayLabel,
                    EligibleCount = focus.EligibleCount,
                    SelectedCount = focus.SelectedCount,
                    Words = words
                }
            };

    /// <summary>The same constraint set with the focus reduced to its code and label.</summary>
    private static CoachConstraintSetDto WithoutFocusWords(CoachConstraintSetDto constraints) =>
        constraints.VocabularyFocus is not { } focus
            ? constraints
            : new CoachConstraintSetDto
            {
                AvailableMinutes = constraints.AvailableMinutes,
                AudioAllowed = constraints.AudioAllowed,
                SpeechAllowed = constraints.SpeechAllowed,
                TypingAllowed = constraints.TypingAllowed,
                SkillEmphasis = constraints.SkillEmphasis,
                GoalTag = constraints.GoalTag,
                GoalHorizonDays = constraints.GoalHorizonDays,
                EnergyLevel = constraints.EnergyLevel,
                VocabularyFocus = new CoachVocabularyFocusDto
                {
                    FocusCode = focus.FocusCode,
                    DisplayLabel = focus.DisplayLabel,
                    EligibleCount = focus.EligibleCount,
                    SelectedCount = focus.SelectedCount,
                    Words = Array.Empty<CoachVocabularyFocusWordDto>()
                }
            };

    /// <summary>Field-by-field equality over the eight constraint fields.</summary>
    private static bool ConstraintsMatch(CoachConstraintSetDto left, CoachConstraintSetDto right) =>
        left.AvailableMinutes == right.AvailableMinutes
        && left.AudioAllowed == right.AudioAllowed
        && left.SpeechAllowed == right.SpeechAllowed
        && left.TypingAllowed == right.TypingAllowed
        && left.SkillEmphasis == right.SkillEmphasis
        && string.Equals(left.GoalTag, right.GoalTag, StringComparison.Ordinal)
        && left.GoalHorizonDays == right.GoalHorizonDays
        && left.EnergyLevel == right.EnergyLevel
        && string.Equals(
            left.VocabularyFocus?.FocusCode, right.VocabularyFocus?.FocusCode, StringComparison.Ordinal);

    /// <summary>
    /// Persists how a turn left the session: the status it is now in, and either the reason it
    /// stopped or an explicit clear of a previous one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A null <paramref name="stopReason"/> means "this turn succeeded", and clears whatever a
    /// previous turn left behind. Without that, a session that once hit an output limit kept
    /// reporting <c>ValidationFailed</c> through every later successful turn, so stored state
    /// described a failure that had already been recovered from.
    /// </para>
    /// <para>
    /// Every terminal point in a turn goes through here so status and stop reason cannot drift
    /// apart, and so a new terminal path cannot forget one of them.
    /// </para>
    /// </remarks>
    private Task RecordTurnOutcomeAsync(
        string userProfileId,
        CoachSession session,
        CoachSessionStatus status,
        CoachStopReason? stopReason,
        CancellationToken cancellationToken,
        int clarificationIncrement = 0,
        CoachConstraintSetDto? activeConstraints = null,
        CoachFocusSelection? focusSelection = null) =>
        _sessions.UpdateAsync(
            userProfileId,
            session.Id,
            new CoachSessionUpdate
            {
                Status = status,
                StopReason = stopReason,
                ClearStopReason = stopReason is null,
                ClarificationIncrement = clarificationIncrement,
                // Constraints and their frozen selection move together, in one column, so a write
                // cannot leave a focus label describing a set that is no longer stored.
                ActiveStateJson = activeConstraints is null
                    ? null
                    : ActiveStateJson(session, activeConstraints, focusSelection)
            },
            cancellationToken);

    /// <summary>
    /// Records a successful non-writing turn and returns the status it left behind.
    /// </summary>
    private async Task<CoachSessionStatus> ClearedStatusAsync(
        string userProfileId,
        CoachSession session,
        CancellationToken cancellationToken)
    {
        var status = StatusWithPending(session);
        await RecordTurnOutcomeAsync(userProfileId, session, status, stopReason: null, cancellationToken)
            .ConfigureAwait(false);
        return status;
    }

    /// <summary>
    /// The status a non-writing turn leaves behind: an unanswered offer keeps the session on
    /// <see cref="CoachSessionStatus.SuggestionPending"/>, everything else returns to Active.
    /// </summary>
    private static CoachSessionStatus StatusWithPending(CoachSession session) =>
        string.IsNullOrWhiteSpace(session.PendingSuggestionId)
            ? CoachSessionStatus.Active
            : CoachSessionStatus.SuggestionPending;

    private CoachConstraintSetDto ActiveConstraints(CoachSession session) =>
        CoachActiveStateEnvelope.TryRead(session.ActiveConstraintsJson)?.Constraints
        ?? CoachConstraintMapper.Default(15);

    /// <summary>
    /// The frozen focus selection behind the active constraints, or null when the plan has none.
    /// </summary>
    /// <remarks>
    /// Read straight from stored state, never re-resolved. A later unrelated change reuses these
    /// exact identifiers, so "make it 20 minutes" cannot quietly swap the focus words.
    /// </remarks>
    private static CoachFocusSelection? ActiveFocusSelection(CoachSession session) =>
        CoachActiveStateEnvelope.TryRead(session.ActiveConstraintsJson)?.FocusSelection;

    /// <summary>Serializes the trio the active-constraints column now holds.</summary>
    private static string ActiveStateJson(
        CoachSession session, CoachConstraintSetDto constraints, CoachFocusSelection? selection)
    {
        var previous = CoachActiveStateEnvelope.TryRead(session.ActiveConstraintsJson);

        return CoachNormalizedJson.Serialize(new CoachActiveStateEnvelope
        {
            Constraints = constraints,
            // Checkpoint coverage rides in the same envelope, so a constraint rewrite must carry
            // it forward. Dropping it here would silently mark a healthy checkpoint as stale and
            // rebuild the agent's memory on the next turn for no reason.
            Checkpoint = previous?.Checkpoint,
            // Ids only here: the words live once, on the constraint set beside it.
            FocusSelection = selection?.WithoutWords(),
            RememberedFocus = previous?.Remembering(selection) ?? (selection?.Words is { Count: > 0 }
                ? [selection]
                : Array.Empty<CoachFocusSelection>())
        });
    }

    private async Task<PendingCoachSuggestionDto?> LoadPendingAsync(
        string userProfileId,
        CoachSession session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.PendingSuggestionId)
            || string.IsNullOrEmpty(session.PendingSuggestionDeltaJson))
        {
            return null;
        }

        var stored = CoachPendingSuggestionEnvelope.TryRead(session.PendingSuggestionDeltaJson);
        var delta = stored?.Delta;
        if (delta is null)
        {
            return null;
        }

        // Re-reading an offer replays the stored selection. Resolving again here would let the
        // offer drift between the turn that made it and the turn that accepts it.
        var storedFocus = delta.ClearVocabularyFocus
            ? null
            : stored!.FocusSelection ?? ActiveFocusSelection(session);

        var current = ActiveConstraints(session);
        var plan = await _planService.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var proposed = _mapper.Apply(
            current, delta,
            stored!.FocusSelection is not null
                ? CoachVocabularyFocusService.Project(stored.FocusSelection)
                : current.VocabularyFocus);

        var preview = await _planService
            .PreviewPlanAsync(
                _mapper.ToPlanConstraints(proposed), storedFocus?.VocabularyWordIds, cancellationToken)
            .ConfigureAwait(false);

        var diff = preview.IsSuccess && preview.Snapshot is not null
            // Same merge as the turn that created the suggestion, so re-reading a session
            // never shows a different preview from the one the learner was offered.
            ? _projection.ToDiff(plan, PlanRevisionPreview.Merge(plan, preview.Snapshot), isPreview: true)
            : _projection.ToDiff(plan, plan, isPreview: true);

        _ = userProfileId;

        return new PendingCoachSuggestionDto
        {
            SuggestionId = session.PendingSuggestionId!,
            Delta = delta,
            // Re-read from the stored selection, so the sentence a learner sees on reload is the
            // one they were first offered, down to the count.
            // Rebuilt from the stored artifact, so a reload shows the same ordered words, the
            // same count, and the same label as the offer. No resolver runs here.
            VocabularyFocus =
                delta.ChangedFields.Contains(CoachConstraintField.VocabularyFocus)
                && !delta.ClearVocabularyFocus
                    ? CoachVocabularyFocusService.Project(stored.FocusSelection)
                    : null,
            Rationale = CoachDeterministicCopy.SuggestionRationale(
                delta, CoachVocabularyFocusService.Project(storedFocus)),
            Preview = diff,
            // A reload consults the stored offer, not the learner's data, so this is normally
            // empty — and it is empty because the buffer is, not because the field was pinned shut.
            Evidence = BuildEvidence(),
            AcceptLabel = AcceptLabel,
            RejectLabel = RejectLabel,
            CreatedAtUtc = session.PendingSuggestionCreatedAt ?? session.UpdatedAt,
            ExpiresAtUtc = session.ExpiresAt
        };
    }

    private static string LearnerTextFor(CoachTurnRequest request) => request.InputKind switch
    {
        CoachTurnInputKind.Chip => request.ChipId ?? string.Empty,
        _ => request.Text ?? string.Empty
    };

    private CoachOperationResult<CoachTurnResponse>? ValidateTurnInput(CoachTurnRequest request)
    {
        if (!Enum.IsDefined(request.InputKind))
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput,
                "The turn input kind is not supported.");
        }

        var maxLength = _options.CurrentValue.MaxTurnTextLength;

        switch (request.InputKind)
        {
            case CoachTurnInputKind.Text:
                if (string.IsNullOrWhiteSpace(request.Text))
                {
                    return CoachOperationResult<CoachTurnResponse>.Problem(
                        CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput,
                        "The message is empty.");
                }

                if (request.Text.Length > maxLength)
                {
                    return CoachOperationResult<CoachTurnResponse>.Problem(
                        CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput,
                        $"The message must be {maxLength} characters or fewer.");
                }

                break;

            case CoachTurnInputKind.Chip:
                if (string.IsNullOrWhiteSpace(request.ChipId)
                    || request.ChipId.Length > CoachConstraintLimits.MaxChipIdLength)
                {
                    return CoachOperationResult<CoachTurnResponse>.Problem(
                        CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput,
                        "The chip identifier is missing or too long.");
                }

                break;

            case CoachTurnInputKind.ConstraintAction:
                if (request.ConstraintAction is null)
                {
                    return CoachOperationResult<CoachTurnResponse>.Problem(
                        CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput,
                        "The constraint action is missing.");
                }

                break;
        }

        if (request.ClientTurnId is { Length: > CoachConstraintLimits.MaxChipIdLength })
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput,
                "The client turn identifier is too long.");
        }

        return null;
    }

    private CoachOperationResult<CoachTurnResponse>? MapRevisionFailure(PlanRevisionResult revision) =>
        revision.Outcome switch
        {
            PlanRevisionOutcome.Applied or PlanRevisionOutcome.NoChange => null,

            PlanRevisionOutcome.StalePlanVersion => CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.PlanChangedElsewhere, CoachProblemTypes.PlanVersionConflict,
                "Today's Plan changed somewhere else. Nothing was written."),

            PlanRevisionOutcome.InvalidConstraints => CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.InvalidConstraint, CoachProblemTypes.InvalidConstraint,
                string.Join(" ", revision.ValidationErrors)),

            PlanRevisionOutcome.NoFeasiblePlan => CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.NoFeasiblePlan, CoachProblemTypes.PlanValidationFailed,
                "No plan satisfies that change."),

            PlanRevisionOutcome.PlanNotFound => CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.NoFeasiblePlan, CoachProblemTypes.PlanValidationFailed,
                "There is no plan for today to adjust."),

            _ => CoachOperationResult<CoachTurnResponse>.Problem(
                CoachOperationStatus.NoFeasiblePlan, CoachProblemTypes.PlanValidationFailed,
                "The revised plan failed validation. Nothing was written.")
        };

    private CoachMessageDto CoachMessage(CoachMessageKind kind, string? text, string? suggestionId = null) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        Role = CoachMessageRole.Coach,
        Kind = kind,
        Text = text ?? string.Empty,
        CreatedAtUtc = _dateContext.UtcNow,
        RelatedSuggestionId = suggestionId
    };

    /// <summary>
    /// The evidence this turn earned: one item per population it actually read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The model no longer supplies this.</b> It used to: the server read
    /// <c>intent.EvidenceReferences</c>, took a kind and a day count from the model's own claim,
    /// invented a label from the enum name, invented a summary, and attached no values at all. A
    /// turn that consulted nothing and asserted "PracticeBalance, 7 days" produced a card the
    /// learner could not tell apart from one backed by a real query — and checking exactly that is
    /// what the card is for.
    /// </para>
    /// <para>
    /// Now it comes from the turn's observation buffer, which holds the scope every completed read
    /// stated. The model's references are still validated, but as a <em>claim</em> checked against
    /// this record rather than as the source of it; see the grounding gate in
    /// <c>ProcessTurnAsync</c>.
    /// </para>
    /// <para>
    /// <b>No argument, on purpose.</b> The buffer is scoped to the turn, so every call inside one
    /// turn projects the same reads. Threading a list through the reducers would have meant
    /// touching every branch of a file this batch forbids splitting, for no additional truth.
    /// </para>
    /// </remarks>
    private IReadOnlyList<CoachEvidenceDto> BuildEvidence()
    {
        if (_observations is null)
        {
            return Array.Empty<CoachEvidenceDto>();
        }

        return CoachTurnEvidenceProjection.Project(
            _observations.Observations,
            _dateContext.UserLocalDate);
    }

    /// <summary>
    /// Refuses a turn whose grounding claim the reads do not support, and evidence the server
    /// itself built wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rules, and the first is the one that used to be missing. A turn may cite evidence only
    /// if it read something: if the intent names references and no scoped read completed, the
    /// citation is decoration on an answer that consulted nothing. That used to pass — evidence was
    /// built from the claim itself, so a claim could never fail to be supported by it.
    /// </para>
    /// <para>
    /// The second replaces a silent bypass. Window validation used to end in
    /// <c>validation.IsValid ? evidence : Array.Empty&lt;&gt;()</c>: an item the server could not
    /// stand behind was dropped, and the turn went out looking like one that had cited nothing.
    /// Evidence is now built from the server's own scopes, so a bad window is a server defect, and
    /// hiding a defect behind an empty list is how it survives to the next release. It refuses
    /// through the same path a malformed intent takes.
    /// </para>
    /// </remarks>
    private CoachValidationResult ValidateGrounding(CoachTurnIntent intent)
    {
        var violations = new List<CoachViolation>();
        var observations = _observations?.Observations ?? Array.Empty<Tools.Observation.CoachToolCallObservation>();

        if (intent.EvidenceReferences.Count > 0 && !CoachTurnEvidenceProjection.AnyGroundedRead(observations))
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.EvidenceWindow,
                "evidence_ungrounded",
                $"The answer cites {intent.EvidenceReferences.Count} piece(s) of evidence, but no read "
                + "this turn stated a scope. Nothing was consulted, so there is nothing to cite."));
        }

        violations.AddRange(_intentValidator
            .ValidateEvidence(BuildEvidence(), _dateContext.UserLocalDate)
            .Violations);

        return CoachValidationResult.From(violations);
    }

    private CoachRevisionDto ToRevisionDto(CoachPlanRevision revision)
    {
        var delta = CoachNormalizedJson.Deserialize<CoachConstraintDeltaDto>(revision.AcceptedConstraintDeltaJson)
                    ?? new CoachConstraintDeltaDto();

        return ToRevisionDto(
            revision,
            delta,
            revision.Source == CoachRevisionSource.Undo
                ? "Restored the previous remaining items."
                : CoachConstraintMapper.Summarize(delta),
            canUndo: !revision.IsUndone && revision.Source != CoachRevisionSource.Undo);
    }

    private static CoachRevisionDto ToRevisionDto(
        CoachPlanRevision revision,
        CoachConstraintDeltaDto delta,
        string summary,
        bool canUndo) => new()
        {
            RevisionId = revision.Id,
            RevisionNumber = revision.RevisionNumber,
            Source = revision.Source,
            ChangedFields = delta.ChangedFields,
            Summary = summary,
            BeforePlanVersion = revision.BeforePlanVersion,
            AfterPlanVersion = revision.AfterPlanVersion,
            CreatedAtUtc = revision.CreatedAt,
            IsUndone = revision.IsUndone,
            CanUndo = canUndo
        };

    private static CoachOperationResult<T> Unavailable<T>(string detail) =>
        CoachOperationResult<T>.Problem(CoachOperationStatus.Unavailable, CoachProblemTypes.Unavailable, detail);

    private static CoachOperationResult<T> NotFoundFor<T>(CoachSessionLoadStatus status) => status switch
    {
        CoachSessionLoadStatus.Expired => CoachOperationResult<T>.Problem(
            CoachOperationStatus.SessionExpired, CoachProblemTypes.SessionExpired,
            "That coach session has expired."),

        CoachSessionLoadStatus.ConfigVersionMismatch or CoachSessionLoadStatus.Unreadable =>
            CoachOperationResult<T>.Problem(
                CoachOperationStatus.SessionExpired, CoachProblemTypes.SessionExpired,
                "That coach session can no longer be resumed."),

        // A session owned by another learner is indistinguishable from one that never existed.
        _ => CoachOperationResult<T>.Problem(
            CoachOperationStatus.SessionNotFound, CoachProblemTypes.SessionNotFound,
            "No coach session with that id.")
    };
}
