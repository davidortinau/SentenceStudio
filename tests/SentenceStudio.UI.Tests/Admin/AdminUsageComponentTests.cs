using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Services.Progress;
using SentenceStudio.UI.Tests.Activities;
using SentenceStudio.UI.Tests.Coach;
using SentenceStudio.WebApp.Admin;
using SentenceStudio.WebApp.Components.Pages.Admin;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Admin;

/// <summary>
/// The AI usage and jobs page in a live circuit, against a scripted API: what an admin reads, that a
/// section that fails says so instead of drawing an empty table, and that "not recorded" is said.
/// </summary>
public sealed class AdminUsageComponentTests
{
    private const string SamRoute = "/api/admin/usage/sam";
    private const string ImportsRoute = "/api/admin/usage/video-imports";
    private const string PosRoute = "/api/admin/usage/part-of-speech";

    private const string SamBody = """
        {"fromLocalDate":"2026-11-02","weeksAsked":4,
         "learners":[{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"}],
         "days":[{"profileId":"p1","localDate":"2026-11-15","runs":2,"inputTokens":12000,"outputTokens":3000,"estimatedCostUsd":0.0123}],
         "weeks":[{"profileId":"p1","weekKey":"2026-W46","runs":2,"inputTokens":12000,"outputTokens":3000,"estimatedCostUsd":0.0123,"daysWithRuns":1}],
         "truncated":false,"notRecorded":["other_ai_calls"]}
        """;

    private const string ImportsBody = """
        {"items":[{"id":"imp-1","profileId":"p1","status":"failed","problem":"failed","reasonCode":"no_transcript","createdAtUtc":"2026-11-14T10:00:00Z","completedAtUtc":"2026-11-14T10:01:00Z","hasResource":false},
                  {"id":"imp-2","profileId":null,"status":"generating_vocabulary","problem":"stuck","reasonCode":null,"createdAtUtc":"2026-11-15T09:00:00Z","completedAtUtc":null,"hasResource":false}],
         "failedCount":1,"stuckCount":1,"reasons":[{"reasonCode":"no_transcript","count":1}],
         "stuckAfterMinutes":10,"learners":[{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"}],"truncated":false}
        """;

    private const string PosBody = """
        {"totalWords":6,"classified":3,"unclassified":3,"unownedWords":2,
         "learners":[{"learner":{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"},"words":3,"classified":1,"unclassified":2}],
         "truncated":false,"notRecorded":["backfill_last_run","backfill_enabled"]}
        """;

