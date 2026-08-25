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
/// A handler that previews normally and refuses to execute, so a proposal can be driven into
/// <c>Failed</c> without writing the status by hand.
/// </summary>
/// <remarks>
/// Forcing a status with an <c>UPDATE</c> would prove the ledger reads the column, not that it
/// reaches the state the way production reaches it. <c>Failed</c> in particular is only reachable
/// through the claim: the row is claimed, the handler throws, and the claim is spent on the way
/// out. A test that skipped that would not be testing the state it names.
/// </remarks>
internal sealed class CoachRefusingWriteHandler : ICoachWriteHandler
{
    private readonly ICoachWriteHandler _inner;

    public CoachRefusingWriteHandler(ICoachWriteHandler inner) => _inner = inner;

    public string ToolName => _inner.ToolName;

    public CoachToolRiskClass RiskClass => _inner.RiskClass;

    public CoachWriteUndoKind UndoKind => _inner.UndoKind;

    public CoachWriteEntityKind EntityKind => _inner.EntityKind;

    public Task<CoachWritePreview> PrepareAsync(
        string userProfileId, string argumentsJson, CancellationToken cancellationToken) =>
        _inner.PrepareAsync(userProfileId, argumentsJson, cancellationToken);

    public Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, string argumentsJson, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This handler exists to fail.");

    public Task<CoachWriteExecution> UndoAsync(
        string userProfileId, string argumentsJson, string priorStateJson, CancellationToken cancellationToken) =>
        _inner.UndoAsync(userProfileId, argumentsJson, priorStateJson, cancellationToken);
}

