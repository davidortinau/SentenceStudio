using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.Memory;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Tests.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Builds a <see cref="CoachSessionService"/> over a real relational <see cref="CoachDbContext"/>
/// and fakes for the pieces that are covered elsewhere: the planner (exercised end to end by
/// <c>PlanServiceCoachRevisionTests</c>) and the model.
/// </summary>
internal sealed class CoachApplicationHarness : IDisposable
{
    public const string OwnerUserId = "coach-owner-1";
    public const string OtherUserId = "coach-intruder-2";

    private readonly CoachPersistenceHarness _persistence;
    private readonly CoachDbContext _db;

    public CoachApplicationHarness(
        CoachOptions? options = null,
        bool withHistory = false,
        bool withMemory = false,
        ILoggerProvider? loggerProvider = null,
        SentenceStudio.Api.Coach.Opportunities.ICoachOpportunityRecorder? opportunities = null,
        bool withUnboundAnswerDetector = false)
    {
        // Real loggers only when a test asks for them, so the default harness stays quiet and a
        // leak test can prove what was never written.
        var loggers = loggerProvider is null
            ? NullLoggerFactory.Instance
            : LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(loggerProvider));

        _persistence = new CoachPersistenceHarness();
        _db = _persistence.NewContext();

        // The session service takes the ledger only when durable history is in play, so the
        // default harness still proves the pre-history behavior is untouched.
        Messages = withHistory ? _persistence.NewMessageStore(_db) : null;
        Conversations = withHistory ? _persistence.NewConversationStore(_db) : null;