    private readonly ScriptedApi _api = new();

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped(_ => new AdminApiClient(
            new HttpClient(_api) { BaseAddress = new Uri("http://api.test") },
            NullLogger<AdminApiClient>.Instance));
        services.AddScoped<AdminSessionState>();
        services.AddScoped<NavigationManager, SentenceStudio.UI.Tests.Coach.TestNavigationManager>();
        services.AddScoped<NavigationMemoryService>();
        services.AddSingleton<IPreferencesService, SwitchablePreferences>();
        services.AddSingleton<IAppState, AppState>();
        services.AddSingleton<ProgressCacheService>();
        services.AddScoped<AuthenticationStateProvider, SignedIn>();
        services.AddScoped<IJSRuntime>(_ => new ModuleAwareJSRuntime());
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<SentenceStudio.WebUI.Shared.Sam.SamEntryState>();
        return services.BuildServiceProvider();
    }

    private static InteractiveTestRenderer Circuit(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());

    private static ParameterView Learner(string profileId) =>
        ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(AdminLearnerSyncHealth.ProfileId)] = profileId });

    private void AnswerAll()
    {
        _api.Answer(SamRoute, HttpStatusCode.OK, SamBody);
        _api.Answer(ImportsRoute, HttpStatusCode.OK, ImportsBody);
        _api.Answer(PosRoute, HttpStatusCode.OK, PosBody);
    }

    [Fact]
    public async Task The_page_shows_sam_usage_imports_and_backfill_counts_with_their_headers()
    {
        AnswerAll();
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminUsage>();

        circuit.Unhandled.Should().BeEmpty();
        var text = circuit.RenderedText(id);
        text.Should().Contain("alpha@x.test").And.Contain("2026-W46").And.Contain("12,000").And.Contain("0.0123");
        text.Should().Contain("Other AI calls: not recorded");
        text.Should().Contain("imp-1").And.Contain("imp-2").And.Contain("No transcript in the learner")
            .And.Contain("Recorded to no learner").And.Contain("Still not finished").And.Contain("Not ended");
        text.Should().Contain("Failed").And.Contain("Stuck", "the problem is words, not only an icon");
        text.Should().Contain("last run: not recorded").And.Contain("Words").And.Contain("Without one");
        text.Should().Contain("not the backfill's queue", "the counts are by recorded owner, which is not how the backfill picks words");
        text.Should().Contain("retried a moment ago", "stuck is counted from creation, which a retry does not reset");
        foreach (var table in new[]
                 {
                     "admin-usage-sam-weeks", "admin-usage-sam-days", "admin-usage-imports-table",
                     "admin-usage-import-reasons", "admin-usage-pos-learners"
                 })
        {
            circuit.HasElementWithId(id, table).Should().BeTrue(table);
        }

        _api.Paths.Should().Equal(SamRoute, ImportsRoute, PosRoute);
        _api.Queries[0].Should().Be("?weeks=4");
    }

    [Fact]
    public async Task Choosing_other_weeks_asks_for_sam_usage_again_and_nothing_else()
    {
        AnswerAll();
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminUsage>();

        await circuit.ChangeValueByIdAsync(id, "admin-usage-weeks", "8");

        circuit.Unhandled.Should().BeEmpty();
        _api.Paths.Should().Equal(SamRoute, ImportsRoute, PosRoute, SamRoute);
        _api.Queries.Last().Should().Be("?weeks=8");
    }

    [Fact]
    public async Task A_section_that_fails_says_so_and_the_others_still_show()
    {
        _api.Answer(SamRoute, HttpStatusCode.OK, SamBody);
        _api.Answer(ImportsRoute, HttpStatusCode.InternalServerError);
        _api.Answer(PosRoute, HttpStatusCode.OK, PosBody);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminUsage>();

        var text = circuit.RenderedText(id);
        text.Should().Contain("Imports could not be loaded").And.NotContain("No failed or stuck imports");
        circuit.HasElementWithId(id, "admin-usage-imports-table").Should().BeFalse();
        circuit.HasElementWithId(id, "admin-usage-sam-weeks").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-usage-pos-learners").Should().BeTrue();
    }

    [Fact]
    public async Task No_failed_imports_says_so_in_words()
    {
        AnswerAll();
        _api.Answer(ImportsRoute, HttpStatusCode.OK,
            """{"items":[],"failedCount":0,"stuckCount":0,"reasons":[],"stuckAfterMinutes":10,"learners":[],"truncated":false}""");
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminUsage>();

        circuit.RenderedText(id).Should().Contain("No failed or stuck imports");
        circuit.HasElementWithId(id, "admin-usage-imports-table").Should().BeFalse();
    }

    [Fact]
    public async Task A_person_the_API_refuses_sees_page_not_found_and_no_numbers()
    {
        _api.Answer(SamRoute, HttpStatusCode.NotFound);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminUsage>();

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("12,000");
        circuit.HasElementWithId(id, "admin-usage").Should().BeFalse();
        _api.Paths.Should().Equal(SamRoute);
    }

    [Fact]
    public async Task When_the_API_gives_no_answer_the_page_says_so_and_Try_again_loads_it()
    {
        _api.Answer(SamRoute, HttpStatusCode.BadGateway);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminUsage>();
        circuit.RenderedText(id).Should().Contain("could not be loaded");
        circuit.HasElementWithId(id, "admin-usage").Should().BeFalse();

        AnswerAll();
        await circuit.ClickButtonByIdAsync(id, "page-retry");

        circuit.HasElementWithId(id, "admin-usage").Should().BeTrue();
    }

    [Fact]
    public async Task A_refresh_the_API_now_refuses_closes_the_page()
    {
        AnswerAll();
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminUsage>();

        _api.Answer(SamRoute, HttpStatusCode.NotFound);
        await circuit.ClickButtonByIdAsync(id, "admin-usage-refresh");

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("12,000");
    }

    // ---------------------------------------------------------------- doubles

    private sealed class ScriptedApi : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public List<string> Paths { get; } = [];

        public List<string> Queries { get; } = [];

        public void Answer(string path, HttpStatusCode status, string body = "") => _answers[path] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Queries.Add(request.RequestUri.Query);
            var (status, body) = _answers.TryGetValue(path, out var answer) ? answer : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity("test"))));
    }
}
