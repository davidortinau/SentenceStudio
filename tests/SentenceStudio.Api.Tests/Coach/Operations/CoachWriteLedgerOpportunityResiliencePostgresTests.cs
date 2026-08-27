using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// The write ledger's audit survives a broken opportunity recorder, proven through the real path.
/// </summary>
/// <remarks>
/// <para>
/// The previous version of this claim was tested by calling the recorder directly and asserting
/// it threw. That proves the fake works. It says nothing about <c>SaveAuditedAsync</c> — the
/// method that actually flushes queued signals after a commit — because the flush loop was never
/// entered.
/// </para>
/// <para>
/// These drive the real refusal paths: a proposal that violates ownership, and an approval for an
/// operation that does not exist. Both queue a signal through <c>QueueOpportunity</c>, commit
/// their audit row, and then flush. The assertions are on the durable audit and the caller's own
/// outcome, which are the two things a telemetry failure must never touch.
/// </para>
/// <para>
/// PostgreSQL, because the audit row and the operation row are what is being asserted, and the
/// composite foreign key between them only exists on a real schema.
/// </para>
/// </remarks>
public sealed class CoachWriteLedgerOpportunityResiliencePostgresTests : IAsyncLifetime
{
    private const string Owner = "user-owner";
    private const string Stranger = "user-stranger";
    private const string Conversation = "conv-resilience";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _appServices = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync(
            "write-opportunity-resilience", withApplicationSchema: true);

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

    /// <summary>
    /// A refused approval for an unknown operation still writes its audit when the ledger throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the path <c>AuditOrphanDenialAsync</c> owns: an approval naming an operation that
    /// does not resolve. It writes a standalone <c>Denied</c> audit row — the forensic record of
    /// what is, in shape, a cross-tenant probe — queues one aggregate-only signal, and flushes it
    /// after the commit.
    /// </para>
    /// <para>
    /// The recorder throws <see cref="InvalidOperationException"/>. The audit row must still be
    /// on disk and the caller must still receive the same <c>CoachToolException</c> it would have
    /// received on a host with no ledger at all, because telemetry never outranks forensics.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task An_orphan_denial_audit_survives_a_throwing_recorder()
    {
        var thrower = new ThrowingCoachOpportunityRecorder();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner, opportunities: thrower);

        var act = async () => await ledger.AcceptAsync(Conversation, "no-such-operation");

        // The caller's outcome is the tool refusal, not the recorder's exception.
        await act.Should().ThrowAsync<CoachToolException>();

        thrower.Calls.Should().BeGreaterThan(0,
            "the flush loop must actually have been entered, or this test proves nothing about it");

        await using var check = _harness.NewContext();
        var audits = await check.CoachWriteAudits
            .AsNoTracking()
            .Where(row => row.UserProfileId == Owner && row.ConversationId == Conversation)
            .ToListAsync();

