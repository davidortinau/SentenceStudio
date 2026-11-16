using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.WebApp.Admin;

namespace SentenceStudio.WebApp.Tests.Admin;

/// <summary>
/// The web app's side of the admin portal, against a scripted API: what the client reads out of
/// each answer, what one browser tab remembers, and when the sidebar link appears.
/// </summary>
/// <remarks>
/// The API is the authority and has its own suite. These tests pin that the web app treats a 404
/// as "not an admin" everywhere, never invents access from a failure, and sends one request per
/// action.
/// </remarks>
public sealed class AdminClientAndStateTests
{
    private readonly ScriptedApi _api = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    private AdminApiClient Client() =>
        new(new HttpClient(_api) { BaseAddress = new Uri("http://api.test") }, NullLogger<AdminApiClient>.Instance);

    private AdminSessionState State() => new(Client(), _time);

    // ------------------------------------------------------------------ probe

    [Fact]
    public async Task The_probe_says_admin_for_204_not_admin_for_404_and_unknown_for_anything_else()
    {
        _api.Answer(HttpStatusCode.NoContent);
        (await Client().ProbeAsync()).Should().Be(AdminAccess.Admin);

        _api.Answer(HttpStatusCode.NotFound);
        (await Client().ProbeAsync()).Should().Be(AdminAccess.NotAdmin);

        foreach (var status in new[]
                 {
                     HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.BadGateway,
                     HttpStatusCode.InternalServerError
                 })
        {
            _api.Answer(status);
            (await Client().ProbeAsync()).Should().Be(AdminAccess.Unknown,
                $"{(int)status} is neither the API's yes nor its no");
        }
    }

    [Fact]
    public async Task A_probe_that_cannot_reach_the_API_is_unknown_not_a_refusal_and_does_not_throw()
    {
        _api.Throw(new HttpRequestException("connection refused"));

        (await Client().ProbeAsync()).Should().Be(AdminAccess.Unknown);
    }

    [Fact]
    public async Task An_API_that_never_answers_is_given_up_on_and_reported_as_no_answer_not_thrown()
    {
        // The client has no resilience pipeline, so its own deadline is the only short bound on a
        // request that hangs. Running out of time must read as "no answer", never as a refusal
        // and never as an exception in the page or the sidebar.
        var silent = new NeverAnswers();
        var client = new AdminApiClient(
            new HttpClient(silent) { BaseAddress = new Uri("http://api.test") }, NullLogger<AdminApiClient>.Instance)
        {
            Deadline = TimeSpan.FromMilliseconds(50)
        };

        (await client.ProbeAsync()).Should().Be(AdminAccess.Unknown);
        (await client.GetSessionAsync(proof: null)).Status.Should().Be(AdminApiStatus.Failed);
        (await client.StepUpAsync("AnyPassword1234!")).Status.Should().Be(AdminApiStatus.Failed);
        (await client.GetAuditAsync(before: null, take: 50)).Status.Should().Be(AdminApiStatus.Failed);
        silent.Abandoned.Should().Be(4, "each request was cancelled when its time ran out");
    }