        Options = new TestOptionsMonitor<CoachOptions>(options ?? new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = { OwnerUserId, OtherUserId },
            MaxRunsPerDay = 10,
            MaxRunsPerWeek = 40
        });

        UserScope = new FakeUserScopeProvider(OwnerUserId);
        DateContext = new FakePlanDateContext(new DateOnly(2026, 8, 14));
        PlanService = new FakePlanService(DateContext.UserLocalDate);
        Coach = new ScriptedLearningCoach();
        AgentFactory = new FakeCoachAgentFactory { IsModelAvailable = true };
        Budget = new InMemoryCoachBudgetService(Options, _persistence.Time);
        Sessions = _persistence.NewSessionStore(_db);
        Usage = _persistence.NewUsageStore(_db);
        Telemetry = new CoachTelemetry();
        Runs = new CoachRunRegistry();
        Idempotency = new CoachTurnIdempotencyStore(_persistence.Time);

        // Memory runs against the real store and the real selector so a test proves the
        // encrypted round trip and the selection policy, not a stub of both. The rotator is the
        // production one, pointed at this harness's session store.
        if (withMemory)
        {
            MemoryRotator = new CoachMemoryCheckpointRotator(
                new SingleInstanceScopeFactory(Sessions),
                loggers.CreateLogger<CoachMemoryCheckpointRotator>());

            MemoryOptions = new CoachMemoryOptions { Enabled = true };
            Memories = _persistence.NewMemoryStore(
                _db, MemoryRotator, MemoryOptions, loggers.CreateLogger<CoachMemoryStore>());

            MemorySelector = new RecordingMemoryContextSelector(
                new CoachMemoryContextSelector(
                    Memories,
                    Microsoft.Extensions.Options.Options.Create(MemoryOptions),
                    loggers.CreateLogger<CoachMemoryContextSelector>()));

            MemoryCoordinator = new CoachMemoryTurnCoordinator(
                MemorySelector,
                Memories,
                Microsoft.Extensions.Options.Options.Create(MemoryOptions),
                loggers.CreateLogger<CoachMemoryTurnCoordinator>());
        }

        // The fake plan mints "resource-{itemId}", so ownership passes by default and a test
        // only opts into a failure by pointing the provider somewhere else.
        ValidationData.OwnedProvider = () => PlanService.Current.Items
            .Select(i => i.ResourceId)
            .Concat(PlanService.NextRemainder.Select(i => i.ResourceId))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!);

        Opportunities = opportunities;

        // The turn's captured reads. Always present, always empty until a test seeds it, so the
        // default harness models a turn that consulted nothing — which is what every pre-W3b test
        // was implicitly asserting when it expected no evidence.
        Observations = new SentenceStudio.Api.Coach.Tools.Observation.CoachTurnObservationBuffer();

        UnboundAnswers = withUnboundAnswerDetector
            ? new SentenceStudio.Api.Coach.Opportunities.Detection.CoachUnboundAnswerDetector(
                new CoachExplicitAcceptanceClassifier(),
                NullLogger<SentenceStudio.Api.Coach.Opportunities.Detection.CoachUnboundAnswerDetector>.Instance,
                Messages)
            : null;

        // The grounding ladder, built from the real engine over the real capability manifest and
        // frozen registry. A fake here would prove the call site exists and nothing about what it
        // calls, and "the engine was never called" is the defect this wiring exists to close.
        ClaimFindings = new SentenceStudio.Api.Coach.Validation.Claims.CoachClaimFindingBuffer();

        var capabilityManifest = new SentenceStudio.Api.Coach.Capabilities.CoachCapabilityManifest(
            SentenceStudio.Api.Coach.Tools.CoachToolServiceCollectionExtensions.BuildValidatedRegistry(
                new CoachOptions
                {
                    DurableHistory = new CoachFeatureSwitch { Enabled = true },
                    SamOverlay = new CoachFeatureSwitch { Enabled = true },
                    SamReadTools = new CoachFeatureSwitch { Enabled = true },
                    SamWriteTools = new CoachFeatureSwitch { Enabled = true }
                }));

        CapabilityResolver = new SentenceStudio.Api.Coach.Capabilities.CoachCapabilityResolver(
            capabilityManifest);

        Grounding = new SentenceStudio.Api.Coach.Validation.Claims.CoachTurnGroundingEvaluator(
            new SentenceStudio.Api.Coach.Validation.Claims.CoachClaimRuleEngine(
                CapabilityResolver, capabilityManifest),
            CapabilityResolver,
            loggers.CreateLogger<SentenceStudio.Api.Coach.Validation.Claims.CoachTurnGroundingEvaluator>(),
            new SentenceStudio.Api.Coach.Validation.Claims.CoachShadowClaimRouter(),
            ClaimFindings);

        // Correction state, built from the real classifier and the real options monitor so a test
        // exercises the shipped decision rather than a stub of it.
        Disputes = new SentenceStudio.Api.Coach.Application.CoachDisputeCoordinator(
            new SentenceStudio.Api.Coach.Application.CoachCorrectionClassifier(),
            Options);

        Service = new CoachSessionService(
            UserScope,
            DateContext,
            PlanService,
            Sessions,
            Usage,
            new CoachAvailabilityPolicy(Options),
            Budget,
            AgentFactory,
            Coach,
            new CoachConstraintMapper(),
            new CoachPlanProjection(new EnglishPlanCopyProvider()),
            new CoachExplicitAcceptanceClassifier(),
            new CoachSuggestionValidator(),
            new CoachAnswerProjection(),
            Languages,
            new CoachWriteAuthority(),
            new CoachIntentValidator(),
            LeakValidator,
            new CoachVocabularyFocusService(FocusResolver, Languages, NullLogger<CoachVocabularyFocusService>.Instance),
            ValidationData,
            Runs,
            Idempotency,
            Telemetry,
            Options,
            NullLogger<CoachSessionService>.Instance,
            Messages,
            Conversations,
            MemoryCoordinator,
            writeTurn: null,
            writeLedger: null,
            opportunities: Opportunities,
            unboundAnswers: UnboundAnswers,
            observations: Observations,
            grounding: Grounding,
            disputes: Disputes);
    }

    /// <summary>The shipped correction-state coordinator the service calls.</summary>
    public SentenceStudio.Api.Coach.Application.CoachDisputeCoordinator Disputes { get; }

    /// <summary>Turns correction state on for subsequent turns.</summary>
    public void EnableCorrectionState(bool enabled = true)
    {
        Options.CurrentValue.CorrectionState = new CoachFeatureSwitch { Enabled = enabled };
    }

    /// <summary>The grounding ladder the service calls. Real engine, real manifest.</summary>
    public SentenceStudio.Api.Coach.Validation.Claims.CoachTurnGroundingEvaluator Grounding { get; }

    /// <summary>What the ladder recorded on the last turn. Null when it never ran.</summary>
    public SentenceStudio.Api.Coach.Validation.Claims.CoachClaimFindingBuffer ClaimFindings { get; }

    /// <summary>The resolver the capability rules consult.</summary>
    public SentenceStudio.Api.Coach.Capabilities.ICoachCapabilityResolver CapabilityResolver { get; }

    /// <summary>Moves the grounding ladder to <paramref name="stage"/> for subsequent turns.</summary>
    public void SetGroundingStage(SentenceStudio.Api.Coach.Validation.Claims.CoachGroundingStage stage)
    {
        Options.CurrentValue.Grounding.Stage = stage;
    }

    /// <summary>
    /// The turn's captured tool reads, which is where the session service now gets evidence from.
    /// </summary>
    /// <remarks>
    /// Exposed as the concrete buffer so a test can seed a read through
    /// <c>ICoachTurnObservationSink</c>. A test that seeds nothing is modelling a turn that read
    /// nothing, and the service must show no evidence for it.
    /// </remarks>
    public SentenceStudio.Api.Coach.Tools.Observation.CoachTurnObservationBuffer Observations { get; }

    /// <summary>
    /// Records that the turn actually made one scoped read, so a grounding claim in the intent is
    /// backed by something.
    /// </summary>
    /// <remarks>
    /// Before W3b, evidence was built from the model's own claim, so a test could assert a
    /// practice-balance card without anything ever having read a practice balance. Now the claim
    /// and the read are separate facts and the service checks one against the other, so a test
    /// that wants the card has to say the read happened.
    /// </remarks>
    public void SeedPracticeBalanceRead(int windowDays = 14, int activityTypes = 2)
    {
        var end = DateContext.UserLocalDate;

        ((SentenceStudio.Api.Coach.Tools.Observation.ICoachTurnObservationSink)Observations).Add(
            new SentenceStudio.Api.Coach.Tools.Observation.CoachToolCallObservation(
                SentenceStudio.Api.Coach.Tools.CoachToolNames.GetPracticeBalance,
                Ordinal: 1,
                Outcome: SentenceStudio.Api.Coach.Tools.Observation.CoachToolCallOutcome.Succeeded,
                FailureKind: null,
                ArgumentMask: SentenceStudio.Api.Coach.Tools.Observation.CoachToolArgumentMask.None,
                ElapsedMs: 1,
                Scope: new SentenceStudio.Api.Coach.Tools.CoachResultScope
                {
                    Coverage = SentenceStudio.Api.Coach.Tools.CoachScopeCoverage.WindowBounded,
                    Order = SentenceStudio.Api.Coach.Tools.CoachScopeOrder.MinutesDescending,
                    OrderHonored = true,
                    Filters = SentenceStudio.Api.Coach.Tools.CoachScopeFilters.OwnerScoped
                        | SentenceStudio.Api.Coach.Tools.CoachScopeFilters.DateWindow,
                    AsOfUtc = DateContext.UtcNow,
                    WindowStartDate = end.AddDays(-(windowDays - 1)),
                    WindowEndDate = end,
                    ReturnedCount = activityTypes,
                    MatchedCount = activityTypes,
                    DefinitionCode = SentenceStudio.Api.Coach.Tools.CoachScopeDefinition.PracticeWindowBalance,
                    EligiblePopulationCount = activityTypes,
                    MinimumEvidence = SentenceStudio.Api.Coach.Tools.CoachScopeMinimumEvidence.LoggedWorkRequired,
                    TieBreak = SentenceStudio.Api.Coach.Tools.CoachScopeTieBreak.ActivityTypeOrdinal,
                    ClockBasis = SentenceStudio.Api.Coach.Tools.CoachScopeClockBasis.LearnerLocalDay,
                    ReferenceMode = SentenceStudio.Api.Coach.Tools.CoachScopeReferenceMode.DateWindow
                }));
    }

    /// <summary>
    /// Records that a read was attempted and failed, so the turn has a trace but no grounding.
    /// </summary>
    /// <remarks>
    /// The distinction matters to more than one rule. A turn with no trace at all is unproven and
    /// the honesty rules stay silent on it; a turn whose only read failed <em>did</em> reach for the
    /// data, got nothing, and any claim about the learner made afterwards is unsupported. Seeding
    /// this is how a test models the second case without inventing a successful read.
    /// </remarks>
    public void SeedFailedRead()
    {
        ((SentenceStudio.Api.Coach.Tools.Observation.ICoachTurnObservationSink)Observations).Add(
            new SentenceStudio.Api.Coach.Tools.Observation.CoachToolCallObservation(
                SentenceStudio.Api.Coach.Tools.CoachToolNames.GetPracticeBalance,
                Ordinal: 1,
                Outcome: SentenceStudio.Api.Coach.Tools.Observation.CoachToolCallOutcome.Faulted,
                FailureKind: SentenceStudio.Api.Coach.Tools.CoachToolFailureKind.Unauthorized,
                ArgumentMask: SentenceStudio.Api.Coach.Tools.Observation.CoachToolArgumentMask.None,
                ElapsedMs: 1,
                Scope: null));
    }

    /// <summary>
    /// Records a successful vocabulary read that deliberately held rows back.
    /// </summary>
    /// <remarks>
    /// The due-word embargo is the reason a read withholds, and an answer built on a withheld page
    /// that does not say so is the <c>WithheldNotDisclosed</c> case. There is no span to repair —
    /// the defect is a sentence nobody wrote — so this is also the fixture that separates Repair
    /// from Enforce.
    /// </remarks>
    /// <param name="reason">
    /// Why rows were held back. <c>DueReviewEmbargo</c> is the realistic production value and is
    /// <em>disclosure</em>: the panel renders the count and the reason together in the learner's
    /// own language. Pass <c>None</c> to model the incoherent pair — a count the panel cannot
    /// explain — which is what leaves <c>WithheldNotDisclosed</c> standing.
    /// </param>
    public void SeedWithheldVocabularyRead(
        int matched = 14,
        int returned = 10,
        int withheld = 4,
        SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason reason =
            SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason.DueReviewEmbargo)
    {
        ((SentenceStudio.Api.Coach.Tools.Observation.ICoachTurnObservationSink)Observations).Add(
            new SentenceStudio.Api.Coach.Tools.Observation.CoachToolCallObservation(
                SentenceStudio.Api.Coach.Tools.CoachToolNames.ListUserVocabularies,
                Ordinal: 1,
                Outcome: SentenceStudio.Api.Coach.Tools.Observation.CoachToolCallOutcome.Succeeded,
                FailureKind: null,
                ArgumentMask: SentenceStudio.Api.Coach.Tools.Observation.CoachToolArgumentMask.None,
                ElapsedMs: 1,
                Scope: new SentenceStudio.Api.Coach.Tools.CoachResultScope
                {
                    Coverage = SentenceStudio.Api.Coach.Tools.CoachScopeCoverage.PageOfOwnedSet,
                    Order = SentenceStudio.Api.Coach.Tools.CoachScopeOrder.MasteryDescending,
                    OrderHonored = true,
                    Filters = SentenceStudio.Api.Coach.Tools.CoachScopeFilters.OwnerScoped
                        | SentenceStudio.Api.Coach.Tools.CoachScopeFilters.ProgressRowExists
                        | SentenceStudio.Api.Coach.Tools.CoachScopeFilters.ExcludeDue,
                    AsOfUtc = DateContext.UtcNow,
                    RequestedCount = 25,
                    MatchedCount = matched,
                    ReturnedCount = returned,
                    WithheldCount = withheld,
                    WithheldReason = reason,
                    DefinitionCode = SentenceStudio.Api.Coach.Tools.CoachScopeDefinition.UndueVocabularySearch,
                    ClockBasis = SentenceStudio.Api.Coach.Tools.CoachScopeClockBasis.LearnerLocalDay,
                    ReferenceMode = SentenceStudio.Api.Coach.Tools.CoachScopeReferenceMode.AsOfInstant
                }));
    }

    /// <summary>
    /// The opportunity recorder the session service observes through, when one was supplied.
    /// </summary>
    /// <remarks>
    /// Null by default, so every existing test proves the pre-ledger behaviour is untouched and
    /// the session service falls back to <c>NullCoachOpportunityRecorder</c> exactly as a host
    /// with capture off does.
    /// </remarks>
    public SentenceStudio.Api.Coach.Opportunities.ICoachOpportunityRecorder? Opportunities { get; }

    /// <summary>
    /// The real referent-loss detector, wired to this harness's message store, when asked for.
    /// </summary>
    /// <remarks>
    /// The production classifier and the production offer grading, not a stub — the whole point
    /// of an application-level test here is that the same predicate the write gate trusts is the
    /// one deciding whether a row is written.
    /// </remarks>
    public SentenceStudio.Api.Coach.Opportunities.Detection.CoachUnboundAnswerDetector? UnboundAnswers { get; }

    /// <summary>The real memory store, when the harness was built with memory. Null otherwise.</summary>
    public SentenceStudio.Api.Coach.Memory.CoachMemoryStore? Memories { get; }

    /// <summary>The production checkpoint rotator wired to this harness's session store.</summary>
    public CoachMemoryCheckpointRotator? MemoryRotator { get; }

    /// <summary>Wraps the real selector so a test can read what was asked for, or fail it.</summary>
    public RecordingMemoryContextSelector? MemorySelector { get; }

    public CoachMemoryTurnCoordinator? MemoryCoordinator { get; }

    public CoachMemoryOptions? MemoryOptions { get; }

    /// <summary>The live answer-leak validator, so the gate is exercised, not stubbed.</summary>
    public CoachDueItemLeakValidator LeakValidator { get; } = new();

    /// <summary>Server-only validation facts. Tests set the embargo and ownership sets here.</summary>
    public FakeCoachValidationDataSource ValidationData { get; } = new();

    /// <summary>The scripted vocabulary focus resolver. Tests set its next result.</summary>
    public FakeVocabularyFocusResolver FocusResolver { get; } = new();

    /// <summary>The learner's language tags. Korean target, English display, by default.</summary>
    public StubLanguageResolver Languages { get; } = new();

    public TestOptionsMonitor<CoachOptions> Options { get; }

    public FakeUserScopeProvider UserScope { get; }

    public FakePlanDateContext DateContext { get; }

    public FakePlanService PlanService { get; }

    public ScriptedLearningCoach Coach { get; }

    public FakeCoachAgentFactory AgentFactory { get; }

    public ICoachSessionStore Sessions { get; }

    public ICoachUsageStore Usage { get; }

    public InMemoryCoachBudgetService Budget { get; }

    public CoachTelemetry Telemetry { get; }

    public CoachRunRegistry Runs { get; }

    public CoachTurnIdempotencyStore Idempotency { get; }

    public CoachSessionService Service { get; }

    public CoachDbContext Db => _db;

    /// <summary>The shared persistence fixture, so a history harness can reuse the same database.</summary>
    public CoachPersistenceHarness Persistence => _persistence;

    /// <summary>The canonical ledger, when this harness was built with durable history on.</summary>
    public SentenceStudio.Api.Coach.Persistence.History.CoachMessageStore? Messages { get; }

    /// <summary>
    /// The conversation ledger the session service opens a row in when history is on, so the
    /// compatibility session route and the conversation routes describe the same thread.
    /// </summary>
    public SentenceStudio.Api.Coach.Persistence.History.CoachConversationStore? Conversations { get; }

    public void Dispose()
    {
        Telemetry.Dispose();
        _db.Dispose();
        _persistence.Dispose();
    }

    /// <summary>Starts a session for the current user and returns its id.</summary>
    /// <summary>
    /// Offers a vocabulary focus and accepts it, which is the only way one is ever applied.
    /// </summary>
    /// <remarks>
    /// A focus names a category and leaves the server to choose the words, so it always takes the
    /// Accept / Not now path. A test that wants a focus in force has to go through both halves,
    /// exactly as a learner does.
    /// </remarks>
    public async Task<CoachOperationResult<CoachTurnResponse>> OfferAndAcceptFocusAsync(
        string sessionId, CoachAgentTurnResult focusTurn, string text)
    {
        Coach.NextResult = focusTurn;

        var offered = await Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });

        offered.Value!.PendingSuggestion.Should().NotBeNull(
            "a semantic focus is always offered before it is applied");

        return await Service.AcceptSuggestionAsync(
            sessionId, offered.Value.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());
    }

    public async Task<string> StartSessionAsync()
    {
        var result = await Service.StartSessionAsync(new StartCoachSessionRequest { Resume = false });
        result.IsOk.Should().BeTrue();
        return result.Value!.SessionId;
    }

    /// <summary>Every memory row the current owner holds, read back through the real store.</summary>
    public async Task<IReadOnlyList<SentenceStudio.Contracts.LearnerMemory.CoachMemoryFactDto>> StoredMemoriesAsync(
        CoachMemoryListFilter filter = CoachMemoryListFilter.All)
    {
        if (Memories is null)
        {
            return [];
        }

        var owner = SentenceStudio.Api.Coach.Persistence.History.CoachOwner.TryCreate(UserScope.Current!, null, out var value)
            ? value
            : throw new InvalidOperationException("No owner in scope.");

        var page = await Memories.ListAsync(owner, filter);
        return page.Items.Select(f => f.ToDto()).ToList();
    }

    /// <summary>The protected checkpoint of one session, or null when it has been rotated away.</summary>
    public async Task<string?> CheckpointAsync(string sessionId)
    {
        using var db = _persistence.NewContext();
        var row = await db.CoachSessions.FindAsync(sessionId);
        return row?.ProtectedAgentSession;
    }
}

