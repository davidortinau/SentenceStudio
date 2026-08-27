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
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// What a client is told about a proposed change, read back from a real ledger.
/// </summary>
/// <remarks>
/// <para>
/// The card a learner approves is assembled entirely from these reads, so the properties that
/// matter are the ones a fake cannot honestly stand in for: that a proposal is visible before
/// anything has run and says so, that its state survives a reload because it is re-read rather
/// than remembered, that a receipt appears only after execution, and that another learner asking
/// for the same operation gets nothing at all.
/// </para>
/// <para>
/// Skipped, not failed, when there is no PostgreSQL to talk to — matching the rest of the write
/// suite. Nothing here would be meaningful in memory: the ownership filter and the composite key
/// are the things under test.
/// </para>
/// </remarks>
public sealed class CoachWriteClientStatePostgresTests : IAsyncLifetime
{
    private const string Owner = "state-owner";
    private const string Stranger = "state-stranger";
    private const string Conversation = "state-conv";
    private const string Turn = "state-turn";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _appServices = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("writestate", withApplicationSchema: true);
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

    private ApplicationDbContext NewAppContext() => _harness.NewApplicationContext();

    private CoachWriteOperationService NewLedger(
        CoachDbContext db, ApplicationDbContext appDb, string? owner)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var resources = new LearningResourceRepository(
            _appServices, NullLogger<LearningResourceRepository>.Instance, new StubFileSystem());

        var handlers = new ICoachWriteHandler[]
        {
            new CoachVocabularyEntryHandler(ownership, resources),
            new CoachVocabularyRemovalHandler(ownership, resources)
        };

        return CoachWriteTestScope.NewLedger(
            db, _harness.ContentProtector, handlers, new FakeUserScope(owner), _harness.Time);
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

