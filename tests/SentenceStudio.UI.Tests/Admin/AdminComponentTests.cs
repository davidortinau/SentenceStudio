using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Services.Progress;
using SentenceStudio.UI.Tests.Activities;
using SentenceStudio.UI.Tests.Coach;
using SentenceStudio.WebApp.Admin;
using SentenceStudio.WebApp.Components.Admin;
using SentenceStudio.WebUI.Layout;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Admin;

/// <summary>
/// The admin portal's web components in a live circuit: what an admin sees, what everyone else
/// sees, and that nothing is asked or shown while a page is only prerendering.
/// </summary>
/// <remarks>
/// The API is the authority (its own suite proves who it admits). These tests pin the web side of
/// the same promise: a person the API does not admit sees the "Page not found" every unknown route
/// shows, and the Admin link is never drawn for them.
/// </remarks>
public sealed class AdminComponentTests
{
    private readonly ScriptedAdminApi _api = new();

    private ServiceProvider Services(params IHostNavItemProvider[] navProviders) =>
        Services(webAppAdminLink: false, navProviders);

    /// <param name="webAppAdminLink">Registers the web app's own sidebar provider, as its Program does.</param>
    private ServiceProvider Services(bool webAppAdminLink, params IHostNavItemProvider[] navProviders)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped(_ => new AdminApiClient(
            new HttpClient(_api) { BaseAddress = new Uri("http://api.test") },
            NullLogger<AdminApiClient>.Instance));
        services.AddScoped<AdminSessionState>();

        // What the shared sidebar injects.
        services.AddScoped<NavigationManager, SentenceStudio.UI.Tests.Coach.TestNavigationManager>();
        services.AddScoped<NavigationMemoryService>();
        services.AddSingleton<IPreferencesService, SwitchablePreferences>();
        services.AddSingleton<IAppState, AppState>();
        services.AddSingleton<ProgressCacheService>();
        services.AddScoped<AuthenticationStateProvider, SignedInState>();
        services.AddScoped<IJSRuntime>(_ => new ModuleAwareJSRuntime());
        services.AddScoped<BlazorLocalizationService>();

        // What the shared page header injects, for a test that renders a whole admin page.
        services.AddScoped<SentenceStudio.WebUI.Shared.Sam.SamEntryState>();
        if (webAppAdminLink)
        {
            services.AddScoped<IHostNavItemProvider, AdminNavItemProvider>();
        }

        foreach (var provider in navProviders)
        {
            services.AddSingleton(provider);
        }