    [Fact]
    public async Task A_caller_that_cancels_gets_its_cancellation_back()
    {
        var client = new AdminApiClient(
            new HttpClient(new NeverAnswers()) { BaseAddress = new Uri("http://api.test") },
            NullLogger<AdminApiClient>.Instance);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var session = () => client.GetSessionAsync(proof: null, caller.Token);
        var probe = () => client.ProbeAsync(caller.Token);

        await session.Should().ThrowAsync<OperationCanceledException>();
        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void An_admin_request_is_given_up_on_in_seconds_not_minutes()
    {
        AdminApiClient.RequestTimeout.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        Client().Deadline.Should().Be(AdminApiClient.RequestTimeout, "the deadline is the client's own, whoever built it");
    }

    [Fact]
    public void A_result_nobody_filled_in_is_a_failure()
    {
        // The zero value of each result type must never read as success.
        default(AdminApiResult<AdminSession>).IsOk.Should().BeFalse();
        default(AdminApiStatus).Should().Be(AdminApiStatus.Failed);
        default(AdminPasswordOutcome).Should().Be(AdminPasswordOutcome.Failed);
        default(AdminAccess).Should().Be(AdminAccess.Unknown);
        new AdminApiResult<AdminSession>(AdminApiStatus.Ok, null).IsOk.Should().BeFalse(
            "a success with nothing in it is not one");
    }

    // ----------------------------------------------------------------- client

    [Fact]
    public async Task The_session_is_read_with_the_proof_in_its_header()
    {
        _api.Answer(HttpStatusCode.OK,
            """{"userId":"u1","email":"captain@example.test","displayName":"Captain","stepUpExpiresAtUtc":"2026-11-15T12:10:00+00:00"}""");

        var result = await Client().GetSessionAsync("the-proof");

        result.IsOk.Should().BeTrue();
        result.Value!.Email.Should().Be("captain@example.test");
        result.Value.StepUpExpiresAtUtc.Should().Be(new DateTimeOffset(2026, 11, 15, 12, 10, 0, TimeSpan.Zero));
        var request = _api.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/api/admin/session");
        request.Proof.Should().Be("the-proof");
    }

    [Fact]
    public async Task No_proof_means_no_proof_header()
    {
        _api.Answer(HttpStatusCode.OK, """{"userId":"u1"}""");

        await Client().GetSessionAsync(proof: null);

        _api.Requests.Should().ContainSingle().Which.Proof.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, AdminApiStatus.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, AdminApiStatus.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, AdminApiStatus.Refused)]
    [InlineData(HttpStatusCode.BadRequest, AdminApiStatus.Refused)]
    [InlineData(HttpStatusCode.InternalServerError, AdminApiStatus.Failed)]
    [InlineData(HttpStatusCode.NoContent, AdminApiStatus.Failed)]
    public async Task Every_answer_but_200_carries_no_value(HttpStatusCode status, AdminApiStatus expected)
    {
        _api.Answer(status, """{"userId":"u1","email":"captain@example.test"}""");

        var result = await Client().GetSessionAsync(proof: null);

        result.Status.Should().Be(expected);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task A_refusal_carries_the_APIs_code()
    {
        _api.Answer(HttpStatusCode.Forbidden, """{"title":"Forbidden","status":403,"code":"password_incorrect"}""");

        var result = await Client().StepUpAsync("wrong");

        result.Status.Should().Be(AdminApiStatus.Refused);
        result.Code.Should().Be("password_incorrect");
    }

    [Fact]
    public async Task The_audit_page_is_asked_for_by_size_and_cursor()
    {
        _api.Answer(HttpStatusCode.OK,
            """{"items":[{"id":7,"occurredAtUtc":"2026-11-15T12:00:00Z","actorKind":"Admin","actorId":"u1","actorEmail":"captain@example.test","action":"session.read","outcome":"Succeeded","statusCode":200}],"nextBefore":7}""");

        var first = await Client().GetAuditAsync(before: null, take: 50);
        _api.Answer(HttpStatusCode.OK, """{"items":[],"nextBefore":null}""");
        await Client().GetAuditAsync(before: 7, take: 50);

        first.Value!.Items.Should().ContainSingle().Which.Action.Should().Be("session.read");
        first.Value.NextBefore.Should().Be(7);
        _api.Requests.Select(r => r.PathAndQuery).Should().Equal(
            "/api/admin/audit?take=50", "/api/admin/audit?take=50&before=7");
    }

    [Fact]
    public async Task One_call_is_one_request_even_when_the_API_fails()
    {
        _api.Answer(HttpStatusCode.ServiceUnavailable);

        await Client().StepUpAsync("password");

        _api.Requests.Should().ContainSingle(
            "a retried password check would count twice toward lockout and leave two audit rows");
    }

    // ------------------------------------------------------------------ state

    [Fact]
    public async Task A_confirmed_password_counts_until_the_APIs_expiry_and_not_after()
    {
        _api.Answer(HttpStatusCode.OK, """{"proof":"sealed","expiresAtUtc":"2026-11-15T12:10:00+00:00"}""");
        var state = State();
        var changes = 0;
        state.Changed += () => changes++;

        (await state.ConfirmPasswordAsync("right")).Should().Be(AdminPasswordOutcome.Confirmed);

        changes.Should().Be(1);
        state.CurrentProof.Should().Be("sealed");
        state.PasswordConfirmedUntilUtc.Should().Be(new DateTimeOffset(2026, 11, 15, 12, 10, 0, TimeSpan.Zero));
        _api.Requests.Should().ContainSingle().Which.Body.Should().Be("""{"password":"right"}""");

        _time.Now = _time.Now.AddMinutes(10);
        state.PasswordConfirmedUntilUtc.Should().BeNull();
        state.CurrentProof.Should().BeNull("an expired proof must not be sent");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "password_incorrect", AdminPasswordOutcome.Incorrect)]
    [InlineData(HttpStatusCode.TooManyRequests, "locked_out", AdminPasswordOutcome.LockedOut)]
    [InlineData(HttpStatusCode.NotFound, null, AdminPasswordOutcome.NotAdmin)]
    [InlineData(HttpStatusCode.Forbidden, "something_new", AdminPasswordOutcome.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, null, AdminPasswordOutcome.Failed)]
    public async Task Anything_but_a_proof_confirms_nothing(
        HttpStatusCode status, string? code, AdminPasswordOutcome expected)
    {
        _api.Answer(status, code is null ? "" : $$"""{"code":"{{code}}"}""");
        var state = State();

        (await state.ConfirmPasswordAsync("whatever")).Should().Be(expected);

        state.CurrentProof.Should().BeNull();
        state.PasswordConfirmedUntilUtc.Should().BeNull();
    }

