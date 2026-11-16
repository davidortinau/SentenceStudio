using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.WebApp.Admin;

namespace SentenceStudio.WebApp.Tests.Admin;

/// <summary>The web app's client for the sync health routes, against a scripted API.</summary>
public sealed class AdminSyncHealthClientTests
{
    private readonly Scripted _api = new();

    private AdminApiClient Client() =>
        new(new HttpClient(_api) { BaseAddress = new Uri("http://api.test") }, NullLogger<AdminApiClient>.Instance);

    [Fact]
    public async Task The_app_read_returns_what_the_API_says_and_asks_once()
    {
        _api.Answer(HttpStatusCode.OK,
            """{"apiVersion":"1.2.3+abc","latestMigration":"20261115_X","appliedMigrations":9,"pendingMigrations":0,"serverChangeVersion":120,"oldestChangeVersion":4}""");

        var result = await Client().GetSyncHealthAppAsync();

        result.IsOk.Should().BeTrue();
        result.Value!.ApiVersion.Should().Be("1.2.3+abc");
        result.Value.LatestMigration.Should().Be("20261115_X");
        result.Value.ServerChangeVersion.Should().Be(120);
        _api.Paths.Should().Equal("/api/admin/sync-health/app");
    }

    [Fact]
    public async Task What_an_admin_searched_for_is_not_written_to_the_log_when_the_request_fails()
    {
        // The search text is a learner's name or email, and it travels in the query string.
        var log = new CapturedLog();
        var client = new AdminApiClient(new HttpClient(_api) { BaseAddress = new Uri("http://api.test") }, log);

        _api.Answer(HttpStatusCode.InternalServerError);
        await client.GetSyncHealthLearnersAsync(after: null, search: "jane@example.com", take: 50);
        _api.Throw = new HttpRequestException("connection refused");
        await client.GetSyncHealthLearnersAsync(after: null, search: "jane@example.com", take: 50);

        log.Lines.Should().HaveCount(2);
        log.Lines.Should().OnlyContain(line => line.Contains("/api/admin/sync-health/learners"));
        log.Lines.Should().NotContain(line => line.Contains("jane") || line.Contains("q=") || line.Contains('?'));
    }

    private sealed class CapturedLog : Microsoft.Extensions.Logging.ILogger<AdminApiClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task The_learner_list_sends_size_and_cursor_in_the_URL_and_a_search_in_the_body()
    {
        _api.Answer(HttpStatusCode.OK, """{"items":[{"profileId":"p1","displayName":"A","email":"a@x.test"}],"nextAfter":"p1"}""");

        var first = await Client().GetSyncHealthLearnersAsync(after: null, search: null, take: 50);
        await Client().GetSyncHealthLearnersAsync(after: "p 1", search: "  jane@x.test ", take: 50);
        await Client().GetSyncHealthLearnersAsync(after: null, search: "   ", take: 50);

        first.Value!.Items.Should().ContainSingle().Which.ProfileId.Should().Be("p1");
        first.Value.NextAfter.Should().Be("p1");
        _api.Requests.Should().Equal(
            "GET /api/admin/sync-health/learners?take=50",
            "POST /api/admin/sync-health/learners/search?take=50&after=p%201",
            "GET /api/admin/sync-health/learners?take=50");
        _api.Paths.Should().NotContain(path => path.Contains("jane"), "a name or email never travels in a URL");
        _api.Bodies[1].Should().Be("""{"q":"jane@x.test"}""");
    }

    [Fact]
    public async Task A_learners_health_reads_tables_devices_and_what_is_not_recorded()
    {
        _api.Answer(HttpStatusCode.OK, """
            {"learner":{"profileId":"p1","displayName":"A","email":"a@x.test"},
             "tables":[{"table":"VocabularyWord","status":"counted","rowCount":3,"newestChangeField":"UpdatedAt","newestChangeUtc":"2026-11-11T08:00:00Z"},
                       {"table":"Challenge","status":"no_owner","rowCount":null,"newestChangeField":null,"newestChangeUtc":null}],
             "newestChangeUtc":"2026-11-11T08:00:00Z",
             "devices":[{"storeId":"abc","downloadedThroughVersion":10,"uploadedThroughVersion":null,"changesBehind":2}],
             "serverChangeVersion":12,
             "notRecorded":["last_sync_time","refused_rows"]}
            """);

        var result = await Client().GetSyncHealthLearnerAsync("p/1");

        result.IsOk.Should().BeTrue();
        result.Value!.Tables[0].RowCount.Should().Be(3);
        result.Value.Tables[1].Status.Should().Be(AdminSyncCodes.TableNoOwner);
        result.Value.Devices.Single().ChangesBehind.Should().Be(2);
        result.Value.NotRecorded.Should().Equal(
            AdminSyncCodes.NotRecordedLastSyncTime, AdminSyncCodes.NotRecordedRefusedRows);
        _api.Paths.Should().Equal("/api/admin/sync-health/learners/p%2F1");
    }

    [Fact]
    public async Task A_404_is_NotFound_a_server_error_is_failed_and_neither_is_retried()
    {
        _api.Answer(HttpStatusCode.NotFound);
        (await Client().GetSyncHealthLearnerAsync("p1")).Status.Should().Be(AdminApiStatus.NotFound);

        _api.Answer(HttpStatusCode.BadGateway);
        (await Client().GetSyncHealthAppAsync()).Status.Should().Be(AdminApiStatus.Failed);

        _api.Answer(HttpStatusCode.BadRequest, """{"code":"invalid_request"}""");
        var refused = await Client().GetSyncHealthLearnersAsync(null, null, 999);
        refused.Status.Should().Be(AdminApiStatus.Refused);
        refused.Code.Should().Be("invalid_request");

        _api.Paths.Should().HaveCount(3, "one call is one request");
    }

    private sealed class Scripted : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.NotFound;
        private string _body = "";

        public List<string> Paths { get; } = [];

        /// <summary>Method and path of each request, in order.</summary>
        public List<string> Requests { get; } = [];

        /// <summary>The body of each request, in order; null for one with none.</summary>
        public List<string?> Bodies { get; } = [];

        /// <summary>When set, every request fails with it instead of being answered.</summary>
        public Exception? Throw { get; set; }

        public void Answer(HttpStatusCode status, string body = "")
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Requests.Add($"{request.Method} {request.RequestUri.PathAndQuery}");
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            if (Throw is not null)
            {
                throw Throw;
            }

            return await Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
