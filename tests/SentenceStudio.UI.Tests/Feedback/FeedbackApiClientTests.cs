using System.Net;
using System.Text;
using FluentAssertions;
using SentenceStudio.Contracts.Feedback;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UI.Tests.Feedback;

/// <summary>
/// Answers a fixed response, and counts how many times it was asked.
/// </summary>
internal sealed class CountingHandler : HttpMessageHandler
{
    private readonly Func<int, HttpResponseMessage> _factory;
    private int _calls;

    public CountingHandler(Func<int, HttpResponseMessage> factory) => _factory = factory;

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _calls);
        return Task.FromResult(_factory(index));
    }
}

/// <summary>
/// Throws, as a dropped connection does.
/// </summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        throw new HttpRequestException("Connection dropped after the request left.");
    }
}

/// <summary>
/// What the client does with each answer the feedback endpoints can give.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour under test is mostly a refusal to do things. This client never retries a
/// submission, never invents a wait, and never collapses "your report is already being filed" into
/// a generic error — because each of those, done the obvious way, produces a duplicate public
/// GitHub issue or an unactionable message.
/// </para>
/// <para>
/// Every other API client in the app can be retried freely, which is exactly why this one needs
/// tests saying it must not be.
/// </para>
/// </remarks>
public sealed class FeedbackApiClientTests
{
    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.test.local") };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------------ success

