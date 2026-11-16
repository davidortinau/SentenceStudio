using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.WebApp.Admin;

namespace SentenceStudio.WebApp.Tests.Admin;

/// <summary>
/// The web app's client for the feedback inbox and the agent tokens, against a scripted API: what
/// goes on the wire for each call, and what the sidebar's count remembers.
/// </summary>
public sealed class AdminReportsClientTests
{
    private readonly Scripted _api = new();

    private AdminApiClient Client() =>
        new(new HttpClient(_api) { BaseAddress = new Uri("http://api.test") }, NullLogger<AdminApiClient>.Instance);

    private AdminSessionState State() => new(Client(), TimeProvider.System);

    [Fact]
    public async Task The_flagged_answers_are_asked_for_by_status_and_read_with_their_codes()
    {
        _api.Answer("/api/admin/reports/sam", HttpStatusCode.OK, """
            {"items":[{"id":"r1","learnerId":"p1","status":"deferred","reason":"confusing","flagCount":1,
                       "firstFlaggedAtUtc":"2026-11-15T10:00:00Z","lastFlaggedAtUtc":"2026-11-15T10:00:00Z",
                       "failureCode":null,"stopReason":"completed","issueNumber":9,"issueUrl":"https://github.com/o/r/issues/9",
                       "hasExchange":true,"exchangeOpenedCount":2,"exchangeLastOpenedAtUtc":"2026-11-15T11:00:00Z","reviewedAtUtc":null}],
             "learners":[],"newCount":3,"unlistedFlagCount":1,"truncated":true}
            """);

        var filtered = await Client().GetSamReportsAsync("deferred", 200);
        await Client().GetSamReportsAsync(null, 50);

        filtered.IsOk.Should().BeTrue();
        var row = filtered.Value!.Items.Single();
        (row.Id, row.Status, row.Reason, row.IssueNumber, row.ExchangeOpenedCount).Should().Be(("r1", "deferred", "confusing", 9, 2));
        (filtered.Value.NewCount, filtered.Value.UnlistedFlagCount, filtered.Value.Truncated).Should().Be((3, 1, true));
        _api.Lines.Should().Equal(
            "GET /api/admin/reports/sam?take=200&status=deferred",
            "GET /api/admin/reports/sam?take=50");
    }

    [Fact]
    public async Task One_report_its_exchange_and_its_triage_each_name_the_report_in_the_path_and_nothing_in_the_query()
    {
        _api.Answer("/api/admin/reports/sam/r 1", HttpStatusCode.OK, """{"report":null,"learner":null,"flags":[],"flagsTruncated":false}""");
        _api.Answer("/api/admin/reports/sam/r 1/exchange", HttpStatusCode.OK,
            """{"reportId":"r 1","state":"available","exchanges":[{"flaggedAtUtc":null,"question":"q","answer":"a"}],"truncated":false,"openedCount":4}""");
        _api.Answer("/api/admin/reports/sam/r 1/triage", HttpStatusCode.Conflict, """{"code":"transition_refused"}""");
        var client = Client();

        await client.GetSamReportAsync("r 1");
        var exchange = await client.OpenSamExchangeAsync("r 1", "the-proof");
        var triage = await client.TriageSamReportAsync("r 1", AdminReportCodes.StatusNew, null);

        exchange.Value!.Exchanges.Single().Answer.Should().Be("a");
        exchange.Value.OpenedCount.Should().Be(4);
        triage.Status.Should().Be(AdminApiStatus.Refused);
        triage.Code.Should().Be(AdminReportCodes.FailureTransitionRefused);
        _api.Lines.Should().Equal(
            "GET /api/admin/reports/sam/r%201",
            "POST /api/admin/reports/sam/r%201/exchange",
            "POST /api/admin/reports/sam/r%201/triage");
        _api.Proofs.Should().Equal(null, "the-proof", null);
        _api.Bodies[1].Should().BeNull("opening an exchange sends nothing but the proof");
        _api.Bodies[2].Should().Be("""{"status":"new","issueNumber":null}""");
    }

    [Fact]
    public async Task The_GitHub_reports_and_a_saved_screen_state_are_read_with_the_state_as_JSON()
    {
        _api.Answer("/api/admin/reports/github", HttpStatusCode.OK, """
            {"items":[{"status":"submitted","issueNumber":77,"issueUrl":"https://github.com/o/r/issues/77","issueTitle":"t","failureCode":null,
                       "learnerId":"p1","platform":"web","appVersion":"1.0.0","screen":"coach","createdAtUtc":"2026-11-15T10:00:00Z",
                       "screenStateReference":"fs-0123456789ab"}],"learners":[],"truncated":false}
            """);
        _api.Answer("/api/admin/reports/github/screen-states/fs-0123456789ab", HttpStatusCode.OK,
            """{"reference":"fs-0123456789ab","learnerId":"p1","createdAtUtc":"2026-11-15T10:00:00Z","context":{"route":"/sam","state":{"k":"v"}}}""");
        var client = Client();

        var list = await client.GetGitHubReportsAsync(200);
        var state = await client.ReadScreenStateAsync("fs-0123456789ab", "the-proof");

        list.Value!.Items.Single().ScreenStateReference.Should().Be("fs-0123456789ab");
        state.Value!.Context.GetProperty("state").GetProperty("k").GetString().Should().Be("v");
        _api.Lines.Should().Equal(
            "GET /api/admin/reports/github?take=200",
            "POST /api/admin/reports/github/screen-states/fs-0123456789ab");
        _api.Proofs[1].Should().Be("the-proof");
    }