    [Fact]
    public async Task Being_told_this_person_is_no_longer_an_admin_drops_a_live_proof_and_the_link()
    {
        _api.Answer(HttpStatusCode.NoContent);
        var state = State();
        (await state.ProbeForSidebarAsync()).Should().BeTrue();
        _api.Answer(HttpStatusCode.OK, """{"proof":"sealed","expiresAtUtc":"2026-11-15T12:10:00+00:00"}""");
        await state.ConfirmPasswordAsync("right");

        _api.Answer(HttpStatusCode.NotFound);
        (await state.ConfirmPasswordAsync("right")).Should().Be(AdminPasswordOutcome.NotAdmin);

        state.CurrentProof.Should().BeNull();
        var requestsSoFar = _api.Requests.Count;
        (await state.ProbeForSidebarAsync()).Should().BeFalse();
        _api.Requests.Should().HaveCount(requestsSoFar, "the API already said no; the sidebar does not ask again");
    }

    [Fact]
    public async Task The_APIs_word_on_the_proof_replaces_this_tabs()
    {
        _api.Answer(HttpStatusCode.OK, """{"proof":"sealed","expiresAtUtc":"2026-11-15T12:10:00+00:00"}""");
        var state = State();
        await state.ConfirmPasswordAsync("right");
        var changes = 0;
        state.Changed += () => changes++;

        // Same expiry: nothing to do.
        state.Reconcile(new DateTimeOffset(2026, 11, 15, 12, 10, 0, TimeSpan.Zero));
        changes.Should().Be(0);
        state.CurrentProof.Should().Be("sealed");

        // The API no longer honours it (the password changed in another tab, say).
        state.Reconcile(apiExpiry: null);
        changes.Should().Be(1);
        state.CurrentProof.Should().BeNull();
        state.PasswordConfirmedUntilUtc.Should().BeNull();

        // With no proof held there is nothing to reconcile, whatever the API says.
        state.Reconcile(new DateTimeOffset(2026, 11, 15, 12, 30, 0, TimeSpan.Zero));
        state.PasswordConfirmedUntilUtc.Should().BeNull();
        changes.Should().Be(1);
    }

    // ---------------------------------------------------------------- sidebar

    [Fact]
    public async Task The_sidebar_shows_the_Admin_link_to_an_admin_and_asks_once_per_tab()
    {
        _api.Answer(HttpStatusCode.NoContent);
        var provider = new AdminNavItemProvider(State());

        var first = await provider.GetItemsAsync();
        var second = await provider.GetItemsAsync();

        first.Should().ContainSingle().Which.Href.Should().Be("/admin");
        second.Should().BeEquivalentTo(first);
        _api.Requests.Should().ContainSingle("the desktop and mobile menus share one answer")
            .Which.Path.Should().Be("/api/admin/access");
    }

    [Fact]
    public async Task The_sidebar_shows_nothing_to_a_learner_and_does_not_ask_twice()
    {
        _api.Answer(HttpStatusCode.NotFound);
        var provider = new AdminNavItemProvider(State());

        (await provider.GetItemsAsync()).Should().BeEmpty();
        (await provider.GetItemsAsync()).Should().BeEmpty();

        _api.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_probe_that_got_no_answer_is_not_remembered_as_a_no()
    {
        // The API was restarting when this tab opened. The link must be able to appear once it
        // answers, without a reload.
        _api.Throw(new HttpRequestException("connection refused"));
        var provider = new AdminNavItemProvider(State());
        (await provider.GetItemsAsync()).Should().BeEmpty();

        _api.Answer(HttpStatusCode.InternalServerError);
        (await provider.GetItemsAsync()).Should().BeEmpty();

        _api.Answer(HttpStatusCode.NoContent);
        (await provider.GetItemsAsync()).Should().ContainSingle().Which.Href.Should().Be("/admin");
        _api.Requests.Should().HaveCount(3);
    }

    // ---------------------------------------------------------------- doubles

    private sealed record Recorded(string Path, string PathAndQuery, string? Proof, string? Body);

    private sealed class ScriptedApi : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.NotFound;
        private string _body = "";
        private Exception? _exception;

        public List<Recorded> Requests { get; } = [];

        public void Answer(HttpStatusCode status, string body = "")
        {
            _status = status;
            _body = body;
            _exception = null;
        }

        public void Throw(Exception exception) => _exception = exception;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Recorded(
                request.RequestUri!.AbsolutePath,
                request.RequestUri.PathAndQuery,
                request.Headers.TryGetValues(AdminApiClient.ProofHeader, out var values) ? values.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (_exception is not null)
            {
                throw _exception;
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Accepts every request and answers none, until the request is cancelled.</summary>
    private sealed class NeverAnswers : HttpMessageHandler
    {
        private int _abandoned;

        public int Abandoned => Volatile.Read(ref _abandoned);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _abandoned);
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
