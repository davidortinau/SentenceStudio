using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Learner reports against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite tests cover provider-independent behaviour. These prove the parts a relational
/// stand-in would let pass: the migration applies, the uniqueness of a report is enforced by the
/// <em>database</em> rather than by a read-then-write in one process, and two instances racing on
/// the same response produce exactly one row with one winner.
/// </para>
/// <para>
/// The race is the reason this file exists. A pre-check on its own is not idempotency, it is a
/// window; only the unique index closes it, and only a real server can be asked whether it does.
/// </para>
/// </remarks>
public class CoachResponseReportPostgresTests
{
    [PostgresFact]
    public async Task TheMigrationCreatesTheTableAndItsIndexes()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-schema");

        var columns = await harness.StringsAsync(
            "select column_name from information_schema.columns " +
            "where table_name = 'CoachResponseReport' order by column_name;");

        columns.Should().Contain("CoachMessageId");
        columns.Should().Contain("RequestMessageId");
        columns.Should().Contain("Reason");
        columns.Should().Contain("StopReason");
        columns.Should().Contain("InvokedToolNames");
        columns.Should().Contain("OpportunityId");

        // The privacy claim, checked against the shipped schema rather than the model.
        columns.Should().NotContain(column =>
            column.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || column.Contains("Protected", StringComparison.OrdinalIgnoreCase)
            || column.Equals("Text", StringComparison.OrdinalIgnoreCase));

        var indexes = await harness.StringsAsync(
            "select indexname from pg_indexes where tablename = 'CoachResponseReport' order by indexname;");