        return services.BuildServiceProvider();
    }

    private static InteractiveTestRenderer Circuit(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());

    /// <summary>A gate around a marker, whose page-load is one real client call.</summary>
    private ParameterView GateParameters(IServiceProvider services, Action? onLoad = null) =>
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(AdminGate.ChildContent)] = (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "id", "secret-admin-content");
                builder.AddContent(2, "Admin only");
                builder.CloseElement();
            }),
            [nameof(AdminGate.Load)] = (Func<Task<AdminApiStatus>>)(async () =>
            {
                onLoad?.Invoke();
                return (await services.GetRequiredService<AdminApiClient>().GetSessionAsync(proof: null)).Status;
            })
        });

    private const string SessionRoute = "/api/admin/session";
    private const string SessionBody = """{"userId":"u1","email":"captain@example.test"}""";

    // ------------------------------------------------------------------- gate

    [Fact]
    public async Task The_gate_shows_the_page_once_the_API_has_answered_the_pages_first_request()
    {
        _api.Answer(SessionRoute, HttpStatusCode.OK, SessionBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var loads = 0;

        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services, () => loads++));

        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "secret-admin-content").Should().BeTrue();
        circuit.RenderedText(id).Should().NotContain("Page not found");
        loads.Should().Be(1);
        _api.Requests.Should().ContainSingle("the page's own request is the gate; there is no second probe");
    }

    [Fact]
    public async Task The_gate_shows_page_not_found_to_a_person_the_API_refuses()
    {
        _api.Answer(SessionRoute, HttpStatusCode.NotFound);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services));

        circuit.HasElementWithId(id, "secret-admin-content").Should().BeFalse();
        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("Admin only");
        circuit.RenderedComponentNames(id).Should().Contain(nameof(SentenceStudio.WebApp.Components.NotFoundContent),
            "the gate draws the router's own not-found view, not a copy of it");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task When_the_API_gives_no_answer_the_gate_says_so_and_shows_nothing(HttpStatusCode status)
    {
        // Not "Page not found": an admin caught by an outage must be told to try again, and
        // nothing may be drawn for anyone until the API has actually said yes.
        _api.Answer(SessionRoute, status);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services));

        circuit.HasElementWithId(id, "secret-admin-content").Should().BeFalse();
        circuit.RenderedText(id).Should().Contain("could not be loaded").And.NotContain("Page not found");
    }

    [Fact]
    public async Task When_the_API_cannot_be_reached_the_gate_says_so_and_shows_nothing()
    {
        _api.Throw = new HttpRequestException("connection refused");
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services));

        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "secret-admin-content").Should().BeFalse();
        circuit.RenderedText(id).Should().Contain("could not be loaded");
    }

    [Fact]
    public async Task While_the_API_has_not_answered_the_gate_shows_it_is_waiting_and_nothing_else()
    {
        // Found on the running stack: an API that accepts the connection and never answers left
        // the page blank. The page must say it is waiting, and draw no admin content meanwhile.
        var answer = new TaskCompletionSource<AdminApiStatus>();
        await using var services = Services();
        var circuit = Circuit(services);
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(AdminGate.ChildContent)] = (RenderFragment)(builder => builder.AddContent(0, "Admin only")),
            [nameof(AdminGate.Load)] = (Func<Task<AdminApiStatus>>)(() => answer.Task)
        });

        // The render does not finish until the API answers, so the id it returns is not available
        // yet. A renderer's first root component is always id 0; the last line checks that.
        const int id = 0;
        var rendering = circuit.RenderAsync<AdminGate>(parameters);
        await circuit.Dispatcher.InvokeAsync(async () => await Task.Yield());

        // The waiting mark is static markup, which a renderer holds as one block of text, so it
        // is found by its words here; the prerender tests find it by id in the real HTML.
        circuit.RenderedText(id).Should()
            .Contain("Loading").And.NotContain("Admin only").And.NotContain("Page not found");

        answer.SetResult(AdminApiStatus.Failed);
        (await rendering).Should().Be(id);
        circuit.RenderedText(id).Should()
            .Contain("could not be loaded").And.NotContain("Loading").And.NotContain("Admin only");
    }

    [Fact]
    public async Task Try_again_asks_in_the_same_circuit_and_shows_the_page_once_the_API_answers()
    {
        // Not a reload: that would start a new circuit and throw away a confirmed password.
        _api.Answer(SessionRoute, HttpStatusCode.BadGateway);
        await using var services = Services();
        var circuit = Circuit(services);
        var loads = 0;
        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services, () => loads++));
        circuit.HasElementWithId(id, "page-unavailable").Should().BeTrue();

        _api.Answer(SessionRoute, HttpStatusCode.OK, SessionBody);
        await circuit.ClickButtonByIdAsync(id, "page-retry");

        circuit.Unhandled.Should().BeEmpty();
        loads.Should().Be(2);
        circuit.HasElementWithId(id, "secret-admin-content").Should().BeTrue();
        circuit.HasElementWithId(id, "page-unavailable").Should().BeFalse();
    }

    [Fact]
    public async Task Try_again_that_gets_no_answer_either_stays_on_could_not_be_loaded()
    {
        _api.Answer(SessionRoute, HttpStatusCode.BadGateway);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services));

        await circuit.ClickButtonByIdAsync(id, "page-retry");

        circuit.HasElementWithId(id, "page-unavailable").Should().BeTrue();
        circuit.HasElementWithId(id, "secret-admin-content").Should().BeFalse();
    }

    [Fact]
    public async Task Try_again_can_end_in_page_not_found()
    {
        // The role was removed during the outage: the retry is the API's first real answer.
        _api.Answer(SessionRoute, HttpStatusCode.BadGateway);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services));

        _api.Answer(SessionRoute, HttpStatusCode.NotFound);
        await circuit.ClickButtonByIdAsync(id, "page-retry");

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("Admin only");
        circuit.HasElementWithId(id, "page-retry").Should().BeFalse();
    }

    [Fact]
    public async Task A_second_ask_while_one_is_in_flight_makes_no_second_request()
    {
        // A second click can still arrive after "Try again" has been replaced by the waiting
        // mark. Two requests would be two audit rows, and the later answer would decide the gate.
        var first = true;
        var loads = 0;
        var answer = new TaskCompletionSource<AdminApiStatus>();
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminGate>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(AdminGate.ChildContent)] = (RenderFragment)(builder => builder.AddContent(0, "Admin only")),
            [nameof(AdminGate.Load)] = (Func<Task<AdminApiStatus>>)(() =>
            {
                loads++;
                if (first)
                {
                    first = false;
                    return Task.FromResult(AdminApiStatus.Failed);
                }

                return answer.Task;
            })
        }));
        var gate = (AdminGate)circuit.LastRootComponent!;

        Task one = Task.CompletedTask, two = Task.CompletedTask;
        await circuit.Dispatcher.InvokeAsync(() =>
        {
            one = gate.RetryAsync();
            two = gate.RetryAsync();
        });

        loads.Should().Be(2, "the first render's ask, and one retry for two clicks");
        one.IsCompleted.Should().BeFalse();
        two.IsCompleted.Should().BeFalse("the second ask joins the first; it does not return before the answer");
        circuit.RenderedText(id).Should().Contain("Loading").And.NotContain("Admin only");

        answer.SetResult(AdminApiStatus.Ok);
        await circuit.Dispatcher.InvokeAsync(() => Task.WhenAll(one, two));

        circuit.Unhandled.Should().BeEmpty();
        loads.Should().Be(2);
        circuit.RenderedText(id).Should().Contain("Admin only").And.NotContain("could not be loaded");
    }

    public static TheoryData<Exception> LoadFaults => new()
    {
        new InvalidOperationException("a fault in the page's own loading"),
        new OperationCanceledException("the page cancelled its request"),
        new TaskCanceledException("the request was cancelled")
    };

    [Theory]
    [MemberData(nameof(LoadFaults))]
    public async Task A_first_request_that_throws_ends_in_could_not_be_loaded_not_a_mark_that_waits_for_ever(
        Exception fault)
    {
        // Every later admin page is wrapped in this gate. The client reports a failed call as a
        // status, but a page that passes its own cancellation, or has a fault of its own, throws.
        await using var services = Services();
        var circuit = Circuit(services);
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(AdminGate.ChildContent)] = (RenderFragment)(builder => builder.AddContent(0, "Admin only")),
            [nameof(AdminGate.Load)] = (Func<Task<AdminApiStatus>>)(() => Task.FromException<AdminApiStatus>(fault))
        });

        var id = await circuit.RenderAsync<AdminGate>(parameters);

        circuit.Unhandled.Should().BeEmpty("the gate must not take the circuit down");
        circuit.HasElementWithId(id, "page-unavailable").Should().BeTrue();
        circuit.RenderedText(id).Should()
            .NotContain("Loading").And.NotContain("Admin only").And.NotContain("Page not found");
    }

    [Fact]
    public async Task A_refusal_that_names_a_reason_still_means_the_API_admitted_this_person()
    {
        _api.Answer(SessionRoute, HttpStatusCode.BadRequest, """{"code":"invalid_request"}""");
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services));

        circuit.HasElementWithId(id, "secret-admin-content").Should().BeTrue();
    }

    [Fact]
    public async Task While_prerendering_the_gate_shows_only_that_it_is_waiting_and_asks_the_API_nothing()
    {
        _api.Answer(SessionRoute, HttpStatusCode.OK, SessionBody);
        await using var services = Services();
        await using var prerender = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var loads = 0;

        var html = await prerender.Dispatcher.InvokeAsync(async () =>
            (await prerender.RenderComponentAsync<AdminGate>(GateParameters(services, () => loads++))).ToHtmlString());

        html.Should().Contain("id=\"page-loading\"", "everyone gets the same waiting mark until the circuit asks");
        html.Should().NotContain("admin", "the first HTML must not say what the mark is waiting for");
        html.Should().NotContain("secret-admin-content").And.NotContain("Page not found").And.NotContain("could not");
        loads.Should().Be(0);
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_can_close_the_gate_when_a_later_request_is_refused()
    {
        _api.Answer(SessionRoute, HttpStatusCode.OK, SessionBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminGate>(GateParameters(services));
        circuit.HasElementWithId(id, "secret-admin-content").Should().BeTrue();

        await circuit.Dispatcher.InvokeAsync(() => ((AdminGate)circuit.LastRootComponent!).Close());

        circuit.HasElementWithId(id, "secret-admin-content").Should().BeFalse();
        circuit.RenderedText(id).Should().Contain("Page not found");
    }

    [Fact]
    public async Task The_front_pages_own_try_again_asks_in_the_same_circuit()
    {
        // An answer the page cannot use (here 429) admits the admin and leaves nothing to show.
        // Its "Try again" goes through the gate, not through a reload that drops a confirmed password.
        _api.Answer(SessionRoute, HttpStatusCode.TooManyRequests, """{"code":"locked_out"}""");
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<SentenceStudio.WebApp.Components.Pages.Admin.AdminHome>();
        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "admin-home-retry").Should().BeTrue();
        circuit.RenderedText(id).Should().Contain("did not answer as expected").And.NotContain("Reload");

        _api.Answer(SessionRoute, HttpStatusCode.OK, SessionBody);
        await circuit.ClickButtonByIdAsync(id, "admin-home-retry");

        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "admin-home-retry").Should().BeFalse();
        circuit.RenderedText(id).Should().Contain("Signed in as").And.Contain("captain@example.test");
        _api.Requests.Count(r => r.Path == SessionRoute).Should().Be(2);
    }

    // -------------------------------------------------------- password again

    [Fact]
    public async Task The_right_password_turns_the_card_to_confirmed()
    {
        _api.Answer("/api/admin/step-up", HttpStatusCode.OK,
            $$"""{"proof":"sealed","expiresAtUtc":"{{DateTimeOffset.UtcNow.AddMinutes(10):O}}"}""");
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminPasswordCard>();
        circuit.RenderedText(id).Should().Contain("Not confirmed");

        await circuit.InputValueByIdAsync(id, "admin-password", "right-password");
        await circuit.SubmitFormByIdAsync(id, "admin-password-form");

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain("Password confirmed");
        circuit.HasElementWithId(id, "admin-password").Should().BeFalse("the form goes away once confirmed");
        _api.Requests.Should().ContainSingle().Which.Body.Should().Be("""{"password":"right-password"}""");
        services.GetRequiredService<AdminSessionState>().CurrentProof.Should().Be("sealed");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "password_incorrect", "That password is not correct.")]
    [InlineData(HttpStatusCode.TooManyRequests, "locked_out", "Too many failed attempts")]
    [InlineData(HttpStatusCode.InternalServerError, "", "could not be checked")]
    public async Task A_refused_password_says_why_and_confirms_nothing(
        HttpStatusCode status, string code, string expectedMessage)
    {
        _api.Answer("/api/admin/step-up", status, $$"""{"code":"{{code}}"}""");
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminPasswordCard>();

        await circuit.InputValueByIdAsync(id, "admin-password", "a-password");
        await circuit.SubmitFormByIdAsync(id, "admin-password-form");

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain(expectedMessage).And.Contain("Not confirmed");
        circuit.AttributeValue(id, "admin-password", "value").Should().BeNullOrEmpty(
            "a typed password must not stay in the field after it was sent");
    }

    [Fact]
    public async Task An_empty_password_is_not_sent()
    {
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminPasswordCard>();

        await circuit.SubmitFormByIdAsync(id, "admin-password-form");

        _api.Requests.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain("Enter your password",
            "the button is never disabled, so the card has to say why nothing was sent");
    }

    [Fact]
    public async Task The_confirm_button_is_not_disabled_while_the_field_is_empty()
    {
        // Found on the running stack: a password filled and submitted with Enter in one go did
        // nothing. The server had not yet heard the field was filled, the button was still
        // disabled, and a browser ignores Enter in a form whose submit button is disabled.
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminPasswordCard>();

        circuit.HasElementWithId(id, "admin-password-confirm").Should().BeTrue();
        circuit.AttributesOfElementWithId(id, "admin-password-confirm").Should().NotContain("disabled");
    }

    [Fact]
    public async Task Being_refused_as_no_longer_an_admin_tells_the_page()
    {
        _api.Answer("/api/admin/step-up", HttpStatusCode.NotFound);
        await using var services = Services();
        var circuit = Circuit(services);
        var notAdmin = 0;
        var id = await circuit.RenderAsync<AdminPasswordCard>(ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                [nameof(AdminPasswordCard.OnNotAdmin)] = EventCallback.Factory.Create(new object(), () => notAdmin++)
            }));

        await circuit.InputValueByIdAsync(id, "admin-password", "a-password");
        await circuit.SubmitFormByIdAsync(id, "admin-password-form");

        notAdmin.Should().Be(1);
    }

    // ---------------------------------------------------------------- sidebar

    [Fact]
    public async Task The_sidebar_draws_the_Admin_link_for_an_admin()
    {
        _api.Answer("/api/admin/access", HttpStatusCode.NoContent);
        await using var services = Services(webAppAdminLink: true);
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<NavMenu>();

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain("Admin");
        circuit.RenderedHrefs(id).Should().Contain("/admin");
        _api.Requests.Should().ContainSingle().Which.Path.Should().Be("/api/admin/access");
    }

    [Fact]
    public async Task The_link_goes_on_the_next_navigation_after_the_API_says_this_person_is_no_longer_an_admin()
    {
        _api.Answer("/api/admin/access", HttpStatusCode.NoContent);
        await using var services = Services(webAppAdminLink: true);
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<NavMenu>();
        circuit.RenderedHrefs(id).Should().Contain("/admin");

        // An admin page learned it from a 404, and the person then moves to another page.
        services.GetRequiredService<AdminSessionState>().MarkNotAdmin();
        var nav = (SentenceStudio.UI.Tests.Coach.TestNavigationManager)services.GetRequiredService<NavigationManager>();
        await circuit.Dispatcher.InvokeAsync(() => nav.GoTo("/vocabulary"));
        await circuit.Dispatcher.InvokeAsync(async () => await Task.Yield());

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedHrefs(id).Should().NotContain("/admin");
    }

    [Fact]
    public async Task The_link_appears_on_the_next_navigation_when_the_first_ask_got_no_answer()
    {
        _api.Throw = new HttpRequestException("the API is restarting");
        await using var services = Services(webAppAdminLink: true);
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<NavMenu>();
        circuit.RenderedHrefs(id).Should().NotContain("/admin");

        _api.Throw = null;
        _api.Answer("/api/admin/access", HttpStatusCode.NoContent);
        var nav = (SentenceStudio.UI.Tests.Coach.TestNavigationManager)services.GetRequiredService<NavigationManager>();
        await circuit.Dispatcher.InvokeAsync(() => nav.GoTo("/vocabulary"));
        await circuit.Dispatcher.InvokeAsync(async () => await Task.Yield());

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedHrefs(id).Should().Contain("/admin");
    }

    [Fact]
    public async Task The_sidebar_draws_no_Admin_link_for_a_learner()
    {
        _api.Answer("/api/admin/access", HttpStatusCode.NotFound);
        await using var services = Services(webAppAdminLink: true);
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<NavMenu>();

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().NotContain("Admin");
        circuit.RenderedHrefs(id).Should().NotContain("/admin");
        _api.Requests.Should().ContainSingle("the sidebar asked, and was told no");
    }

    [Fact]
    public async Task A_host_with_no_provider_gets_the_shared_sidebar_unchanged()
    {
        // Every MAUI head: nothing registered, nothing asked, nothing drawn.
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<NavMenu>();

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().NotContain("Admin");
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_that_throws_does_not_take_the_sidebar_down()
    {
        await using var services = Services(new ThrowingNavProvider());
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<NavMenu>();

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().NotContain("Admin");
    }

    [Fact]
    public async Task While_prerendering_the_sidebar_asks_no_provider()
    {
        var provider = new CountingNavProvider();
        await using var services = Services(provider);
        await using var prerender = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await prerender.Dispatcher.InvokeAsync(async () =>
            (await prerender.RenderComponentAsync<NavMenu>()).ToHtmlString());

        provider.Calls.Should().Be(0);
        html.Should().NotContain("href=\"/admin\"");
    }

    // ---------------------------------------------------------------- doubles

    private sealed record Recorded(string Path, string? Body);

    private sealed class ScriptedAdminApi : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public List<Recorded> Requests { get; } = [];

        public Exception? Throw { get; set; }

        public void Answer(string path, HttpStatusCode status, string body = "") => _answers[path] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new Recorded(
                path, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (Throw is not null)
            {
                throw Throw;
            }

            var (status, body) = _answers.TryGetValue(path, out var answer) ? answer : (HttpStatusCode.NotFound, "");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SignedInState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity("test"))));
    }

    private sealed class ThrowingNavProvider : IHostNavItemProvider
    {
        public ValueTask<IReadOnlyList<HostNavItem>> GetItemsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the host's lookup failed");
    }

    private sealed class CountingNavProvider : IHostNavItemProvider
    {
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<HostNavItem>> GetItemsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<HostNavItem>>([new HostNavItem("/admin", "bi-shield-lock", "Admin")]);
        }
    }
}

