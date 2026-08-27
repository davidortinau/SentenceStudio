using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.Compatibility;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Builds the durable conversation service over the same real relational database as the session
/// service beneath it.
/// </summary>
/// <remarks>
/// <para>
/// The point of sharing one database is that these tests can assert the two halves agree: the
/// session service is still the only writer of plan state, and everything the learner can read
/// back comes out of the encrypted ledger this harness also holds a handle on.
/// </para>
/// <para>
/// Nothing here is a stand-in for the store layer. The conversation store, message store, and
/// turn-operation store are the production types over the production entity configuration; only
/// the model and the planner are faked, exactly as in <see cref="CoachApplicationHarness"/>.
/// </para>
/// </remarks>
internal sealed class CoachConversationHarness : IDisposable
{
    public const string OwnerUserId = CoachApplicationHarness.OwnerUserId;
    public const string OtherUserId = CoachApplicationHarness.OtherUserId;

    public CoachConversationHarness(
        bool durableHistory = true,
        SentenceStudio.Api.Coach.Opportunities.ICoachOpportunityRecorder? opportunities = null,
        bool withUnboundAnswerDetector = false,
        int maxRunsPerDay = 50,
        SentenceStudio.Application.Practice.IPracticeHistoryQueries? practiceHistory = null)
    {
        App = new CoachApplicationHarness(
            new CoachOptions
            {
                Enabled = true,
                AllowedUserProfileIds = { OwnerUserId, OtherUserId },
                MaxRunsPerDay = maxRunsPerDay,
                MaxRunsPerWeek = 200,
                DurableHistory = new CoachFeatureSwitch { Enabled = durableHistory }
            },
            withHistory: true,
            opportunities: opportunities,
            withUnboundAnswerDetector: withUnboundAnswerDetector,
            practiceHistory: practiceHistory);

        Conversations = App.Persistence.NewConversationStore(App.Db);
        Messages = App.Persistence.NewMessageStore(App.Db);
        Operations = App.Persistence.NewTurnOperationStore(App.Db);
        Export = App.Persistence.NewExportReader(App.Db);

        // The conversation service writes through the faulting decorators, so a test can put a
        // process death in a chosen window. Reads in tests go straight to the real stores.
        FaultingMessages = new FaultingCoachMessageStore(Messages);
        FaultingOperations = new FaultingCoachTurnOperationStore(Operations);

        // Renewal runs on its own context, as it does in production, and reports what it did so a
        // test can wait for a renewal rather than sleep past one.
        Renewer = new RecordingCoachTurnLeaseRenewer(App.Persistence);

        Service = NewService();
    }

    public CoachApplicationHarness App { get; }

    public CoachConversationStore Conversations { get; }

    public CoachMessageStore Messages { get; }

    public CoachTurnOperationStore Operations { get; }

    public CoachHistoryExportReader Export { get; }

    /// <summary>The write path the service uses. Set its fail point to simulate a crash.</summary>
    public FaultingCoachMessageStore FaultingMessages { get; }

    /// <summary>The operation write path the service uses.</summary>
    public FaultingCoachTurnOperationStore FaultingOperations { get; }

    /// <summary>The lease renewal path the service uses.</summary>
    public RecordingCoachTurnLeaseRenewer Renewer { get; }

    public CoachConversationService Service { get; private set; }

    /// <summary>
    /// The fork the old <c>/sessions</c> routes go through. Built per read because
    /// <see cref="Restart"/> replaces the conversation service underneath it.
    /// </summary>
    public CoachCompatibilitySessionService Compat => new(
        App.Service,
        Service,
        NullLogger<CoachCompatibilitySessionService>.Instance);

    public TestTimeProvider Time => App.Persistence.Time;

    public CoachDbContext Db => App.Db;

    public ScriptedLearningCoach Coach => App.Coach;

    public CoachOwner Owner => CoachOwner.ForUser(OwnerUserId);

    public CoachOwner Intruder => CoachOwner.ForUser(OtherUserId);

    /// <summary>Switches the acting learner. Used by every owner-isolation test.</summary>
    public void ActAs(string userProfileId) => App.UserScope.Current = userProfileId;

    /// <summary>
    /// Rebuilds the service over the same database, which is how these tests model a process
    /// restart: durable state survives, everything in memory does not.
    /// </summary>
    public void Restart()
    {
        // Durable state (conversations, the ledger, operation rows, checkpoints) lives in the
        // database and survives. Everything the conversation service held in memory does not.
        Service = NewService();
    }

