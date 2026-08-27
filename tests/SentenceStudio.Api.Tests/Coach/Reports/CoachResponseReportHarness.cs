using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// A real ledger, a real message store, and the production report service over them.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is a stand-in for the thing under test. The service writes through a real
/// <see cref="CoachDbContext"/>, the messages it pairs are real encrypted rows appended by the
/// real <see cref="CoachMessageStore"/>, and the ledger row it raises goes through the production
/// <see cref="CoachOpportunityRecorder"/>. A harness that faked any of those would have proved the
/// harness.
/// </para>
/// <para>
/// SQLite here for speed; <c>CoachResponseReportPostgresTests</c> runs the same service against a
/// real PostgreSQL server, where the unique index and its race behaviour are the database's rather
/// than the application's.
/// </para>
/// </remarks>
internal sealed class CoachResponseReportHarness : IDisposable
{
    private readonly CoachPersistenceHarness _persistence;
    private readonly ServiceProvider _provider;

    public CoachResponseReportHarness(
        DateTimeOffset? start = null,
        bool reportsEnabled = true,
        bool opportunitiesEnabled = true,
        string? userProfileId = "learner-a")
    {
        _persistence = new CoachPersistenceHarness(start);

        Options = new TestOptionsMonitor<CoachResponseReportOptions>(
            new CoachResponseReportOptions { Enabled = reportsEnabled });

        OpportunityOptions = new TestOptionsMonitor<CoachOpportunityOptions>(
            new CoachOpportunityOptions { Enabled = opportunitiesEnabled });

        UserScope = new TestUserScope(userProfileId);

        Registry = new CoachToolRegistry(new CoachOptions
        {
            Enabled = true,
            DurableHistory = new CoachFeatureSwitch { Enabled = true },
            SamOverlay = new CoachFeatureSwitch { Enabled = true },
            SamReadTools = new CoachFeatureSwitch { Enabled = true },
            SamWriteTools = new CoachFeatureSwitch { Enabled = true }
        });

        var services = new ServiceCollection();
        services.AddSingleton(_persistence.DbOptions);
        services.AddScoped<CoachDbContext>(sp =>
            new CoachDbContext(sp.GetRequiredService<DbContextOptions<CoachDbContext>>()));
        _provider = services.BuildServiceProvider();

        Recorder = new CoachOpportunityRecorder(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            UserScope,
            Registry,
            OpportunityOptions,
            Options,
            _persistence.Time,
            NullLogger<CoachOpportunityRecorder>.Instance);
    }

    public TestTimeProvider Time => _persistence.Time;

    public TestOptionsMonitor<CoachResponseReportOptions> Options { get; }

    public TestOptionsMonitor<CoachOpportunityOptions> OpportunityOptions { get; }

    public TestUserScope UserScope { get; }

    public CoachToolRegistry Registry { get; }

    public CoachOpportunityRecorder Recorder { get; }

    public CoachDbContext NewContext() => _persistence.NewContext();

    public CoachMessageStore NewMessageStore(CoachDbContext db) => _persistence.NewMessageStore(db);

    public CoachConversationStore NewConversationStore(CoachDbContext db) =>
        _persistence.NewConversationStore(db);

    public CoachTurnOperationStore NewTurnOperationStore(CoachDbContext db) =>
        _persistence.NewTurnOperationStore(db);

    /// <summary>
    /// The production service, on its own context, for the given owner.
    /// </summary>
    /// <remarks>
    /// The recorder is built per owner too, not shared. In production both the service and the
    /// recorder read the same ambient request scope, so a harness that reused one recorder across
    /// two owners would have written both learners' ledger rows under the first one — and the
    /// cross-learner rollup assertion would have been measuring the harness.
    /// </remarks>
    public CoachResponseReportService NewService(CoachDbContext db, string? userProfileId = null)
    {
        var scope = userProfileId is null ? UserScope : new TestUserScope(userProfileId);

        var recorder = userProfileId is null
            ? Recorder
            : new CoachOpportunityRecorder(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                scope,
                Registry,
                OpportunityOptions,
                Options,
                _persistence.Time,
                NullLogger<CoachOpportunityRecorder>.Instance);

        return new CoachResponseReportService(
            db,
            scope,
            NewTurnOperationStore(db),
            Registry,
            recorder,
            Options,
            _persistence.Time,
            NullLogger<CoachResponseReportService>.Instance);
    }

    public CoachResponseReportRetentionSweep NewRetentionSweep(CoachDbContext db) =>
        new(db, Options, _persistence.Time, NullLogger<CoachResponseReportRetentionSweep>.Instance);

    public CoachResponseReportDeletionContributor NewDeletionContributor(CoachDbContext db) =>
        new(db, NullLogger<CoachResponseReportDeletionContributor>.Instance);

    /// <summary>Every report row, oldest first.</summary>
    public async Task<List<CoachResponseReport>> RowsAsync()
    {
        await using var db = NewContext();
        return await db.CoachResponseReports
            .AsNoTracking()
            .OrderBy(row => row.ReportedAtUtc)
            .ThenBy(row => row.Id)
            .ToListAsync();
    }

    public async Task<List<CoachOpportunity>> OpportunitiesAsync()
    {
        await using var db = NewContext();
        return await db.CoachOpportunities
            .AsNoTracking()
            .OrderBy(row => row.FirstObservedAtUtc)
            .ThenBy(row => row.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Seeds one exchange: a conversation, a durable turn operation, the learner's message, and
    /// the coach's answer, correlated by the operation the way a real turn correlates them.
    /// </summary>
    public async Task<SeededTurn> SeedTurnAsync(
        string owner = "learner-a",
        string conversationId = "c-1",
        string operationId = "op-1",
        CoachMessageKind responseKind = CoachMessageKind.Text,
        bool correlate = true)
    {
        await using var db = NewContext();
        var conversations = NewConversationStore(db);
        var messages = NewMessageStore(db);

        var coachOwner = CoachOwner.ForUser(owner);

        var created = await conversations.CreateAsync(
            coachOwner,
            new CreateCoachConversationRequest(
                Title: "Grammar",
                TitleSource: CoachConversationTitleSource.Generated,
                TargetLanguageCode: "ko",
                ConversationId: conversationId));

        created.Status.Should().Be(CoachHistoryStatus.Success);

        var learner = await messages.AppendAsync(coachOwner, new AppendCoachMessageRequest(
            conversationId,
            CoachMessageRole.Learner,
            CoachMessageKind.Text,
            Payload("How do I use 은/는?"),
            correlate ? operationId : null));

        var response = await messages.AppendAsync(coachOwner, new AppendCoachMessageRequest(
            conversationId,
            CoachMessageRole.Coach,
            responseKind,
            Payload("은/는 marks the topic."),
            correlate ? operationId : null));

        learner.Status.Should().Be(CoachHistoryStatus.Success);
        response.Status.Should().Be(CoachHistoryStatus.Success);

        return new SeededTurn(
            conversationId,
            learner.Message!.Id,
            response.Message!.Id,
            operationId);
    }

    private static CoachMessagePayload Payload(string text) => new()
    {
        Kind = CoachMessagePayloadKind.CoachText,
        Text = text,
        CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
    };

    public void Dispose()
    {
        _provider.Dispose();
        _persistence.Dispose();
    }
}

/// <summary>One seeded exchange, by the identifiers a report request would carry.</summary>
internal readonly record struct SeededTurn(
    string ConversationId,
    string LearnerMessageId,
    string ResponseMessageId,
    string OperationId);
