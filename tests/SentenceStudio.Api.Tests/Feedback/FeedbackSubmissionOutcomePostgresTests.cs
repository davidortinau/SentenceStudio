using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// What the ledger does with each way a submission can end, against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// The organising question for every test here is the same one the design turns on: given what we
/// know, may this token be sent to GitHub again? The answer is yes exactly never, and the cases
/// below are the distinct routes to that answer — plus the one case where an issue definitely
/// exists and we cannot say which.
/// </remarks>
public sealed class FeedbackSubmissionOutcomePostgresTests : IAsyncLifetime
{
    private const string Owner = "user-feedback-outcomes";

    private FeedbackPostgresHarness _harness = null!;
    private FeedbackApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("outcomes");
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

    // ------------------------------------------------------------------- known-failed outcome

    /// <summary>
    /// A non-success status from GitHub closes the attempt, and the closed attempt never runs
    /// again.
    /// </summary>
    /// <remarks>
    /// This is the only external failure that may be recorded as "no issue exists", because
    /// GitHub's issue creation is atomic per request. Recording it lets the learner be told the
    /// truth — nothing was filed — instead of the vaguer answer a transport failure gets.
    /// </remarks>
    [PostgresFact]
    public async Task A_rejected_issue_closes_the_attempt_and_cannot_be_retried()
    {
        _factory.GitHub.FailWith = HttpStatusCode.UnprocessableEntity;

        var token = await PreviewAsync("Labels are wrong.");
        using var client = _factory.CreateClientFor(Owner);

        var first = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });
        first.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await using (var check = _harness.NewContext())
        {
            var row = await check.FeedbackSubmissions.AsNoTracking().SingleAsync();
            row.Status.Should().Be(FeedbackSubmissionStatus.Failed);
            row.FailureCode.Should().Be(FeedbackFailureCodes.GitHubRejected);
            row.IssueNumber.Should().BeNull();
        }

        // Now let GitHub succeed. A closed attempt must still refuse: the token is spent, and
        // reopening it would mean the ledger row no longer answers for its request.
        _factory.GitHub.FailWith = null;

        var retry = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.GitHub.Calls.Should().Be(1, "a closed attempt never reaches GitHub again");

        // The status is shared with the in-doubt refusal, so the code is what carries the meaning.
        // Without it the client would tell a learner to check GitHub for an issue that provably
        // does not exist.
        (await ProblemCodeAsync(retry)).Should().Be(FeedbackFailureCodes.SubmissionClosed);
    }

    /// <summary>
    /// The two 409s are distinguishable, and the closed one says nothing was filed.
    /// </summary>
    /// <remarks>
    /// The pair is asserted together because the risk is not that either message is wrong on its
    /// own — it is that they are interchangeable. A learner told to "check GitHub" after a proved
    /// failure goes looking for something that is not there; a learner told "nothing was sent"
    /// after an unknown outcome writes the report again and may file a duplicate.
    /// </remarks>
    [PostgresFact]
    public async Task A_closed_submission_and_an_in_doubt_one_are_distinguishable()
    {
        // The submit cooldown is a per-owner limit, and this test deliberately makes two claimed
        // submissions in quick succession. Relaxing the cooldown here keeps the test about the two
        // 409 codes rather than about the 429 that would otherwise arrive first; the cooldown has
        // its own tests in FeedbackRateLimitPostgresTests.
        using var factory = new FeedbackApiFactory(_harness.ConnectionString);
        factory.Settings["Feedback:SubmitCooldown"] = "00:00:00";

        using var client = factory.CreateClientFor("user-outcome-codes");

        factory.GitHub.FailWith = HttpStatusCode.UnprocessableEntity;
        var closedToken = await PreviewOnAsync(factory, client, "Closed report.");

        await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = closedToken });

        var closed = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = closedToken });

        factory.GitHub.FailWith = null;
        factory.GitHub.ThrowTransport = true;
        var doubtToken = await PreviewOnAsync(factory, client, "In-doubt report.");

        await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = doubtToken });

        var inDoubt = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = doubtToken });

        closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        inDoubt.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var closedCode = await ProblemCodeAsync(closed);
        var doubtCode = await ProblemCodeAsync(inDoubt);

        closedCode.Should().Be(FeedbackFailureCodes.SubmissionClosed);
        doubtCode.Should().Be(FeedbackFailureCodes.SubmissionInDoubt);
        closedCode.Should().NotBe(doubtCode, "the status alone cannot separate them");

        // The prose has to match the code, or a client that reads the detail and a client that
        // reads the code disagree about what happened.
        var closedDetail = await DetailAsync(closed);
        closedDetail.Should().NotContain("check GitHub");
        closedDetail.Should().Contain("not filed");

        (await DetailAsync(inDoubt)).Should().Contain("GitHub");
    }

    /// <summary>Every refusal that closes a submission carries a code the client can branch on.</summary>
    [PostgresFact]
    public async Task A_rate_limited_submission_also_carries_its_code()
    {
        using var factory = new FeedbackApiFactory(_harness.ConnectionString);
        factory.Settings["Feedback:MaxSubmitsPerWindow"] = "1";

        using var client = factory.CreateClientFor("user-outcome-ratelimit");

        var first = await PreviewOnAsync(factory, client, "First.");
        (await client.PostAsJsonAsync(
                "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = first }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await PreviewOnAsync(factory, client, "Second.");
        var refused = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = second });

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ProblemCodeAsync(refused)).Should().Be(FeedbackFailureCodes.RateLimited);
        refused.Headers.RetryAfter.Should().NotBeNull();

        factory.GitHub.Calls.Should().Be(1);
    }

    /// <summary>
    /// A retryable 5xx from GitHub is sent once, not four times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exactly-once ledger sits above the <see cref="HttpClient"/>, so it cannot see a retry
    /// that happens inside the pipeline. <c>AddServiceDefaults</c> installs the standard resilience
    /// handler through <c>ConfigureHttpClientDefaults</c>, which applies to every named client, and
    /// its retry strategy re-sends on 5xx, 408, 429, and <c>HttpRequestException</c> — up to three
    /// times. For a POST that creates a public GitHub issue that is four issues from one press of
    /// Submit, with nothing in our database recording that it happened.
    /// </para>
    /// <para>
    /// The host removes the pipeline for this one client. This test is what keeps it removed: the
    /// removal API is experimental, and a rename or behaviour change would otherwise reintroduce
    /// the duplicates silently.
    /// </para>
    /// </remarks>
    [PostgresTheory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_retryable_github_failure_is_sent_exactly_once(HttpStatusCode status)
    {
        _factory.GitHub.FailWith = status;

        var token = await PreviewAsync($"Retryable {status}.");
        using var client = _factory.CreateClientFor(Owner);

        await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        _factory.GitHub.Calls.Should().Be(
            1,
            "issue creation is not idempotent, so the transport must never re-send it — the "
            + "standard resilience handler is removed for this client precisely for this reason");
    }

    // ------------------------------------------------------------------ unknown outcome

    /// <summary>
    /// A transport failure leaves the attempt in doubt — never closed — and every later
    /// submission of the token refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request may have created an issue before the connection died. Marking it Failed would
    /// assert something unknown and hand the token back for a retry that files a duplicate; the
    /// row therefore stays Claimed, which is the state that refuses everything.
    /// </para>
    /// <para>
    /// This is the single most important negative test in the family, because the intuitive fix —
    /// "the call threw, so nothing happened, let them try again" — is exactly the bug.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_transport_failure_leaves_the_attempt_in_doubt_and_never_retries()
    {
        _factory.GitHub.ThrowTransport = true;

        var token = await PreviewAsync("The app closed mid-submit.");
        using var client = _factory.CreateClientFor(Owner);

        var first = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });
        first.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        await using (var check = _harness.NewContext())
        {
            var row = await check.FeedbackSubmissions.AsNoTracking().SingleAsync();

            row.Status.Should().Be(
                FeedbackSubmissionStatus.Claimed,
                "a failure that cannot prove an issue was not created must not be recorded as one "
                + "that can");
            row.Status.Should().NotBe(FeedbackSubmissionStatus.Failed);
        }

        _factory.GitHub.ThrowTransport = false;

        var retry = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.GitHub.Calls.Should().Be(
            1, "an in-doubt attempt is never sent again, however tempting the retry looks");
    }

    // ---------------------------------------------------------------- committed-but-unrecorded

    /// <summary>
    /// An issue that was created but could not be identified is recorded as committed, and the
    /// token is still never re-sent.
    /// </summary>
    /// <remarks>
    /// Committed exists so an operator can tell "look and see whether an issue exists" (Claimed)
    /// from "an issue is definitely there, find it". Both refuse retries; only one of them tells
    /// the operator where to look.
    /// </remarks>
    [PostgresFact]
    public async Task An_unreadable_created_response_is_recorded_as_committed_and_never_retried()
    {
        _factory.GitHub.CreatedBodyOverride = "this is not json";

        var token = await PreviewAsync("Something went wrong on our side.");
        using var client = _factory.CreateClientFor(Owner);

        var first = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });
        first.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        await using (var check = _harness.NewContext())
        {
            var row = await check.FeedbackSubmissions.AsNoTracking().SingleAsync();
            row.Status.Should().Be(FeedbackSubmissionStatus.Committed);
            row.FailureCode.Should().Be(FeedbackFailureCodes.SettlementFailed);
        }

        _factory.GitHub.CreatedBodyOverride = null;

        var retry = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.GitHub.Calls.Should().Be(1);
    }

    /// <summary>
    /// No stored status permits an external call, including one nobody has classified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A behavioural mutation test for the ledger's classifier. Every declared status is written
    /// straight into the database and then submitted against; none may reach GitHub. The
    /// undeclared ordinal is the mutation: it stands in for a status a future change adds without
    /// updating <c>Classify</c>, and it must fall into the in-doubt arm rather than the
    /// claimable one.
    /// </para>
    /// <para>
    /// Changing <c>Classify</c>'s default from in-doubt to anything else makes this fail, which is
    /// the property that keeps "fail closed" from being a comment.
    /// </para>
    /// </remarks>
    [PostgresTheory]
    [InlineData(FeedbackSubmissionStatus.Claimed)]
    [InlineData(FeedbackSubmissionStatus.Submitted)]
    [InlineData(FeedbackSubmissionStatus.Failed)]
    [InlineData(FeedbackSubmissionStatus.Committed)]
    [InlineData((FeedbackSubmissionStatus)97)]
    public async Task No_stored_status_lets_a_submission_reach_github(FeedbackSubmissionStatus status)
    {
        var token = await PreviewAsync($"Status probe {(int)status}.");
        var jti = JtiOf(token);

        await using (var seed = _harness.NewContext())
        {
            var now = DateTime.UtcNow;
            seed.FeedbackSubmissions.Add(new FeedbackSubmission
            {
                Jti = jti,
                UserProfileId = Owner,
                Status = status,
                ContentDigest = "seeded",
                IssueNumber = status == FeedbackSubmissionStatus.Submitted ? 7 : null,
                IssueUrl = status == FeedbackSubmissionStatus.Submitted
                    ? "https://github.com/davidortinau/SentenceStudio/issues/7"
                    : null,
                IssueTitle = status == FeedbackSubmissionStatus.Submitted ? "Seeded" : null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                TokenExpiresAtUtc = now.AddMinutes(10),
                Version = 1
            });
            await seed.SaveChangesAsync();
        }

        using var client = _factory.CreateClientFor(Owner);
        var response = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        _factory.GitHub.Calls.Should().Be(
            0,
            "an existing ledger row answers for its token; only a token with no row may be claimed");

        response.StatusCode.Should().Be(status switch
        {
            FeedbackSubmissionStatus.Submitted => HttpStatusCode.OK,
            _ => HttpStatusCode.Conflict
        });
    }

    // -------------------------------------------------------------------------- helpers

    /// <summary>The <c>code</c> extension the server put on a problem response.</summary>
    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty(FeedbackProblemCodes.ExtensionName, out var code)
            ? code.GetString()
            : null;
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("detail", out var detail)
            ? detail.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task<string> PreviewOnAsync(
        FeedbackApiFactory factory, HttpClient client, string description)
    {
        var response = await client.PostAsJsonAsync("/api/v1/feedback/preview", new FeedbackRequest
        {
            Description = description,
            FeedbackType = "bug"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<FeedbackPreviewResponse>();
        return preview!.PreviewToken;
    }

    private async Task<string> PreviewAsync(string description)
    {
        using var client = _factory.CreateClientFor(Owner);

        var response = await client.PostAsJsonAsync("/api/v1/feedback/preview", new FeedbackRequest
        {
            Description = description,
            FeedbackType = "bug"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<FeedbackPreviewResponse>();
        return preview!.PreviewToken;
    }

    /// <summary>The nonce inside a signed token, read the way the endpoint reads it.</summary>
    private string JtiOf(string token)
    {
        var keyProvider = _factory.Services.GetService(typeof(IFeedbackHmacKeyProvider)) as IFeedbackHmacKeyProvider;
        keyProvider.Should().NotBeNull();

        FeedbackPreviewToken.TryValidate(token, keyProvider!.Key, DateTimeOffset.UtcNow, out var payload)
            .Should().Be(FeedbackTokenRejection.None);

        return payload!.Jti;
    }
}