internal sealed class FakeUserScopeProvider : IUserScopeProvider
{
    public FakeUserScopeProvider(string? userProfileId) => Current = userProfileId;

    public string? Current { get; set; }

    public string UserProfileId => string.IsNullOrWhiteSpace(Current)
        ? throw new UnauthorizedAccessException("No user scope.")
        : Current;

    public bool TryGetUserProfileId(out string userProfileId)
    {
        userProfileId = Current ?? string.Empty;
        return !string.IsNullOrWhiteSpace(Current);
    }
}

internal sealed class FakePlanDateContext : IPlanDateContext
{
    public FakePlanDateContext(DateOnly today) => UserLocalDate = today;

    public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

    public DateTime UtcNow { get; set; } = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    public DateOnly UserLocalDate { get; set; }

    public DateOnly ToUserLocal(DateTime utc) => DateOnly.FromDateTime(utc);

    public DateTime ToUtcMidnight(DateOnly userLocal) => userLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}

/// <summary>
/// A plan service that models the parts of the real contract the coach reducer depends on:
/// version checking, preservation of completed and started items, and replacement of the
/// untouched remainder. The real <c>PlanService</c> behaviour is pinned by
/// <c>PlanServiceCoachRevisionTests</c> in the unit-test project.
/// </summary>
internal sealed class FakePlanService : IPlanService
{
    private readonly DateOnly _today;
    private List<PlanSnapshotItem> _items;

