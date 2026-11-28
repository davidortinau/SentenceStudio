using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Records what a wrapped handler actually did, across every caller racing for it.
/// </summary>
/// <remarks>
/// The counters are the point. "Exactly one domain effect" is a claim about how many times the
/// handler ran, and the database can only show the state that survived — a second execution that
/// wrote the same row, or wrote and then failed, would leave a database indistinguishable from one
/// execution. Counting the calls is what makes the difference observable.
/// </remarks>
internal sealed class CoachWriteExecutionRecorder
{
    private int _executions;
    private int _undos;
    private int _externalEffects;

    /// <summary>How many times the wrapped handler's write ran.</summary>
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>How many times the wrapped handler's reversal ran.</summary>
    public int Undos => Volatile.Read(ref _undos);

    /// <summary>How many times the stand-in for an un-revocable outside call was made.</summary>
    public int ExternalEffects => Volatile.Read(ref _externalEffects);

    /// <summary>The identifiers the external effect was invoked for, in arrival order.</summary>
    public ConcurrentQueue<string> ExternalEffectTargets { get; } = new();

    public void RecordExecution() => Interlocked.Increment(ref _executions);

    public void RecordUndo() => Interlocked.Increment(ref _undos);

    public void RecordExternalEffect(string target)
    {
        Interlocked.Increment(ref _externalEffects);
        ExternalEffectTargets.Enqueue(target);
    }
}

/// <summary>
/// A production write handler with an observation point around it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a decorator over the real handler rather than a stand-in for it. The write under
/// test still goes through the real repository into the real database, and the ledger still
/// dispatches by tool name through the real catalogue — only the counting is added. A fake handler
/// would prove that the ledger calls something once, which is a claim about the ledger's own
/// bookkeeping and not about whether the learner's data changed twice.
/// </para>
/// <para>
/// <paramref name="externalEffect"/> stands in for the part of a protected write that leaves the
/// server: it runs before the domain write, is not covered by any transaction, and cannot be
/// undone by rolling one back. If the claim protocol were merely a database constraint rather than
/// a gate in front of the handler, this counter would be the thing that showed it.
/// </para>
/// </remarks>
internal sealed class CoachObservedWriteHandler : ICoachWriteHandler
{
    private readonly ICoachWriteHandler _inner;
    private readonly CoachWriteExecutionRecorder _recorder;
    private readonly bool _externalEffect;
    private readonly TimeSpan _executionDelay;
    private readonly CoachHandlerHold? _executionHold;

    public CoachObservedWriteHandler(
        ICoachWriteHandler inner,
        CoachWriteExecutionRecorder recorder,
        bool externalEffect = false,
        TimeSpan executionDelay = default,
        CoachHandlerHold? executionHold = null)
    {
        _inner = inner;
        _recorder = recorder;
        _externalEffect = externalEffect;
        _executionDelay = executionDelay;
        _executionHold = executionHold;
    }

    public string ToolName => _inner.ToolName;

    public CoachToolRiskClass RiskClass => _inner.RiskClass;

    public CoachWriteUndoKind UndoKind => _inner.UndoKind;

    public CoachWriteEntityKind EntityKind => _inner.EntityKind;

    public Task<CoachWritePreview> PrepareAsync(
        string userProfileId, string argumentsJson, CancellationToken cancellationToken) =>
        _inner.PrepareAsync(userProfileId, argumentsJson, cancellationToken);

