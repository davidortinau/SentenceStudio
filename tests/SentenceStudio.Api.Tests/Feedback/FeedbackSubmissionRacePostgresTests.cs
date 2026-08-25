using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// Concurrent submissions of one preview token, against a real PostgreSQL server and through the
/// real HTTP endpoints.
/// </summary>
/// <remarks>
/// <para>
/// This is the family the whole design exists for, and the assertions are deliberately about four
/// separate things, because a design can satisfy any three and still be wrong: GitHub was called
/// once, exactly one ledger row exists and it holds a receipt, every caller was told the same
/// truth, and nothing was left in a state a later submission could act on.
/// </para>
/// <para>
/// The callers are separate <see cref="HttpClient"/> requests into the host, so each one runs on
/// its own request scope with its own <see cref="FeedbackDbContext"/> over its own connection.
/// They share no change tracker and no in-process lock — which is what two replicas behind a load
/// balancer look like, and what an in-memory guard cannot survive.
/// </para>
/// </remarks>
public sealed class FeedbackSubmissionRacePostgresTests : IAsyncLifetime
{
    private const string Owner = "user-feedback-race";

    private FeedbackPostgresHarness _harness = null!;
    private FeedbackApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("race");
        _factory = new FeedbackApiFactory(_harness.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------------ the race

    /// <summary>
    /// Two simultaneous submissions of one token create one issue, and both callers are told
    /// about that same issue.
    /// </summary>
    [PostgresFact]
    public async Task Concurrent_submissions_of_one_token_create_exactly_one_issue()
    {
        _factory.GitHub.Dwell = TimeSpan.FromMilliseconds(250);

        var token = await PreviewAsync("The reading activity freezes on the second page.");

        var responses = await RaceAsync(token, 2);

        _factory.GitHub.Calls.Should().Be(
            1, "the claim is taken before the external call, not after");

        await AssertSingleSettledRowAsync();

        // Every caller gets an answer that names the same issue. A refusal for the loser would be
        // telling a learner their report failed while it was in fact being filed.
        var payloads = await ReadSubmitResponsesAsync(responses);
        payloads.Should().HaveCount(2);
        payloads.Select(p => p.IssueNumber).Distinct().Should().ContainSingle();
        payloads.Select(p => p.IssueUrl).Distinct().Should().ContainSingle();

        payloads.Count(p => p.Outcome == FeedbackSubmitOutcome.Created)
            .Should().Be(1, "only the winner created it");
        payloads.Count(p => p.Outcome == FeedbackSubmitOutcome.Replayed)
            .Should().Be(1, "the loser is answered from the winner's receipt");
    }

    /// <summary>
    /// Four simultaneous submissions behave exactly as two do.
    /// </summary>
    /// <remarks>
    /// Two callers can pass a guard that is only accidentally exclusive — one happens to finish
    /// before the other starts. Four make that coincidence much harder to arrange, and the call
    /// count is still the assertion.
    /// </remarks>
    [PostgresFact]
    public async Task Four_simultaneous_submissions_still_create_one_issue()
    {
        _factory.GitHub.Dwell = TimeSpan.FromMilliseconds(250);

        var token = await PreviewAsync("Vocabulary quiz shows the same word twice in a row.");

        var responses = await RaceAsync(token, 4);

        _factory.GitHub.Calls.Should().Be(1);
        responses.Should().HaveCount(4);

        await AssertSingleSettledRowAsync();

        var payloads = await ReadSubmitResponsesAsync(responses);
        payloads.Select(p => p.IssueNumber).Distinct().Should().ContainSingle();
    }

    /// <summary>
    /// After the race, the token cannot be redeemed again by anybody.
    /// </summary>
    /// <remarks>
    /// The dangerous failure is not a duplicate during the race; it is a row the race leaves in a
    /// state that still looks claimable afterwards. A later submission must replay the receipt and
    /// must not reach GitHub.
    /// </remarks>
    [PostgresFact]
    public async Task A_raced_token_leaves_nothing_a_later_submission_can_file()
    {
        _factory.GitHub.Dwell = TimeSpan.FromMilliseconds(150);

        var token = await PreviewAsync("Shadowing audio cuts off before the last syllable.");
        await RaceAsync(token, 2);
        _factory.GitHub.Calls.Should().Be(1);

        using var client = _factory.CreateClientFor(Owner);
        var replay = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.GitHub.Calls.Should().Be(1, "a settled submission replays and never re-files");

        var payload = await replay.Content.ReadFromJsonAsync<FeedbackSubmitResponse>();
        payload!.Outcome.Should().Be(FeedbackSubmitOutcome.Replayed);

        await using var check = _harness.NewContext();
        var stored = await check.FeedbackSubmissions.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(FeedbackSubmissionStatus.Submitted);
        stored.Status.Should().NotBe(FeedbackSubmissionStatus.Claimed);
    }

    /// <summary>
    /// A serial retry is answered from the receipt without spending submission budget.
    /// </summary>
    /// <remarks>
    /// The client this protects is the correct one: its response was lost and it retried. Charging
    /// the retry against a three-per-day limit would mean an unreliable network costs a learner
    /// their allowance for reports they already filed.
    /// </remarks>
    [PostgresFact]
    public async Task A_replay_does_not_consume_submission_budget()
    {
        var token = await PreviewAsync("Numbers drill accepts a blank answer.");

        using var client = _factory.CreateClientFor(Owner);

        var first = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        for (var i = 0; i < 5; i++)
        {
            var replay = await client.PostAsJsonAsync(
                "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });
            replay.StatusCode.Should().Be(HttpStatusCode.OK, "a replay is an answer, not an attempt");
        }

        _factory.GitHub.Calls.Should().Be(1);

        await using var check = _harness.NewContext();
        var window = await check.FeedbackRateWindows.AsNoTracking()
            .SingleAsync(w => w.Kind == FeedbackRateKind.Submit);

        FeedbackRateLimiter.Parse(window.RecentTicksCsv).Should().ContainSingle(
            "only the submission that actually claimed and filed is charged");
    }

    // ---------------------------------------------------------------------- exact binding

    /// <summary>
    /// What is posted to GitHub is byte-for-byte what the preview promised.
    /// </summary>
    /// <remarks>
    /// The whole value of a preview is that it is a commitment. If the body were re-derived at
    /// submit time — re-formatted, re-labelled, re-enriched — the learner would be approving an
    /// illustration of an issue rather than the issue, and any change in that step would reach a
    /// public repository without anyone having seen it.
    /// </remarks>
    [PostgresFact]
    public async Task The_posted_issue_matches_the_preview_exactly()
    {
        using var client = _factory.CreateClientFor(Owner);

        var previewResponse = await client.PostAsJsonAsync("/api/v1/feedback/preview", new FeedbackRequest
        {
            Description = "The writing activity loses my draft when I rotate the device.",
            FeedbackType = "bug",
            ClientMetadata = new ClientMetadata
            {
                AppVersion = "3.4.5",
                Platform = FeedbackPlatform.Native,
                RouteCategory = FeedbackRouteCategory.Activity,
                Timestamp = DateTime.UtcNow
            }
        });

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<FeedbackPreviewResponse>())!;

        var submit = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = preview.PreviewToken });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.GitHub.Bodies.TryDequeue(out var posted).Should().BeTrue();

