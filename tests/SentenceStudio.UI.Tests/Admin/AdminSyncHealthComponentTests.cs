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
/// The sync health pages in a live circuit, against a scripted API: what an admin reads, that a
/// failed load says so instead of drawing an empty table, and that a refused person sees nothing.
/// </summary>
public sealed class AdminSyncHealthComponentTests
{
    private const string AppRoute = "/api/admin/sync-health/app";
    private const string LearnersRoute = "/api/admin/sync-health/learners";
    private const string LearnerRoute = "/api/admin/sync-health/learners/p1";

    private const string AppBody =
        """{"apiVersion":"1.2.3+abc","latestMigration":"20261115_Latest","appliedMigrations":9,"pendingMigrations":1,"serverChangeVersion":1200,"oldestChangeVersion":4}""";

    private const string LearnersBody =
        """{"items":[{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"},{"profileId":"p2","displayName":null,"email":null}],"nextAfter":"p2"}""";

    private const string LearnerBody = """
        {"learner":{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"},
         "tables":[{"table":"VocabularyWord","status":"counted","rowCount":1234,"newestChangeField":"UpdatedAt","newestChangeUtc":"2026-11-11T08:00:00Z"},
                   {"table":"SceneImage","status":"counted","rowCount":0,"newestChangeField":null,"newestChangeUtc":null},
                   {"table":"Challenge","status":"no_owner","rowCount":null,"newestChangeField":null,"newestChangeUtc":null}],
         "newestChangeUtc":"2026-11-11T08:00:00Z",
         "devices":[{"storeId":"0123456789abcdef0123456789abcdef","downloadedThroughVersion":1198,"uploadedThroughVersion":null,"changesBehind":2}],
         "serverChangeVersion":1200,
         "notRecorded":["last_sync_time","refused_rows"]}
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

    // ------------------------------------------------------------------ front page

    [Fact]
    public async Task The_front_page_shows_what_is_deployed_and_the_learners_with_headers_and_ids()
    {
        _api.Answer(AppRoute, HttpStatusCode.OK, AppBody);
        _api.Answer(LearnersRoute, HttpStatusCode.OK, LearnersBody);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminSyncHealth>();

        circuit.Unhandled.Should().BeEmpty();
        var text = circuit.RenderedText(id);
        text.Should().Contain("1.2.3+abc").And.Contain("20261115_Latest")
            .And.Contain("applied").And.Contain("pending").And.Contain("1,200");
        text.Should().Contain("Alpha").And.Contain("alpha@x.test").And.Contain("p2");
        text.Should().Contain("Not recorded", "a learner with no name or email says so rather than showing a blank");
        circuit.HasElementWithId(id, "admin-sync-learners-table").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-sync-open-p1").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-sync-more").Should().BeTrue("the API said there is a next page");
        circuit.RenderedHrefs(id).Should().Contain("/admin/sync-health/p1");
    }

    [Fact]
    public async Task Load_more_asks_for_the_next_page_by_cursor_and_appends_it()
    {
        _api.Answer(AppRoute, HttpStatusCode.OK, AppBody);
        _api.Answer(LearnersRoute, HttpStatusCode.OK, LearnersBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminSyncHealth>();

        _api.Answer(LearnersRoute, HttpStatusCode.OK,
            """{"items":[{"profileId":"p3","displayName":"Charlie","email":"c@x.test"}],"nextAfter":null}""");
        await circuit.ClickButtonByIdAsync(id, "admin-sync-more");

        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "admin-sync-open-p3").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-sync-open-p1").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-sync-more").Should().BeFalse();
        _api.Queries.Last().Should().Contain("after=p2");
    }

    [Fact]
    public async Task Searching_asks_the_API_for_the_text_and_replaces_the_list()
    {
        _api.Answer(AppRoute, HttpStatusCode.OK, AppBody);
        _api.Answer(LearnersRoute, HttpStatusCode.OK, LearnersBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminSyncHealth>();

        _api.Answer(LearnersRoute, HttpStatusCode.OK,
            """{"items":[{"profileId":"p9","displayName":"Nine","email":"nine@x.test"}],"nextAfter":null}""");
        await circuit.InputValueByIdAsync(id, "admin-sync-search", "nine");
        await circuit.SubmitFormByIdAsync(id, "admin-sync-search-form");

        circuit.HasElementWithId(id, "admin-sync-open-p9").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-sync-open-p1").Should().BeFalse();
        _api.Queries.Last().Should().Contain("q=nine").And.NotContain("after=");
    }

    [Fact]
    public async Task A_failed_learner_list_says_so_and_is_not_an_empty_table()
    {
        _api.Answer(AppRoute, HttpStatusCode.OK, AppBody);
        _api.Answer(LearnersRoute, HttpStatusCode.InternalServerError);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminSyncHealth>();

        // Static markup is held as one block by the renderer, so these are found by their words.
        circuit.RenderedText(id).Should().Contain("The learners could not be loaded")
            .And.NotContain("No learners found", "a failure is not 'no learners'");
        circuit.HasElementWithId(id, "admin-sync-learners-table").Should().BeFalse();
    }

    [Fact]
    public async Task A_person_the_API_refuses_sees_page_not_found_and_none_of_the_numbers()
    {
        _api.Answer(AppRoute, HttpStatusCode.NotFound);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminSyncHealth>();

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("1.2.3+abc");
        circuit.HasElementWithId(id, "admin-sync-health").Should().BeFalse();
        _api.Paths.Should().Equal(AppRoute);
    }

    [Fact]
    public async Task When_the_API_gives_no_answer_the_front_page_says_it_could_not_be_loaded_and_Try_again_asks_again()
    {
        _api.Answer(AppRoute, HttpStatusCode.BadGateway);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminSyncHealth>();
        circuit.RenderedText(id).Should().Contain("could not be loaded");
        circuit.HasElementWithId(id, "admin-sync-health").Should().BeFalse();

        _api.Answer(AppRoute, HttpStatusCode.OK, AppBody);
        _api.Answer(LearnersRoute, HttpStatusCode.OK, LearnersBody);
        await circuit.ClickButtonByIdAsync(id, "page-retry");

        circuit.HasElementWithId(id, "admin-sync-health").Should().BeTrue();
        _api.Paths.Count(p => p == AppRoute).Should().Be(2);
    }

    // ------------------------------------------------------------------ learner page

    [Fact]
    public async Task A_learners_page_shows_each_table_its_count_and_says_not_recorded_for_what_the_server_lacks()
    {
        _api.Answer(LearnerRoute, HttpStatusCode.OK, LearnerBody);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminLearnerSyncHealth>(Learner("p1"));

        circuit.Unhandled.Should().BeEmpty();
        var text = circuit.RenderedText(id);
        text.Should().Contain("VocabularyWord").And.Contain("1,234").And.Contain("2026-11-11 08:00:00");
        text.Should().Contain("No change column", "a table with no last-changed column says so rather than a blank");
        text.Should().Contain("Not counted", "an ownerless table is not passed off as zero rows");
        text.Should().Contain("0123456789abcdef0123456789abcdef").And.Contain("1,198");
        text.Should().Contain("Last sync time per device: not recorded")
            .And.Contain("Rows the server refused: not recorded");
        circuit.HasElementWithId(id, "admin-sync-tables").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-sync-devices").Should().BeTrue();
        _api.Paths.Should().Equal(LearnerRoute);
    }

    [Fact]
    public async Task A_learner_with_no_device_says_so()
    {
        _api.Answer(LearnerRoute, HttpStatusCode.OK, LearnerBody.Replace(
            """[{"storeId":"0123456789abcdef0123456789abcdef","downloadedThroughVersion":1198,"uploadedThroughVersion":null,"changesBehind":2}]""", "[]"));
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminLearnerSyncHealth>(Learner("p1"));

        circuit.RenderedText(id).Should().Contain("No device store is bound to this learner");
        circuit.HasElementWithId(id, "admin-sync-devices").Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_learner_is_page_not_found_and_does_not_hide_the_admins_own_link()
    {
        _api.Answer(LearnerRoute, HttpStatusCode.NotFound);
        _api.Answer("/api/admin/access", HttpStatusCode.NoContent);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminLearnerSyncHealth>(Learner("p1"));

        circuit.RenderedText(id).Should().Contain("Page not found");
        circuit.HasElementWithId(id, "admin-sync-learner").Should().BeFalse();
        (await services.GetRequiredService<AdminApiClient>().ProbeAsync()).Should().Be(AdminAccess.Admin);
    }

    [Fact]
    public async Task A_refresh_that_fails_keeps_the_numbers_on_screen_and_says_it_failed()
    {
        _api.Answer(LearnerRoute, HttpStatusCode.OK, LearnerBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminLearnerSyncHealth>(Learner("p1"));

        _api.Answer(LearnerRoute, HttpStatusCode.InternalServerError);
        await circuit.ClickButtonByIdAsync(id, "admin-sync-learner-refresh");

        circuit.RenderedText(id).Should().Contain("could not be refreshed")
            .And.Contain("1,234").And.Contain("last successful load");
    }

    [Fact]
    public async Task A_refresh_the_API_now_refuses_closes_the_page()
    {
        _api.Answer(LearnerRoute, HttpStatusCode.OK, LearnerBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminLearnerSyncHealth>(Learner("p1"));

        _api.Answer(LearnerRoute, HttpStatusCode.NotFound);
        await circuit.ClickButtonByIdAsync(id, "admin-sync-learner-refresh");

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("1,234");
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