        audits.Should().ContainSingle(
            "the audit committed before the flush ran, and a telemetry failure must not undo it");
        audits[0].Event.Should().Be(CoachWriteAuditEvent.Denied);
        audits[0].FailureCode.Should().Be(CoachWriteFailureCodes.OperationNotFound);
    }

    /// <summary>
    /// The same path with a recorder that throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The narrow case the old <c>when (ex is not OperationCanceledException)</c> clause let
    /// through. It reads as prudent — cancellation should normally propagate — but here the
    /// learner's operation is already finished: <c>SaveChangesAsync</c> observed the caller's
    /// token and committed. A cancelled <em>observation</em> escaping at that point replaces a
    /// bounded, actionable refusal with an unrelated cancellation, and the caller's recovery path
    /// then writes a second, contradictory audit row.
    /// </para>
    /// <para>
    /// Separate from the test above because they exercise different catch clauses, and a suite
    /// that only used <see cref="ThrowingCoachOpportunityRecorder"/> passed against the broken
    /// version.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task An_orphan_denial_audit_survives_a_cancelling_recorder()
    {
        var canceller = new CancellingCoachOpportunityRecorder();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner, opportunities: canceller);

        var act = async () => await ledger.AcceptAsync(Conversation, "no-such-operation");

        await act.Should().ThrowAsync<CoachToolException>(
            "a cancelled observation must not replace the refusal the caller is entitled to");

        canceller.Calls.Should().BeGreaterThan(0);

        await using var check = _harness.NewContext();
        var audits = await check.CoachWriteAudits
            .AsNoTracking()
            .Where(row => row.UserProfileId == Owner && row.ConversationId == Conversation)
            .ToListAsync();

        audits.Should().ContainSingle();
        audits[0].FailureCode.Should().Be(CoachWriteFailureCodes.OperationNotFound);
    }

    /// <summary>
    /// A successful proposal is unchanged by a broken recorder.
    /// </summary>
    /// <remarks>
    /// The positive half. A proposal queues a signal only for a <c>Denied</c> event, so this one
    /// flushes nothing — but it proves the ledger's happy path is not merely surviving the
    /// recorder, it is untouched by it: the operation row, its audit, and the returned proposal
    /// are identical to a run with no recorder wired at all.
    /// </remarks>
    [PostgresFact]
    public async Task A_successful_proposal_is_identical_with_a_broken_recorder()
    {
        var resourceId = await SeedResourceAsync(Owner);
        var thrower = new ThrowingCoachOpportunityRecorder();

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var ledger = NewLedger(db, appDb, Owner, opportunities: thrower);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        proposal.OperationId.Should().NotBeNullOrWhiteSpace();
        thrower.Calls.Should().Be(0, "a successful proposal has no refusal to record");

        await using var check = _harness.NewContext();
        var operation = await check.CoachWriteOperations
            .AsNoTracking()
            .SingleAsync(row => row.Id == proposal.OperationId);

        operation.Status.Should().Be(CoachWriteOperationStatus.Proposed);
        operation.UserProfileId.Should().Be(Owner);

        var audits = await check.CoachWriteAudits
            .AsNoTracking()
            .Where(row => row.OperationId == proposal.OperationId)
            .ToListAsync();

        audits.Should().ContainSingle().Which.Event.Should().Be(CoachWriteAuditEvent.Proposed);
    }

    /// <summary>
    /// A cross-owner approval refusal keeps its audit and its refusal through a broken recorder.
    /// </summary>
    /// <remarks>
    /// The stranger's approval is refused because the operation is not theirs. That refusal is
    /// the shape of a cross-tenant probe, so its audit row is the one that most has to survive —
    /// and it is a different code path from the orphan case, because the operation genuinely
    /// exists.
    /// </remarks>
    [PostgresFact]
    public async Task A_cross_owner_refusal_keeps_its_audit_with_a_throwing_recorder()
    {
        var resourceId = await SeedResourceAsync(Owner);

        await using var appDb = NewAppContext();
        await using var db = _harness.NewContext();
        var proposal = await NewLedger(db, appDb, Owner).ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeVocabularyEntry,
            Json(new CoachVocabularyEntryArgs(resourceId, "사과", "apple")));

        var thrower = new ThrowingCoachOpportunityRecorder();

        await using var strangerDb = _harness.NewContext();
        await using var strangerApp = NewAppContext();
        var strangerLedger = NewLedger(strangerDb, strangerApp, Stranger, opportunities: thrower);

        var act = async () => await strangerLedger.AcceptAsync(Conversation, proposal.OperationId);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var check = _harness.NewContext();

        // The stranger's refusal left its own audit, and the owner's proposal is untouched.
        var strangerAudits = await check.CoachWriteAudits
            .AsNoTracking()
            .Where(row => row.UserProfileId == Stranger)
            .ToListAsync();

        strangerAudits.Should().NotBeEmpty(
            "a refusal that looks like a cross-tenant probe has to leave a trace, whatever the " +
            "ledger did afterwards");

        var operation = await check.CoachWriteOperations
            .AsNoTracking()
            .SingleAsync(row => row.Id == proposal.OperationId);

        operation.Status.Should().Be(CoachWriteOperationStatus.Proposed,
            "the stranger changed nothing");
    }

    // ------------------------------------------------------------------ wiring

    private ApplicationDbContext NewAppContext() => _harness.NewApplicationContext();

    private CoachWriteOperationService NewLedger(
        CoachDbContext db,
        ApplicationDbContext appDb,
        string? owner,
        SentenceStudio.Api.Coach.Opportunities.ICoachOpportunityRecorder? opportunities = null)
    {
        var ownership = new CoachWriteOwnership(appDb);
        var resources = new LearningResourceRepository(
            _appServices, NullLogger<LearningResourceRepository>.Instance, new StubFileSystem());
        var skills = new SkillProfileRepository(
            _appServices, NullLogger<SkillProfileRepository>.Instance);

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
            db,
            _harness.ContentProtector,
            handlers,
            new FakeUserScope(owner),
            _harness.Time,
            registry: null,
            opportunities: opportunities);
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

    private async Task<string> SeedResourceAsync(string userProfileId, string title = "Resource")
    {
        await using var db = NewAppContext();
        var resource = new LearningResource
        {
            Title = title,
            UserProfileId = userProfileId,
            Language = "Korean",
            MediaType = "Text",
            CreatedAt = _harness.Time.GetUtcNow().UtcDateTime,
            UpdatedAt = _harness.Time.GetUtcNow().UtcDateTime
        };

        db.LearningResources.Add(resource);
        await db.SaveChangesAsync();

        return resource.Id.ToString();
    }

    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
