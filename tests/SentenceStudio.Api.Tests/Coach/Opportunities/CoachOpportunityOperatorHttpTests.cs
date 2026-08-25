using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The operator surface over real HTTP: status codes, cache headers, and the export's casing.
/// </summary>
/// <remarks>
/// <para>
/// <c>CoachOpportunityOperatorSurfaceTests</c> proves what the service decides;
/// <c>CoachOpportunityRolloutTests</c> proves which routes exist. Neither can prove what a client
/// actually receives, and three of the review findings this file covers were about exactly
/// that — a status code that leaked existence, a missing cache directive, and a serializer whose
/// property names disagreed with the sibling route's.
/// </para>
/// <para>
/// The host boots with the coach enabled and this test's learner in the cohort, in Development,
/// which is the only configuration where these routes exist at all.
/// </para>
/// </remarks>
public class CoachOpportunityOperatorHttpTests
{
    private const string OperatorUser = "coach-operator-user";
    private const string OtherLearner = "coach-other-learner";
    private const string BasePath = CoachOpportunityOperatorEndpoints.RoutePrefix;

    // ---------------------------------------------------------------- cross-owner is a 404

    /// <summary>
    /// A cross-owner evidence request is indistinguishable from "no such row".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The surface previously answered 403 here. That made the identifier space an existence
    /// oracle: a caller could tell "this id names a real row owned by somebody else" from "this
    /// id names nothing", which is precisely what every other refusal on this surface is shaped
    /// to deny.
    /// </para>
    /// <para>
    /// The assertion is that the two responses are the <em>same</em>, not merely that one of them
    /// is a 404 — a future refusal that added a distinguishing body or header would still be an
    /// oracle, and comparing them catches that.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACrossOwnerEvidenceRequestIsIndistinguishableFromAnUnknownRow()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory);

        var crossOwnerId = await SeedRowAsync(factory, OtherLearner);

        using var crossOwner = await RevealAsync(client, crossOwnerId);
        using var unknown = await RevealAsync(client, "no-such-opportunity-id");

        crossOwner.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a 403 would confirm that this identifier names a real row owned by somebody else");