    public FakePlanService(DateOnly today)
    {
        _today = today;
        _items =
        [
            Item("done-1", PlanActivityType.VocabularyReview, priority: 1, minutes: 5, spent: 5, completed: true),
            Item("started-1", PlanActivityType.Reading, priority: 2, minutes: 8, spent: 3, completed: false),
            Item("fresh-1", PlanActivityType.Listening, priority: 3, minutes: 7, spent: 0, completed: false)
        ];
    }

    /// <summary>
    /// The planner's whole remainder for the next preview/apply, with top-of-plan priorities
    /// exactly like the real <c>PreviewPlanAsync</c>. The apply path re-bases them behind the
    /// preserved rows.
    /// </summary>
    public List<PlanSnapshotItem> NextRemainder { get; set; } =
    [
        Item("fresh-2", PlanActivityType.Writing, priority: 1, minutes: 4, spent: 0, completed: false)
    ];

    public PlanPreviewOutcome PreviewOutcome { get; set; } = PlanPreviewOutcome.Success;

    public int ApplyCallCount { get; private set; }

    /// <summary>How many previews were built. An answer turn must build none.</summary>
    public int PreviewCallCount { get; private set; }

    public int UndoCallCount { get; private set; }

    public PlanConstraints? LastAppliedConstraints { get; private set; }

