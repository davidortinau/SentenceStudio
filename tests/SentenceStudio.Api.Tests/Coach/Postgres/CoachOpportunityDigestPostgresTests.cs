using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Digest;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The production reviewer path against the provider it actually runs on.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite tests cover the digest's shape, its window, and its privacy guarantee. They cannot
/// cover the part that decides whether the reviewer path works at all in Production: whether
/// <c>GROUP BY</c> with a nested <c>Select(...).Distinct().Count()</c> translates on Npgsql. SQLite
/// is forgiving about aggregate shapes PostgreSQL is not, so a digest that passes there and throws
/// a translation exception in Production would be discovered by the reviewer, once, on the Monday
/// they needed it.
/// </para>
/// <para>
/// These also re-prove the privacy claim against the emitted PostgreSQL rather than the SQLite
/// dialect — the projection is the same expression tree, but the SQL a reviewer would have to
/// audit is not.
/// </para>
/// </remarks>
public class CoachOpportunityDigestPostgresTests
{
    private const string OwnerA = "learner-alpha-pg";
    private const string OwnerB = "learner-beta-pg";
    private const string ConversationId = "conversation-pg-secret";
    private const string CoachMessageId = "message-coach-pg-secret";
    private const string LearnerMessageId = "message-learner-pg-secret";

    [PostgresFact]
    public async Task TheDigestAggregatesOnTheRealProvider()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("digest-aggregate");

        await SeedAsync(harness);

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        var line = digest.Lines.Should().ContainSingle(entry =>
            entry.CapabilityCode == CoachOpportunityCapabilityCodes.LearnerReportedIncorrect)
            .Subject;

        line.TotalOccurrences.Should().Be(4);
        line.DistinctLearners.Should().Be(2,
            "COUNT(DISTINCT \"UserProfileId\") is the only path from this reader to the column " +
            "that names a person, and it has to translate on the provider Production runs on");
        line.RowCount.Should().Be(2);
        line.Statuses.Should().BeEquivalentTo(
            [nameof(CoachOpportunityStatus.New), nameof(CoachOpportunityStatus.Reviewed)]);

