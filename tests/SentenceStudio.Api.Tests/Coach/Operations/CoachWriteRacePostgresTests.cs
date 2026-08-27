using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

    public CoachObservedWriteHandler(
        ICoachWriteHandler inner,
        CoachWriteExecutionRecorder recorder,
        bool externalEffect = false,
        TimeSpan executionDelay = default)
    {
        _inner = inner;
        _recorder = recorder;
        _externalEffect = externalEffect;
        _executionDelay = executionDelay;
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

        // Exactly one caller may be told it worked. Unlike the soft route, a loser here presented
        // confirmation material, and answering it with the winner's receipt would mean a spent
        // secret still bought a successful-looking reply.
        outcomes.OfType<CoachWriteReceipt>().Should().ContainSingle();
        outcomes.OfType<CoachToolException>().Should().ContainSingle();

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

        outcomes.OfType<CoachWriteReceipt>().Should().ContainSingle();
        outcomes.OfType<CoachToolException>().Should().ContainSingle();

        await using var check = NewAppContext();
        (await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId))
            .IsArchived.Should().BeTrue();

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
    private async Task<IReadOnlyList<object>> RaceUnderRowLockAsync(
        int callers,
        CoachWriteExecutionRecorder recorder,
        string operationId,
        Func<CoachWriteOperationService, Task<CoachWriteReceipt>> approve,
        bool externalEffect = false)
    {
        using var gate = new Barrier(callers);
        var contexts = new List<IAsyncDisposable>();

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
                ledgers.Add(await NewWarmLedgerAsync(contexts, recorder, TimeSpan.Zero, externalEffect)
                    .ConfigureAwait(false));
            }

            var running = StartAll(ledgers, approve, gate);

            await WaitForBlockedBackendsAsync(locker.ProcessID, callers).ConfigureAwait(false);
            await lockTransaction.CommitAsync().ConfigureAwait(false);

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
        bool externalEffect)
    {
        var db = _harness.NewContext();
        var appDb = NewAppContext();
        contexts.Add(db);
        contexts.Add(appDb);

        // Warm both connections and both query pipelines. The first query on a fresh context pays
        // for connection setup and model warm-up, which is far longer than the window these tests
        // aim at; paying it here means every caller starts from the same state.
        await db.CoachWriteOperations.AsNoTracking().Take(1).ToListAsync().ConfigureAwait(false);
        await appDb.SkillProfiles.AsNoTracking().Take(1).ToListAsync().ConfigureAwait(false);

        return NewLedger(db, appDb, Owner, recorder, handlerDwell, externalEffect);
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
        bool externalEffect = false)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var skills = new SkillProfileRepository(_appServices, NullLogger<SkillProfileRepository>.Instance);

        var handlers = new ICoachWriteHandler[]
        {
            new CoachObservedWriteHandler(
                new CoachSkillEntryHandler(skills, ownership), recorder, false, handlerDwell),
            new CoachObservedWriteHandler(
                new CoachSkillArchiveHandler(skills, ownership), recorder, externalEffect, handlerDwell)
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