        crossOwner.StatusCode.Should().Be(unknown.StatusCode);
        (await crossOwner.Content.ReadAsStringAsync())
            .Should().Be(await unknown.Content.ReadAsStringAsync(),
                "the bodies must match too, or the body becomes the oracle the status code no " +
                "longer is");
    }

    /// <summary>A row outside the cohort caller's ownership is also a 404 on the read routes.</summary>
    [Fact]
    public async Task AnUnknownRowIsANotFoundOnEveryReadRoute()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory);

        using var get = await client.GetAsync($"{BasePath}/no-such-opportunity-id");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var review = await client.PostAsJsonAsync(
            $"{BasePath}/no-such-opportunity-id/review",
            new { status = "Reviewed" });

        review.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- no-store

    /// <summary>
    /// Every operator response forbids caching.
    /// </summary>
    /// <remarks>
    /// The evidence route returns decrypted learner messages and is the reason the rule exists,
    /// but the listing, rollup, export, and row routes carry an operator's triage view of a
    /// learner's problems — a browser, a proxy, or a shared-machine back button must not be able
    /// to re-serve any of them.
    /// </remarks>
    [Theory]
    [InlineData("/")]
    [InlineData("/rollup")]
    [InlineData("/export")]
    public async Task EveryOperatorReadIsNoStore(string route)
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory);

        await SeedRowAsync(factory, OperatorUser);

        using var response = await client.GetAsync($"{BasePath}{route}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull($"{route} must not be cacheable");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.NoCache.Should().BeTrue();
    }

    [Fact]
    public async Task TheEvidenceRouteIsNoStoreEvenWhenItRefuses()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory);

        var id = await SeedRowAsync(factory, OtherLearner);

        using var response = await RevealAsync(client, id);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue(
            "a refusal is still a response about a learner's ledger, and the rule is applied to " +
            "the whole group rather than to the routes somebody remembered");
    }

    // ---------------------------------------------------------------- export casing

    /// <summary>
    /// The NDJSON export and the JSON rollup use the same property names.
    /// </summary>
    /// <remarks>
    /// <c>Results.Ok</c> writes with the host's web defaults (camelCase); the export previously
    /// used <c>JsonSerializer</c>'s own defaults (PascalCase). A tool written against the JSON
    /// route therefore read nothing but nulls out of the export — silently, because every field
    /// is nullable or defaultable.
    /// </remarks>
    [Fact]
    public async Task TheExportUsesTheSamePropertyNamesAsTheRollup()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory);

        await SeedRowAsync(factory, OperatorUser);

        using var rollupResponse = await client.GetAsync($"{BasePath}/rollup");
        using var exportResponse = await client.GetAsync($"{BasePath}/export");

        rollupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        exportResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/x-ndjson");

        using var rollup = JsonDocument.Parse(await rollupResponse.Content.ReadAsStringAsync());
        var rollupNames = rollup.RootElement.EnumerateArray().First()
            .EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

        var lines = (await exportResponse.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().ContainSingle("one seeded problem is one rollup line");

        using var exported = JsonDocument.Parse(lines[0]);
        var exportNames = exported.RootElement
            .EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

        exportNames.Should().BeEquivalentTo(rollupNames,
            "one consumer must not have to know which route it read from");

        exportNames.Should().Contain("fingerprint");
        exportNames.Should().Contain("distinctLearners");
        exportNames.Should().NotContain("Fingerprint");
    }

    /// <summary>
    /// The export carries counts and closed-vocabulary codes, never an owner.
    /// </summary>
    /// <remarks>
    /// Asserted against the bytes a caller can save to a file, because that is the artifact that
    /// gets pasted into a spec. The two learners below produce one line with
    /// <c>distinctLearners: 2</c> and no identifier anywhere in it.
    /// </remarks>
    [Fact]
    public async Task TheExportCountsLearnersWithoutNamingThem()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory);

        await SeedRowAsync(factory, OperatorUser);
        await SeedRowAsync(factory, OtherLearner);

        using var response = await client.GetAsync($"{BasePath}/export");
        var body = await response.Content.ReadAsStringAsync();

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().ContainSingle("both learners hit the same problem, so it is one line");

        using var line = JsonDocument.Parse(lines[0]);
        line.RootElement.GetProperty("distinctLearners").GetInt32().Should().Be(2);
        line.RootElement.GetProperty("rowCount").GetInt32().Should().Be(2);

        body.Should().NotContain(OperatorUser);
        body.Should().NotContain(OtherLearner);
    }

    // ---------------------------------------------------------------- transition refusal

    /// <summary>
    /// A refused review transition answers 409, not 403 or 404.
    /// </summary>
    /// <remarks>
    /// The row exists and the caller may review it — what is refused is the transition, and the
    /// caller's correct response is to re-read and decide again. That is a conflict, not an
    /// authorization failure, and unlike the cross-owner case it leaks nothing: the caller
    /// already knows this row exists, because they accepted it.
    /// </remarks>
    [Fact]
    public async Task ARefusedTransitionAnswersConflict()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory);

        var id = await SeedRowAsync(factory, OperatorUser);

        using var accepted = await client.PostAsJsonAsync(
            $"{BasePath}/{id}/review", new { status = "Accepted" });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var walkBack = await client.PostAsJsonAsync(
            $"{BasePath}/{id}/review", new { status = "Dismissed" });

        walkBack.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A host with the coach on, this test's learner in the cohort, and a durable key ring.
    /// </summary>
    /// <remarks>
    /// The key ring is durable so the evidence route's own ephemeral-ring refusal — which runs
    /// before the row is loaded, on purpose — does not mask the cross-owner gate these tests
    /// exist to exercise.
    /// </remarks>
    private static CoachApiFactory NewFactory() => new()
    {
        CoachEnabled = true,
        CohortUserProfileId = OperatorUser,
        DurableKeyRing = true
    };

    private static HttpClient Authenticated(CoachApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userProfileId: OperatorUser));
        return client;
    }

    private static Task<HttpResponseMessage> RevealAsync(HttpClient client, string id) =>
        client.PostAsJsonAsync(
            $"{BasePath}/{id}/evidence",
            new { acknowledgement = CoachOpportunityLimits.EvidenceRevealAcknowledgement });

    /// <summary>
    /// Writes one Product row for <paramref name="userProfileId"/> straight through the context.
    /// </summary>
    /// <remarks>
    /// Seeded directly rather than through the recorder because the recorder resolves its owner
    /// from the ambient request scope, and these tests need a row owned by somebody other than
    /// the caller — which is the whole point of the cross-owner case.
    /// </remarks>
    private static async Task<string> SeedRowAsync(CoachApiFactory factory, string userProfileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachDbContext>();

        var now = DateTime.UtcNow;
        var row = new CoachOpportunity
        {
            Id = Guid.NewGuid().ToString("n"),
            UserProfileId = userProfileId,
            ConversationId = "conv-http",
            Kind = CoachOpportunityKind.AmbiguousFollowUp,
            Disposition = CoachOpportunityDisposition.Product,
            Surface = CoachOpportunitySurface.TurnOutcome,
            CapabilityCode = CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            OfferLink = CoachOpportunityOfferLink.PriorCoachQuestion,
            StopReason = CoachStopReason.ClarificationRequested,
            EvidenceMessageId = "msg-2",
            EvidenceMessageSequence = 2,
            EvidenceOfferMessageId = "msg-1",
            EvidenceOfferMessageSequence = 1,
            Fingerprint = CoachOpportunityFingerprint.Compute(
                CoachOpportunityKind.AmbiguousFollowUp,
                CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
                toolName: null,
                failureCode: null,
                CoachStopReason.ClarificationRequested,
                CoachOpportunityOfferLink.PriorCoachQuestion),
            DedupBucketDate = DateOnly.FromDateTime(now),
            OccurrenceCount = 1,
            FirstObservedAtUtc = now,
            LastObservedAtUtc = now,
            Status = CoachOpportunityStatus.New,
            SchemaVersion = CoachOpportunityLimits.SchemaVersion
        };

        db.CoachOpportunities.Add(row);
        await db.SaveChangesAsync();

        return row.Id;
    }
}
