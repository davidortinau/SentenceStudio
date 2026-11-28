using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Startup;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The feedback inbox and the agent token on PostgreSQL, the provider they run on: the three
/// server-only schemas come from their production migrations, and the real host reads and writes
/// them under the API's legacy timestamp switch. Skips without a scratch server.
/// </summary>
public sealed class AdminReportsPostgresTests : IAsyncLifetime
{
    private string? _database;
    private string _connectionString = default!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _database = await CoachPostgresServer.CreateDatabaseAsync("adminreports");
        _connectionString = CoachPostgresServer.ConnectionStringFor(_database);

        await using (var admin = new AdminDbContext(new DbContextOptionsBuilder<AdminDbContext>()
                         .UseNpgsql(_connectionString, n => n.MigrationsHistoryTable(AdminSchema.MigrationsHistoryTable)).Options))
        {
            await admin.Database.MigrateAsync();
        }

        await using (var coach = new CoachDbContext(new DbContextOptionsBuilder<CoachDbContext>()
                         .UseNpgsql(_connectionString, n => n.MigrationsHistoryTable(StartupDbContextFactory.CoachMigrationsHistoryTable)).Options))
        {
            await coach.Database.MigrateAsync();
        }

        await using var feedback = new FeedbackDbContext(new DbContextOptionsBuilder<FeedbackDbContext>()
            .UseNpgsql(_connectionString, n => n.MigrationsHistoryTable(FeedbackSchema.MigrationsHistoryTable)).Options);
        await feedback.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await CoachPostgresServer.DropDatabaseAsync(_database);
        }
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    [PostgresFact]
    public async Task The_coach_migration_adds_one_nullable_number_column_for_the_issue_link()
    {
        (await ScalarAsync<string>(
            "SELECT data_type || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_name = 'CoachOpportunity' AND column_name = 'LinkedIssueNumber'"))
            .Should().Be("integer:YES");

        (await ScalarAsync<string>(
            "SELECT \"MigrationId\" FROM \"__CoachMigrationsHistory\" ORDER BY 1 DESC LIMIT 1"))
            .Should().Be("20261116161852_AddCoachOpportunityLinkedIssueNumber");
    }

    [PostgresFact]
    public async Task The_inbox_and_the_token_work_end_to_end_on_PostgreSQL()
    {
        using var factory = new AdminApiFactory(_connectionString) { ReportsOnPostgres = true };
        var admin = await factory.SeedAdminAsync("captain@admin-tests.local");
        var learner = await factory.SeedAccountAsync("learner@admin-tests.local");
        var now = factory.Time.GetUtcNow().UtcDateTime;
        var reportId = await factory.SeedFlaggedAnswerAsync(learner, "the question", "the answer");
        await factory.WithFeedbackDbAsync(async db =>
        {
            db.FeedbackSubmissions.Add(new FeedbackSubmission
            {
                Jti = "jti-0000000000000001",
                UserProfileId = learner.UserProfileId!,
                Status = FeedbackSubmissionStatus.Submitted,
                ContentDigest = new string('d', 64),
                IssueNumber = 77,
                IssueUrl = "https://github.com/davidortinau/SentenceStudio/issues/77",
                IssueTitle = "A title",
                RouteCategory = FeedbackRouteCategory.Coach,
                Platform = FeedbackPlatform.Web,
                AppVersion = "1.0.0",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                TokenExpiresAtUtc = now.AddMinutes(10)
            });
            db.FeedbackScreenSnapshots.Add(new FeedbackScreenSnapshot
            {
                Reference = "fs-0123456789ab",
                UserProfileId = learner.UserProfileId!,
                Jti = "jti-0000000000000001",
                ContextJson = """{"route":"/sam"}""",
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync();
        });

        // The token: created, stored, used, counted and revoked on PostgreSQL.
        var token = await factory.IssueTokenAsync(
            admin, AdminTokenPermissionWire.Reports, AdminTokenPermissionWire.LearnerContent, AdminTokenPermissionWire.Triage);
        token.Row.ExpiresAtUtc.Should().Be(now.AddDays(30));
        using var agent = factory.CreateClientForToken(token.Token);

        var self = (await agent.GetFromJsonAsync<AdminTokenSelf>("/api/admin/token"))!;
        self.ExpiresAtUtc.Should().Be(now.AddDays(30));
        self.ExpiresAtUtc.Kind.Should().Be(DateTimeKind.Utc);

        var list = (await agent.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?attention=true"))!;
        list.Items.Should().ContainSingle().Which.LastFlaggedAtUtc.Should().Be(now);
        list.AttentionCount.Should().Be(1);

        var opened = (await (await agent.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null))
            .Content.ReadFromJsonAsync<AdminSamExchangeResponse>())!;
        opened.Exchanges.Should().ContainSingle().Which.Should().Be(new AdminSamExchange(now, "the question", "the answer"));
        opened.OpenedCount.Should().Be(1);

        var triaged = (await (await agent.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
                new AdminSamTriageRequest(1, AdminReportCodes.StatusAccepted, 412)))
            .Content.ReadFromJsonAsync<AdminSamReportRow>())!;
        triaged.Status.Should().Be(AdminReportCodes.StatusAccepted);
        triaged.IssueNumber.Should().Be(412);
        triaged.ReviewedAtUtc.Should().Be(now);
        triaged.ExchangeOpenedCount.Should().Be(1);
        triaged.ExchangeLastOpenedAtUtc.Should().Be(now);
        triaged.NeedsAttention.Should().BeFalse();

        var github = (await agent.GetFromJsonAsync<AdminGitHubReportList>("/api/admin/reports/github"))!;
        github.Items.Should().ContainSingle().Which.ScreenStateReference.Should().Be("fs-0123456789ab");
        github.Items[0].CreatedAtUtc.Should().Be(now);
        (await agent.PostAsync("/api/admin/reports/github/screen-states/fs-0123456789ab", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ScalarAsync<long>(
            "SELECT count(*) FROM \"CoachOpportunity\" WHERE \"LinkedIssueNumber\" = 412 AND \"EvidenceRevealCount\" = 1 "
            + "AND \"EvidenceLastRevealedAtUtc\" = TIMESTAMPTZ '2026-11-15 12:00:00+00'")).Should().Be(1);
        (await ScalarAsync<long>(
            "SELECT count(*) FROM \"AdminAgentToken\" WHERE \"LastUsedAtUtc\" = TIMESTAMPTZ '2026-11-15 12:00:00+00' "
            + "AND \"ExpiresAtUtc\" = TIMESTAMPTZ '2026-12-15 12:00:00+00'")).Should().Be(1);

        using var adminClient = factory.CreateClientFor(admin);
        var tokens = (await adminClient.GetFromJsonAsync<AdminTokenList>("/api/admin/tokens"))!;
        tokens.Items.Should().ContainSingle().Which.LastUsedAtUtc.Should().Be(now);
        (await adminClient.PostAsync($"/api/admin/tokens/{token.Row.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await agent.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Every one of those requests is in the audit log, by actor.
        var audit = await factory.ReadAuditAsync();
        audit.Where(r => r.ActorKind == AdminActorKind.AgentToken).Select(r => r.Action).Should().Equal(
            AdminActions.TokenSelfRead, AdminActions.ReportsSamList, AdminActions.ReportsSamExchangeRead,
            AdminActions.ReportsSamTriage, AdminActions.ReportsGitHubList, AdminActions.ReportsScreenStateRead);
        audit.Should().OnlyContain(r => r.Outcome == AdminAuditOutcome.Succeeded);

        // A later flag on the same report: its status stays, and it needs attention again. The
        // comparison of the two stored times runs in PostgreSQL.
        factory.Time.Advance(TimeSpan.FromMinutes(30));
        await factory.SeedFlaggedAnswerAsync(learner, "later question", "later answer", addToReportId: reportId);
        var again = (await adminClient.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?attention=true"))!;
        again.AttentionCount.Should().Be(1);
        again.Items.Should().ContainSingle().Which.Should().Match<AdminSamReportRow>(
            r => r.Status == AdminReportCodes.StatusAccepted && r.NeedsAttention && r.FlagCount == 2);
    }

    [PostgresFact]
    public async Task The_counts_are_grouped_summed_and_windowed_by_PostgreSQL()
    {
        using var factory = new AdminApiFactory(_connectionString) { ReportsOnPostgres = true };
        var admin = await factory.SeedAdminAsync();
        var a = await factory.SeedAccountAsync();
        var b = await factory.SeedAccountAsync();
        var now = factory.Time.GetUtcNow().UtcDateTime;
        await factory.SeedFlaggedAnswerAsync(a, "the question", "the answer");
        await factory.WithCoachDbAsync(async db =>
        {
            CoachOpportunity Counter(ApplicationUser learner, string code, int count, DateTime at) => new()
            {
                Id = Guid.NewGuid().ToString("n"),
                UserProfileId = learner.UserProfileId!,
                Kind = (CoachOpportunityKind)11,
                Disposition = CoachOpportunityDisposition.AggregateOnly,
                Surface = CoachOpportunitySurface.TurnOutcome,
                CapabilityCode = code,
                Fingerprint = Guid.NewGuid().ToString("n"),
                DedupBucketDate = DateOnly.FromDateTime(at),
                OccurrenceCount = count,
                FirstObservedAtUtc = at,
                LastObservedAtUtc = at
            };

            db.CoachOpportunities.AddRange(
                Counter(a, "empty_announcement_second_reply_held_content", 3, now.AddDays(-2)),
                Counter(a, "empty_announcement_second_reply_held_content", 2, now.AddHours(-1)),
                Counter(b, "empty_announcement_second_reply_held_content", 4, now),
                Counter(b, "empty_announcement_second_reply_empty", 1, now.AddDays(-2)));
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClientFor(admin);
        using var everything = JsonDocument.Parse(await client.GetStringAsync("/api/admin/reports/sam/counts"));
        using var sinceToday = JsonDocument.Parse(
            await client.GetStringAsync($"/api/admin/reports/sam/counts?since={now:yyyy-MM-dd}"));

        var lines = everything.RootElement.GetProperty("items");
        lines.GetArrayLength().Should().Be(2);
        lines[0].GetProperty("kind").GetString().Should().Be("empty_announcement_retried");
        lines[0].GetProperty("capabilityCode").GetString().Should().Be("empty_announcement_second_reply_held_content");
        lines[0].GetProperty("count").GetInt64().Should().Be(9);
        lines[0].GetProperty("learners").GetInt32().Should().Be(2);
        lines[0].GetProperty("firstObservedAtUtc").GetDateTime().Should().Be(now.AddDays(-2));
        lines[0].GetProperty("lastObservedAtUtc").GetDateTime().Should().Be(now);
        lines[1].GetProperty("count").GetInt64().Should().Be(1);

        var today = sinceToday.RootElement.GetProperty("items");
        today.GetArrayLength().Should().Be(1);
        today[0].GetProperty("count").GetInt64().Should().Be(6);
        today[0].GetProperty("learners").GetInt32().Should().Be(2);
    }
}