        indexes.Should().Contain("IX_CoachResponseReport_UserProfileId_CoachMessageId");
        indexes.Should().Contain("IX_CoachResponseReport_UserProfileId_ConversationId");
        indexes.Should().Contain("IX_CoachResponseReport_ReportedAtUtc");
    }

    [PostgresFact]
    public async Task TheOneReportPerResponseRuleIsTheDatabasesRuleNotTheApplications()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-unique");

        var unique = await harness.StringsAsync(
            "select c.relname from pg_index i " +
            "join pg_class c on c.oid = i.indexrelid " +
            "where i.indisunique " +
            "and c.relname = 'IX_CoachResponseReport_UserProfileId_CoachMessageId';");

        unique.Should().ContainSingle(
            "a pre-check on its own is a window, not a guarantee; the index is what closes it");
    }

    [PostgresFact]
    public async Task TheTimestampColumnIsPinnedToTimestampTz()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-types");

        var type = await harness.ScalarAsync<string>(
            "select data_type from information_schema.columns " +
            "where table_name = 'CoachResponseReport' and column_name = 'ReportedAtUtc';");

        type.Should().Be("timestamp with time zone");
    }

    // ---------------------------------------------------------------- the race

    [PostgresFact]
    public async Task TwoInstancesReportingTheSameResponseProduceOneRowAndOneWinner()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-race");
        var turn = await SeedTurnAsync(harness);

        // Two contexts on two connections: that is what makes this a race rather than two objects
        // taking turns on one handle.
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();

        var a = NewService(harness, first);
        var b = NewService(harness, second);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var left = Task.Run(async () =>
        {
            await gate.Task;
            return await a.ReportAsync(turn.ConversationId, turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = CoachResponseReportReason.Confusing });
        });

        var right = Task.Run(async () =>
        {
            await gate.Task;
            return await b.ReportAsync(turn.ConversationId, turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = CoachResponseReportReason.Other });
        });

        gate.SetResult();
        var results = await Task.WhenAll(left, right);

        results.Should().OnlyContain(result => result.IsOk,
            "the learner's intent was satisfied on both sides; only one of them wrote the row");

        results.Count(r => r.Value!.State == CoachResponseReportState.Recorded)
            .Should().Be(1, "exactly one request may claim to have recorded it");

        await using var read = harness.NewContext();
        var rows = await read.CoachResponseReports.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle();

        // Both sides report the same reason: the one that actually landed.
        results.Select(r => r.Value!.Reason).Distinct().Should().ContainSingle()
            .Which.Should().Be(rows[0].Reason);
    }

    [PostgresFact]
    public async Task AReportSurvivesTheProcessThatWroteIt()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-durable");
        var turn = await SeedTurnAsync(harness);

        await using (var writing = harness.NewContext())
        {
            await NewService(harness, writing).ReportAsync(
                turn.ConversationId,
                turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = CoachResponseReportReason.DidNotAnswer });
        }

        // A fresh context is the closest a test gets to a fresh circuit: nothing the first one
        // held is available to the second.
        await using var reading = harness.NewContext();
        var listed = await NewService(harness, reading).ListReportedAsync(turn.ConversationId);

        listed.Value!.MessageIds.Should().ContainSingle()
            .Which.Should().Be(turn.ResponseMessageId,
                "'Reported for review' has to survive a reload, and only the server can say so");
    }

    [PostgresFact]
    public async Task ErasureTakesTheReportsWithTheRestOfTheAccount()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-erasure");
        var turn = await SeedTurnAsync(harness);

        await using var db = harness.NewContext();
        await NewService(harness, db).ReportAsync(
            turn.ConversationId,
            turn.ResponseMessageId,
            new CoachResponseReportRequest { Reason = CoachResponseReportReason.Other });

        var contributor = new CoachResponseReportDeletionContributor(
            db, NullLogger<CoachResponseReportDeletionContributor>.Instance);

        var deleted = await contributor.DeleteAllAsync(CoachOwner.ForUser("learner-a"));

        deleted.Should().Be(1);

        await using var read = harness.NewContext();
        (await read.CoachResponseReports.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- a notice is a response

    /// <summary>
    /// The notice case against the real server: it reports, it pairs to its own request, and the
    /// row survives the process that wrote it.
    /// </summary>
    /// <remarks>
    /// The kind is stored as an ordinal, and <c>ResponseKind</c> is read back here rather than
    /// trusted from the object that wrote it — a mapping mistake between the enum and the column
    /// would otherwise show up first as a reviewer being told a notice was ordinary prose.
    /// </remarks>
    [PostgresFact]
    public async Task ANoticeIsReportedAndPairedOnTheRealServer()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-notice");
        var turn = await SeedTurnAsync(
            harness,
            responseKind: CoachMessageKind.Notice,
            responseText: "There is no plan for today yet, so there is nothing to change.");

        await using (var writing = harness.NewContext())
        {
            var result = await NewService(harness, writing).ReportAsync(
                turn.ConversationId,
                turn.ResponseMessageId,
                new CoachResponseReportRequest
                {
                    Reason = CoachResponseReportReason.ExpectedAppAction
                });

            result.IsOk.Should().BeTrue();
            result.Value!.State.Should().Be(CoachResponseReportState.Recorded);
        }

        await using var read = harness.NewContext();
        var row = await read.CoachResponseReports.AsNoTracking().SingleAsync();

        row.CoachMessageId.Should().Be(turn.ResponseMessageId);
        row.RequestMessageId.Should().Be(turn.LearnerMessageId,
            "the request is named by the turn correlation, not by whatever sat above it");
        row.ResponseKind.Should().Be(CoachMessageKind.Notice);
        row.Reason.Should().Be(CoachResponseReportReason.ExpectedAppAction);

        var listed = await NewService(harness, read).ListReportedAsync(turn.ConversationId);
        listed.Value!.MessageIds.Should().ContainSingle().Which.Should().Be(turn.ResponseMessageId,
            "the notice has to come back as reported on reload like any other response");
    }

    /// <summary>
    /// Two instances reporting the same notice still leave one row, enforced by the index.
    /// </summary>
    [PostgresFact]
    public async Task TwoInstancesReportingTheSameNoticeProduceOneRow()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-notice-race");
        var turn = await SeedTurnAsync(harness, responseKind: CoachMessageKind.Notice);

        await using var first = harness.NewContext();
        await using var second = harness.NewContext();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var left = Task.Run(async () =>
        {
            await gate.Task;
            return await NewService(harness, first).ReportAsync(
                turn.ConversationId, turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = CoachResponseReportReason.Confusing });
        });

        var right = Task.Run(async () =>
        {
            await gate.Task;
            return await NewService(harness, second).ReportAsync(
                turn.ConversationId, turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = CoachResponseReportReason.Other });
        });

        gate.SetResult();
        var results = await Task.WhenAll(left, right);

        results.Should().OnlyContain(result => result.IsOk);
        results.Count(r => r.Value!.State == CoachResponseReportState.Recorded).Should().Be(1);

        await using var read = harness.NewContext();
        (await read.CoachResponseReports.AsNoTracking().ToListAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// A receipt is refused by the real server too, so the exclusion is not a client preference.
    /// </summary>
    [PostgresFact]
    public async Task AReceiptIsRefusedOnTheRealServer()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("report-receipt");
        var turn = await SeedTurnAsync(harness, responseKind: CoachMessageKind.Receipt);

        await using var db = harness.NewContext();
        var result = await NewService(harness, db).ReportAsync(
            turn.ConversationId,
            turn.ResponseMessageId,
            new CoachResponseReportRequest { Reason = CoachResponseReportReason.Other });

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);

        await using var read = harness.NewContext();
        (await read.CoachResponseReports.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private static CoachResponseReportService NewService(
        CoachPostgresHarness harness,
        CoachDbContext db,
        string userProfileId = "learner-a")
    {
        var services = new ServiceCollection();
        services.AddSingleton(harness.DbOptions);
        services.AddScoped<CoachDbContext>(sp =>
            new CoachDbContext(sp.GetRequiredService<DbContextOptions<CoachDbContext>>()));

        var provider = services.BuildServiceProvider();

        var registry = new CoachToolRegistry(new CoachOptions
        {
            Enabled = true,
            DurableHistory = new CoachFeatureSwitch { Enabled = true },
            SamOverlay = new CoachFeatureSwitch { Enabled = true },
            SamReadTools = new CoachFeatureSwitch { Enabled = true },
            SamWriteTools = new CoachFeatureSwitch { Enabled = true }
        });

        var scope = new TestUserScope(userProfileId);
        var reportOptions = new TestOptionsMonitor<CoachResponseReportOptions>(
            new CoachResponseReportOptions { Enabled = true });

        var recorder = new CoachOpportunityRecorder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            scope,
            registry,
            new TestOptionsMonitor<CoachOpportunityOptions>(
                new CoachOpportunityOptions { Enabled = true }),
            reportOptions,
            harness.Time,
            NullLogger<CoachOpportunityRecorder>.Instance);

        return new CoachResponseReportService(
            db,
            scope,
            harness.NewTurnOperationStore(db),
            registry,
            recorder,
            reportOptions,
            harness.Time,
            NullLogger<CoachResponseReportService>.Instance);
    }

    private static async Task<(string ConversationId, string ResponseMessageId, string LearnerMessageId)> SeedTurnAsync(
        CoachPostgresHarness harness,
        string owner = "learner-a",
        string conversationId = "c-1",
        string operationId = "op-1",
        CoachMessageKind responseKind = CoachMessageKind.Text,
        string responseText = "은/는 marks the topic.")
    {
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var coachOwner = CoachOwner.ForUser(owner);

        await conversations.CreateAsync(coachOwner, new CreateCoachConversationRequest(
            Title: "Grammar",
            TitleSource: CoachConversationTitleSource.Generated,
            TargetLanguageCode: "ko",
            ConversationId: conversationId));

        var learner = await messages.AppendAsync(coachOwner, new AppendCoachMessageRequest(
            conversationId, CoachMessageRole.Learner, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.LearnerText,
                Text = "How do I use 은/는?",
                CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
            },
            operationId));

        var response = await messages.AppendAsync(coachOwner, new AppendCoachMessageRequest(
            conversationId, CoachMessageRole.Coach, responseKind,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.CoachText,
                Text = responseText,
                CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 1, DateTimeKind.Utc)
            },
            operationId));

        return (conversationId, response.Message!.Id, learner.Message!.Id);
    }
}
