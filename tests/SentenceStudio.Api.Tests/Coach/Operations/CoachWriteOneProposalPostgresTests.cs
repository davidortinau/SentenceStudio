using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// One turn, one proposal — proved against a real ledger rather than a prompt.
/// </summary>
/// <remarks>
/// <para>
/// The defect this family exists for was quiet. The ledger accepted eight proposals per turn while
/// both surfaces carried one: the live turn response has a single write operation, and rebuilt
/// history anchors a single card to the turn's last coach message. Proposals two through eight
/// were therefore invisible — not hidden by a rendering bug, but unreachable by construction — and
/// each of them was still a live, approvable claim on the learner's data.
/// </para>
/// <para>
/// So every test here asserts two things that a weaker fix would separate: exactly one row in the
/// ledger, and exactly one card on the surface. A prompt rule would satisfy neither under a model
/// that ignored it, and a UI fix would satisfy the second while leaving the first.
/// </para>
/// <para>
/// PostgreSQL, because the invariant is a count against a table two API processes share, and
/// because "a second proposal wrote nothing" is a claim about rows.
/// </para>
/// </remarks>
public sealed class CoachWriteOneProposalPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-one-proposal";
    private const string Conversation = "conv-one-proposal";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _appServices = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("oneproposal", withApplicationSchema: true);
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

    // ================================================================== successive calls

    /// <summary>
    /// A second proposal in the same turn is refused and writes nothing.
    /// </summary>
    /// <remarks>
    /// Different arguments, so the idempotency index cannot be what stopped it. The row count is
    /// the assertion: a refusal that still inserted would be the original defect with a nicer
    /// error message.
    /// </remarks>
    [PostgresFact]
    public async Task A_second_proposal_in_one_turn_is_refused_and_writes_nothing()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var act = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "포도", "grape")));

        var refusal = await act.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Kind.Should().Be(
            CoachToolFailureKind.BudgetExhausted, "this is a bound, not a fault in the request");
        refusal.Which.Reason.Should().Contain(
            "one change", "the model has to be told what to do instead, not merely that it failed");

        var rows = await AllForTurnAsync("turn-1");
        rows.Should().ContainSingle().Which.Id.Should().Be(first.OperationId);
    }

    /// <summary>
    /// A different tool in the same turn is refused too.
    /// </summary>
    /// <remarks>
    /// The bound is on the turn, not on the tool. A model that proposed a word and then a skill
    /// would otherwise put two cards' worth of decisions into a surface that shows one, which is
    /// the same defect wearing different arguments.
    /// </remarks>
    [PostgresFact]
    public async Task A_second_proposal_from_a_different_tool_in_one_turn_is_refused()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var act = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillEntry,
            Json(new CoachSkillEntryArgs("Ordering food", "Practising café phrases.", "Korean")));

        await act.Should().ThrowAsync<CoachToolException>();

        (await AllForTurnAsync("turn-1")).Should().ContainSingle();
    }

    /// <summary>
    /// Eight successive attempts leave one row, not eight.
    /// </summary>
    /// <remarks>
    /// Eight because that was the old cap. Running exactly the sequence the old limit permitted is
    /// the clearest way to show what changed, and it fails loudly on any regression that restores
    /// the number.
    /// </remarks>
    [PostgresFact]
    public async Task Eight_successive_attempts_in_one_turn_leave_one_row()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var accepted = 0;
        var refused = 0;

        for (var i = 0; i < 8; i++)
        {
            try
            {
                await ledger.ProposeAsync(
                    Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
                    Json(new CoachVocabularyEntryArgs(resourceId, $"단어{i}", $"word{i}")));
                accepted++;
            }
            catch (CoachToolException)
            {
                refused++;
            }
        }

        accepted.Should().Be(1);
        refused.Should().Be(7);
        (await AllForTurnAsync("turn-1")).Should().ContainSingle();
    }

    // ================================================================== concurrent calls

    /// <summary>
    /// Six different proposals arriving at once leave one row.
    /// </summary>
    /// <remarks>
    /// Each caller gets its own contexts over its own connections, so they contend in the database
    /// rather than on a lock inside one process — which is the only version of this that says
    /// anything about two API instances. Different arguments per caller, so the unique idempotency
    /// index is not what resolves it.
    /// </remarks>
    [PostgresFact]
    public async Task Concurrent_different_proposals_in_one_turn_leave_one_row()
    {
        var resourceId = await SeedResourceAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = _harness.NewContext();
            await using var appDb = NewAppContext();
            var ledger = NewLedger(db, appDb);

            try
            {
                var result = await ledger.ProposeAsync(
                    Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
                    Json(new CoachVocabularyEntryArgs(resourceId, $"단어{i}", $"word{i}")));
                return (object)result;
            }
            catch (CoachToolException ex)
            {
                return ex;
            }
        }));

        outcomes.OfType<CoachWriteProposalResult>().Should().ContainSingle(
            "exactly one caller may be told its change is waiting for the learner");
        outcomes.OfType<CoachToolException>().Should().HaveCount(5);

        (await AllForTurnAsync("turn-1")).Should().ContainSingle();
    }

    /// <summary>
    /// Concurrent proposals from different tools in one turn also leave one row.
    /// </summary>
    [PostgresFact]
    public async Task Concurrent_proposals_from_different_tools_in_one_turn_leave_one_row()
    {
        var resourceId = await SeedResourceAsync();

        var calls = new Func<CoachWriteOperationService, Task<CoachWriteProposalResult>>[]
        {
            ledger => ledger.ProposeAsync(
                Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
                Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"))),
            ledger => ledger.ProposeAsync(
                Conversation, "turn-1", CoachToolNames.ProposeSkillEntry,
                Json(new CoachSkillEntryArgs("Ordering food", "Practising café phrases.", "Korean"))),
            ledger => ledger.ProposeAsync(
                Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
                Json(new CoachVocabularyEntryArgs(resourceId, "포도", "grape")))
        };

        var outcomes = await Task.WhenAll(calls.Select(async call =>
        {
            await using var db = _harness.NewContext();
            await using var appDb = NewAppContext();

            try
            {
                return (object)await call(NewLedger(db, appDb));
            }
            catch (CoachToolException ex)
            {
                return ex;
            }
        }));

        var reasons = string.Join(
            " | ", outcomes.OfType<CoachToolException>().Select(e => $"{e.Kind}:{e.Reason}"));

        outcomes.OfType<CoachWriteProposalResult>().Should().ContainSingle(
            "exactly one caller may be told its change is waiting. Refusals: {0}", reasons);
        (await AllForTurnAsync("turn-1")).Should().ContainSingle();
    }

    // ================================================================== the surfaces agree

    /// <summary>
    /// The live turn response and rebuilt history both show the one proposal, and it is the one
    /// the ledger kept.
    /// </summary>
    /// <remarks>
    /// The point of the invariant is that these two agree with each other and with the row. Before
    /// it, a turn with several proposals produced a surface showing the newest and a ledger holding
    /// all of them — so the learner's screen and the thing that would actually execute were
    /// different objects.
    /// </remarks>
    [PostgresFact]
    public async Task Both_surfaces_show_the_one_proposal_the_ledger_kept()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var kept = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var second = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "포도", "grape")));
        await second.Should().ThrowAsync<CoachToolException>();

        // Live: what the turn response would carry.
        var live = await ledger.GetLatestForTurnAsync(Conversation, "turn-1");
        live.Should().NotBeNull();
        live!.OperationId.Should().Be(kept.OperationId);

        // History: what a rebuilt page would anchor. One coach message for the turn, one card.
        var forTurns = await ledger.ListForTurnsAsync(Conversation, ["turn-1"]);
        forTurns.Should().ContainSingle();

        var records = new[]
        {
            Record("m1", CoachMessageRole.Learner, operationId: null),
            Record("m2", CoachMessageRole.Coach, operationId: "turn-1")
        };

        var anchored = CoachWriteAnchoring.ByMessage(records, forTurns);
        anchored.Should().ContainSingle();
        anchored[1].OperationId.Should().Be(
            kept.OperationId, "the card the learner sees is the row the ledger would execute");
    }

    // ================================================================== replay is preserved

    /// <summary>
    /// Asking for the same change twice in one turn still replays, rather than being refused.
    /// </summary>
    /// <remarks>
    /// The bound counts other proposals, not this one. A model that repeats itself — a retry, a
    /// duplicated tool call — must get the row it already made, because the alternative is telling
    /// the learner their request failed when it is sitting in front of them.
    /// </remarks>
    [PostgresFact]
    public async Task Repeating_the_same_proposal_in_one_turn_replays_it()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var first = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        var again = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);

        again.OperationId.Should().Be(first.OperationId);
        again.IsDuplicate.Should().BeTrue("the caller is told this is the row it already made");

        (await AllForTurnAsync("turn-1")).Should().ContainSingle();
    }

    /// <summary>
    /// Identical proposals arriving at once still resolve to one row and one answer.
    /// </summary>
    /// <remarks>
    /// The race the per-turn count could easily have broken: every caller reads no existing row,
    /// so every caller reaches the bound at the same moment. Excluding the request's own digest is
    /// what keeps the losers on the replay path instead of the refusal path.
    /// </remarks>
    [PostgresFact]
    public async Task Concurrent_identical_proposals_in_one_turn_all_replay_one_row()
    {
        var resourceId = await SeedResourceAsync();
        var args = Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple"));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = _harness.NewContext();
            await using var appDb = NewAppContext();
            return await NewLedger(db, appDb).ProposeAsync(
                Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry, args);
        }));

        results.Select(r => r.OperationId).Distinct().Should().ContainSingle(
            "a repeat is answered from the row that already exists, never refused");

        (await AllForTurnAsync("turn-1")).Should().ContainSingle();
    }

    // ================================================================== the refusal is recorded

    /// <summary>
    /// The refused second proposal leaves an audit row and no operation row.
    /// </summary>
    /// <remarks>
    /// Two different requirements that pull in opposite directions: an operator needs to be able
    /// to see that a model keeps trying, and a learner must have nothing extra to approve. The
    /// audit satisfies the first without the second, and it carries the tool and the turn and no
    /// arguments.
    /// </remarks>
    [PostgresFact]
    public async Task The_refused_second_proposal_is_audited_without_an_operation()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var act = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "포도", "grape")));
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = _harness.NewContext();

        var refusals = await check.CoachWriteAudits.AsNoTracking()
            .Where(a => a.ConversationId == Conversation
                        && a.FailureCode == CoachWriteFailureCodes.ProposalBudgetExhausted)
            .ToListAsync();

        refusals.Should().ContainSingle();
        refusals[0].TurnId.Should().Be("turn-1");
        refusals[0].OperationId.Should().BeEmpty("there is no operation, which is the point");
        refusals[0].ToolName.Should().Be(CoachToolNames.ProposeVocabularyEntry);

        (await AllForTurnAsync("turn-1")).Should().ContainSingle();
    }

    // ================================================================== reserved turn identities

    /// <summary>
    /// A client that tries to claim a reversal's turn identity does not get it, and Undo still works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole flow, against real rows: a change is proposed and accepted in an ordinary turn,
    /// then a second turn arrives carrying the reversal identity of the first. The scope replaces
    /// it, so the proposal that turn records sits somewhere harmless, and the learner's Undo finds
    /// its slot free.
    /// </para>
    /// <para>
    /// The route refuses such a request outright — asserted in
    /// <c>CoachReservedTurnIdentityTests</c>. This asserts the property that refusal exists to
    /// protect, which is a different claim and the one that matters to the learner.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_client_cannot_claim_a_reversals_turn_identity_and_undo_still_works()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        // The attempt: a later turn whose client-supplied identity is the reversal's.
        var scope = new CoachWriteTurnScope();
        scope.Enter(Conversation, CoachWriteTurnScope.UndoTurnPrefix + proposal.OperationId);

        scope.TurnId.Should().NotBe(
            CoachWriteTurnScope.UndoTurnPrefix + proposal.OperationId,
            "the scope replaces a reserved identity rather than honouring it");
        scope.TurnId.Should().StartWith(CoachWriteTurnScope.ServerTurnPrefix);

        await ledger.ProposeAsync(
            Conversation, scope.TurnId!, CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "포도", "grape")));

        // The property the guard protects: the learner can still take their change back.
        var reversal = await ledger.UndoAsync(Conversation, proposal.OperationId);
        reversal.Status.Should().Be(CoachWriteOperationStatus.Undone);

        await using var check = NewAppContext();
        (await check.ResourceVocabularyMappings.CountAsync(m => m.ResourceId == resourceId))
            .Should().Be(0, "the word the first turn added is gone");
    }

    /// <summary>
    /// The guard is load-bearing: a row that did claim the slot breaks the reversal.
    /// </summary>
    /// <remarks>
    /// Written by calling the ledger directly with the reserved identity, which is the one thing
    /// no request can now do. Without it the test above would pass whether or not the guard
    /// existed, and this is the failure it is preventing: the domain write is reversed and the
    /// ledger cannot record it, so the operation is left in doubt and the learner is told their
    /// Undo did not complete.
    /// </remarks>
    [PostgresFact]
    public async Task A_pre_claimed_reversal_slot_would_break_undo()
    {
        var resourceId = await SeedResourceAsync();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        // Bypassing the guard on purpose, to show what it stops.
        await ledger.ProposeAsync(
            Conversation,
            CoachWriteTurnScope.UndoTurnPrefix + proposal.OperationId,
            CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "포도", "grape")));

        var act = async () => await ledger.UndoAsync(Conversation, proposal.OperationId);

        await act.Should().ThrowAsync<CoachToolException>(
            "the reversal cannot record itself into a slot somebody else took");
    }

    // ================================================================== the backstop exists

    /// <summary>
    /// The unique index that makes the bound true under concurrency is actually in the database.
    /// </summary>
    /// <remarks>
    /// Asserted directly because it went missing once already, silently. The migration was written
    /// without its discovery attributes, EF never saw it, <c>MigrateAsync</c> skipped it without a
    /// word, and the only symptom was a concurrency test that passed when the callers happened to
    /// serialise. A schema this depends on is worth one query.
    /// </remarks>
    [PostgresFact]
    public async Task The_turn_uniqueness_index_exists()
    {
        await using var connection = new Npgsql.NpgsqlConnection(_harness.ConnectionString);
        await connection.OpenAsync();

        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE tablename = 'CoachWriteOperation'
              AND indexname = 'IX_CoachWriteOperation_UserProfileId_ConversationId_TurnId'
            """,
            connection);

        var definition = (string?)await command.ExecuteScalarAsync();

        definition.Should().NotBeNull("the migration that creates it must have been applied");
        definition!.Should().Contain("UNIQUE", "a non-unique index would enforce nothing");
        definition.Should().Contain("TurnId");
    }

    /// <summary>
    /// The migration carries the attributes EF needs to discover it.
    /// </summary>
    /// <remarks>
    /// The coach migrations are normally scaffolded with a designer file that holds these; this
    /// one is hand-written, so they are inline and nothing regenerates them. Without
    /// <c>[Migration]</c> the file compiles, ships, and does nothing.
    /// </remarks>
    [Fact]
    public void The_turn_uniqueness_migration_is_discoverable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();

        var path = Path.Combine(
            dir!.FullName,
            "src", "SentenceStudio.Api", "Coach", "Persistence", "Migrations",
            "20260819130000_AddCoachWriteOperationTurnUniqueness.cs");

        File.Exists(path).Should().BeTrue(path);

        var source = File.ReadAllText(path);
        source.Should().Contain("[DbContext(typeof(CoachDbContext))]");
        source.Should().Contain("[Migration(\"20260819130000_AddCoachWriteOperationTurnUniqueness\")]");
        source.Should().Contain("unique: true");
        source.Should().NotContain("DropTable");
    }

    /// <summary>
    /// The turn-uniqueness migration fails loudly and changes no data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A unique index over existing rows has two possible designs. One deletes whatever collides
    /// so the index can build; the other refuses to build and says what collided. This is the
    /// second, deliberately: every row in this table is an operation on a learner's own data, and
    /// choosing which of two survives is not something a deploy step gets to decide silently.
    /// </para>
    /// <para>
    /// The write tables were unshipped and the feature disabled when the index landed, and the E2E
    /// run applied it against a real database cleanly, so there is nothing to dedupe and no reason
    /// to build a tool that could. What exists instead is a read-only preflight
    /// (<c>scripts/preflight-coach-turn-uniqueness.sh</c>) that answers "would this fail, and on
    /// what" before a deploy rather than from a half-migrated database.
    /// </para>
    /// <para>
    /// This test pins both halves: the migration stays additive, and the preflight stays
    /// descriptive. A later edit that adds a dedupe to either one fails here.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_turn_uniqueness_rollout_is_fail_loud_and_never_deletes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();

        var migration = File.ReadAllText(Path.Combine(
            root!.FullName,
            "src", "SentenceStudio.Api", "Coach", "Persistence", "Migrations",
            "20260819130000_AddCoachWriteOperationTurnUniqueness.cs"));

        migration.Should().Contain("CreateIndex");
        migration.Should().Contain("unique: true");
        migration.Should().NotContain(
            "Sql(", "a raw statement is how a dedupe would get in without looking like one");
        migration.Should().NotContain("DELETE");
        migration.Should().NotContain("DropTable");
        migration.Should().NotContain("DropColumn");

        var preflight = Path.Combine(root.FullName, "scripts", "preflight-coach-turn-uniqueness.sh");
        File.Exists(preflight).Should().BeTrue(
            "the descriptive preflight is the safe half of this rollout");

        var script = File.ReadAllText(preflight);
        script.Should().Contain("CoachWriteOperation");

        // Read-only by construction. Checked on the SQL it issues, not on intent.
        foreach (var forbidden in new[] { "DELETE FROM", "UPDATE \"", "DROP INDEX", "ALTER TABLE", "CREATE INDEX" })
        {
            script.Should().NotContain(
                forbidden, $"the preflight reports; it does not change anything ({forbidden})");
        }
    }

    // ================================================================== helpers

    private static CoachMessageRecord Record(string id, CoachMessageRole role, string? operationId) => new(
        id,
        Conversation,
        Sequence: 1,
        role,
        CoachMessageKind.Text,
        new CoachMessagePayload { Text = "seeded" },
        SchemaVersion: 1,
        operationId,
        new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc));

    private async Task<IReadOnlyList<CoachWriteOperation>> AllForTurnAsync(string turnId)
    {
        await using var db = _harness.NewContext();
        return await db.CoachWriteOperations.AsNoTracking()
            .Where(o => o.ConversationId == Conversation && o.TurnId == turnId)
            .OrderBy(o => o.CreatedAtUtc)
            .ToListAsync();
    }

    private ApplicationDbContext NewAppContext() => _harness.NewApplicationContext();

    private CoachWriteOperationService NewLedger(CoachDbContext db, ApplicationDbContext appDb)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var resources = new LearningResourceRepository(
            _appServices, NullLogger<LearningResourceRepository>.Instance, new StubFileSystem());
        var skills = new SkillProfileRepository(_appServices, NullLogger<SkillProfileRepository>.Instance);

        var handlers = new ICoachWriteHandler[]
        {
            new CoachVocabularyEntryHandler(ownership, resources),
            new CoachSkillEntryHandler(skills, ownership)
        };

        return CoachWriteTestScope.NewLedger(
            db, _harness.ContentProtector, handlers, new FakeUserScope(Owner), _harness.Time);
    }

    private async Task<string> SeedResourceAsync()
    {
        await using var db = NewAppContext();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = "Vocabulary list",
            Language = "Korean",
            UserProfileId = Owner,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.LearningResources.Add(resource);
        await db.SaveChangesAsync();
        return resource.Id;
    }

    private async Task SeedConversationAsync(string userProfileId, string conversationId)
    {
        await using var db = _harness.NewContext();
        var now = _harness.Time.GetUtcNow().UtcDateTime;

        db.CoachConversations.Add(new CoachConversation
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
