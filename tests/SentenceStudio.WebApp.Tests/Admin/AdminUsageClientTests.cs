using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.WebApp.Admin;

namespace SentenceStudio.WebApp.Tests.Admin;

/// <summary>The web app's client for the AI usage and jobs routes, against a scripted API.</summary>
public sealed class AdminUsageClientTests
{
    private readonly Scripted _api = new();

    private AdminApiClient Client() =>
        new(new HttpClient(_api) { BaseAddress = new Uri("http://api.test") }, NullLogger<AdminApiClient>.Instance);

    [Fact]
    public async Task Sam_usage_is_asked_for_by_weeks_and_read_per_day_and_week()
    {
        _api.Answer(HttpStatusCode.OK, """
            {"fromLocalDate":"2026-11-02","weeksAsked":2,
             "learners":[{"profileId":"p1","displayName":"A","email":"a@x.test"}],
             "days":[{"profileId":"p1","localDate":"2026-11-15","runs":2,"inputTokens":200,"outputTokens":100,"estimatedCostUsd":0.0123}],
             "weeks":[{"profileId":"p1","weekKey":"2026-W46","runs":2,"inputTokens":200,"outputTokens":100,"estimatedCostUsd":0.0123,"daysWithRuns":1}],
             "truncated":false,"notRecorded":["other_ai_calls"]}
            """);

        var result = await Client().GetSamUsageAsync(2);

        result.IsOk.Should().BeTrue();
        result.Value!.Days.Single().EstimatedCostUsd.Should().Be(0.0123m);
        result.Value.Weeks.Single().WeekKey.Should().Be("2026-W46");
        result.Value.NotRecorded.Should().Equal(AdminUsageCodes.NotRecordedOtherAiCalls);
        _api.Paths.Should().Equal("/api/admin/usage/sam?weeks=2");
    }

    [Fact]
    public async Task Imports_and_the_backfill_counts_are_read_with_their_codes()
    {
        _api.Answer(HttpStatusCode.OK, """
            {"items":[{"id":"i1","profileId":null,"status":"failed","problem":"failed","reasonCode":"no_transcript","createdAtUtc":"2026-11-14T10:00:00Z","completedAtUtc":null,"hasResource":false}],
             "failedCount":1,"stuckCount":0,"reasons":[{"reasonCode":"no_transcript","count":1}],
             "stuckAfterMinutes":10,"learners":[],"truncated":false}
            """);
        var imports = await Client().GetVideoImportsAsync(50);

        _api.Answer(HttpStatusCode.OK, """
            {"totalWords":6,"classified":3,"unclassified":3,"unownedWords":2,"learners":[],"truncated":false,
             "notRecorded":["backfill_last_run","backfill_enabled"]}
            """);
        var pos = await Client().GetPartOfSpeechAsync();

        imports.Value!.Items.Single().ReasonCode.Should().Be("no_transcript");
        imports.Value.Items.Single().ProfileId.Should().BeNull();
        pos.Value!.UnownedWords.Should().Be(2);
        pos.Value.NotRecorded.Should().Equal(
            AdminUsageCodes.NotRecordedBackfillLastRun, AdminUsageCodes.NotRecordedBackfillEnabled);
        _api.Paths.Should().Equal("/api/admin/usage/video-imports?take=50", "/api/admin/usage/part-of-speech");
    }

    [Fact]
    public async Task A_404_is_NotFound_and_a_server_error_is_failed_and_neither_is_retried()
    {
        _api.Answer(HttpStatusCode.NotFound);
        (await Client().GetSamUsageAsync(4)).Status.Should().Be(AdminApiStatus.NotFound);

        _api.Answer(HttpStatusCode.InternalServerError);
        (await Client().GetPartOfSpeechAsync()).Status.Should().Be(AdminApiStatus.Failed);

        _api.Paths.Should().HaveCount(2, "one call is one request");
    }

    private sealed class Scripted : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.NotFound;
        private string _body = "";

        public List<string> Paths { get; } = [];

        public void Answer(HttpStatusCode status, string body = "")
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
