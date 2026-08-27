using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Digest;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The production reviewer path, and the guarantee that makes it shippable.
/// </summary>
/// <remarks>
/// <para>
/// The operator review surface can decrypt learner messages and is Development-only, so Production
/// reads this digest instead. Everything about that trade rests on one claim — the digest cannot
/// carry anything that identifies a learner — and these tests are where that claim is established
/// rather than asserted in a comment.
/// </para>
/// <para>
/// Three independent proofs, because each one covers a way the others can be fooled: the declared
/// shape (a member that could hold content), the emitted SQL (a column that could be projected),
/// and the rendered output (a value that could reach a reviewer's screen anyway).
/// </para>
/// </remarks>
public class CoachOpportunityDigestTests
{
    private const string OwnerA = "learner-alpha-0001";
    private const string OwnerB = "learner-beta-0002";
    private const string ConversationId = "conversation-secret-9f1";
    private const string LearnerMessageId = "message-learner-secret-4c2";
    private const string CoachMessageId = "message-coach-secret-7d3";
    private const string TurnOperationId = "operation-secret-2a8";
    private const string WriteOperationId = "write-secret-6b4";

    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- shape

    /// <summary>
    /// Substrings that name a place learner content could live.
    /// </summary>
    /// <remarks>
    /// The same deliberately broad list <c>CoachOpportunityShapeTests</c> uses. A false positive
    /// costs somebody a rename; a false negative costs a learner's sentence appearing in a CI
    /// artifact that gets pasted into an issue.
    /// </remarks>
    private static readonly string[] ForbiddenNameFragments =
    [
        "payload", "text", "content", "message", "prompt", "response", "answer", "transcript",
        "term", "word", "phrase", "email", "secret", "token", "argument", "arg", "note",
        "comment", "detail", "description", "summary", "title", "body", "raw", "value",
        "owner", "user", "profile", "conversation", "learnerid", "id"
    ];

    /// <summary>
    /// Members whose names trip a fragment but are provably closed codes or counts.
    /// </summary>
    /// <remarks>
    /// Listed by exact name so that adding <c>ReasonText</c> beside <c>Reason</c>, or
    /// <c>LearnerIds</c> beside <c>DistinctLearners</c>, still fails.
    /// </remarks>
    private static readonly HashSet<string> AllowedMembers = new(StringComparer.Ordinal)
    {
        // A closed enum name from CoachResponseReportReason. Five values, all server-owned.
        nameof(CoachOpportunityDigestReasonLine.Reason),

        // Counts. The strongest statement the digest can make about a person is a number.
        nameof(CoachOpportunityDigestLine.DistinctLearners),
        nameof(CoachOpportunityDigestReasonLine.DistinctLearners)
    };

    [Theory]
    [InlineData(typeof(CoachOpportunityDigest))]
    [InlineData(typeof(CoachOpportunityDigestLine))]
    [InlineData(typeof(CoachOpportunityDigestReasonLine))]
    public void NoDigestShapeCarriesAContentOrIdentityMember(Type type)
    {
        var offenders = new List<string>();

        foreach (var member in type
                     .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m is PropertyInfo or FieldInfo))
        {
            if (AllowedMembers.Contains(member.Name)
                || member.Name.StartsWith('<')
                || member.Name is "EqualityContract")
            {
                continue;
            }

            foreach (var fragment in ForbiddenNameFragments)
            {
                if (member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{type.Name}.{member.Name} (matched '{fragment}')");
                    break;
                }
            }
        }