    /// <summary>The constraints the last preview was planned against.</summary>
    public PlanConstraints? LastPreviewConstraints { get; private set; }

    public PlanSnapshot Current => PlanSnapshot.FromItems(_today, _items);

    public void SetItems(IEnumerable<PlanSnapshotItem> items) => _items = items.ToList();

    public Task<PlanSnapshot> GetTodaySnapshotAsync(CancellationToken ct = default) => Task.FromResult(Current);

    /// <summary>The focus identifiers the last preview was given, in order.</summary>
    public IReadOnlyList<string>? LastPreviewFocusIds { get; private set; }

    /// <summary>The focus identifiers the last apply was given, in order.</summary>
    public IReadOnlyList<string>? LastApplyFocusIds { get; private set; }

    public Task<PlanPreviewResult> PreviewPlanAsync(
        PlanConstraints? constraints,
        IReadOnlyList<string>? focusVocabularyWordIds,
        CancellationToken ct = default)
    {
        LastPreviewFocusIds = focusVocabularyWordIds;
        return PreviewPlanAsync(constraints, ct);
    }

    public Task<PlanPreviewResult> PreviewPlanAsync(PlanConstraints? constraints, CancellationToken ct = default)
    {
        LastPreviewConstraints = constraints;
        PreviewCallCount++;

        if (constraints is not null && !constraints.TryValidate(out var errors))
        {
            return Task.FromResult(PlanPreviewResult.InvalidConstraints(errors));
        }

        if (PreviewOutcome == PlanPreviewOutcome.NoFeasiblePlan)
        {
            return Task.FromResult(PlanPreviewResult.NoFeasiblePlan());
        }

        var snapshot = PlanSnapshot.FromItems(_today, NextRemainder);
        return Task.FromResult(PlanPreviewResult.Success(EmptySkeleton(), snapshot));
    }

