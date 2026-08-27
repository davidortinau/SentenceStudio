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
/// The two approval routes, the arguments they accept, and what a refused repository call does.
/// </summary>
/// <remarks>
/// These run against a real PostgreSQL server carrying the real application schema for the same
/// reason the rest of the write family does: every assertion here is about what did or did not
/// happen to a learner's row, and a fake repository would be asserting that the test's own stub
/// returned what the test told it to.
/// </remarks>
public sealed class CoachWriteChannelPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-channel-owner";
    private const string Conversation = "conv-channel";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _appServices = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("writechannels", withApplicationSchema: true);
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

    // ================================================================== route separation

    /// <summary>
    /// A soft write cannot be executed through the protected route, header or no header.
    /// </summary>
    /// <remarks>
    /// The interesting case is the missing header. Deciding the channel by whether a secret turned
    /// up meant a caller could reach the confirmation route, omit the header, and have the request
    /// read as a plain acceptance — which is the one thing having two routes is supposed to
    /// prevent. The channel now comes from the route.
    /// </remarks>
    [PostgresFact]
    public async Task A_soft_proposal_cannot_be_executed_through_the_confirmation_route()
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillEntry,
            Json(new CoachSkillEntryArgs("Soft via confirm", "Should never execute here.", "Korean")));

        var act = async () => await ledger.ConfirmAsync(Conversation, proposal.OperationId, null);
        var refusal = await act.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Reason.Should().Contain("accepted, not confirmed");

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Soft via confirm"))
            .Should().Be(0, "the wrong route must not write");

        await AssertRefusedWithAsync(proposal.OperationId, CoachWriteFailureCodes.WrongAcceptanceChannel);
    }

    /// <summary>Presenting a secret on the confirmation route does not help either.</summary>
    [PostgresFact]
    public async Task A_soft_proposal_is_refused_on_the_confirmation_route_even_with_a_secret()
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillEntry,
            Json(new CoachSkillEntryArgs("Soft with secret", "Should never execute here.", "Korean")));

        var act = async () => await ledger.ConfirmAsync(Conversation, proposal.OperationId, "invented");
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.UserProfileId == Owner && s.Title == "Soft with secret"))
            .Should().Be(0);
    }

    /// <summary>
    /// An already-executed soft write still refuses the protected route.
    /// </summary>
    /// <remarks>
    /// A settled operation replays its receipt, which is what makes approval idempotent — but only
    /// on the route that owns it. Replaying it to the confirmation route would answer a request
    /// that route should never have accepted with something indistinguishable from success.
    /// </remarks>
    [PostgresFact]
    public async Task An_executed_soft_write_still_refuses_the_confirmation_route()
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillEntry,
            Json(new CoachSkillEntryArgs("Settled soft", "Executed properly first.", "Korean")));

        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        var act = async () => await ledger.ConfirmAsync(Conversation, proposal.OperationId, null);
        await act.Should().ThrowAsync<CoachToolException>();
    }

    /// <summary>A protected write is refused on the soft route before anything is claimed.</summary>
    [PostgresFact]
    public async Task A_protected_proposal_is_refused_on_the_acceptance_route()
    {
        var skillId = await SeedSkillAsync("Protected on soft route");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));

        var act = async () => await ledger.AcceptAsync(Conversation, proposal.OperationId);
        var refusal = await act.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Reason.Should().Contain("explicit confirmation");

        await using var check = NewAppContext();
        (await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId))
            .IsArchived.Should().BeFalse();

        await AssertRefusedWithAsync(proposal.OperationId, CoachWriteFailureCodes.WrongAcceptanceChannel);
    }

    // ================================================================== argument strictness

    /// <summary>
    /// A payload carrying a member the contract does not declare is refused, not trimmed.
    /// </summary>
    /// <remarks>
    /// The members that happen to match are not salvaged. A payload written against a different
    /// shape is a request nobody fully read, and previewing the readable half would put a card in
    /// front of the learner describing less than was asked for.
    /// </remarks>
    [PostgresFact]
    public async Task A_proposal_carrying_an_undeclared_member_is_refused()
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var act = async () => await ledger.ProposeAsync(
            Conversation,
            "turn-1",
            CoachToolNames.ProposeSkillEntry,
            """{"Title":"Smuggled","Description":"Has an extra field.","Language":"Korean","UserProfileId":"user-stranger"}""");

        var refusal = await act.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Kind.Should().Be(CoachToolFailureKind.InvalidArgument);

        await using var check = NewAppContext();
        (await check.SkillProfiles.CountAsync(s => s.Title == "Smuggled"))
            .Should().Be(0, "a refused payload leaves no row anywhere");

        await using var ledgerCheck = _harness.NewContext();
        (await ledgerCheck.CoachWriteOperations.CountAsync(o => o.ConversationId == Conversation))
            .Should().Be(0, "a proposal that could never execute must not occupy a ledger row");
    }

    /// <summary>The declared shape still binds, so strictness did not simply refuse everything.</summary>
    [PostgresFact]
    public async Task A_proposal_carrying_only_declared_members_is_accepted()
    {
        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation,
            "turn-1",
            CoachToolNames.ProposeSkillEntry,
            """{"Title":"Declared only","Description":"Every member is on the record.","Language":"Korean"}""");

        proposal.OperationId.Should().NotBeNullOrWhiteSpace();
    }

    // ================================================================== archive semantics

    /// <summary>
    /// Confirming an archive puts the skill away and deletes nothing.
    /// </summary>
    [PostgresFact]
    public async Task Archiving_a_skill_preserves_the_row_and_everything_referencing_it()
    {
        var skillId = await SeedSkillAsync("Ordering food");
        var storyId = await SeedStoryReferencingAsync(skillId);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));
        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);

        var receipt = await ledger.ConfirmAsync(
            Conversation, proposal.OperationId, challenge!.ConfirmationSecret);

        receipt.Status.Should().Be(CoachWriteOperationStatus.Executed);
        receipt.CanUndo.Should().BeTrue("an archive is reversible, which is why it replaced a delete");

        await using var check = NewAppContext();
        var stored = await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId);
        stored.IsArchived.Should().BeTrue();
        stored.Title.Should().Be("Ordering food");

        // The reference is the whole reason this is an archive. A deleted skill would leave this
        // story pointing at nothing.
        var story = await check.Stories.AsNoTracking().SingleAsync(s => s.Id == storyId);
        story.SkillID.Should().Be(skillId);
    }

    /// <summary>Undoing an archive restores the skill to the practice list.</summary>
    [PostgresFact]
    public async Task Undoing_an_archive_restores_the_skill()
    {
        var skillId = await SeedSkillAsync("Restore me");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));
        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        await ledger.ConfirmAsync(Conversation, proposal.OperationId, challenge!.ConfirmationSecret);

        var undo = await ledger.UndoAsync(Conversation, proposal.OperationId);
        undo.Status.Should().Be(CoachWriteOperationStatus.Undone);

        await using var check = NewAppContext();
        (await check.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId))
            .IsArchived.Should().BeFalse();
    }

    /// <summary>An already-archived skill answers exactly like one that does not exist.</summary>
    [PostgresFact]
    public async Task An_archived_skill_cannot_be_archived_again()
    {
        var skillId = await SeedSkillAsync("Already away", archived: true);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var act = async () => await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeSkillArchive, Json(new CoachSkillArchiveArgs(skillId)));

        var refusal = await act.Should().ThrowAsync<CoachToolException>();
        refusal.Which.Reason.Should().Be(
            "No such item for this learner.",
            "a distinguishable answer would confirm the row exists to somebody guessing ids");
    }

    // ================================================================== refused outcomes

    /// <summary>
    /// An undo whose repository call is refused does not report the change as reversed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set-up removes the link out of band, exactly as a second device or another tab would.
    /// The handler's reversal is then a call the repository declines, and the question the test
    /// asks is what the learner is told: before this change the return value was dropped and the
    /// operation was marked <c>Undone</c> with a receipt saying the word had been removed.
    /// </para>
    /// <para>
    /// The row must also not be left claiming it can still be undone. The window is spent by the
    /// claim before the handler runs, and a failed reversal does not give it back.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task An_undo_whose_write_is_refused_is_not_recorded_as_undone()
    {
        var resourceId = await SeedResourceAsync("Vocabulary list");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var receipt = await ledger.AcceptAsync(Conversation, proposal.OperationId);
        receipt.Status.Should().Be(CoachWriteOperationStatus.Executed);

        // Somebody else unlinks the word first. The reversal now has nothing to remove.
        await using (var meddler = NewAppContext())
        {
            var mappings = await meddler.ResourceVocabularyMappings
                .Where(m => m.ResourceId == resourceId)
                .ToListAsync();
            meddler.ResourceVocabularyMappings.RemoveRange(mappings);
            await meddler.SaveChangesAsync();
        }

        var act = async () => await ledger.UndoAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var ledgerCheck = _harness.NewContext();
        var stored = await ledgerCheck.CoachWriteOperations.AsNoTracking()
            .SingleAsync(o => o.Id == proposal.OperationId);

        stored.Status.Should().NotBe(
            CoachWriteOperationStatus.Undone, "nothing was reversed, so nothing may say it was");
        stored.UndoOperationId.Should().BeNull("no reversal row exists to point at");
        stored.UndoneAtUtc.Should().BeNull();

        (await ledgerCheck.CoachWriteAudits.AsNoTracking()
            .CountAsync(a => a.OperationId == proposal.OperationId && a.Event == CoachWriteAuditEvent.Undone))
            .Should().Be(0, "the audit must not carry a reversal that did not happen");
    }

    /// <summary>A refused undo does not offer itself again.</summary>
    [PostgresFact]
    public async Task A_refused_undo_does_not_leave_the_window_open()
    {
        var resourceId = await SeedResourceAsync("Vocabulary list");

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "포도", "grape")));
        await ledger.AcceptAsync(Conversation, proposal.OperationId);

        await using (var meddler = NewAppContext())
        {
            var mappings = await meddler.ResourceVocabularyMappings
                .Where(m => m.ResourceId == resourceId)
                .ToListAsync();
            meddler.ResourceVocabularyMappings.RemoveRange(mappings);
            await meddler.SaveChangesAsync();
        }

        var first = async () => await ledger.UndoAsync(Conversation, proposal.OperationId);
        await first.Should().ThrowAsync<CoachToolException>();

        var second = async () => await ledger.UndoAsync(Conversation, proposal.OperationId);
        await second.Should().ThrowAsync<CoachToolException>();

        await using var ledgerCheck = _harness.NewContext();
        (await ledgerCheck.CoachWriteOperations.AsNoTracking().SingleAsync(o => o.Id == proposal.OperationId))
            .UndoExpiresAtUtc.Should().BeNull("a half-run reversal is not offered a second time");
    }

    // ================================================================== helpers

    private async Task AssertRefusedWithAsync(string operationId, string failureCode)
    {
        await using var db = _harness.NewContext();
        var codes = await db.CoachWriteAudits.AsNoTracking()
            .Where(a => a.OperationId == operationId)
            .Select(a => a.FailureCode)
            .ToListAsync();

        codes.Should().Contain(failureCode);
    }

    private ApplicationDbContext NewAppContext() => _harness.NewApplicationContext();

    private CoachWriteOperationService NewLedger(
        CoachDbContext db, ApplicationDbContext appDb, string? owner)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var resources = new LearningResourceRepository(
            _appServices, NullLogger<LearningResourceRepository>.Instance, new StubFileSystem());
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
            db, _harness.ContentProtector, handlers, new FakeUserScope(owner), _harness.Time);
    }

    private async Task<string> SeedSkillAsync(string title, bool archived = false)
    {
        await using var db = NewAppContext();
        var skill = new SkillProfile
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Description = "Seeded for a channel test.",
            Language = "Korean",
            UserProfileId = Owner,
            IsArchived = archived,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SkillProfiles.Add(skill);
        await db.SaveChangesAsync();
        return skill.Id;
    }

    /// <summary>Creates a row that points at a skill, so a deletion would be visible as breakage.</summary>
    private async Task<int> SeedStoryReferencingAsync(string skillId)
    {
        await using var db = NewAppContext();
        var story = new Story
        {
            Body = "A short practice story.",
            SkillID = skillId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Stories.Add(story);
        await db.SaveChangesAsync();
        return story.Id;
    }

    private async Task<string> SeedResourceAsync(string title)
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