    /// <summary>Creates a conversation for the acting learner and returns its id.</summary>
    public async Task<string> CreateConversationAsync(string? idempotencyKey = null, string? title = null)
    {
        var result = await Service.CreateAsync(new StartCoachConversationRequest
        {
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N"),
            Title = title
        });

        result.IsOk.Should().BeTrue(result.Detail);
        return result.Value!.ConversationId;
    }

    /// <summary>Submits one text turn with a fresh idempotency key and operation id.</summary>
    /// <remarks>
    /// <paramref name="operationId"/> defaults to a fresh value so ordinary tests need not think
    /// about it, and is overridable so a test can play the part of a client retrying a turn whose
    /// response it never saw.
    /// </remarks>
    public Task<CoachOperationResult<CoachTurnOperationDto>> TurnAsync(
        string conversationId,
        string text,
        string? idempotencyKey = null,
        string? operationId = null)
        => Service.SubmitTurnAsync(conversationId, new CoachConversationTurnRequest
        {
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N"),
            OperationId = operationId ?? Guid.NewGuid().ToString("N"),
            Turn = new CoachTurnRequest
            {
                InputKind = CoachTurnInputKind.Text,
                Text = text
            }
        });

    /// <summary>
    /// The plan revision a conversation's turns produced.
    /// </summary>
    /// <remarks>
    /// Resolved through the operations the conversation owns rather than by time or by taking the
    /// newest row, because the tests that use this exist to prove that revisions belong to a
    /// specific turn — a helper that guessed would assume the thing under test.
    /// </remarks>
    public async Task<CoachPlanRevision> RevisionForConversationAsync(string conversationId)
    {
        var operationIds = await Db.CoachTurnOperations
            .Where(o => o.UserProfileId == OwnerUserId && o.ConversationId == conversationId)
            .Select(o => o.Id)
            .ToListAsync();

        return Db.CoachPlanRevisions
            .Single(r => r.UserProfileId == OwnerUserId && operationIds.Contains(r.OperationId!));
    }

    /// <summary>
    /// The id of the most recently created turn operation for a conversation.
    /// </summary>
    /// <remarks>
    /// Operation ids are allocated by the store when a turn is claimed, so a test that wants to
    /// act on an in-flight turn — cancel it, inspect its lease — has to ask the database which
    /// turn that is. Reading it here keeps that detail out of every test that needs it.
    /// </remarks>
    public async Task<string?> LatestOperationIdAsync(string conversationId)
        => await Db.CoachTurnOperations
            .Where(o => o.UserProfileId == OwnerUserId && o.ConversationId == conversationId)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.FencingVersion)
            .Select(o => o.Id)
            .FirstOrDefaultAsync();

    /// <summary>
    /// Leaves the newest turn operation in the state a killed process leaves it: still Running,
    /// with a lease nobody is renewing.
    /// </summary>
    /// <remarks>
    /// A test can only interrupt a turn by throwing, and the service treats a thrown exception as
    /// a graceful failure — it marks the operation terminally Failed on the way out. A real crash
    /// writes nothing. This rewinds that one row so the durable state matches an actual process
    /// death, while every side effect the turn managed to commit stays exactly as the production
    /// code wrote it.
    /// </remarks>
    public async Task SimulateProcessDeathAsync(string conversationId)
    {
        var operationId = await LatestOperationIdAsync(conversationId);
        var operation = await Db.CoachTurnOperations.FirstAsync(
            o => o.UserProfileId == OwnerUserId && o.Id == operationId);

        operation.Status = CoachTurnOperationStatus.Running;
        operation.ErrorCode = null;
        operation.LeaseExpiresAt = Time.GetUtcNow().UtcDateTime.AddMinutes(-10);

        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>Every stored message in the ledger, oldest first, decrypted.</summary>
    public async Task<IReadOnlyList<CoachMessageRecord>> LedgerAsync(string conversationId)
    {
        var page = await Messages.GetLatestAsync(Owner, conversationId, CoachHistoryLimits.MessagePageMax);
        page.Status.Should().Be(CoachHistoryStatus.Success);
        return page.Items;
    }

    public void Dispose() => App.Dispose();

    private CoachConversationService NewService() => new(
        App.UserScope,
        Conversations,
        FaultingMessages,
        FaultingOperations,
        Renewer,
        Export,
        App.Service,
        App.Runs,
        Time,
        App.Options,
        NullLogger<CoachConversationService>.Instance,
        App.Telemetry);
}