    public Task<PlanRevisionResult> ApplyCoachConstraintsAsync(
        CoachPlanRevisionRequest request,
        CancellationToken ct = default)
    {
        ApplyCallCount++;
        LastApplyFocusIds = request.FocusVocabularyWordIds;
        LastAppliedConstraints = request.Constraints;

        var before = Current;

        if (!before.MatchesVersion(request.ExpectedPlanVersion))
        {
            return Task.FromResult(PlanRevisionResult.NoWrite(
                PlanRevisionOutcome.StalePlanVersion, before, request.OperationKey));
        }

        if (request.Constraints is not null && !request.Constraints.TryValidate(out var errors))
        {
            return Task.FromResult(PlanRevisionResult.NoWrite(
                PlanRevisionOutcome.InvalidConstraints, before, request.OperationKey, errors));
        }

        if (PreviewOutcome == PlanPreviewOutcome.NoFeasiblePlan)
        {
            return Task.FromResult(PlanRevisionResult.NoWrite(
                PlanRevisionOutcome.NoFeasiblePlan, before, request.OperationKey));
        }

        // Completed and started rows survive byte-identical; only untouched rows are replaced.
        // This uses the very same merge the coach's suggestion preview uses, which is what
        // makes the preview-versus-accept parity assertion meaningful rather than circular:
        // the service must feed the merge the same inputs on both paths.
        var preserved = _items.Where(i => i.IsCompleted || i.MinutesSpent > 0).ToList();
        var replaced = _items.Count - preserved.Count;
        var after = PlanRevisionPreview.Merge(before, PlanSnapshot.FromItems(_today, NextRemainder));
        var next = after.Items.ToList();

        if (string.Equals(after.Hash, before.Hash, StringComparison.Ordinal))
        {
            return Task.FromResult(PlanRevisionResult.NoWrite(
                PlanRevisionOutcome.NoChange, before, request.OperationKey));
        }

        _items = next;

        return Task.FromResult(new PlanRevisionResult
        {
            Outcome = PlanRevisionOutcome.Applied,
            OperationKey = request.OperationKey,
            Before = before,
            After = after,
            PreservedCompletedCount = before.CompletedItemCount,
            PreservedInProgressCount = before.InProgressItemCount,
            PreservedMinutesSpent = after.TotalMinutesSpent,
            ReplacedItemCount = replaced,
            AddedItemCount = NextRemainder.Count,
            RemovedItemCount = replaced,
            AdjustedItemCount = 0
        });
    }