    public async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, string argumentsJson, CancellationToken cancellationToken)
    {
        _recorder.RecordExecution();

        if (_externalEffect)
        {
            _recorder.RecordExternalEffect(argumentsJson);
        }

        if (_executionDelay > TimeSpan.Zero)
        {
            // Widens the window in which a second caller can arrive while the first still holds
            // the claim, so the loser meets a row in flight rather than one already settled.
            await Task.Delay(_executionDelay, cancellationToken).ConfigureAwait(false);
        }

        if (_executionHold is not null)
        {
            // Keeps the caller that won the claim inside the handler, with the row still
            // Executing, until the test lets go. Not a delay: the test releases it on an event,
            // and a hold nobody releases fails the run instead of quietly letting it through.
            await _executionHold.HoldAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _inner.ExecuteAsync(userProfileId, argumentsJson, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        string argumentsJson,
        string priorStateJson,
        CancellationToken cancellationToken)
    {
        _recorder.RecordUndo();

        if (_executionDelay > TimeSpan.Zero)
        {
            await Task.Delay(_executionDelay, cancellationToken).ConfigureAwait(false);
        }

        return await _inner
            .UndoAsync(userProfileId, argumentsJson, priorStateJson, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Holds the caller that reached the handler inside it, and says when it got there.
/// </summary>
/// <remarks>
/// Winning the claim and entering the handler are two moments, with a read of the row between
/// them. A test that needs the other caller to act while the handler is running has to wait for
/// the second moment, not assume it from the first: <see cref="Entered"/> completes, and
/// <c>onEntered</c> runs, only once a caller is inside and waiting here.
/// </remarks>
internal sealed class CoachHandlerHold
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Action? _onEntered;

    public CoachHandlerHold(Action? onEntered = null) => _onEntered = onEntered;

    /// <summary>Completes when a caller is inside the handler, held.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Lets the held caller go on to the real handler.</summary>
    public void Release() => _released.TrySetResult();

    public async Task HoldAsync(CancellationToken cancellationToken)
    {
        if (_entered.TrySetResult())
        {
            _onEntered?.Invoke();
        }

        await _released.Task.WaitAsync(CoachLostClaimHold.Limit, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Holds a caller that has just lost a claim on an operation row, before it reads the row again.
/// </summary>
/// <remarks>
/// <para>
/// A caller that loses the claim reads the row a second time and answers from what the winner
/// left there: a refusal while the winner is still in the handler, the winner's receipt once it
/// has settled. Which of the two it meets is decided in the gap between its claim and that second
/// read, and that gap is inside one request, where no test can reach from outside. This sits on
/// the loser's own connection, sees the claim come back with no rows, and waits there until the
/// test says the winner has settled.
/// </para>
/// <para>
/// Which caller loses is not known in advance, so every caller's context is given the same
/// instance and it acts only on a claim that changed nothing. A claim is one of the ledger's
/// conditional <c>ExecuteUpdateAsync</c> calls on the operation row, which EF sends as a
/// non-query; what the ledger writes through <c>SaveChanges</c> arrives as a reader and is never
/// mistaken for one. An approval makes one such call, the execution claim. The undo window's
/// claim and the release of an idempotency key are the same kind of statement on the same table,
/// so a test that gives this to an undo holds the loser of that claim instead.
/// </para>
/// <para>
/// The wait has a limit, and running into it throws. A loser that is never released means the
/// winner never answered, and a run that carried on regardless would be reporting on a race that
/// did not go the way the test says it did.
/// </para>
/// <para>
/// <see cref="WonClaimDelay"/> is the one timer here, and nothing waits on it for an answer. It
/// keeps the caller whose claim went through from going on for a while, the way a busy machine
/// can, so that a test whose order depends on how fast the winner reaches the handler shows it.
/// </para>
/// </remarks>
internal sealed class CoachLostClaimHold : DbCommandInterceptor
{
    /// <summary>How long any hold in these tests waits to be released before it fails the run.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _held;

    /// <summary>How many lost claims were held. A test that expects a held loser asserts on this.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>
    /// How long a caller that won a claim is kept, with the claim committed, before it reads the
    /// row again and goes on to the handler. Zero, the default, keeps nobody.
    /// </summary>
    public TimeSpan WonClaimDelay { get; init; }

    /// <summary>Lets every held caller go on to its second read.</summary>
    public void Release() => _released.TrySetResult();

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (!IsOperationUpdate(command))
        {
            return result;
        }

        if (result == 0)
        {
            Interlocked.Increment(ref _held);
            await _released.Task.WaitAsync(Limit, cancellationToken).ConfigureAwait(false);
        }
        else if (WonClaimDelay > TimeSpan.Zero)
        {
            await Task.Delay(WonClaimDelay, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static bool IsOperationUpdate(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith(
            "UPDATE \"CoachWriteOperation\"", StringComparison.Ordinal);
}

/// <summary>
/// Two approvals arriving at once, against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// This family exists because the exactly-once guarantee is a claim about the database and cannot
/// be demonstrated anywhere else. Every caller here gets its own <see cref="CoachDbContext"/> and
/// its own <see cref="ApplicationDbContext"/> over its own connection, which is what makes them
/// genuinely concurrent: they share no change tracker, no transaction, and no lock that lives
/// inside the process. Two API instances behind a load balancer contend exactly this way, and an
/// in-memory guard that passed a single-context test would fail here.
/// </para>
/// <para>
/// The assertions are deliberately about four separate things, because a design can satisfy any
/// three of them and still be wrong: the handler ran once, the learner's data changed once, the
/// ledger recorded one execution, and nothing was left in a state a later approval could run
/// again.
/// </para>
/// </remarks>
public sealed class CoachWriteRacePostgresTests : IAsyncLifetime
{
    private const string Owner = "user-race-owner";
    private const string Conversation = "conv-race";

    /// <summary>Long enough that the loser reliably arrives mid-flight, short enough to be free.</summary>
    private static readonly TimeSpan HandlerDwell = TimeSpan.FromMilliseconds(150);

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _appServices = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("writeraces", withApplicationSchema: true);
        await SeedConversationAsync(Owner, Conversation);

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(_harness.ConnectionString));
        services.AddSingleton<IFileSystemService, StubFileSystem>();
        _appServices = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _appServices?.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ soft acceptance

    /// <summary>
    /// Two acceptances of one soft proposal create one skill, one receipt, and one execution.
    /// </summary>
    [PostgresFact]
    public async Task Concurrent_acceptances_of_a_soft_proposal_write_once()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race soft");

        var outcomes = await RaceAsync(
            2,
            recorder,
            HandlerDwell,
            (ledger, _) => ledger.AcceptAsync(Conversation, operationId));

        // One handler call. Not "one surviving row" — one call, which is the only formulation a
        // second execution that wrote the same values could not satisfy.
        recorder.Executions.Should().Be(1, "the claim is taken before the handler, not after");

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Race soft"))
            .Should().Be(1, "two acceptances of one proposal are one change");

        await AssertSettledExactlyOnceAsync(operationId);

        // A loser on the soft route re-reads and finds a finished operation, so its honest answer
        // is the winner's receipt rather than a refusal. What matters is that it is the same
        // receipt: two different ones would mean two different writes.
        var receipts = outcomes.OfType<CoachWriteReceipt>().ToArray();
        receipts.Should().NotBeEmpty();
        receipts.Select(r => r.OperationId).Distinct().Should().ContainSingle();
        receipts.Should().AllSatisfy(r => r.Status.Should().Be(CoachWriteOperationStatus.Executed));
    }

    /// <summary>
    /// Four simultaneous acceptances behave exactly as two do.
    /// </summary>
    /// <remarks>
    /// Two callers can pass a claim that is only accidentally exclusive — one thread happens to
    /// finish before the other starts. Four make that coincidence much harder to arrange, and the
    /// count is still the assertion.
    /// </remarks>
    [PostgresFact]
    public async Task Four_simultaneous_acceptances_still_write_once()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race wide");

        var outcomes = await RaceAsync(
            4,
            recorder,
            HandlerDwell,
            (ledger, _) => ledger.AcceptAsync(Conversation, operationId));

        recorder.Executions.Should().Be(1);
        outcomes.Should().HaveCount(4);

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Race wide"))
            .Should().Be(1);

        await AssertSettledExactlyOnceAsync(operationId);
    }

    /// <summary>
    /// After the race, the proposal cannot be run again by anybody.
    /// </summary>
    /// <remarks>
    /// The dangerous failure is not a duplicate during the race; it is a row the race leaves in a
    /// state that still looks approvable afterwards. A later acceptance must find a finished
    /// operation and replay it, and must not reach the handler.
    /// </remarks>
    [PostgresFact]
    public async Task A_raced_proposal_leaves_nothing_a_later_approval_can_execute()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race orphan");

        await RaceAsync(2, recorder, HandlerDwell, (ledger, _) => ledger.AcceptAsync(Conversation, operationId));
        recorder.Executions.Should().Be(1);

        await using var db = _harness.NewContext();
        await using var appDb = NewAppContext();
        var ledger = NewLedger(db, appDb, Owner, recorder);

        var replay = await ledger.AcceptAsync(Conversation, operationId);

        replay.Status.Should().Be(CoachWriteOperationStatus.Executed);
        recorder.Executions.Should().Be(1, "a settled operation replays its receipt and never re-runs");

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Race orphan"))
            .Should().Be(1);

        await using var ledgerCheck = _harness.NewContext();
        var stored = await ledgerCheck.CoachWriteOperations.AsNoTracking()
            .SingleAsync(o => o.Id == operationId);

        stored.Status.Should().Be(CoachWriteOperationStatus.Executed);
        stored.Status.Should().NotBe(CoachWriteOperationStatus.Proposed);
        stored.ConfirmationDigest.Should().BeNull();
    }

    // ------------------------------------------------------------------ protected confirmation

    /// <summary>
    /// Two confirmations presenting the same secret archive the skill once and refuse once.
    /// </summary>
    [PostgresFact]
    public async Task Concurrent_confirmations_of_one_secret_execute_once()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var skillId = await SeedSkillAsync("Race protected");
        var (operationId, secret) = await ProposeAndChallengeArchiveAsync(skillId);

        var outcomes = await RaceAsync(
            2,
            recorder,
            HandlerDwell,
            (ledger, _) => ledger.ConfirmAsync(Conversation, operationId, secret));

        recorder.Executions.Should().Be(1, "a one-use secret authorises one execution");

        // One caller executed. What the other is told depends on when it looked, and both
        // answers are honest: see AssertLoserWasRefusedOrReplayedAsync.
        await AssertLoserWasRefusedOrReplayedAsync(outcomes, operationId);

        await using var check = NewAppContext();
        var skill = await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId);
        skill.IsArchived.Should().BeTrue();

        await AssertSettledExactlyOnceAsync(operationId);
    }

    /// <summary>
    /// The un-revocable half of a protected write happens once.
    /// </summary>
    /// <remarks>
    /// The stand-in runs before the domain write and outside any transaction, exactly like the
    /// outbound fetch a real import performs. A design that de-duplicated by rolling back a
    /// transaction, or by relying on the second write being idempotent, would leave this counter
    /// at two while every database assertion still passed.
    /// </remarks>
    [PostgresFact]
    public async Task An_irreversible_external_effect_runs_once_under_a_confirmation_race()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var skillId = await SeedSkillAsync("Race external");
        var (operationId, secret) = await ProposeAndChallengeArchiveAsync(skillId);

        await RaceAsync(
            3,
            recorder,
            HandlerDwell,
            (ledger, _) => ledger.ConfirmAsync(Conversation, operationId, secret),
            externalEffect: true);

        recorder.ExternalEffects.Should().Be(
            1, "the claim gates the handler, so nothing outside the database is reached twice");
        recorder.Executions.Should().Be(1);
        recorder.ExternalEffectTargets.Should().ContainSingle();

        await AssertSettledExactlyOnceAsync(operationId);
    }

    /// <summary>
    /// A caller who loses the claim is refused, and its refusal names a state rather than data.
    /// </summary>
    [PostgresFact]
    public async Task The_loser_of_a_confirmation_race_is_refused_without_reaching_the_handler()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var skillId = await SeedSkillAsync("Race loser");
        var (operationId, secret) = await ProposeAndChallengeArchiveAsync(skillId);

        var outcomes = await RaceAsync(
            2,
            recorder,
            HandlerDwell,
            (ledger, _) => ledger.ConfirmAsync(Conversation, operationId, secret));

        var refusal = outcomes.OfType<CoachToolException>().Should().ContainSingle().Subject;

        refusal.Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
        refusal.Reason.Should().NotContain(skillId, "a refusal names a state, never learner data");
        refusal.Reason.Should().NotContain("Race loser");
        recorder.Executions.Should().Be(1);

        // The refusal is recorded. A lost claim and an unrecorded execution are different
        // situations and the audit has to be able to tell an operator which one happened.
        await using var ledgerCheck = _harness.NewContext();
        var failureCodes = await ledgerCheck.CoachWriteAudits.AsNoTracking()
            .Where(a => a.OperationId == operationId && a.FailureCode != null)
            .Select(a => a.FailureCode!)
            .ToListAsync();

        failureCodes.Should().NotBeEmpty();
        failureCodes.Should().NotContain(
            CoachWriteFailureCodes.ReceiptNotRecorded,
            "nothing failed to settle here, so nothing should be reported as in doubt");
    }

    // ------------------------------------------------------------------ undo

    /// <summary>
    /// Two undos of one executed operation reverse it once.
    /// </summary>
    [PostgresFact]
    public async Task Concurrent_undos_reverse_once()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race undo");

        await using (var db = _harness.NewContext())
        await using (var appDb = NewAppContext())
        {
            await NewLedger(db, appDb, Owner, recorder).AcceptAsync(Conversation, operationId);
        }

        recorder.Executions.Should().Be(1);

        var outcomes = await RaceAsync(
            2,
            recorder,
            HandlerDwell,
            (ledger, _) => ledger.UndoAsync(Conversation, operationId));

        recorder.Undos.Should().Be(1, "the undo window is itself a one-use claim");

        outcomes.OfType<CoachWriteReceipt>().Should().ContainSingle();
        outcomes.OfType<CoachToolException>().Should().ContainSingle();

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Race undo"))
            .Should().Be(0, "the created skill is gone exactly once");

        await using var ledgerCheck = _harness.NewContext();
        var stored = await ledgerCheck.CoachWriteOperations.AsNoTracking()
            .SingleAsync(o => o.Id == operationId);

        stored.Status.Should().Be(CoachWriteOperationStatus.Undone);
        stored.UndoExpiresAtUtc.Should().BeNull("a spent window cannot be spent again");

        var undoEvents = await ledgerCheck.CoachWriteAudits.AsNoTracking()
            .CountAsync(a => a.OperationId == operationId && a.Event == CoachWriteAuditEvent.Undone);
        undoEvents.Should().Be(1);
    }

    /// <summary>
    /// A raced undo leaves nothing a later undo can reverse a second time.
    /// </summary>
    [PostgresFact]
    public async Task A_raced_undo_leaves_no_second_reversal()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race undo twice");

        await using (var db = _harness.NewContext())
        await using (var appDb = NewAppContext())
        {
            await NewLedger(db, appDb, Owner, recorder).AcceptAsync(Conversation, operationId);
        }

        await RaceAsync(2, recorder, HandlerDwell, (ledger, _) => ledger.UndoAsync(Conversation, operationId));
        recorder.Undos.Should().Be(1);

        await using var later = _harness.NewContext();
        await using var laterApp = NewAppContext();
        var ledger = NewLedger(later, laterApp, Owner, recorder);

        var act = async () => await ledger.UndoAsync(Conversation, operationId);
        await act.Should().ThrowAsync<CoachToolException>();

        recorder.Undos.Should().Be(1, "a reversal that already happened is not offered again");
    }

    // ------------------------------------------------------------------ settle failure

    /// <summary>
    /// A write that lands but whose receipt cannot be stored is left in doubt, and said so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure is produced the way production would produce it: another writer moves the row
    /// while the handler is still working, so the settle's concurrency token no longer matches and
    /// the save is rejected. By then the learner's skill exists. There is no honest way to report
    /// success, and retrying would risk creating it twice.
    /// </para>
    /// <para>
    /// So the row stays <c>Executing</c> — the state every later approval refuses — and an audit
    /// row records why. That second half is the part worth pinning: leaving the operation
    /// un-runnable is what keeps the learner's data correct, and leaving a reason is what lets
    /// somebody find out what happened without reading a log that may have rolled over.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_write_whose_receipt_cannot_be_recorded_is_left_in_doubt_and_audited()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race unsettled");

        await using var db = _harness.NewContext();
        await using var appDb = NewAppContext();
        var ledger = NewLedger(db, appDb, Owner, recorder, TimeSpan.FromSeconds(2));

        var approving = ledger.AcceptAsync(Conversation, operationId);

        await WaitForExecutionAsync(recorder).ConfigureAwait(false);
        await BumpOperationVersionAsync(operationId).ConfigureAwait(false);

        var act = async () => await approving;
        var refusal = await act.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Reason.Should().Contain("could not be recorded");

        // The handler did run, so the learner's data changed. Saying otherwise would be the lie
        // this whole path exists to avoid.
        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Race unsettled"))
            .Should().Be(1);

        await using var ledgerCheck = _harness.NewContext();
        var stored = await ledgerCheck.CoachWriteOperations.AsNoTracking()
            .SingleAsync(o => o.Id == operationId);

        stored.Status.Should().Be(
            CoachWriteOperationStatus.Executing, "an unrecorded outcome is in doubt, not finished");
        stored.ProtectedReceipt.Should().BeNull("there is no receipt, which is the whole problem");

        var codes = await ledgerCheck.CoachWriteAudits.AsNoTracking()
            .Where(a => a.OperationId == operationId)
            .Select(a => a.FailureCode)
            .ToListAsync();

        codes.Should().Contain(
            CoachWriteFailureCodes.ReceiptNotRecorded,
            "the state is safe without the audit; the audit is what makes it explicable");
    }

    /// <summary>An operation left in doubt is refused, not retried, by every later approval.</summary>
    [PostgresFact]
    public async Task An_operation_left_in_doubt_is_never_executed_again()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race unsettled twice");

        await using (var db = _harness.NewContext())
        await using (var appDb = NewAppContext())
        {
            var ledger = NewLedger(db, appDb, Owner, recorder, TimeSpan.FromSeconds(2));
            var approving = ledger.AcceptAsync(Conversation, operationId);

            await WaitForExecutionAsync(recorder).ConfigureAwait(false);
            await BumpOperationVersionAsync(operationId).ConfigureAwait(false);

            var act = async () => await approving;
            await act.Should().ThrowAsync<CoachToolException>();
        }

        recorder.Executions.Should().Be(1);

        await using var laterDb = _harness.NewContext();
        await using var laterApp = NewAppContext();
        var later = NewLedger(laterDb, laterApp, Owner, recorder);

        var retry = async () => await later.AcceptAsync(Conversation, operationId);
        var refusal = await retry.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Reason.Should().Contain("already being carried out");

        recorder.Executions.Should().Be(1, "a second attempt would risk a second skill");

        await using var check = NewAppContext();
        (await check.SkillProfiles
            .CountAsync(s => s.UserProfileId == Owner && s.Title == "Race unsettled twice"))
            .Should().Be(1);
    }

    /// <summary>Waits until the handler has started, so the interference lands mid-flight.</summary>
    private static async Task WaitForExecutionAsync(CoachWriteExecutionRecorder recorder)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (recorder.Executions == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException("The handler never started, so nothing was interfered with.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Moves the operation's concurrency token from outside the ledger.
    /// </summary>
    /// <remarks>
    /// Raw SQL on its own connection on purpose: this stands in for another writer, and going
    /// through the ledger would be asking the thing under test to arrange its own failure.
    /// </remarks>
    private async Task BumpOperationVersionAsync(string operationId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(_harness.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new Npgsql.NpgsqlCommand(
            """UPDATE "CoachWriteOperation" SET "Version" = "Version" + 1 WHERE "Id" = @id""",
            connection);
        command.Parameters.AddWithValue("id", operationId);

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false))
            .Should().Be(1, "the row has to exist for the interference to mean anything");
    }

    // ------------------------------------------------------------------ true claim contention

    /// <summary>
    /// Two approvals that have both already read the proposal still execute it once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion the conditional claim exists for, and the simultaneous-start tests
    /// above cannot make. There, one caller reaches the row first and every other caller reads a
    /// row that has already moved, so the refusal comes from the status check and the claim is
    /// never contended. That is the common case and worth pinning, but it would keep passing if
    /// the claim itself were removed.
    /// </para>
    /// <para>
    /// Here the test holds a row lock while both callers run. Their reads are plain selects, which
    /// PostgreSQL answers from the pre-lock snapshot, so both genuinely see <c>Proposed</c>; their
    /// claims are updates, which block. Releasing the lock lets both updates proceed at once
    /// against the same row — the exact situation two API processes produce — and the predicate is
    /// the only thing standing between that and two executions.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Two_approvals_that_both_read_the_proposal_execute_it_once()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race contended");

        var outcomes = await RaceUnderRowLockAsync(
            2, recorder, operationId, ledger => ledger.AcceptAsync(Conversation, operationId));

        recorder.Executions.Should().Be(
            1, "both callers reached the claim, and the claim admits one of them");

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Race contended"))
            .Should().Be(1);

        outcomes.Should().HaveCount(2);
        await AssertSettledExactlyOnceAsync(operationId);
    }

    /// <summary>
    /// Two confirmations that have both read the proposal reach the outside world once.
    /// </summary>
    /// <remarks>
    /// The protected counterpart of the test above, and the one that matters most: the stand-in
    /// effect runs before the domain write and outside every transaction, so a claim that admitted
    /// both callers would leave it at two no matter how the database resolved the writes.
    /// </remarks>
    [PostgresFact]
    public async Task Two_confirmations_that_both_read_the_proposal_reach_the_outside_world_once()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var skillId = await SeedSkillAsync("Race contended external");
        var (operationId, secret) = await ProposeAndChallengeArchiveAsync(skillId);

        var outcomes = await RaceUnderRowLockAsync(
            2,
            recorder,
            operationId,
            ledger => ledger.ConfirmAsync(Conversation, operationId, secret),
            externalEffect: true);

        recorder.ExternalEffects.Should().Be(1, "one confirmation, one outbound effect");
        recorder.Executions.Should().Be(1);

        // The handler does not dwell here, so the caller that lost the claim may re-read the row
        // before or after the winner settled it. Counting receipts would be counting that timing.
        await AssertLoserWasRefusedOrReplayedAsync(outcomes, operationId);

        await using var check = NewAppContext();
        (await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId))
            .IsArchived.Should().BeTrue();

        await AssertSettledExactlyOnceAsync(operationId);
    }

    /// <summary>
    /// A confirmation that loses the claim and reads again after the winner has settled is
    /// answered with the winner's receipt, and the audit records it as a replay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above reaches this answer only when the loser happens to be slow, and accepts a
    /// refusal in its place. Here it is the only answer possible: the loser is held on its own
    /// connection between its lost claim and its second read, and let go when the winner has
    /// returned, so the row it reads is settled on every run.
    /// </para>
    /// <para>
    /// It pins what a learner is told. Two taps on Confirm that race this way both carry the
    /// secret, the first spends it, and the second must hear that the change happened rather than
    /// that it failed.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_confirmation_that_loses_the_claim_and_reads_again_after_the_winner_settled_replays_its_receipt()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var skillId = await SeedSkillAsync("Race replayed after settle");
        var (operationId, secret) = await ProposeAndChallengeArchiveAsync(skillId);
        var hold = new CoachLostClaimHold();

        var outcomes = await RaceUnderRowLockAsync(
            2,
            recorder,
            operationId,
            ledger => ledger.ConfirmAsync(Conversation, operationId, secret),
            externalEffect: true,
            interceptor: hold,
            afterFirstOutcome: hold.Release);

        hold.Held.Should().Be(
            1, "one caller lost the claim and was held there; otherwise this run proved nothing");

        recorder.ExternalEffects.Should().Be(1, "the replay answers from the ledger, never the handler");
        recorder.Executions.Should().Be(1);

        outcomes.OfType<CoachToolException>().Should().BeEmpty(
            "the winner had settled before the loser read again, so there was a receipt to replay");
        outcomes.OfType<CoachWriteReceipt>().Should().HaveCount(2);
        await AssertLoserWasRefusedOrReplayedAsync(outcomes, operationId);

        await using var ledgerCheck = _harness.NewContext();
        var audits = await ledgerCheck.CoachWriteAudits.AsNoTracking()
            .Where(a => a.OperationId == operationId)
            .Select(a => new { a.Event, a.FailureCode })
            .ToListAsync();

        audits.Count(a => a.Event == CoachWriteAuditEvent.Replayed).Should().Be(1);
        audits.Should().NotContain(
            a => a.Event == CoachWriteAuditEvent.Denied,
            "the loser was answered, not refused, so nothing was denied");

        await using var check = NewAppContext();
        (await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId))
            .IsArchived.Should().BeTrue();

        await AssertSettledExactlyOnceAsync(operationId);
    }

    /// <summary>
    /// A confirmation that loses the claim and reads again while the winner is still in the
    /// handler is refused, and the audit records the refusal against the operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other answer of the same second read, pinned the same way. Both callers are held, each
    /// on an event. The loser waits between its lost claim and its second read until the winner
    /// is inside the handler; the winner waits inside the handler, with the row still
    /// <c>Executing</c>, until the loser has been answered. Winning the claim is not being in the
    /// handler: the winner reads the row once more in between, and a loser let through on the
    /// claim alone can be refused and gone before the handler has been entered. The second case
    /// delays the winner at exactly that point, so a test that assumed the order would fail there.
    /// </para>
    /// <para>
    /// <see cref="The_loser_of_a_confirmation_race_is_refused_without_reaching_the_handler"/>
    /// also ends in a refusal, but its loser starts late and is turned away by its first read; it
    /// never reaches the claim.
    /// </para>
    /// </remarks>
    [PostgresTheory]
    [InlineData(0)]
    [InlineData(400)]
    public async Task A_confirmation_that_loses_the_claim_and_reads_again_while_the_winner_is_executing_is_refused(
        int winnerDelayAfterClaimMilliseconds)
    {
        var recorder = new CoachWriteExecutionRecorder();
        var skillId = await SeedSkillAsync("Race refused before settle");
        var (operationId, secret) = await ProposeAndChallengeArchiveAsync(skillId);

        var lostClaims = new CoachLostClaimHold
        {
            WonClaimDelay = TimeSpan.FromMilliseconds(winnerDelayAfterClaimMilliseconds)
        };

        // Entering the handler is what lets the loser read again, and nothing else does.
        var winnerInHandler = new CoachHandlerHold(onEntered: lostClaims.Release);
        var executionsWhenLoserAnswered = -1;
        var winnerWasInHandlerWhenLoserAnswered = false;

        var outcomes = await RaceUnderRowLockAsync(
            2,
            recorder,
            operationId,
            ledger => ledger.ConfirmAsync(Conversation, operationId, secret),
            externalEffect: true,
            interceptor: lostClaims,
            handlerHold: winnerInHandler,
            afterFirstOutcome: () =>
            {
                executionsWhenLoserAnswered = recorder.Executions;
                winnerWasInHandlerWhenLoserAnswered = winnerInHandler.Entered.IsCompleted;
                winnerInHandler.Release();

                // Only matters when the race failed before the handler was reached: the loser
                // must not be left waiting on a connection that is about to be disposed.
                lostClaims.Release();
            });

        lostClaims.Held.Should().Be(
            1, "one caller reached the claim and lost it; otherwise the refusal came from its first read");
        winnerWasInHandlerWhenLoserAnswered.Should().BeTrue(
            "the loser is let go to read again only once the winner is inside the handler");
        executionsWhenLoserAnswered.Should().Be(
            1, "the loser was answered while the winner was still inside the handler");

        recorder.ExternalEffects.Should().Be(1);
        recorder.Executions.Should().Be(1);

        outcomes.OfType<CoachWriteReceipt>().Should().ContainSingle();
        var refusal = outcomes.OfType<CoachToolException>().Should().ContainSingle().Subject;
        refusal.Reason.Should().Contain("already being carried out");
        await AssertLoserWasRefusedOrReplayedAsync(outcomes, operationId);

        await using var ledgerCheck = _harness.NewContext();
        var audits = await ledgerCheck.CoachWriteAudits.AsNoTracking()
            .Where(a => a.OperationId == operationId)
            .Select(a => new { a.Event, a.FailureCode })
            .ToListAsync();

        audits.Where(a => a.Event == CoachWriteAuditEvent.Denied)
            .Should().ContainSingle()
            .Which.FailureCode.Should().Be(
                CoachWriteFailureCodes.ExecutionInDoubt,
                "the second read found the claim held, which is not the same as running out of reads");
        audits.Should().NotContain(a => a.Event == CoachWriteAuditEvent.Replayed);

        await using var check = NewAppContext();
        (await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId))
            .IsArchived.Should().BeTrue("the refusal of the loser does not disturb the winner's write");

        await AssertSettledExactlyOnceAsync(operationId);
    }

    /// <summary>
    /// Two undos that have both read the executed operation reverse it once.
    /// </summary>
    /// <remarks>
    /// The undo window is the claim here — there is no separate status to move — so the contended
    /// case is the only one that shows the window is genuinely one-use rather than merely usually
    /// one-use.
    /// </remarks>
    [PostgresFact]
    public async Task Two_undos_that_both_read_the_receipt_reverse_it_once()
    {
        var recorder = new CoachWriteExecutionRecorder();
        var operationId = await ProposeSkillAsync("Race contended undo");

        await using (var db = _harness.NewContext())
        await using (var appDb = NewAppContext())
        {
            await NewLedger(db, appDb, Owner, recorder).AcceptAsync(Conversation, operationId);
        }

        var outcomes = await RaceUnderRowLockAsync(
            2, recorder, operationId, ledger => ledger.UndoAsync(Conversation, operationId));

        recorder.Undos.Should().Be(1, "both callers held a live window and only one spent it");

        outcomes.OfType<CoachWriteReceipt>().Should().ContainSingle();
        outcomes.OfType<CoachToolException>().Should().ContainSingle();

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Race contended undo"))
            .Should().Be(0);

        await using var ledgerCheck = _harness.NewContext();
        var undoEvents = await ledgerCheck.CoachWriteAudits.AsNoTracking()
            .CountAsync(a => a.OperationId == operationId && a.Event == CoachWriteAuditEvent.Undone);
        undoEvents.Should().Be(1);

        var reversals = await ledgerCheck.CoachWriteOperations.AsNoTracking()
            .CountAsync(o => o.ConversationId == Conversation && o.UndoOperationId != null);
        reversals.Should().Be(1, "one reversal row, not one per caller");
    }

    // ------------------------------------------------------------------ race harness

    /// <summary>
    /// Runs <paramref name="callers"/> approvals against the same operation at the same moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each caller is built its own ledger over its own pair of contexts, and each of those
    /// connections is opened and exercised <em>before</em> the gate opens. That warm-up is not
    /// tidiness: an unopened Npgsql connection costs milliseconds on first use, and a caller that
    /// pays it while another does not is not racing, it is queueing.
    /// </para>
    /// <para>
    /// The callers run on dedicated threads and meet at a <see cref="Barrier"/> rather than
    /// awaiting a task, because a thread-pool continuation is scheduled at the pool's convenience
    /// and the skew between two of them is itself larger than the window.
    /// </para>
    /// <para>
    /// Results and refusals are both returned rather than thrown, because which caller wins is the
    /// one thing this test may not assume.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<object>> RaceAsync(
        int callers,
        CoachWriteExecutionRecorder recorder,
        TimeSpan handlerDwell,
        Func<CoachWriteOperationService, int, Task<CoachWriteReceipt>> approve,
        bool externalEffect = false)
    {
        using var gate = new Barrier(callers);
        var contexts = new List<IAsyncDisposable>();
        var ledgers = new List<CoachWriteOperationService>();

        try
        {
            for (var i = 0; i < callers; i++)
            {
                ledgers.Add(await NewWarmLedgerAsync(contexts, recorder, handlerDwell, externalEffect)
                    .ConfigureAwait(false));
            }

            var running = StartAll(ledgers, ledger => approve(ledger, ledgers.IndexOf(ledger)), gate);
            return await Task.WhenAll(running).ConfigureAwait(false);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs approvals that have all read the row before any of them may change it.
    /// </summary>
    /// <remarks>
    /// The lock is taken on the operation row by a connection this method owns. Each caller's read
    /// is a plain select and is answered from the snapshot, so every caller sees the state the row
    /// was in before the race; each caller's claim is an update and waits. The method does not
    /// release the lock on a timer — it waits until every caller is genuinely blocked, because a
    /// timer would silently degrade into the uncontended case on a slow machine and the test would
    /// go on passing while proving nothing.
    /// </remarks>
    /// <param name="interceptor">Given to every caller's coach context, when a test has to act between two of a caller's statements.</param>
    /// <param name="handlerHold">Keeps whichever caller reaches the handler inside it until the test releases it.</param>
    /// <param name="afterFirstOutcome">
    /// Runs once the first caller has been answered, and also if the race fails before that, so a
    /// test that holds one caller back can let it go when the other is done.
    /// </param>
    private async Task<IReadOnlyList<object>> RaceUnderRowLockAsync(
        int callers,
        CoachWriteExecutionRecorder recorder,
        string operationId,
        Func<CoachWriteOperationService, Task<CoachWriteReceipt>> approve,
        bool externalEffect = false,
        IInterceptor? interceptor = null,
        CoachHandlerHold? handlerHold = null,
        Action? afterFirstOutcome = null)
    {
        using var gate = new Barrier(callers);
        var contexts = new List<IAsyncDisposable>();
        var firstOutcomeSeen = false;

        await using var locker = new Npgsql.NpgsqlConnection(_harness.ConnectionString);
        await locker.OpenAsync().ConfigureAwait(false);
        await using var lockTransaction = await locker.BeginTransactionAsync().ConfigureAwait(false);

        try
        {
            await using (var command = new Npgsql.NpgsqlCommand(
                """SELECT 1 FROM "CoachWriteOperation" WHERE "Id" = @id FOR UPDATE""",
                locker,
                lockTransaction))
            {
                command.Parameters.AddWithValue("id", operationId);
                (await command.ExecuteScalarAsync().ConfigureAwait(false))
                    .Should().NotBeNull("the row being contended has to exist before it can be locked");
            }

            var ledgers = new List<CoachWriteOperationService>();
            for (var i = 0; i < callers; i++)
            {
                ledgers.Add(await NewWarmLedgerAsync(
                        contexts, recorder, TimeSpan.Zero, externalEffect, interceptor, handlerHold)
                    .ConfigureAwait(false));
            }

            var running = StartAll(ledgers, approve, gate);

            await WaitForBlockedBackendsAsync(locker.ProcessID, callers).ConfigureAwait(false);
            await lockTransaction.CommitAsync().ConfigureAwait(false);

            if (afterFirstOutcome is not null)
            {
                await Task.WhenAny(running).ConfigureAwait(false);
                firstOutcomeSeen = true;
                afterFirstOutcome();
            }

            return await Task.WhenAll(running).ConfigureAwait(false);
        }
        finally
        {
            // A race that failed before any caller answered still lets a held caller go: its
            // context is disposed below, and it must not be left waiting on that connection.
            if (!firstOutcomeSeen)
            {
                afterFirstOutcome?.Invoke();
            }

            foreach (var context in contexts)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Blocks until <paramref name="expected"/> other backends are waiting on a lock.</summary>
    /// <remarks>
    /// Asserting rather than timing out quietly. If the callers never block, they never contended,
    /// and a test that proceeded anyway would be reporting a result about a race that did not
    /// happen.
    /// </remarks>
    private async Task WaitForBlockedBackendsAsync(int lockHolderProcessId, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            await using var probe = new Npgsql.NpgsqlConnection(_harness.ConnectionString);
            await probe.OpenAsync().ConfigureAwait(false);

            await using var command = new Npgsql.NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event_type = 'Lock'
                  AND pid <> @holder
                  AND pid <> pg_backend_pid()
                """,
                probe);
            command.Parameters.AddWithValue("holder", lockHolderProcessId);

            var blocked = Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
            if (blocked >= expected)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Only fewer than {expected} approvals ever blocked on the operation row, so this run "
            + "did not contend and its result would not mean anything.");
    }

    private async Task<CoachWriteOperationService> NewWarmLedgerAsync(
        List<IAsyncDisposable> contexts,
        CoachWriteExecutionRecorder recorder,
        TimeSpan handlerDwell,
        bool externalEffect,
        IInterceptor? interceptor = null,
        CoachHandlerHold? handlerHold = null)
    {
        var db = interceptor is null ? _harness.NewContext() : _harness.NewContext(interceptor);
        var appDb = NewAppContext();
        contexts.Add(db);
        contexts.Add(appDb);

        // Warm both connections and both query pipelines. The first query on a fresh context pays
        // for connection setup and model warm-up, which is far longer than the window these tests
        // aim at; paying it here means every caller starts from the same state.
        await db.CoachWriteOperations.AsNoTracking().Take(1).ToListAsync().ConfigureAwait(false);
        await appDb.SkillProfiles.AsNoTracking().Take(1).ToListAsync().ConfigureAwait(false);

        return NewLedger(db, appDb, Owner, recorder, handlerDwell, externalEffect, handlerHold);
    }

    private static List<Task<object>> StartAll(
        IReadOnlyList<CoachWriteOperationService> ledgers,
        Func<CoachWriteOperationService, Task<CoachWriteReceipt>> approve,
        Barrier gate) =>
        ledgers.Select(ledger => Task.Factory.StartNew(
            async () =>
            {
                gate.SignalAndWait();
                try
                {
                    return (object)await approve(ledger).ConfigureAwait(false);
                }
                catch (CoachToolException ex)
                {
                    return ex;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap()).ToList();

    /// <summary>
    /// Asserts that of two confirmations one executed, and the other was either refused or
    /// answered with the winner's own receipt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A confirmation that loses the claim re-reads the row and answers from what the winner left
    /// behind. While the winner is still in the handler that is a refusal. Once the winner has
    /// settled it is the stored receipt, replayed: the ledger does this on purpose, whatever
    /// secret the request carries, so that a retried confirmation is told the change happened
    /// (<c>CoachWriteLedgerPostgresTests</c> pins the same answer for a spent confirmation).
    /// Which of the two the loser meets is a matter of milliseconds, so neither may be assumed.
    /// </para>
    /// <para>
    /// A replayed receipt is told from a second write by the audit, not by counting receipts: the
    /// ledger writes one <c>Replayed</c> row for each receipt it hands out again, and
    /// <see cref="AssertSettledExactlyOnceAsync"/> holds the <c>Executed</c> rows at one. Two
    /// receipts with no replay behind the second would be two executions.
    /// </para>
    /// </remarks>
    private async Task AssertLoserWasRefusedOrReplayedAsync(
        IReadOnlyList<object> outcomes, string operationId)
    {
        outcomes.Should().HaveCount(2);

        var receipts = outcomes.OfType<CoachWriteReceipt>().ToArray();
        var refusals = outcomes.OfType<CoachToolException>().ToArray();

        receipts.Should().NotBeEmpty("the caller that executed is answered with its receipt");
        (receipts.Length + refusals.Length).Should().Be(2, "every caller gets a receipt or a refusal");

        receipts.Should().AllSatisfy(r =>
        {
            r.OperationId.Should().Be(operationId);
            r.Status.Should().Be(CoachWriteOperationStatus.Executed);
        });
        receipts.Select(r => (r.EntityId, r.Summary, Lines: string.Join('\n', r.Lines)))
            .Distinct()
            .Should().ContainSingle("a replay says what the winner's receipt says");

        foreach (var refusal in refusals)
        {
            refusal.Reason.Should().Contain("already being carried out");
        }

        await using var db = _harness.NewContext();
        var replays = await db.CoachWriteAudits.AsNoTracking()
            .CountAsync(a => a.OperationId == operationId && a.Event == CoachWriteAuditEvent.Replayed);

        replays.Should().Be(
            receipts.Length - 1,
            "every receipt beyond the winner's is one the audit recorded as a replay");
    }

    /// <summary>
    /// Asserts the ledger recorded one execution and left the row settled.
    /// </summary>
    private async Task AssertSettledExactlyOnceAsync(string operationId)
    {
        await using var db = _harness.NewContext();

        var operations = await db.CoachWriteOperations.AsNoTracking()
            .Where(o => o.Id == operationId)
            .ToListAsync();

        operations.Should().ContainSingle();
        var operation = operations[0];

        operation.Status.Should().Be(CoachWriteOperationStatus.Executed);
        operation.ProtectedReceipt.Should().NotBeNull("a settled operation carries exactly one receipt");
        operation.ExecutedAtUtc.Should().NotBeNull();
        operation.ConfirmationDigest.Should().BeNull("the secret is spent when the change lands");

        var executed = await db.CoachWriteAudits.AsNoTracking()
            .CountAsync(a => a.OperationId == operationId && a.Event == CoachWriteAuditEvent.Executed);

        executed.Should().Be(1, "the audit records one execution, whatever the losers did");
    }

    // ------------------------------------------------------------------ wiring

    private ApplicationDbContext NewAppContext() => _harness.NewApplicationContext();

    /// <summary>Builds a ledger whose skill handlers are the real ones, wrapped in a counter.</summary>
    private CoachWriteOperationService NewLedger(
        CoachDbContext db,
        ApplicationDbContext appDb,
        string owner,
        CoachWriteExecutionRecorder recorder,
        TimeSpan handlerDwell = default,
        bool externalEffect = false,
        CoachHandlerHold? handlerHold = null)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var skills = new SkillProfileRepository(_appServices, NullLogger<SkillProfileRepository>.Instance);

        var handlers = new ICoachWriteHandler[]
        {
            new CoachObservedWriteHandler(
                new CoachSkillEntryHandler(skills, ownership), recorder, false, handlerDwell, handlerHold),
            new CoachObservedWriteHandler(
                new CoachSkillArchiveHandler(skills, ownership), recorder, externalEffect, handlerDwell, handlerHold)
        };

        return CoachWriteTestScope.NewLedger(
            db, _harness.ContentProtector, handlers, new FakeUserScope(owner), _harness.Time);
    }

    // ------------------------------------------------------------------ seeding

    private async Task<string> ProposeSkillAsync(string title)
    {
        await using var db = _harness.NewContext();
        await using var appDb = NewAppContext();
        var ledger = NewLedger(db, appDb, Owner, new CoachWriteExecutionRecorder());

        var proposal = await ledger.ProposeAsync(
            Conversation,
            "turn-race",
            CoachToolNames.ProposeSkillEntry,
            Json(new CoachSkillEntryArgs(title, "Practising for the race test.", "Korean")));

        return proposal.OperationId;
    }

    private async Task<(string OperationId, string Secret)> ProposeAndChallengeArchiveAsync(string skillId)
    {
        await using var db = _harness.NewContext();
        await using var appDb = NewAppContext();
        var ledger = NewLedger(db, appDb, Owner, new CoachWriteExecutionRecorder());

        var proposal = await ledger.ProposeAsync(
            Conversation,
            "turn-race",
            CoachToolNames.ProposeSkillArchive,
            Json(new CoachSkillArchiveArgs(skillId)));

        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        challenge.Should().NotBeNull();

        return (proposal.OperationId, challenge!.ConfirmationSecret);
    }

    private async Task<string> SeedSkillAsync(string title)
    {
        await using var db = NewAppContext();
        var skill = new SkillProfile
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Description = "Seeded for a race test.",
            Language = "Korean",
            UserProfileId = Owner,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SkillProfiles.Add(skill);
        await db.SaveChangesAsync();
        return skill.Id;
    }

    private async Task SeedConversationAsync(string userProfileId, string conversationId)
    {
        await using var db = _harness.NewContext();
        var now = _harness.Time.GetUtcNow().UtcDateTime;

        db.CoachConversations.Add(new SentenceStudio.Api.Coach.Persistence.History.CoachConversation
        {
            Id = conversationId,
            UserProfileId = userProfileId,
            ProtectedTitle = "seeded",
            HistoryStartsAt = now,
            ContentProtectionVersion = _harness.ContentProtector.CurrentVersion,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync();
    }

    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, CoachNormalizedJson.Options);
}