/// <summary>The password card's clock: how long a confirmation is shown as live.</summary>
public sealed class AdminPasswordCountdownTests
{
    [Fact]
    public async Task The_minutes_shown_keep_falling_and_the_card_reverts_when_the_confirmation_runs_out()
    {
        var time = new SentenceStudio.Api.Tests.Coach.TestTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var api = new OneAnswer("""{"proof":"sealed","expiresAtUtc":"2026-11-15T12:10:00+00:00"}""");
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(time)
            .AddScoped(_ => new AdminApiClient(
                new HttpClient(api) { BaseAddress = new Uri("http://api.test") }, NullLogger<AdminApiClient>.Instance))
            .AddScoped<AdminSessionState>()
            .BuildServiceProvider();
        await using var _ = services;
        var circuit = new InteractiveTestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var id = await circuit.RenderAsync<AdminPasswordCard>();

        await circuit.InputValueByIdAsync(id, "admin-password", "right-password");
        await circuit.SubmitFormByIdAsync(id, "admin-password-form");
        circuit.RenderedText(id).Should().Contain("10 more minutes");

        // Nothing else re-renders the card; the tick has to.
        time.Advance(TimeSpan.FromMinutes(6));
        await circuit.Dispatcher.InvokeAsync(async () => await Task.Yield());
        circuit.RenderedText(id).Should().Contain("4 more minutes");

        time.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(30));
        await circuit.Dispatcher.InvokeAsync(async () => await Task.Yield());
        circuit.RenderedText(id).Should().Contain("less than a minute more");

        time.Advance(TimeSpan.FromMinutes(1));
        await circuit.Dispatcher.InvokeAsync(async () => await Task.Yield());
        circuit.RenderedText(id).Should().Contain("Not confirmed");
        circuit.HasElementWithId(id, "admin-password").Should().BeTrue("the form is back");
        circuit.Unhandled.Should().BeEmpty();
    }

    private sealed class OneAnswer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