        offenders.Should().BeEmpty(
            "the digest is read outside the request pipeline that enforces owner scope, so a " +
            "member able to hold an identifier is a member able to leak one into a CI artifact");
    }

    [Theory]
    [InlineData(typeof(CoachOpportunityDigest))]
    [InlineData(typeof(CoachOpportunityDigestLine))]
    [InlineData(typeof(CoachOpportunityDigestReasonLine))]
    public void EveryDigestMemberIsABoundedPrimitiveOrAListOfClosedCodes(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propertyType = property.PropertyType;

            var allowed = propertyType == typeof(string)
                          || propertyType == typeof(int)
                          || propertyType == typeof(bool)
                          || propertyType == typeof(DateTime)
                          || propertyType == typeof(DateTime?)
                          || propertyType == typeof(IReadOnlyList<string>)
                          || propertyType == typeof(IReadOnlyList<CoachOpportunityDigestLine>)
                          || propertyType == typeof(IReadOnlyList<CoachOpportunityDigestReasonLine>);

            allowed.Should().BeTrue(
                $"{type.Name}.{property.Name} is typed {propertyType.Name}; anything richer on " +
                "this shape is a payload by another name");
        }
    }

    // ---------------------------------------------------------------- emitted SQL

    /// <summary>
    /// Columns that could address a learner, a conversation, a message, or an operation.
    /// </summary>
    private static readonly string[] ForbiddenColumns =
    [
        "ConversationId", "TurnId", "TurnOperationId", "WriteOperationId",
        "EvidenceMessageId", "EvidenceOfferMessageId", "RelatedOpportunityId",
        "RequestMessageId", "CoachMessageId", "TenantId", "LinkedSpecPath"
    ];

    [Fact]
    public async Task TheDigestQueriesProjectNoIdentifierColumn()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await using var db = harness.NewContext();

        var queries = new CoachOpportunityDigestReader(db, harness.Time)
            .DescribeQueries(Now.AddDays(-7));

        queries.Should().HaveCount(3);

        foreach (var sql in queries)
        {
            foreach (var column in ForbiddenColumns)
            {
                sql.Should().NotContain(column,
                    $"'{column}' addresses a row, a person, or an artifact; the digest reports " +
                    "counts and closed codes and has no use for it");
            }
        }
    }

    [Fact]
    public async Task TheOnlyUseOfTheOwnerColumnIsADistinctCount()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await using var db = harness.NewContext();

        var queries = new CoachOpportunityDigestReader(db, harness.Time).DescribeQueries(null);

        foreach (var sql in queries)
        {
            var occurrences = CountOccurrences(sql, "UserProfileId");
            if (occurrences == 0)
            {
                continue;
            }

            // Every mention must sit inside a COUNT(DISTINCT ...). A provider is free to spell the
            // aggregate differently, so the assertion is that the column never appears without one
            // rather than that the SQL matches a fixed string.
            CountOccurrences(sql, "COUNT(DISTINCT").Should().BeGreaterThanOrEqualTo(1,
                "the owner column may be counted and never selected — a query that returns it " +
                "turns a product rollup into a cross-tenant read");

            occurrences.Should().Be(CountOccurrences(sql, "COUNT(DISTINCT"),
                "each mention of the owner column must be the argument of a distinct count");
        }
    }

    // ---------------------------------------------------------------- fixture output

    [Fact]
    public async Task TheRenderedDigestContainsNoSeededIdentifier()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await SeedAsync(harness);

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        var markdown = CoachOpportunityDigestMarkdown.Render(digest);
        var json = CoachOpportunityDigestJson.Serialize(digest);

        string[] identifiers =
        [
            OwnerA, OwnerB, ConversationId, LearnerMessageId, CoachMessageId,
            TurnOperationId, WriteOperationId
        ];

        foreach (var identifier in identifiers)
        {
            markdown.Should().NotContain(identifier,
                "the markdown is designed to be printed to a CI log and pasted into an issue");
            json.Should().NotContain(identifier,
                "the JSON is designed to be uploaded as a build artifact");
        }

        // The counts still arrived, so the assertion above is not passing on an empty digest.
        digest.Lines.Should().NotBeEmpty();
        digest.ReportReasons.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheMarkdownFixtureRendersTheExpectedShape()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await SeedAsync(harness);

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        var markdown = CoachOpportunityDigestMarkdown.Render(digest);

        markdown.Should().Contain("# Sam opportunity digest");
        markdown.Should().Contain("2026-08-21 12:00 UTC");
        markdown.Should().Contain("## Learner reports by reason");
        markdown.Should().Contain("`IncorrectOrMisleading`");
        markdown.Should().Contain("## Problems by frequency");
        markdown.Should().Contain(CoachOpportunityCapabilityCodes.LearnerReportedIncorrect);
        markdown.Should().Contain("coach-opportunity://",
            "the fingerprint is rendered in its described form so it can be pasted into the log");
        markdown.Should().NotContain("**Truncated:**");
    }

    [Fact]
    public async Task TheJsonFixtureIsCamelCaseAndCarriesTheCounts()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await SeedAsync(harness);

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        using var document = JsonDocument.Parse(CoachOpportunityDigestJson.Serialize(digest));
        var root = document.RootElement;

        root.GetProperty("totalReports").GetInt32().Should().Be(3);
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();
        root.GetProperty("windowStartUtc").ValueKind.Should().Be(JsonValueKind.Null);

        var line = root.GetProperty("lines").EnumerateArray().First();
        line.GetProperty("distinctLearners").GetInt32().Should().BeGreaterThan(0);
        line.GetProperty("kind").GetString().Should().NotBeNullOrWhiteSpace(
            "enums are rendered as names, so an artifact read a year later is still legible");

        var reason = root.GetProperty("reportReasons").EnumerateArray().First();
        reason.GetProperty("reason").GetString().Should().Be(
            nameof(CoachResponseReportReason.IncorrectOrMisleading));
    }

    // ---------------------------------------------------------------- query behaviour

    [Fact]
    public async Task TheDigestAggregatesOccurrencesAndCountsLearnersWithoutNamingThem()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await SeedAsync(harness);

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        var reported = digest.Lines.Single(line =>
            line.CapabilityCode == CoachOpportunityCapabilityCodes.LearnerReportedIncorrect);

        reported.TotalOccurrences.Should().Be(5, "two buckets carrying 3 and 2 occurrences");
        reported.DistinctLearners.Should().Be(2);
        reported.RowCount.Should().Be(2);
        reported.Kind.Should().Be(nameof(CoachOpportunityKind.UserReportedResponse));
        reported.FirstObservedAtUtc.Should().Be(Now.AddDays(-3));
        reported.LastObservedAtUtc.Should().Be(Now.AddDays(-1));

        digest.TotalOpportunityRows.Should().Be(3, "two report buckets plus one automatic row");
        digest.Lines.Should().BeInDescendingOrder(line => line.TotalOccurrences);
    }

    [Fact]
    public async Task TheDigestReportsTheDistinctStatusesForAProblem()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await SeedAsync(harness);

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        var reported = digest.Lines.Single(line =>
            line.CapabilityCode == CoachOpportunityCapabilityCodes.LearnerReportedIncorrect);

        reported.Statuses.Should().BeEquivalentTo(
            [nameof(CoachOpportunityStatus.New), nameof(CoachOpportunityStatus.Reviewed)],
            "a line spans several learners, so what has been decided is a set rather than a value");
    }

    [Fact]
    public async Task TheWindowExcludesEverythingOlderThanItsBound()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await SeedAsync(harness);

        await using var db = harness.NewContext();

        var digest = await new CoachOpportunityDigestReader(db, harness.Time)
            .ReadAsync(Now.AddDays(-2));

        digest.WindowStartUtc.Should().Be(Now.AddDays(-2));
        digest.WindowEndUtc.Should().Be(Now);

        var reported = digest.Lines.Single(line =>
            line.CapabilityCode == CoachOpportunityCapabilityCodes.LearnerReportedIncorrect);

        reported.TotalOccurrences.Should().Be(2, "the three-day-old bucket is outside the window");
        reported.RowCount.Should().Be(1);
        reported.DistinctLearners.Should().Be(1);

        reported.Statuses.Should().BeEquivalentTo([nameof(CoachOpportunityStatus.Reviewed)],
            "a status belonging to a bucket outside the window would invert the decision the " +
            "window was opened to support");

        digest.TotalReports.Should().Be(1,
            "two of the three reports were filed three days ago, outside the two-day bound");
    }

    [Fact]
    public async Task AnEmptyLedgerRendersAnHonestDigestRatherThanNothing()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));
        await using var db = harness.NewContext();

        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        digest.Lines.Should().BeEmpty();
        digest.ReportReasons.Should().BeEmpty();
        digest.TotalReports.Should().Be(0);

        var markdown = CoachOpportunityDigestMarkdown.Render(digest);

        markdown.Should().Contain("not evidence of an absence of problems",
            "an empty digest is the exact shape a missed signal takes, so it says so");
    }

    [Fact]
    public async Task ADigestLargerThanTheCapSaysSoRatherThanTruncatingSilently()
    {
        using var harness = new CoachOpportunityHarness(new DateTimeOffset(Now));

        await using (var seed = harness.NewContext())
        {
            for (var i = 0; i <= CoachOpportunityDigestReader.MaxLines; i++)
            {
                seed.CoachOpportunities.Add(Row(
                    id: $"row-{i}",
                    owner: OwnerA,
                    fingerprint: $"fingerprint-{i:D4}",
                    capabilityCode: CoachOpportunityCapabilityCodes.WriteToolsDisabled,
                    observedAtUtc: Now.AddHours(-1)));
            }

            await seed.SaveChangesAsync();
        }

        await using var db = harness.NewContext();
        var digest = await new CoachOpportunityDigestReader(db, harness.Time).ReadAsync(null);

        digest.Lines.Should().HaveCount(CoachOpportunityDigestReader.MaxLines);
        digest.Truncated.Should().BeTrue();

        CoachOpportunityDigestMarkdown.Render(digest).Should().Contain("**Truncated:**",
            "a digest that quietly dropped the tail lets a reviewer read 'these are the problems' " +
            "when it meant 'these are the first five hundred'");
    }

    // ---------------------------------------------------------------- no HTTP surface

    [Fact]
    public void TheDigestIsNotReachableOverHttp()
    {
        var endpointSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Api", "Coach", "Opportunities", "Endpoints",
            "CoachOpportunityOperatorEndpoints.cs"));

        endpointSource.Should().NotContain("Digest",
            "the digest is an out-of-band operator tool; an admin-shaped HTTP route would need " +
            "an authorization primitive this codebase does not have");

        var programSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Api", "Program.cs"));

        programSource.Should().NotContain("CoachOpportunityDigest",
            "nothing in the request pipeline resolves the digest reader");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Two report buckets for the same problem under two learners, one automatic row, and the
    /// three report rows that raised them — all carrying identifiers the output must not echo.
    /// </summary>
    private static async Task SeedAsync(CoachOpportunityHarness harness)
    {
        await using var db = harness.NewContext();

        db.CoachOpportunities.AddRange(
            Row(
                id: "opportunity-report-a",
                owner: OwnerA,
                fingerprint: "fingerprint-reported-incorrect",
                capabilityCode: CoachOpportunityCapabilityCodes.LearnerReportedIncorrect,
                observedAtUtc: Now.AddDays(-3),
                occurrenceCount: 3,
                kind: CoachOpportunityKind.UserReportedResponse,
                status: CoachOpportunityStatus.New,
                withPointers: true),
            Row(
                id: "opportunity-report-b",
                owner: OwnerB,
                fingerprint: "fingerprint-reported-incorrect",
                capabilityCode: CoachOpportunityCapabilityCodes.LearnerReportedIncorrect,
                observedAtUtc: Now.AddDays(-1),
                occurrenceCount: 2,
                kind: CoachOpportunityKind.UserReportedResponse,
                status: CoachOpportunityStatus.Reviewed,
                withPointers: true),
            Row(
                id: "opportunity-automatic",
                owner: OwnerA,
                fingerprint: "fingerprint-write-tools-disabled",
                capabilityCode: CoachOpportunityCapabilityCodes.WriteToolsDisabled,
                observedAtUtc: Now.AddDays(-2),
                occurrenceCount: 1));

        db.CoachResponseReports.AddRange(
            Report("report-1", OwnerA, CoachMessageId, "opportunity-report-a", Now.AddDays(-3)),
            Report("report-2", OwnerA, $"{CoachMessageId}-second", "opportunity-report-a", Now.AddDays(-3).AddHours(2)),
            Report("report-3", OwnerB, CoachMessageId, "opportunity-report-b", Now.AddDays(-1)));

        await db.SaveChangesAsync();
    }

    private static CoachOpportunity Row(
        string id,
        string owner,
        string fingerprint,
        string capabilityCode,
        DateTime observedAtUtc,
        int occurrenceCount = 1,
        CoachOpportunityKind kind = CoachOpportunityKind.UnsupportedCapability,
        CoachOpportunityStatus status = CoachOpportunityStatus.New,
        bool withPointers = false) => new()
        {
            Id = id,
            UserProfileId = owner,
            ConversationId = withPointers ? ConversationId : null,
            TurnId = withPointers ? TurnOperationId : null,
            TurnOperationId = withPointers ? TurnOperationId : null,
            WriteOperationId = withPointers ? WriteOperationId : null,
            EvidenceMessageId = withPointers ? LearnerMessageId : null,
            EvidenceMessageSequence = withPointers ? 1 : null,
            EvidenceOfferMessageId = withPointers ? CoachMessageId : null,
            EvidenceOfferMessageSequence = withPointers ? 2 : null,
            Kind = kind,
            Disposition = CoachOpportunityDisposition.Product,
            Surface = CoachOpportunitySurface.TurnOutcome,
            CapabilityCode = capabilityCode,
            OfferLink = CoachOpportunityOfferLink.None,
            Fingerprint = fingerprint,
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
            TurnOperationId = TurnOperationId,
            WriteOperationId = WriteOperationId,
            OpportunityId = opportunityId,
            ReportedAtUtc = reportedAtUtc
        };

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