/// <summary>
/// What repeating a request means once the first attempt has settled, against a real ledger.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency used to be "one row per request, forever, whatever became of it". That answered
/// three questions wrongly at once. A reversed operation replayed a receipt for a change the
/// learner had already put back; a declined or elapsed one came back looking live and could never
/// be approved; and because the digest is unique, both outcomes locked the request out for the
/// life of the conversation — the learner could ask again in as many words as they liked and get
/// the same dead row.
/// </para>
/// <para>
/// These tests are the specification for the replacement. Each one drives a proposal into one real
/// state through the real transitions, asks for the same thing again, and asserts what comes back
/// — including, for the reversible case, that the second attempt can actually execute. They run on
/// PostgreSQL because the rule they encode is enforced by a unique index and by conditional
/// updates, and neither of those is a thing an in-memory provider has.
/// </para>
/// </remarks>
public sealed class CoachWriteReproposalPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-owner";
    private const string Conversation = "conv-1";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _appServices = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("repropose", withApplicationSchema: true);
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

    // ------------------------------------------------------------------ wiring

    private ApplicationDbContext NewAppContext() => _harness.NewApplicationContext();

    private LearningResourceRepository NewResourceRepository() =>
        new(_appServices, NullLogger<LearningResourceRepository>.Instance, new StubFileSystem());

    private SkillProfileRepository NewSkillRepository() =>
        new(_appServices, NullLogger<SkillProfileRepository>.Instance);

    private ICoachWriteHandler[] Handlers(ApplicationDbContext appDb)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var resources = NewResourceRepository();
        var skills = NewSkillRepository();

        return
        [
            new CoachVocabularyEntryHandler(ownership, resources),
            new CoachVocabularyEditHandler(ownership, resources),
            new CoachSkillEntryHandler(skills, ownership),
            new CoachSkillEditHandler(skills, ownership),
            new CoachSkillArchiveHandler(skills, ownership)
        ];
    }

    private CoachWriteOperationService NewLedger(
        CoachDbContext db, ApplicationDbContext appDb, IEnumerable<ICoachWriteHandler>? handlers = null) =>
        CoachWriteTestScope.NewLedger(
            db,
            _harness.ContentProtector,
            handlers ?? Handlers(appDb),
            new FakeUserScope(Owner),
            _harness.Time);

    /// <summary>A ledger whose vocabulary-entry handler always throws on execution.</summary>
    private CoachWriteOperationService NewFailingLedger(CoachDbContext db, ApplicationDbContext appDb)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var resources = NewResourceRepository();

        return NewLedger(
            db,
            appDb,
            [new CoachRefusingWriteHandler(new CoachVocabularyEntryHandler(ownership, resources))]);
    }

    // ------------------------------------------------------------------ seeding

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

    private async Task<string> SeedResourceAsync(string title = "Resource")
    {
        await using var db = NewAppContext();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Language = "Korean",
            UserProfileId = Owner,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.LearningResources.Add(resource);
        await db.SaveChangesAsync();
        return resource.Id;
    }

    private async Task<string> SeedSkillAsync(string title = "Ordering food")
    {
        await using var db = NewAppContext();
        var skill = new SkillProfile
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Description = "Seeded.",
            Language = "Korean",
            UserProfileId = Owner,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SkillProfiles.Add(skill);
        await db.SaveChangesAsync();
        return skill.Id;
    }

    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, CoachNormalizedJson.Options);

    private async Task<CoachWriteOperationStatus> StatusAsync(string operationId)
    {
        await using var db = _harness.NewContext();
        return (await db.CoachWriteOperations.AsNoTracking().SingleAsync(o => o.Id == operationId)).Status;
    }

    private async Task<int> OperationCountAsync(string toolName)
    {
        await using var db = _harness.NewContext();
        return await db.CoachWriteOperations.AsNoTracking()
            .CountAsync(o => o.UserProfileId == Owner && o.ToolName == toolName);
    }

    // ================================================================== live

    /// <summary>
    /// A proposal the learner has not answered yet replays itself.
    /// </summary>
    /// <remarks>
    /// The behaviour the change had to preserve. A model that calls the same tool twice in a turn
    /// must produce one card, not two buttons that both work.
    /// </remarks>
    [PostgresFact]
    public async Task A_live_proposal_replays_itself()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        var second = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);

        second.OperationId.Should().Be(first.OperationId);
        second.IsDuplicate.Should().BeTrue();
        second.AlreadyExecuted.Should().BeFalse("nothing has been approved");
        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry)).Should().Be(1);
    }

    // ================================================================== executed

    /// <summary>
    /// An executed operation replays the receipt it wrote, and says so.
    /// </summary>
    /// <remarks>
    /// The receipt — not the preview — is the authoritative record of what happened, so this also
    /// asserts the narrative changes: the reply describes what was done, in the past tense the
    /// handler wrote, rather than what was going to be done.
    /// </remarks>
    [PostgresFact]
    public async Task An_executed_operation_replays_its_receipt()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        var receipt = await ledger.AcceptAsync(Conversation, proposal.OperationId);

        var repeat = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);

        repeat.OperationId.Should().Be(proposal.OperationId);
        repeat.IsDuplicate.Should().BeTrue();
        repeat.AlreadyExecuted.Should().BeTrue("the change is in place");
        repeat.Summary.Should().Be(receipt.Summary, "the receipt is what is replayed");

        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry)).Should().Be(1);

        await using var check = NewAppContext();
        (await check.VocabularyWords.CountAsync(w => w.TargetLanguageTerm == "사과"))
            .Should().Be(1, "a replay writes nothing");
    }

    // ================================================================== executing

    /// <summary>
    /// A repeat arriving while an execution claim is outstanding refuses, rather than looking live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Executing</c> means somebody took the claim and the outcome was never recorded: the
    /// domain write may have happened. There is no honest reply that is not a refusal. A live
    /// proposal would be a second card for a change that may already exist, and a receipt would
    /// describe a result nobody has.
    /// </para>
    /// <para>
    /// The claim is left outstanding here by writing the status directly, because that is exactly
    /// what an in-doubt row is: the process that held the claim went away without settling. There
    /// is no in-process way to produce one, which is the point of the state.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_repeat_of_an_operation_being_carried_out_is_refused()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);

        await using (var stall = _harness.NewContext())
        {
            await stall.CoachWriteOperations
                .Where(o => o.Id == proposal.OperationId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(o => o.Status, CoachWriteOperationStatus.Executing));
        }

        await using var fresh = _harness.NewContext();
        var act = async () => await NewLedger(fresh, appDb).ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);

        (await act.Should().ThrowAsync<CoachToolException>())
            .Which.Reason.Should().Contain("already being carried out");

        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry))
            .Should().Be(1, "an in-doubt request records no second proposal");

        await using var audit = _harness.NewContext();
        (await audit.CoachWriteAudits.CountAsync(
                a => a.OperationId == proposal.OperationId
                     && a.FailureCode == CoachWriteFailureCodes.ExecutionInDoubt))
            .Should().Be(1, "the refusal is on the record");
    }

    // ================================================================== rejected

    /// <summary>
    /// A declined request can be asked again, and the second ask is a real proposal.
    /// </summary>
    /// <remarks>
    /// Declining says "not this time", not "never again in this conversation". The old behaviour
    /// returned the rejected row with a future expiry and no marker, so the card looked live and
    /// every approval refused it — a request the learner could restate forever and never get.
    /// </remarks>
    [PostgresFact]
    public async Task A_rejected_request_can_be_proposed_again()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        (await ledger.RejectAsync(Conversation, first.OperationId)).Should().BeTrue();

        var second = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);

        second.OperationId.Should().NotBe(first.OperationId, "this is a new proposal, not the old one");
        second.IsDuplicate.Should().BeFalse();
        second.AlreadyExecuted.Should().BeFalse();

        (await StatusAsync(first.OperationId))
            .Should().Be(CoachWriteOperationStatus.Rejected, "the declined row is kept, not rewritten");
        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry)).Should().Be(2);

        // And the second one works, which is the whole point of letting it exist.
        await ledger.AcceptAsync(Conversation, second.OperationId);

        await using var check = NewAppContext();
        (await check.VocabularyWords.CountAsync(w => w.TargetLanguageTerm == "사과")).Should().Be(1);
    }

    // ================================================================== expired

    /// <summary>
    /// A proposal that elapsed can be asked again, and its expiry is recorded first.
    /// </summary>
    [PostgresFact]
    public async Task An_expired_proposal_can_be_asked_again()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);

        _harness.Time.Advance(CoachWriteLimits.ProposalLifetime + TimeSpan.FromMinutes(1));

        var second = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);

        second.OperationId.Should().NotBe(first.OperationId);
        second.IsDuplicate.Should().BeFalse();
        (await StatusAsync(first.OperationId)).Should().Be(CoachWriteOperationStatus.Expired);

        await using var audit = _harness.NewContext();
        (await audit.CoachWriteAudits.CountAsync(
                a => a.OperationId == first.OperationId
                     && a.FailureCode == CoachWriteFailureCodes.ProposalExpired))
            .Should().Be(1, "the elapsed proposal is recorded as elapsed before its slot is freed");

        await ledger.AcceptAsync(Conversation, second.OperationId);

        await using var check = NewAppContext();
        (await check.VocabularyWords.CountAsync(w => w.TargetLanguageTerm == "사과")).Should().Be(1);
    }

    // ================================================================== failed

    /// <summary>
    /// A request whose handler refused can be asked again once, without re-running the old row.
    /// </summary>
    /// <remarks>
    /// <c>Failed</c> is deliberately terminal for the row it closed: the ledger cannot tell a
    /// handler that changed nothing from one that changed something and failed on the way back, so
    /// it will not offer that proposal a second life. Asking again is a different thing — a new
    /// proposal, a new preview, and a new approval — and that is what the learner meant.
    /// </remarks>
    [PostgresFact]
    public async Task A_failed_operation_does_not_block_asking_again()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var failing = NewFailingLedger(db, appDb);

        var first = await failing.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);

        var act = async () => await failing.AcceptAsync(Conversation, first.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();
        (await StatusAsync(first.OperationId)).Should().Be(CoachWriteOperationStatus.Failed);

        // The old row stays closed: approving it again is still refused.
        var retryOld = async () => await failing.AcceptAsync(Conversation, first.OperationId);
        await retryOld.Should().ThrowAsync<CoachToolException>();

        await using var fresh = _harness.NewContext();
        var second = await NewLedger(fresh, appDb).ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);

        second.OperationId.Should().NotBe(first.OperationId);
        second.IsDuplicate.Should().BeFalse();

        await NewLedger(fresh, appDb).AcceptAsync(Conversation, second.OperationId);

        await using var check = NewAppContext();
        (await check.VocabularyWords.CountAsync(w => w.TargetLanguageTerm == "사과"))
            .Should().Be(1, "the retry is what wrote the word");
    }

    // ================================================================== undone

    /// <summary>
    /// After an undo, the same request can be proposed and executed again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case that was most wrong. The reversed row replayed its execution receipt with
    /// <c>AlreadyExecuted</c> true, so a learner who archived a skill, undid it, and asked again
    /// was told the archive had already been carried out — while looking at an unarchived skill.
    /// And because the row held the digest, no second proposal could ever be recorded, so the
    /// answer would never have changed.
    /// </para>
    /// <para>
    /// Asserted end to end on a protected write, because the protected path is where the
    /// consequences are largest: propose, confirm, undo, propose again, confirm again, and check
    /// the learner's data at each step rather than the ledger's opinion of it.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task An_undone_operation_can_be_proposed_and_executed_again()
    {
        var skillId = await SeedSkillAsync();
        var args = Json(new CoachSkillArchiveArgs(skillId));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, args);
        var challenge = await ledger.IssueConfirmationAsync(Conversation, first.OperationId);
        await ledger.ConfirmAsync(Conversation, first.OperationId, challenge!.ConfirmationSecret);

        await using (var mid = NewAppContext())
        {
            (await mid.SkillProfiles.SingleAsync(s => s.Id == skillId)).IsArchived.Should().BeTrue();
        }

        await ledger.UndoAsync(Conversation, first.OperationId);

        await using (var restored = NewAppContext())
        {
            (await restored.SkillProfiles.SingleAsync(s => s.Id == skillId)).IsArchived
                .Should().BeFalse("the undo really put it back");
        }

        var second = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeSkillArchive, args);

        second.OperationId.Should().NotBe(first.OperationId);
        second.IsDuplicate.Should().BeFalse();
        second.AlreadyExecuted.Should().BeFalse("the skill is not archived right now");

        var secondChallenge = await ledger.IssueConfirmationAsync(Conversation, second.OperationId);
        secondChallenge.Should().NotBeNull("a fresh proposal can be confirmed");
        await ledger.ConfirmAsync(Conversation, second.OperationId, secondChallenge!.ConfirmationSecret);

        await using var check = NewAppContext();
        (await check.SkillProfiles.SingleAsync(s => s.Id == skillId)).IsArchived
            .Should().BeTrue("the second archive actually ran");

        (await StatusAsync(first.OperationId)).Should().Be(CoachWriteOperationStatus.Undone);
        (await StatusAsync(second.OperationId)).Should().Be(CoachWriteOperationStatus.Executed);
    }

    /// <summary>
    /// A reversed operation does not claim to be in effect, on the read path either.
    /// </summary>
    /// <remarks>
    /// <c>AlreadyExecuted</c> is read by the model in a tool result and by the card on the
    /// operation route, and both must mean the same thing: the change is in place now. An
    /// operation that ran and was put back is not in place, whatever its receipt says.
    /// </remarks>
    [PostgresFact]
    public async Task An_undone_operation_does_not_report_itself_as_executed()
    {
        var skillId = await SeedSkillAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive,
            Json(new CoachSkillArchiveArgs(skillId)));

        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        await ledger.ConfirmAsync(Conversation, proposal.OperationId, challenge!.ConfirmationSecret);

        var executed = await ledger.GetAsync(Conversation, proposal.OperationId);
        executed!.AlreadyExecuted.Should().BeTrue("the archive is in place");

        await ledger.UndoAsync(Conversation, proposal.OperationId);

        var reversed = await ledger.GetAsync(Conversation, proposal.OperationId);
        reversed!.AlreadyExecuted.Should().BeFalse("the learner has their skill back");

        // The receipt route is where a reversed operation's history lives, and it reports the
        // status outright rather than leaving a caller to infer it.
        var receipt = await ledger.GetReceiptAsync(Conversation, proposal.OperationId);
        receipt!.Status.Should().Be(CoachWriteOperationStatus.Undone);
        receipt.CanUndo.Should().BeFalse();
    }

    // ================================================================== concurrency

    /// <summary>
    /// Concurrent identical proposals still collapse to one row.
    /// </summary>
    /// <remarks>
    /// The deduplication that had to survive the change. Six callers on six independent contexts
    /// contend on the unique index rather than on anything in one process, and the losers answer
    /// from the winner's row instead of retrying an insert.
    /// </remarks>
    [PostgresFact]
    public async Task Concurrent_identical_proposals_still_produce_one_row()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = _harness.NewContext();
            await using var scopedApp = NewAppContext();
            return await NewLedger(db, scopedApp).ProposeAsync(
                Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        }));

        results.Select(r => r.OperationId).Distinct().Should().HaveCount(1);
        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry)).Should().Be(1);
    }

    /// <summary>
    /// Concurrent repeats after a decline still collapse to one new row.
    /// </summary>
    /// <remarks>
    /// Releasing a closed row's slot is the new step, and it is the step that could have opened a
    /// hole: if two callers could each release and then insert, the learner would get two live
    /// cards for one request. They cannot, because releasing is a conditional update on the digest
    /// the caller read and inserting still contends on the unique index. This is that assertion.
    /// </remarks>
    [PostgresFact]
    public async Task Concurrent_repeats_after_a_rejection_still_produce_one_new_row()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var seed = _harness.NewContext();
        var seedLedger = NewLedger(seed, appDb);

        var first = await seedLedger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        await seedLedger.RejectAsync(Conversation, first.OperationId);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = _harness.NewContext();
            await using var scopedApp = NewAppContext();
            return await NewLedger(db, scopedApp).ProposeAsync(
                Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);
        }));

        var ids = results.Select(r => r.OperationId).Distinct().ToList();
        ids.Should().HaveCount(1);
        ids[0].Should().NotBe(first.OperationId);

        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry))
            .Should().Be(2, "the rejected row and exactly one replacement");
    }

    /// <summary>
    /// A released row keeps everything except its claim on the request.
    /// </summary>
    /// <remarks>
    /// Releasing must not look like deleting. The operation id is what the audit, the receipt
    /// route, and any link the learner already has all point at, so the row stays readable and its
    /// audit trail stays complete; only the digest — which is bookkeeping, not history — changes.
    /// </remarks>
    [PostgresFact]
    public async Task A_released_row_keeps_its_identity_and_its_audit_trail()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        await ledger.RejectAsync(Conversation, first.OperationId);
        await ledger.ProposeAsync(Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);

        (await ledger.GetAsync(Conversation, first.OperationId))
            .Should().NotBeNull("the declined operation is still the learner's to look at");

        await using var audit = _harness.NewContext();
        var events = await audit.CoachWriteAudits.AsNoTracking()
            .Where(a => a.OperationId == first.OperationId)
            .Select(a => a.Event)
            .ToListAsync();

        events.Should().Contain(CoachWriteAuditEvent.Proposed);
        events.Should().Contain(CoachWriteAuditEvent.Rejected);
    }

    // ================================================================== turn identity

    /// <summary>
    /// A proposal with no turn to belong to is refused rather than counted against nothing.
    /// </summary>
    /// <remarks>
    /// The per-turn write budget counts by turn. A blank turn used to skip the count entirely,
    /// which left the shared twenty-call tool budget as the only bound on how many changes one
    /// turn could queue up — a different, looser bound that read tools compete for. Failing closed
    /// is the only answer that keeps the write-only cap meaning what it says.
    /// </remarks>
    [PostgresFact]
    public async Task A_proposal_without_a_turn_is_refused()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var act = async () => await ledger.ProposeAsync(
            Conversation, turnId: null, CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        (await act.Should().ThrowAsync<CoachToolException>())
            .Which.Reason.Should().Contain("no turn");

        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry)).Should().Be(0);
    }

    /// <summary>
    /// A turn records one proposal, and the second is refused before anything is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different word, so the idempotency index is not what stops it — this is the per-turn
    /// invariant doing the work, and the distinction matters because the two failures look the
    /// same to a caller and mean completely different things.
    /// </para>
    /// <para>
    /// The row count is the assertion that matters. A refusal that still left a row behind would
    /// be the original defect: a proposal the learner can never see and an approval claim that
    /// still exists.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_turn_records_one_proposal_and_refuses_the_second()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        CoachWriteLimits.ProposalsPerTurnMax.Should().Be(
            1, "the surface carries one card per turn, and the ledger's cap is that number");

        await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var act = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "하나더", "one more")));

        (await act.Should().ThrowAsync<CoachToolException>())
            .Which.Kind.Should().Be(CoachToolFailureKind.BudgetExhausted);

        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry))
            .Should().Be(1, "the refusal wrote nothing");
    }

    /// <summary>
    /// The next turn may propose again, so the cap bounds a turn rather than a conversation.
    /// </summary>
    [PostgresFact]
    public async Task A_later_turn_may_propose_again()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var next = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "하나더", "one more")));

        next.OperationId.Should().NotBeNullOrWhiteSpace();

        (await OperationCountAsync(CoachToolNames.ProposeVocabularyEntry))
            .Should().Be(2, "each turn has its own card and both are reachable");
    }

    // ================================================================== expiry audit

    /// <summary>
    /// Two requests that both find the same elapsed proposal both record the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Expiry is written under the concurrency token, so the second request to try loses. Losing
    /// is fine — the row is already in the state it wanted — but recovering from the conflict
    /// means clearing the change tracker, and the audit row was queued on that same tracker. So
    /// the loser's refusal used to vanish: the learner was told the proposal had expired and
    /// nothing anywhere recorded that they had been told.
    /// </para>
    /// <para>
    /// The stale reader here is genuine rather than staged. The first ledger proposed the row, so
    /// its context still tracks it at the version it was written with; the second ledger expires
    /// it from its own context; the first then tries and finds its token stale. That is the shape
    /// of two tabs answering the same card.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_concurrently_expired_proposal_still_records_its_refusal()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var stale = _harness.NewContext();
        var staleLedger = NewLedger(stale, appDb);

        var proposal = await staleLedger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);

        _harness.Time.Advance(CoachWriteLimits.ProposalLifetime + TimeSpan.FromMinutes(1));

        await using (var winner = _harness.NewContext())
        {
            var act = async () => await NewLedger(winner, appDb)
                .AcceptAsync(Conversation, proposal.OperationId);
            await act.Should().ThrowAsync<CoachToolException>();
        }

        var loser = async () => await staleLedger.AcceptAsync(Conversation, proposal.OperationId);
        (await loser.Should().ThrowAsync<CoachToolException>())
            .Which.Reason.Should().Contain("expired");

        await using var audit = _harness.NewContext();
        (await audit.CoachWriteAudits.CountAsync(
                a => a.OperationId == proposal.OperationId
                     && a.FailureCode == CoachWriteFailureCodes.ProposalExpired))
            .Should().Be(2, "both refusals are evidence, and the losing one is the easier to lose");
    }
}