    public Task<PlanRevisionResult> UndoCoachRevisionAsync(
        CoachPlanUndoRequest request,
        CancellationToken ct = default)
    {
        UndoCallCount++;
        var before = Current;

        if (!before.MatchesVersion(request.ExpectedPlanVersion))
        {
            return Task.FromResult(PlanRevisionResult.NoWrite(
                PlanRevisionOutcome.StalePlanVersion, before, request.OperationKey));
        }

        // Never touches completed or started work, exactly like the real undo.
        var preserved = _items.Where(i => i.IsCompleted || i.MinutesSpent > 0).ToList();
        var preservedIds = preserved.Select(i => i.PlanItemId).ToHashSet(StringComparer.Ordinal);
        var restored = request.TargetSnapshot.Items.Where(i => !preservedIds.Contains(i.PlanItemId));

        _items = preserved.Concat(restored).ToList();
        var after = Current;

        return Task.FromResult(new PlanRevisionResult
        {
            Outcome = PlanRevisionOutcome.Applied,
            OperationKey = request.OperationKey,
            Before = before,
            After = after,
            PreservedCompletedCount = before.CompletedItemCount,
            PreservedInProgressCount = before.InProgressItemCount,
            PreservedMinutesSpent = after.TotalMinutesSpent
        });
    }

    public Task<TodaysPlanDto?> GetTodayAsync(CancellationToken ct = default) =>
        Task.FromResult<TodaysPlanDto?>(null);

    public Task<TodaysPlanDto> GenerateTodayAsync(GenerateTodaysPlanRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<bool> UpdateProgressAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<PlanItemDto?> MarkCompleteAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default) =>
        Task.FromResult<PlanItemDto?>(null);

    public Task ResetTodayAsync(CancellationToken ct = default) => Task.CompletedTask;

    internal static PlanSnapshotItem Item(
        string id, PlanActivityType type, int priority, int minutes, int spent, bool completed) => new()
        {
            PlanItemId = id,
            ActivityType = type.ToString(),
            ResourceId = $"resource-{id}",
            SkillId = null,
            Priority = priority,
            EstimatedMinutes = minutes,
            MinutesSpent = spent,
            IsCompleted = completed
        };

    private static PlanSkeleton EmptySkeleton() => new() { TotalMinutes = 0 };
}

/// <summary>A coach arm whose answer each test sets explicitly. No model, no network.</summary>
internal sealed class ScriptedLearningCoach : ILearningCoach
{
    /// <summary>Which arm this stands in for. Both arms run through the same reducer.</summary>
    public CoachImplementation Implementation { get; set; } = CoachImplementation.Baseline;

    public CoachAgentTurnResult NextResult { get; set; } = new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = "Nothing to change." }
    };

    public int RunCount { get; private set; }

    public CoachAgentTurnRequest? LastRequest { get; private set; }

    /// <summary>Every request the reducer sent, so a test can inspect a rebuilt turn's history.</summary>
    public List<CoachAgentTurnRequest> Requests { get; } = new();

    /// <summary>Results handed out in order, ahead of <see cref="NextResult"/>. Empty by default.</summary>
    public Queue<CoachAgentTurnResult> Script { get; } = new();

    /// <summary>Raised before each result is returned, so a test can fail or stall mid-turn.</summary>
    public Func<CoachAgentTurnRequest, Task>? OnRun { get; set; }

    public async Task<CoachAgentTurnResult> RunTurnAsync(
        CoachAgentTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        RunCount++;
        LastRequest = request;
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (OnRun is not null)
        {
            await OnRun(request).ConfigureAwait(false);
        }

        return Script.Count > 0 ? Script.Dequeue() : NextResult;
    }
}