    private async Task<string> SeedResourceAsync(string userProfileId)
    {
        await using var db = NewAppContext();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = "Resource",
            Language = "Korean",
            UserProfileId = userProfileId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.LearningResources.Add(resource);
        await db.SaveChangesAsync();
        return resource.Id;
    }

    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, CoachNormalizedJson.Options);

    private async Task<string> ProposeAsync(string resourceId, string term = "사과")
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation,
            Turn,
            CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, term, "apple")));

        return proposal.OperationId;
    }

    // ================================================================== visibility

    [PostgresFact]
    public async Task A_proposal_is_visible_to_its_owner_before_anything_has_run()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var state = await NewLedger(db, appDb, Owner).GetStateAsync(Conversation, operationId);

        state.Should().NotBeNull();
        state!.OperationId.Should().Be(operationId);
        state.Status.Should().Be(CoachWriteStatus.Proposed);
        state.ChangeKind.Should().Be(CoachWriteChangeKind.VocabularyAdd);
        state.RiskClass.Should().Be(CoachWriteRiskClass.WriteSoft);
        state.ApprovalMode.Should().Be("accept");
        state.RequiresConfirmation.Should().BeFalse();
        state.Summary.Should().NotBeNullOrWhiteSpace("the card has nothing to show without it");
        state.Receipt.Should().BeNull("nothing has run, so there is nothing to receipt");
        state.AlreadyExecuted.Should().BeFalse();
    }

    /// <summary>
    /// The card is re-read rather than remembered, which is what makes it survive a reload, a
    /// route change, and a second device.
    /// </summary>
    [PostgresFact]
    public async Task Reading_the_same_proposal_again_reports_the_same_state()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId);

        await using var appDb = NewAppContext();
        await using var first = _harness.NewContext();
        await using var second = _harness.NewContext();

        var before = await NewLedger(first, appDb, Owner).GetStateAsync(Conversation, operationId);
        var after = await NewLedger(second, appDb, Owner).GetStateAsync(Conversation, operationId);

        after.Should().BeEquivalentTo(before);
    }

    /// <summary>
    /// The turn binding is what places the card back inside the exchange that produced it.
    /// </summary>
    [PostgresFact]
    public async Task A_proposal_names_the_turn_that_produced_it()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var byId = await ledger.GetStateAsync(Conversation, operationId);
        var byTurn = await ledger.GetLatestForTurnAsync(Conversation, Turn);
        var byTurns = await ledger.ListForTurnsAsync(Conversation, [Turn]);

        byId!.TurnId.Should().Be(Turn);
        byTurn!.OperationId.Should().Be(operationId);
        byTurns.Should().ContainSingle().Which.OperationId.Should().Be(operationId);
    }

    [PostgresFact]
    public async Task A_turn_that_proposed_nothing_reports_nothing()
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        (await ledger.GetLatestForTurnAsync(Conversation, "turn-with-no-proposal")).Should().BeNull();
        (await ledger.ListForTurnsAsync(Conversation, ["turn-with-no-proposal"])).Should().BeEmpty();
    }

    // ================================================================== after execution

    [PostgresFact]
    public async Task Accepting_produces_a_receipt_the_card_can_render()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId, "포도");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        await ledger.AcceptAsync(Conversation, operationId);

        var state = await ledger.GetStateAsync(Conversation, operationId);

        state!.Status.Should().Be(CoachWriteStatus.Executed);
        state.AlreadyExecuted.Should().BeTrue();
        state.Receipt.Should().NotBeNull();
        state.Receipt!.Status.Should().Be(CoachWriteStatus.Executed);
        state.Receipt.TargetKind.Should().Be(CoachWriteTargetKind.VocabularyWord);
        state.Receipt.TargetId.Should().NotBeNullOrWhiteSpace();
        state.Receipt.CanUndo.Should().BeTrue("a created row can be deleted again inside its window");
        state.Receipt.UndoExpiresAtUtc.Should().NotBeNull();
    }

    [PostgresFact]
    public async Task Undoing_leaves_a_card_that_says_undone_and_offers_no_further_undo()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId, "딸기");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        await ledger.AcceptAsync(Conversation, operationId);
        await ledger.UndoAsync(Conversation, operationId);

        var state = await ledger.GetStateAsync(Conversation, operationId);

        state!.Status.Should().Be(CoachWriteStatus.Undone);
        state.AlreadyExecuted.Should().BeFalse("the learner does not have the change any more");
        state.Receipt!.CanUndo.Should().BeFalse("it has already been put back");
    }

    [PostgresFact]
    public async Task Declining_leaves_a_card_that_says_declined_and_no_receipt()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId, "수박");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        await ledger.RejectAsync(Conversation, operationId);

        var state = await ledger.GetStateAsync(Conversation, operationId);

        state!.Status.Should().Be(CoachWriteStatus.Rejected);
        state.Receipt.Should().BeNull("nothing ran");
    }

    // ================================================================== protected changes

    [PostgresFact]
    public async Task A_protected_proposal_says_it_needs_a_confirmation_and_carries_no_value()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var word = await SeedWordAsync(resourceId);

        var proposal = await ledger.ProposeAsync(
            Conversation,
            Turn + "-hard",
            CoachToolNames.ProposeVocabularyRemoval,
            Json(new CoachVocabularyRemovalArgs(word)));

        var before = await ledger.GetStateAsync(Conversation, proposal.OperationId);

        before!.RiskClass.Should().Be(CoachWriteRiskClass.WriteHard);
        before.RequiresConfirmation.Should().BeTrue();
        before.ApprovalMode.Should().Be("confirm");
        before.ConfirmationExpiresAtUtc.Should().BeNull(
            "nothing has been minted yet, and an expiry with no value behind it would imply one had");

        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        challenge.Should().NotBeNull();

        var after = await ledger.GetStateAsync(Conversation, proposal.OperationId);
        after!.ConfirmationExpiresAtUtc.Should().NotBeNull("there is now a value in flight");

        // The state a client reads carries the window and never the value.
        System.Text.Json.JsonSerializer.Serialize(after).ToLowerInvariant()
            .Should().NotContain(challenge!.ConfirmationSecret.ToLowerInvariant());
    }

    // ================================================================== ownership

    /// <summary>
    /// Another learner asking about a real operation gets exactly what they would get for one that
    /// never existed.
    /// </summary>
    [PostgresFact]
    public async Task Another_learner_sees_nothing_at_all()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId, "참외");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var stranger = NewLedger(db, appDb, Stranger);

        var real = await stranger.GetStateAsync(Conversation, operationId);
        var invented = await stranger.GetStateAsync(Conversation, Guid.NewGuid().ToString("n"));

        real.Should().BeNull();
        invented.Should().BeNull();
        real.Should().BeEquivalentTo(invented,
            "a real operation and an invented one must be indistinguishable to a non-owner");
    }

    [PostgresFact]
    public async Task A_proposal_addressed_through_the_wrong_conversation_is_not_found()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var operationId = await ProposeAsync(resourceId, "자두");

        await SeedConversationAsync(Owner, "state-conv-other");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        (await ledger.GetStateAsync("state-conv-other", operationId)).Should().BeNull();
    }

    [PostgresFact]
    public async Task Another_learners_turn_id_reveals_nothing()
    {
        var resourceId = await SeedResourceAsync(Owner);
        await ProposeAsync(resourceId, "매실");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var stranger = NewLedger(db, appDb, Stranger);

        (await stranger.GetLatestForTurnAsync(Conversation, Turn)).Should().BeNull();
        (await stranger.ListForTurnsAsync(Conversation, [Turn])).Should().BeEmpty();
    }

    /// <summary>
    /// Creates a word the way the app does, by proposing and accepting one.
    /// </summary>
    /// <remarks>
    /// Hand-inserting a row would let this test pass against a shape production never writes. The
    /// handler is the only thing that knows how a coach-created word is put together, so it is the
    /// thing that creates it.
    /// </remarks>
    private async Task<string> SeedWordAsync(string resourceId)
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation,
            Turn + "-seed",
            CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "배", "pear")));

        var receipt = await ledger.AcceptAsync(Conversation, proposal.OperationId);
        receipt.EntityId.Should().NotBeNullOrWhiteSpace();

        return receipt.EntityId!;
    }
}
