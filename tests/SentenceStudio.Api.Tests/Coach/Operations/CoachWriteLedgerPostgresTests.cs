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

/// <summary>A file system that answers inside the worktree and is never actually read from.</summary>
internal sealed class StubFileSystem : IFileSystemService
{
    public string AppDataDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "coach-write-tests");

    public Task<Stream> OpenAppPackageFileAsync(string filename) =>
        throw new NotSupportedException("Coach write tests do not read packaged files.");
}

/// <summary>
/// The write ledger against a real PostgreSQL server carrying the real application schema.
/// </summary>
/// <remarks>
/// These are the assertions that cannot be made anywhere else. Idempotency is a unique index;
/// "no duplicate row" is a count against a table the repository actually wrote to; cross-tenant
/// refusal is only meaningful when the other tenant's row genuinely exists and is genuinely
/// reachable by primary key. Running them in memory would be testing the test.
/// </remarks>
public sealed class CoachWriteLedgerPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-owner";
    private const string Stranger = "user-stranger";
    private const string Conversation = "conv-1";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _appServices = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("writes", withApplicationSchema: true);

        // The owner's conversations only. A conversation id is globally unique, so the stranger
        // shares this one rather than owning a copy of it — which is the situation the ownership
        // checks have to survive: a real conversation id, belonging to somebody else.
        await SeedConversationAsync(Owner, Conversation);
        await SeedConversationAsync(Owner, "conv-other");

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

    private LearningResourceRepository NewResourceRepository() =>
        new(_appServices, NullLogger<LearningResourceRepository>.Instance, new StubFileSystem());

    private ApplicationDbContext NewAppContext() => _harness.NewApplicationContext();

    /// <summary>A ledger for the named owner, with the vocabulary handlers wired to real storage.</summary>
    private CoachWriteOperationService NewLedger(
        CoachDbContext db,
        ApplicationDbContext appDb,
        string? owner,
        ICoachToolRegistry? registry = null)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var resources = NewResourceRepository();
        var skills = new SkillProfileRepository(_appServices, NullLogger<SkillProfileRepository>.Instance);

        var handlers = new ICoachWriteHandler[]
        {
            new CoachVocabularyEntryHandler(ownership, resources),
            new CoachVocabularyEditHandler(ownership, resources),
            new CoachVocabularyLinkHandler(ownership, resources),
            new CoachVocabularyRemovalHandler(ownership, resources),
            new CoachSkillEntryHandler(skills, ownership),
            new CoachSkillEditHandler(skills, ownership),
            new CoachSkillArchiveHandler(skills, ownership)
        };

        return CoachWriteTestScope.NewLedger(
            db, _harness.ContentProtector, handlers, new FakeUserScope(owner), _harness.Time, registry);
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>
    /// Creates the conversation a write operation hangs from.
    /// </summary>
    /// <remarks>
    /// The ledger's composite foreign key is on (UserProfileId, ConversationId), so an operation
    /// cannot exist without a conversation belonging to the same learner. Seeding it here is not
    /// scaffolding around an inconvenience — it is the shape of the constraint, and a test that
    /// skipped it would only be showing that the database enforces its own key.
    /// </remarks>
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

    private async Task<string> SeedResourceAsync(string userProfileId, string title = "Resource")
    {
        await using var db = NewAppContext();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Language = "Korean",
            UserProfileId = userProfileId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.LearningResources.Add(resource);
        await db.SaveChangesAsync();
        return resource.Id;
    }

    /// <summary>
    /// Serializes arguments exactly as the tool does.
    /// </summary>
    /// <remarks>
    /// The tool receives a typed record from the function-calling layer and serializes it with the
    /// coach's own options, so the ledger reads PascalCase. Hand-writing JSON here would let a test
    /// pass against a shape production never produces, or fail against one it does — either way the
    /// test would be about the test. Using the real record types keeps that honest, and means a
    /// renamed argument breaks compilation rather than silently deserializing to null.
    /// </remarks>
    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, CoachNormalizedJson.Options);

    // ================================================================== identity

    [PostgresFact]
    public async Task An_empty_user_scope_refuses_before_the_database_is_asked()
    {
        var interceptor = new QueryCountingInterceptor();
        var appOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_harness.ConnectionString)
            .AddInterceptors(interceptor)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var appDb = new ApplicationDbContext(appOptions);
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, owner: null);

        var act = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs("r1", "사과", "apple")));

        // The scope provider throws, and the ledger turns that into the same refusal shape every
        // other tool failure uses, so an unauthenticated caller cannot tell from the error which
        // stage rejected them.
        var refusal = await act.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Kind.Should().Be(CoachToolFailureKind.Unauthorized);

        // The point of the assertion: not merely that it refused, but that it refused without
        // having asked the database anything about anyone.
        interceptor.Commands.Should().Be(0);
    }

    [PostgresFact]
    public async Task A_resource_belonging_to_another_learner_is_refused()
    {
        var strangersResource = await SeedResourceAsync(Stranger);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var act = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(strangersResource, "사과", "apple")));

        await act.Should().ThrowAsync<CoachToolException>();

        // Nothing was written against the stranger's resource.
        await using var check = NewAppContext();
        var linked = await check.ResourceVocabularyMappings
            .CountAsync(m => m.ResourceId == strangersResource);
        linked.Should().Be(0);
    }

    // ================================================================== proposal

    [PostgresFact]
    public async Task A_proposal_changes_no_learner_data_until_it_is_accepted()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        proposal.Should().NotBeNull();
        proposal.OperationId.Should().NotBeNullOrWhiteSpace();

        await using var check = NewAppContext();
        var words = await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId);
        words.Should().Be(0, "a proposal is a description of a change, not the change");
    }

    [PostgresFact]
    public async Task Accepting_a_proposal_writes_exactly_one_row()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var receipt = await ledger.AcceptAsync(Conversation, proposal.OperationId);
        receipt.Should().NotBeNull();

        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings
            .CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(1);
    }

    [PostgresFact]
    public async Task Accepting_twice_replays_the_receipt_and_does_not_write_again()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var first = await ledger.AcceptAsync(Conversation, proposal.OperationId);
        var second = await ledger.AcceptAsync(Conversation, proposal.OperationId);

        second.OperationId.Should().Be(first.OperationId);

        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings
            .CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(1, "the second acceptance replays the receipt rather than repeating the write");
    }

    [PostgresFact]
    public async Task The_same_request_twice_in_one_conversation_is_one_operation()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        var second = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeVocabularyEntry, args);

        second.OperationId.Should().Be(first.OperationId);

        await using var ledgerCheck = _harness.NewContext();
        var rows = await ledgerCheck.CoachWriteOperations
            .CountAsync(o => o.UserProfileId == Owner && o.ConversationId == Conversation);
        rows.Should().Be(1);
    }

    // ================================================================== ownership of the proposal

    [PostgresFact]
    public async Task Another_learner_cannot_accept_this_learners_proposal()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var proposal = await NewLedger(db, appDb, Owner).ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        await using var strangerDb = _harness.NewContext();
        await using var strangerApp = NewAppContext();
        var strangerLedger = NewLedger(strangerDb, strangerApp, Stranger);

        var act = async () => await strangerLedger.AcceptAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(0);
    }

    [PostgresFact]
    public async Task A_proposal_cannot_be_accepted_from_a_different_conversation()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var act = async () => await ledger.AcceptAsync("conv-other", proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();
    }

    [PostgresFact]
    public async Task A_rejected_proposal_can_never_execute()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        (await ledger.RejectAsync(Conversation, proposal.OperationId)).Should().BeTrue();

        var act = async () => await ledger.AcceptAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(0);
    }

    // ================================================================== expiry

    [PostgresFact]
    public async Task A_proposal_older_than_its_lifetime_cannot_be_accepted()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        _harness.Time.Advance(CoachWriteLimits.ProposalLifetime + TimeSpan.FromMinutes(1));

        var act = async () => await ledger.AcceptAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(0);
    }

    // ================================================================== undo

    [PostgresFact]
    public async Task Undo_removes_what_the_operation_created_and_leaves_its_own_receipt()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        var undo = await ledger.UndoAsync(Conversation, proposal.OperationId);
        undo.Should().NotBeNull();

        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(0);

        await using var ledgerCheck = _harness.NewContext();
        var original = await ledgerCheck.CoachWriteOperations
            .SingleAsync(o => o.Id == proposal.OperationId);
        original.Status.Should().Be(CoachWriteOperationStatus.Undone);
    }

    [PostgresFact]
    public async Task Undo_is_one_use()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);
        await ledger.UndoAsync(Conversation, proposal.OperationId);

        var act = async () => await ledger.UndoAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();
    }

    [PostgresFact]
    public async Task Undo_expires()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        _harness.Time.Advance(CoachWriteLimits.UndoWindow + TimeSpan.FromMinutes(1));

        var act = async () => await ledger.UndoAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        // The write stands; an expired undo is a refusal, not a silent partial reversal.
        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(1);
    }

    [PostgresFact]
    public async Task Another_learner_cannot_undo_this_learners_write()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        await using var strangerDb = _harness.NewContext();
        await using var strangerApp = NewAppContext();
        var strangerLedger = NewLedger(strangerDb, strangerApp, Stranger);

        var act = async () => await strangerLedger.UndoAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        var links = await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId);
        links.Should().Be(1);
    }

    // ================================================================== confirmation

    [PostgresFact]
    public async Task A_protected_write_refuses_a_plain_acceptance()
    {
        var skillId = await SeedSkillAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive,
            Json(new CoachSkillArchiveArgs(skillId)));

        var act = async () => await ledger.AcceptAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.Id == skillId)).Should().Be(1);
    }

    /// <summary>
    /// A spent confirmation buys nothing new, and says so by returning what already happened.
    /// </summary>
    /// <remarks>
    /// One-use is a property of what the secret can cause, not of what the server is willing to
    /// say. Presenting it again must not run the handler a second time and must not leave a
    /// second receipt — and that is what is asserted here. What it must also not do is answer a
    /// completed change with a failure: the learner's client retries with the secret it was
    /// handed, and telling it the confirmation was rejected would describe an archive that
    /// actually happened as one that did not.
    /// </remarks>
    [PostgresFact]
    public async Task A_confirmation_secret_is_one_use()
    {
        var skillId = await SeedSkillAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));

        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        challenge.Should().NotBeNull();

        var first = await ledger.ConfirmAsync(
            Conversation, proposal.OperationId, challenge!.ConfirmationSecret);

        var replay = await ledger.ConfirmAsync(
            Conversation, proposal.OperationId, challenge.ConfirmationSecret);

        replay.OperationId.Should().Be(first.OperationId, "the stored receipt is replayed");
        replay.Status.Should().Be(CoachWriteOperationStatus.Executed);
        replay.ExecutedAtUtc.Should().Be(first.ExecutedAtUtc, "nothing ran a second time");

        // The secret authorizes nothing after it is spent: the digest is gone, so the reply came
        // from ownership. Proving that means proving the ledger did not execute again.
        await using var check = NewAppContext();
        var skill = await check.SkillProfiles.SingleAsync(s => s.Id == skillId);
        skill.IsArchived.Should().BeTrue();

        await using var audit = _harness.NewContext();
        (await audit.CoachWriteOperations.CountAsync(o => o.Id == proposal.OperationId))
            .Should().Be(1, "a replay creates no second operation");
        (await audit.CoachWriteAudits.CountAsync(
                a => a.OperationId == proposal.OperationId
                     && a.Event == CoachWriteAuditEvent.Executed))
            .Should().Be(1, "the write happened once");
        (await audit.CoachWriteAudits.CountAsync(
                a => a.OperationId == proposal.OperationId
                     && a.Event == CoachWriteAuditEvent.Replayed))
            .Should().Be(1, "the retry is recorded as the replay it was");
    }

    /// <summary>
    /// A spent confirmation cannot be redeemed against a different operation.
    /// </summary>
    /// <remarks>
    /// The receipt replay above is scoped to the operation the secret was minted for. Reaching a
    /// second operation with it is a forgery attempt, and it is refused on the ordinary
    /// confirmation path because the digest it is compared against was never derived from it.
    /// </remarks>
    [PostgresFact]
    public async Task A_spent_secret_does_not_open_a_different_operation()
    {
        var firstSkill = await SeedSkillAsync(Owner, "First");
        var secondSkill = await SeedSkillAsync(Owner, "Second");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive,
            Json(new CoachSkillArchiveArgs(firstSkill)));

        // A second turn, because a turn records one proposal. Two live proposals is a real
        // situation — a learner who leaves the first unanswered and asks for something else — and
        // it is the only one in which a secret could be pointed at the wrong operation at all.
        var second = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeSkillArchive,
            Json(new CoachSkillArchiveArgs(secondSkill)));

        var challenge = await ledger.IssueConfirmationAsync(Conversation, first.OperationId);
        await ledger.ConfirmAsync(Conversation, first.OperationId, challenge!.ConfirmationSecret);

        var act = async () => await ledger.ConfirmAsync(
            Conversation, second.OperationId, challenge.ConfirmationSecret);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        (await check.SkillProfiles.SingleAsync(s => s.Id == secondSkill)).IsArchived
            .Should().BeFalse("the other skill was never confirmed");
    }

    [PostgresFact]
    public async Task A_wrong_confirmation_secret_is_refused()
    {
        var skillId = await SeedSkillAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));
        await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);

        var act = async () => await ledger.ConfirmAsync(
            Conversation, proposal.OperationId, "not-the-secret");
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.Id == skillId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task A_confirmation_secret_from_one_operation_does_not_open_another()
    {
        var firstSkill = await SeedSkillAsync(Owner, "First");
        var secondSkill = await SeedSkillAsync(Owner, "Second");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(firstSkill)));
        var second = await ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(secondSkill)));

        var firstChallenge = await ledger.IssueConfirmationAsync(Conversation, first.OperationId);

        var act = async () => await ledger.ConfirmAsync(
            Conversation, second.OperationId, firstChallenge!.ConfirmationSecret);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.Id == secondSkill)).Should().Be(1);
    }

    [PostgresFact]
    public async Task A_confirmation_expires()
    {
        var skillId = await SeedSkillAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));
        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);

        _harness.Time.Advance(CoachWriteLimits.ConfirmationLifetime + TimeSpan.FromMinutes(1));

        var act = async () => await ledger.ConfirmAsync(
            Conversation, proposal.OperationId, challenge!.ConfirmationSecret);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.Id == skillId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task Reissuing_a_confirmation_retires_the_previous_secret()
    {
        var skillId = await SeedSkillAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));

        var stale = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        var current = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);

        current!.ConfirmationSecret.Should().NotBe(stale!.ConfirmationSecret);

        var act = async () => await ledger.ConfirmAsync(
            Conversation, proposal.OperationId, stale.ConfirmationSecret);
        await act.Should().ThrowAsync<CoachToolException>();
    }

    [PostgresFact]
    public async Task Another_learner_cannot_obtain_a_confirmation_for_this_learners_operation()
    {
        var skillId = await SeedSkillAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var proposal = await NewLedger(db, appDb, Owner).ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));

        await using var strangerDb = _harness.NewContext();
        await using var strangerApp = NewAppContext();
        var strangerLedger = NewLedger(strangerDb, strangerApp, Stranger);

        var challenge = await strangerLedger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        challenge.Should().BeNull("a stranger is told nothing, including whether the operation exists");
    }

    // ================================================================== audit

    [PostgresFact]
    public async Task Every_stage_of_a_write_leaves_an_audit_row()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);
        await ledger.UndoAsync(Conversation, proposal.OperationId);

        await using var check = _harness.NewContext();
        var events = await check.CoachWriteAudits
            .Where(a => a.OperationId == proposal.OperationId)
            .Select(a => a.Event)
            .ToListAsync();

        events.Should().Contain(CoachWriteAuditEvent.Proposed);
        events.Should().Contain(CoachWriteAuditEvent.Executed);
        events.Should().Contain(CoachWriteAuditEvent.Undone);
    }

    [PostgresFact]
    public async Task A_refusal_is_audited_too()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        await using var strangerDb = _harness.NewContext();
        await using var strangerApp = NewAppContext();
        var strangerLedger = NewLedger(strangerDb, strangerApp, Stranger);

        try
        {
            await strangerLedger.AcceptAsync(Conversation, proposal.OperationId);
        }
        catch (CoachToolException)
        {
            // Expected. The assertion is about what was recorded, not what was thrown.
        }

        await using var check = _harness.NewContext();
        var denied = await check.CoachWriteAudits
            .CountAsync(a => a.Event == CoachWriteAuditEvent.Denied);
        denied.Should().BeGreaterThan(0);
    }

    [PostgresFact]
    public async Task No_audit_row_carries_the_learners_words()
    {
        var resourceId = await SeedResourceAsync(Owner, title: "Weekend market trip");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        const string Term = "사과나무";
        const string Meaning = "apple tree";

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, Term, Meaning)));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        // Read the audit table as raw text, so the assertion covers every column rather than the
        // ones the entity happens to expose.
        await using var raw = new Npgsql.NpgsqlConnection(_harness.ConnectionString);
        await raw.OpenAsync();
        await using var command = raw.CreateCommand();
        command.CommandText = """SELECT to_jsonb(a)::text FROM "CoachWriteAudit" a""";
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        rows.Should().NotBeEmpty();
        foreach (var row in rows)
        {
            row.Should().NotContain(Term);
            row.Should().NotContain(Meaning);
            row.Should().NotContain("Weekend market trip");
        }
    }

    // ================================================================== helpers

    private async Task<string> SeedSkillAsync(string userProfileId, string title = "Skill")
    {
        await using var db = NewAppContext();
        var skill = new SkillProfile
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Description = "seeded",
            Language = "Korean",
            UserProfileId = userProfileId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SkillProfiles.Add(skill);
        await db.SaveChangesAsync();
        return skill.Id;
    }
}