internal sealed class FakeCoachAgentFactory : ICoachAgentFactory
{
    public bool IsModelAvailable { get; set; } = true;

    public Microsoft.Agents.AI.AIAgent? TryCreateAgent(IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools) => null;

    public Microsoft.Agents.AI.AIAgent? TryCreateHarnessAgent(IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools) => null;
}

/// <summary>
/// Stands in for the scoped database reads the validators make. Tests set the embargoed
/// items and the owned resource ids directly, so a leak or an unowned preview is easy to
/// arrange without seeding vocabulary rows.
/// </summary>
internal sealed class FakeCoachValidationDataSource : ICoachValidationDataSource
{
    public List<CoachEmbargoedItem> EmbargoedItems { get; } = new();

    /// <summary>Replaces the embargoed set in one statement.</summary>
    public IReadOnlyList<CoachEmbargoedItem> Embargoed
    {
        set
        {
            EmbargoedItems.Clear();
            EmbargoedItems.AddRange(value);
        }
    }

    public HashSet<string> OwnedResourceIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Supplies the ids that count as owned. The harness points this at the fake plan, so a
    /// test only has to override it when it wants an ownership failure.
    /// </summary>
    public Func<IEnumerable<string>>? OwnedProvider { get; set; }

    public int EmbargoQueryCount { get; private set; }

    public int OwnershipQueryCount { get; private set; }

    public Task<IReadOnlyList<CoachEmbargoedItem>> GetEmbargoedItemsAsync(
        IEnumerable<string>? additionalWordIds = null,
        CancellationToken cancellationToken = default)
    {
        EmbargoQueryCount++;
        return Task.FromResult<IReadOnlyList<CoachEmbargoedItem>>(EmbargoedItems.ToList());
    }

    public Task<IReadOnlyCollection<string>> GetOwnedResourceIdsAsync(CancellationToken cancellationToken = default)
    {
        OwnershipQueryCount++;

        var owned = new HashSet<string>(OwnedResourceIds, StringComparer.Ordinal);
        if (OwnedProvider is not null)
        {
            foreach (var id in OwnedProvider())
            {
                owned.Add(id);
            }
        }

        return Task.FromResult<IReadOnlyCollection<string>>(owned);
    }
}


/// <summary>A language resolver a test can set, so answer tags are deterministic.</summary>
internal sealed class StubLanguageResolver : ICoachLanguageResolver
{
    public CoachLanguageProfile Profile { get; set; } = new("ko-KR", "en-US", "en-US");

    public Task<CoachLanguageProfile> ResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Profile);
}

/// <summary>
/// A scripted <see cref="IVocabularyFocusResolver"/>. Tests set the outcome and the words, so a
/// focus turn is exercised without seeding vocabulary rows.
/// </summary>
internal sealed class FakeVocabularyFocusResolver : IVocabularyFocusResolver
{
    public VocabularyFocusResult NextResult { get; set; } = new()
    {
        Outcome = VocabularyFocusOutcome.Success,
        Items =
        [
            Word("v-1", "\uAC00\uB2E4", "to go"),
            Word("v-2", "\uBA39\uB2E4", "to eat"),
            Word("v-3", "\uBCF4\uB2E4", "to see"),
            Word("v-4", "\uD558\uB2E4", "to do"),
            Word("v-5", "\uC77D\uB2E4", "to read")
        ],
        MatchedCount = 12,
        OwnedCandidateCount = 40,
        ClassifiedCandidateCount = 30,
        RequestedCount = 10
    };

    /// <summary>Every request the coach made, so a test can prove one resolve or none.</summary>
    public List<VocabularyFocusRequest> Requests { get; } = new();

    public int ResolveCount => Requests.Count;

    public Task<VocabularyFocusResult> ResolveAsync(VocabularyFocusRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(NextResult);
    }

    public static VocabularyFocusItem Word(string id, string target, string native) => new()
    {
        VocabularyWordId = id,
        TargetLanguageTerm = target,
        NativeLanguageTerm = native,
        PartOfSpeech = VocabularyPartOfSpeech.Verb,
        MatchReason = VocabularyFocusMatchReason.DueForReview
    };

    /// <summary>A typed failure with no items.</summary>
    public static VocabularyFocusResult Failure(VocabularyFocusOutcome outcome, int matched = 0) => new()
    {
        Outcome = outcome,
        MatchedCount = matched,
        OwnedCandidateCount = 40,
        ClassifiedCandidateCount = outcome == VocabularyFocusOutcome.MetadataUnavailable ? 2 : 30,
        RequestedCount = 10
    };
}