    [Fact]
    public async Task Tokens_are_listed_created_with_the_proof_and_revoked_by_id()
    {
        const string row = """
            {"id":"t1","name":"weekday check","permissions":["reports"],"state":"live","createdAtUtc":"2026-11-15T12:00:00Z",
             "createdByUserId":"u1","createdByEmail":null,"expiresAtUtc":"2026-12-15T12:00:00Z","revokedAtUtc":null,"lastUsedAtUtc":null}
            """;
        _api.Answer("/api/admin/tokens", HttpStatusCode.OK, $$"""{"items":[{{row}}],"lifetimeDays":30,"maxLive":10,"token":"ssadm_x","row":{{row}}}""");
        _api.Answer("/api/admin/tokens/t1/revoke", HttpStatusCode.OK, row);
        var client = Client();

        var list = await client.GetTokensAsync();
        var created = await client.CreateTokenAsync("weekday check", [AdminTokenCodes.PermissionReports], "the-proof");
        var revoked = await client.RevokeTokenAsync("t1");

        list.Value!.Items.Single().Permissions.Should().Equal(AdminTokenCodes.PermissionReports);
        created.Value!.Token.Should().Be("ssadm_x");
        revoked.Value!.Id.Should().Be("t1");
        _api.Lines.Should().Equal("GET /api/admin/tokens", "POST /api/admin/tokens", "POST /api/admin/tokens/t1/revoke");
        _api.Bodies[1].Should().Be("""{"name":"weekday check","permissions":["reports"]}""");
        _api.Proofs.Should().Equal(null, "the-proof", null);
    }

    // ---------------------------------------------------------------- the sidebar's count

    [Fact]
    public async Task The_count_of_new_reports_is_asked_once_and_kept_for_the_tab()
    {
        _api.Answer("/api/admin/reports/new-count", HttpStatusCode.OK, """{"sam":3}""");
        var state = State();

        (await state.GetNewReportCountAsync()).Should().Be(3);
        (await state.GetNewReportCountAsync()).Should().Be(3);

        _api.Lines.Should().Equal("GET /api/admin/reports/new-count");
    }

    [Fact]
    public async Task A_count_that_could_not_be_read_is_not_remembered_as_none()
    {
        _api.Answer("/api/admin/reports/new-count", HttpStatusCode.InternalServerError);
        var state = State();
        (await state.GetNewReportCountAsync()).Should().BeNull();

        _api.Answer("/api/admin/reports/new-count", HttpStatusCode.OK, """{"sam":0}""");
        (await state.GetNewReportCountAsync()).Should().Be(0);
        _api.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task What_the_inbox_just_read_replaces_the_count_without_a_request()
    {
        _api.Answer("/api/admin/reports/new-count", HttpStatusCode.OK, """{"sam":3}""");
        var state = State();
        await state.GetNewReportCountAsync();

        state.SetNewReportCount(1);

        (await state.GetNewReportCountAsync()).Should().Be(1);
        _api.Lines.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_sidebar_link_carries_the_count_only_when_there_is_something_new()
    {
        _api.Answer("/api/admin/access", HttpStatusCode.NoContent);
        _api.Answer("/api/admin/reports/new-count", HttpStatusCode.OK, """{"sam":2}""");
        var state = State();
        var provider = new AdminNavItemProvider(state);

        var withNew = (await provider.GetItemsAsync()).Single();
        state.SetNewReportCount(1);
        var withOne = (await provider.GetItemsAsync()).Single();
        state.SetNewReportCount(0);
        var withNone = (await provider.GetItemsAsync()).Single();

        (withNew.Href, withNew.Badge, withNew.BadgeLabel).Should().Be(("/admin", 2, "2 new flagged answers"));
        (withOne.Badge, withOne.BadgeLabel).Should().Be((1, "1 new flagged answer"));
        (withNone.Badge, withNone.BadgeLabel).Should().Be((null, null));
    }

    [Fact]
    public async Task Being_told_this_person_is_no_longer_an_admin_forgets_the_count()
    {
        _api.Answer("/api/admin/access", HttpStatusCode.NoContent);
        _api.Answer("/api/admin/reports/new-count", HttpStatusCode.OK, """{"sam":2}""");
        var state = State();
        var provider = new AdminNavItemProvider(state);
        (await provider.GetItemsAsync()).Should().ContainSingle();

        state.MarkNotAdmin();

        (await provider.GetItemsAsync()).Should().BeEmpty();
        _api.Lines.Should().HaveCount(2, "a person who is not an admin is not asked about again");
    }

    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public List<string> Lines { get; } = [];

        public List<string?> Bodies { get; } = [];

        public List<string?> Proofs { get; } = [];

        public void Answer(string path, HttpStatusCode status, string body = "") => _answers[path] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Lines.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Proofs.Add(request.Headers.TryGetValues(AdminApiClient.ProofHeader, out var values) ? values.Single() : null);
            var (status, body) = _answers.TryGetValue(Uri.UnescapeDataString(request.RequestUri.AbsolutePath), out var answer)
                ? answer
                : (HttpStatusCode.NotFound, "");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