        using var doc = JsonDocument.Parse(posted!);
        var root = doc.RootElement;

        root.GetProperty("title").GetString().Should().Be(preview.Title);
        root.GetProperty("body").GetString().Should().Be(preview.FormattedBody);
        root.GetProperty("labels").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(preview.Labels);
    }

    /// <summary>Every posted label is one of the two the deployment allows.</summary>
    [PostgresFact]
    public async Task Posted_labels_are_always_inside_the_closed_set()
    {
        using var client = _factory.CreateClientFor(Owner);

        foreach (var type in new[] { "bug", "enhancement", "security", null })
        {
            var previewResponse = await client.PostAsJsonAsync("/api/v1/feedback/preview", new FeedbackRequest
            {
                Description = $"Report of kind {type ?? "unspecified"}.",
                FeedbackType = type
            });

            previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var preview = (await previewResponse.Content.ReadFromJsonAsync<FeedbackPreviewResponse>())!;

            preview.Labels.Should().NotBeEmpty(
                "an empty label array posts an unlabelled issue, which reads as a triage oversight "
                + "rather than a rejected model output");
            preview.Labels.Should().OnlyContain(l => l == "bug" || l == "enhancement");
        }
    }

    // -------------------------------------------------------------------------- helpers

    private async Task<string> PreviewAsync(string description)
    {
        using var client = _factory.CreateClientFor(Owner);

        var response = await client.PostAsJsonAsync("/api/v1/feedback/preview", new FeedbackRequest
        {
            Description = description,
            FeedbackType = "bug",
            ClientMetadata = new ClientMetadata
            {
                AppVersion = "1.2.3",
                Platform = FeedbackPlatform.Web,
                RouteCategory = FeedbackRouteCategory.Activity
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<FeedbackPreviewResponse>();
        preview!.PreviewToken.Should().NotBeNullOrWhiteSpace();
        return preview.PreviewToken;
    }

    /// <summary>
    /// Fires <paramref name="callers"/> submissions of one token at the same moment.
    /// </summary>
    /// <remarks>
    /// Each caller gets its own <see cref="HttpClient"/>, so each request lands on its own scope
    /// and its own database connection. The barrier makes them genuinely simultaneous rather than
    /// merely queued.
    /// </remarks>
    private async Task<IReadOnlyList<HttpResponseMessage>> RaceAsync(string token, int callers)
    {
        var results = new ConcurrentBag<HttpResponseMessage>();
        using var barrier = new Barrier(callers);

        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            using var client = _factory.CreateClientFor(Owner);
            barrier.SignalAndWait();

            var response = await client.PostAsJsonAsync(
                "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

            results.Add(response);
        })).ToArray();

        await Task.WhenAll(tasks);
        return results.ToArray();
    }

    private static async Task<IReadOnlyList<FeedbackSubmitResponse>> ReadSubmitResponsesAsync(
        IEnumerable<HttpResponseMessage> responses)
    {
        var payloads = new List<FeedbackSubmitResponse>();
        foreach (var response in responses)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "every caller in a won race is answered from the winner's receipt");

            payloads.Add((await response.Content.ReadFromJsonAsync<FeedbackSubmitResponse>())!);
            response.Dispose();
        }

        return payloads;
    }

    private async Task AssertSingleSettledRowAsync()
    {
        await using var check = _harness.NewContext();

        var rows = await check.FeedbackSubmissions.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle("one token is one claim");

        var row = rows[0];
        row.Status.Should().Be(FeedbackSubmissionStatus.Submitted);
        row.IssueNumber.Should().NotBeNull();
        row.IssueUrl.Should().NotBeNullOrWhiteSpace();
        row.FailureCode.Should().BeNull();
    }
}