        digest.ReportReasons.Should().ContainSingle()
            .Which.ReportCount.Should().Be(2);
        digest.TotalReports.Should().Be(2);
    }

    [PostgresFact]
    public async Task TheWindowBoundTranslatesAgainstTimestampTz()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("digest-window");

        await SeedAsync(harness);

        await using var db = harness.NewContext();

        var digest = await new CoachOpportunityDigestReader(db, harness.Time)
            .ReadAsync(harness.Time.GetUtcNow().UtcDateTime.AddDays(-2));

        var line = digest.Lines.Should().ContainSingle(entry =>
            entry.CapabilityCode == CoachOpportunityCapabilityCodes.LearnerReportedIncorrect)
            .Subject;

        line.RowCount.Should().Be(1, "the three-day-old bucket sits outside the bound");
        line.Statuses.Should().BeEquivalentTo([nameof(CoachOpportunityStatus.Reviewed)]);
    }

    [PostgresFact]
    public async Task TheEmittedPostgresSqlNamesNoIdentifierColumn()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("digest-sql");

        await using var db = harness.NewContext();

        var queries = new CoachOpportunityDigestReader(db, harness.Time)
            .DescribeQueries(harness.Time.GetUtcNow().UtcDateTime.AddDays(-7));

        string[] forbidden =
        [
            "ConversationId", "TurnId", "TurnOperationId", "WriteOperationId",
            "EvidenceMessageId", "EvidenceOfferMessageId", "RequestMessageId", "CoachMessageId"
        ];

        foreach (var sql in queries)
        {
            foreach (var column in forbidden)
            {
                sql.Should().NotContain(column,
                    "the SQL a reviewer would have to audit is the PostgreSQL one, not the " +
                    "SQLite dialect the unit tests emit");
            }
        }
    }

    [PostgresFact]
    public async Task TheRenderedDigestLeaksNoSeededIdentifier()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("digest-leak");

        await SeedAsync(harness);

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        var markdown = CoachOpportunityDigestMarkdown.Render(digest);
        var json = CoachOpportunityDigestJson.Serialize(digest);

        foreach (var identifier in new[]
                 {
                     OwnerA, OwnerB, ConversationId, CoachMessageId, LearnerMessageId
                 })
        {
            markdown.Should().NotContain(identifier);
            json.Should().NotContain(identifier);
        }

        digest.Lines.Should().NotBeEmpty("the assertion above must not be passing on an empty digest");
    }

    [PostgresFact]
    public async Task TheDigestSessionIsReadOnlyAtTheServer()
    {
        await using var harness = await CoachPostgresHarness.CreateAsync("digest-readonly");

        var readOnly = CoachOpportunityDigestConnection.ForReadOnly(
            harness.ConnectionString, "sam-opportunity-digest-test");

        await using var connection = new NpgsqlConnection(readOnly);
        await connection.OpenAsync();

        await using (var show = new NpgsqlCommand("SHOW transaction_read_only;", connection))
        {
            var value = (string?)await show.ExecuteScalarAsync();

            value.Should().Be("on",
                "the startup option has to reach the server — a SET issued once on one pooled " +
                "connection would leave every other one writable");
        }

        await using var write = new NpgsqlCommand(
            """INSERT INTO "CoachOpportunity" ("Id") VALUES ('should-not-exist');""",
            connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());

        refused.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction,
            "a write from the reviewer path must be refused by PostgreSQL, not by this " +
            "program's good intentions");
    }

    [Theory]
    [InlineData(null, "-c default_transaction_read_only=on")]
    [InlineData("", "-c default_transaction_read_only=on")]
    [InlineData("-c search_path=coach", "-c search_path=coach -c default_transaction_read_only=on")]
    public void TheReadOnlyOptionIsAppendedRatherThanAssigned(string? existing, string expected) =>
        CoachOpportunityDigestConnection.AppendReadOnly(existing).Should().Be(expected,
            "a deployment that already passes startup options must keep them");

    private static async Task SeedAsync(CoachPostgresHarness harness)
    {
        var now = harness.Time.GetUtcNow().UtcDateTime;

        await using var db = harness.NewContext();

        db.CoachOpportunities.AddRange(
            Row("row-a", OwnerA, now.AddDays(-3), 3, CoachOpportunityStatus.New),
            Row("row-b", OwnerB, now.AddDays(-1), 1, CoachOpportunityStatus.Reviewed));

        db.CoachResponseReports.AddRange(
            Report("report-a", OwnerA, CoachMessageId, "row-a", now.AddDays(-3)),
            Report("report-b", OwnerB, CoachMessageId, "row-b", now.AddDays(-1)));

        await db.SaveChangesAsync();
    }

    private static CoachOpportunity Row(
        string id,
        string owner,
        DateTime observedAtUtc,
        int occurrenceCount,
        CoachOpportunityStatus status) => new()
        {
            Id = id,
            UserProfileId = owner,
            ConversationId = ConversationId,
            EvidenceMessageId = LearnerMessageId,
            EvidenceMessageSequence = 1,
            EvidenceOfferMessageId = CoachMessageId,
            EvidenceOfferMessageSequence = 2,
            Kind = CoachOpportunityKind.UserReportedResponse,
            Disposition = CoachOpportunityDisposition.Product,
            Surface = CoachOpportunitySurface.TurnOutcome,
            CapabilityCode = CoachOpportunityCapabilityCodes.LearnerReportedIncorrect,
            OfferLink = CoachOpportunityOfferLink.None,
            Fingerprint = "fingerprint-reported-incorrect",
            DedupBucketDate = DateOnly.FromDateTime(observedAtUtc),
            OccurrenceCount = occurrenceCount,
            FirstObservedAtUtc = observedAtUtc,
            LastObservedAtUtc = observedAtUtc,
            Status = status
        };

    private static CoachResponseReport Report(
        string id,
        string owner,
        string coachMessageId,
        string opportunityId,
        DateTime reportedAtUtc) => new()
        {
            Id = id,
            UserProfileId = owner,
            ConversationId = ConversationId,
            CoachMessageId = coachMessageId,
            CoachMessageSequence = 2,
            RequestMessageId = LearnerMessageId,
            RequestMessageSequence = 1,
            Reason = CoachResponseReportReason.IncorrectOrMisleading,
            ResponseKind = CoachMessageKind.Text,
            OpportunityId = opportunityId,
            ReportedAtUtc = reportedAtUtc
        };
}