    [Fact]
    public async Task A_successful_submit_returns_the_issue()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.OK,
            """{"issueUrl":"https://github.com/o/r/issues/7","issueNumber":7,"title":"Filed","outcome":0}"""));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Succeeded.Should().BeTrue();
        result.Value!.IssueNumber.Should().Be(7);
        result.Value.Outcome.Should().Be(FeedbackSubmitOutcome.Created);
        handler.Calls.Should().Be(1);
    }

    /// <summary>
    /// A replayed receipt is surfaced as a replay, not as a fresh creation.
    /// </summary>
    /// <remarks>
    /// Without the distinction the page would say "submitted" twice for one issue, which reads to
    /// the learner as two issues having been filed — the exact confusion the exactly-once design
    /// exists to prevent, reintroduced at the last possible moment.
    /// </remarks>
    [Fact]
    public async Task A_replayed_receipt_is_reported_as_a_replay()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.OK,
            """{"issueUrl":"https://github.com/o/r/issues/7","issueNumber":7,"title":"Filed","outcome":1}"""));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Succeeded.Should().BeTrue();
        result.Value!.Outcome.Should().Be(FeedbackSubmitOutcome.Replayed);
    }

    // -------------------------------------------------------------------- rate limiting

    /// <summary>The server's Retry-After is carried back, in seconds, unchanged.</summary>
    [Fact]
    public async Task A_rate_limited_response_carries_the_servers_wait()
    {
        using var handler = new CountingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.Add("Retry-After", "43");
            return response;
        });

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.PreviewAsync(new FeedbackRequest { Description = "x" });

        result.Failure.Should().Be(FeedbackApiFailure.RateLimited);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(43));
    }

    /// <summary>A Retry-After given as a date is converted to a wait.</summary>
    [Fact]
    public async Task A_date_form_retry_after_is_understood()
    {
        using var handler = new CountingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.Add("Retry-After", DateTimeOffset.UtcNow.AddMinutes(2).ToString("R"));
            return response;
        });

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.PreviewAsync(new FeedbackRequest { Description = "x" });

        result.Failure.Should().Be(FeedbackApiFailure.RateLimited);
        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// A rate-limited response with no header reports no wait rather than a guessed one.
    /// </summary>
    /// <remarks>
    /// A default that is too short trains the client to hammer the endpoint; one that is too long
    /// is a worse experience than saying nothing. Null is the honest answer, and the page has a
    /// message for it.
    /// </remarks>
    [Fact]
    public async Task A_rate_limited_response_without_a_header_invents_no_wait()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}"));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.PreviewAsync(new FeedbackRequest { Description = "x" });

        result.Failure.Should().Be(FeedbackApiFailure.RateLimited);
        result.RetryAfter.Should().BeNull();
    }

    // ------------------------------------------------------------------------ refusals

    [Fact]
    public async Task A_refused_token_is_reported_as_a_token_rejection()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.BadRequest, "{}"));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Failure.Should().Be(FeedbackApiFailure.TokenRejected);
        result.Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// A 400 from preview is a rejected description, not a rejected token.
    /// </summary>
    /// <remarks>
    /// A preview presents no token, so there is none to reject: a 400 there means the description
    /// was empty or past the length limit. Sharing one status mapping between the two calls would
    /// tell a learner whose report was too long that "this preview is no longer valid" and would
    /// disable a Submit button they had not yet reached — a message that is both wrong and
    /// unactionable.
    /// </remarks>
    [Fact]
    public async Task A_rejected_description_on_preview_is_not_reported_as_a_token_rejection()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.BadRequest, "{}"));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.PreviewAsync(new FeedbackRequest { Description = "" });

        result.Failure.Should().NotBe(FeedbackApiFailure.TokenRejected);
        result.Failure.Should().NotBe(FeedbackApiFailure.InDoubt);
        result.Failure.Should().Be(FeedbackApiFailure.Unavailable);
    }

    /// <summary>A preview is still rate limited the same way a submission is.</summary>
    [Fact]
    public async Task A_rate_limited_preview_is_reported_as_rate_limited()
    {
        using var handler = new CountingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.Add("Retry-After", "120");
            return response;
        });

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.PreviewAsync(new FeedbackRequest { Description = "x" });

        result.Failure.Should().Be(FeedbackApiFailure.RateLimited);
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// A 409 is reported as in-doubt, which is the signal the page uses to remove the button.
    /// </summary>
    [Fact]
    public async Task A_conflict_is_reported_as_in_doubt()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.Conflict,
            """{"status":409,"code":"submission_in_doubt"}"""));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Failure.Should().Be(FeedbackApiFailure.InDoubt);
    }

    /// <summary>
    /// A 409 carrying the closed code is reported as closed, not as in-doubt.
    /// </summary>
    /// <remarks>
    /// The two share a status, so without the code the client cannot tell "nothing was filed" from
    /// "we do not know". Getting it wrong in this direction sends a learner to search a public
    /// repository for an issue that provably does not exist, and leaves them unsure whether they
    /// reported the bug at all.
    /// </remarks>
    [Fact]
    public async Task A_conflict_carrying_the_closed_code_is_reported_as_closed()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.Conflict,
            """{"status":409,"detail":"That report was not filed.","code":"submission_closed"}"""));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Failure.Should().Be(FeedbackApiFailure.Closed);
        result.Failure.Should().NotBe(FeedbackApiFailure.InDoubt);
        handler.Calls.Should().Be(1, "a closed submission is terminal, not retryable");
    }

    /// <summary>
    /// A 409 whose code is missing or unrecognised falls back to in-doubt, never to closed.
    /// </summary>
    /// <remarks>
    /// The direction of the fallback is the decision. "We do not know" is always safe to tell a
    /// learner; asserting "nothing was filed" without the server having said so is a claim this
    /// client is not in a position to make, and acting on it means writing the report again — which
    /// is how a duplicate gets filed.
    /// </remarks>
    [Theory]
    [InlineData("""{"status":409}""")]
    [InlineData("""{"status":409,"code":"something_new"}""")]
    [InlineData("""{"status":409,"code":42}""")]
    [InlineData("<html><body>Gateway error</body></html>")]
    [InlineData("")]
    public async Task An_unrecognised_conflict_falls_back_to_in_doubt(string body)
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.Conflict, body));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Failure.Should().Be(FeedbackApiFailure.InDoubt);
        result.Failure.Should().NotBe(FeedbackApiFailure.Closed);
    }

    /// <summary>The closed code is honoured whatever status accompanies it.</summary>
    /// <remarks>
    /// The code is the meaning; the status is transport. Binding the interpretation to both would
    /// mean a future change of status — 410, say — silently reverted the client to the in-doubt
    /// message.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task The_closed_code_wins_over_the_status(HttpStatusCode status)
    {
        using var handler = new CountingHandler(_ => Json(status,
            """{"code":"submission_closed"}"""));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Failure.Should().Be(FeedbackApiFailure.Closed);
    }

    /// <summary>Reading the code does not disturb the rate-limit path.</summary>
    [Fact]
    public async Task A_rate_limited_submission_still_reports_its_wait_alongside_its_code()
    {
        using var handler = new CountingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"code":"rate_limited"}""");
            response.Headers.Add("Retry-After", "17");
            return response;
        });

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Failure.Should().Be(FeedbackApiFailure.RateLimited);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(17));
    }

    // -------------------------------------------------------------------- never retries

    /// <summary>
    /// The client never re-sends a submission, whatever the failure.
    /// </summary>
    /// <remarks>
    /// The counter is the assertion. A client that retried would look correct in every other test
    /// here — the result type would be identical — and would file duplicates in production.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task A_failed_submit_is_never_re_sent(HttpStatusCode status)
    {
        using var handler = new CountingHandler(_ => Json(status, "{}"));

        var client = new FeedbackApiClient(Client(handler));
        await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        handler.Calls.Should().Be(
            1, "creating a public GitHub issue is not idempotent, so a retry may duplicate it");
    }

    /// <summary>
    /// A dropped connection is treated as in doubt, not as a failure to retry.
    /// </summary>
    /// <remarks>
    /// The request may have reached the server, which may have filed the issue. The client has no
    /// more information than the server does in the same situation, so it makes the same call:
    /// assume nothing, and never re-send.
    /// </remarks>
    [Fact]
    public async Task A_dropped_connection_on_submit_is_in_doubt_and_is_not_re_sent()
    {
        using var handler = new ThrowingHandler();

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Failure.Should().Be(FeedbackApiFailure.InDoubt);
        handler.Calls.Should().Be(1);
    }

    /// <summary>
    /// A dropped connection on preview is merely unavailable, because a preview costs nothing
    /// public.
    /// </summary>
    /// <remarks>
    /// The asymmetry with submit is the point: preview is safe to try again, submit is not, and
    /// treating them the same in either direction is a defect. Making preview in-doubt would block
    /// a learner for no reason; making submit unavailable would file duplicates.
    /// </remarks>
    [Fact]
    public async Task A_dropped_connection_on_preview_is_merely_unavailable()
    {
        using var handler = new ThrowingHandler();

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.PreviewAsync(new FeedbackRequest { Description = "x" });

        result.Failure.Should().Be(FeedbackApiFailure.Unavailable);
    }

    /// <summary>
    /// A success status with an unusable body is in doubt, not a success and not a retry.
    /// </summary>
    /// <remarks>
    /// The server answered 200, so the issue exists. Reporting an error the page could retry would
    /// duplicate it; reporting success with no issue number would show the learner a broken link.
    /// </remarks>
    [Fact]
    public async Task A_successful_status_with_no_body_is_in_doubt()
    {
        using var handler = new CountingHandler(_ => Json(HttpStatusCode.OK, "null"));

        var client = new FeedbackApiClient(Client(handler));
        var result = await client.SubmitAsync(new FeedbackSubmitRequest { PreviewToken = "t" });

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FeedbackApiFailure.InDoubt);
    }

    // ------------------------------------------------------------------------ contract

    /// <summary>
    /// The client surface offers no way to throw away a failure's meaning.
    /// </summary>
    /// <remarks>
    /// Structural. <c>EnsureSuccessStatusCode</c> — which the previous version called — collapses
    /// "wait 43 seconds", "already being filed, do not send again", and "the network blipped" into
    /// one exception, and a caller holding that exception cannot tell the third from the second.
    /// So it retries, and files a duplicate.
    /// </remarks>
    [Fact]
    public void The_client_returns_results_rather_than_throwing_status_exceptions()
    {
        var methods = typeof(IFeedbackApiClient).GetMethods();

        methods.Should().OnlyContain(m =>
            m.ReturnType.IsGenericType
            && m.ReturnType.GetGenericArguments()[0].IsGenericType
            && m.ReturnType.GetGenericArguments()[0].GetGenericTypeDefinition()
                == typeof(FeedbackApiResult<>));
    }

    [Fact]
    public void The_client_source_does_not_call_ensure_success_status_code()
    {
        var root = RepositoryRoot();

        var path = Path.Combine(
            root, "src", "SentenceStudio.AppLib", "Services", "Api", "FeedbackApiClient.cs");

        // The invocation, not the identifier: the file names it in a comment explaining why it is
        // not used, and a test that failed on the explanation would be pressure to delete the
        // explanation.
        File.ReadAllText(path).Should().NotContain("EnsureSuccessStatusCode()");
    }

    /// <summary>
    /// The feedback HttpClient runs with no resilience pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AddServiceDefaults</c> installs the standard resilience handler through
    /// <c>ConfigureHttpClientDefaults</c>, which applies to every named client — so the client above
    /// can be scrupulous about never re-sending while the transport underneath it re-sends anyway.
    /// Its retry strategy handles 429, which means a rate-limited submission would be re-sent three
    /// more times, ignoring the Retry-After the server had just computed and making the wait shown
    /// to the learner a fiction.
    /// </para>
    /// <para>
    /// Source-level because the alternative is asserting on the internals of an
    /// <c>IHttpClientFactory</c> pipeline, which is neither stable nor readable. The registration is
    /// one line and this is the line that must stay on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_feedback_client_registration_removes_the_shared_resilience_pipeline()
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.AppLib", "ServiceCollectionExtentions.cs");

        var source = File.ReadAllText(path);
        var index = source.IndexOf("AddHttpClient<IFeedbackApiClient", StringComparison.Ordinal);

        index.Should().BeGreaterThan(-1, "the feedback client must still be registered");

        var registration = source[index..];
        var end = registration.IndexOf(';', StringComparison.Ordinal);
        end.Should().BeGreaterThan(-1);

        registration[..end].Should().Contain(
            "RemoveAllResilienceHandlers",
            "a transport that re-sends a feedback submission ignores the server's Retry-After and "
            + "turns one press of Submit into four requests");
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();
        return root!.FullName;
    }
}
