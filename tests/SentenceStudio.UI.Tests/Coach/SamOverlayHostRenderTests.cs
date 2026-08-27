using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Renders <see cref="SamOverlayHost"/> interactively and asserts on what it actually puts in the
/// tree, per authentication state.
/// </summary>
/// <remarks>
/// <para>
/// The static <c>HtmlRenderer</c> the other render tests use reports itself non-interactive, and
/// the host renders nothing at all in that mode by design — which would make an "is Sam absent"
/// assertion pass for the wrong reason. <see cref="InteractiveTestRenderer"/> reports an
/// interactive server circuit, so the gate under test is the authentication one rather than the
/// prerender one.
/// </para>
/// <para>
/// The host used to set its own authenticated flag to a literal <c>true</c>, reasoning that the
/// layout only mounted it inside an authenticated branch. It did not, and even where it does, a
/// component that hardcodes the answer cannot react when the answer changes underneath it — which
/// on a persistent MAUI scope it does, without the component being rebuilt.
/// </para>
/// </remarks>
public class SamOverlayHostRenderTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;

        public Harness(bool samOverlayAvailable = true)
        {
            Client = new FakeCoachApiClient
            {
                DurableHistoryAvailable = true,
                Availability = new CoachAvailabilityResponse
                {
                    IsAvailable = true,
                    State = CoachAvailabilityState.Available,
                    CanEditPlan = true,
                    IsDurableHistoryAvailable = true,
                    IsSamOverlayAvailable = samOverlayAvailable
                }
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICoachApiClient>(Client);
            services.AddScoped<BlazorLocalizationService>();
            // The coach's name comes from the learner's study language, so every component that
            // names it needs the resolver. The all-optional constructor makes this a one-liner:
            // with no language source it answers with the default persona.
            services.AddScoped<CoachPersona>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
            services.AddScoped<NavigationManager, TestNavigationManager>();
            services.AddScoped<AuthenticationStateProvider, TestAuthenticationStateProvider>();
            services.AddScoped<CoachFeatureFlags>();
            services.AddScoped<CoachConversationDirectory>();
            services.AddScoped<CoachMemoryDirectory>();
            services.AddScoped<CoachWorkspaceState>();
            services.AddScoped<CoachAccountBoundary>();

            _provider = services.BuildServiceProvider();

            Auth = (TestAuthenticationStateProvider)_provider
                .GetRequiredService<AuthenticationStateProvider>();
            Boundary = _provider.GetRequiredService<CoachAccountBoundary>();
            Renderer = new InteractiveTestRenderer(
                _provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public FakeCoachApiClient Client { get; }

        public TestAuthenticationStateProvider Auth { get; }

        public CoachAccountBoundary Boundary { get; }

        public InteractiveTestRenderer Renderer { get; }

        public async ValueTask DisposeAsync()
        {
            Renderer.Dispose();
            await _provider.DisposeAsync();
        }
    }

    // ================================================================ the gate

    [Fact]
    public async Task A_signed_out_shell_renders_no_Sam_surface_at_all()
    {
        await using var harness = new Harness();
        harness.Auth.SignOut();

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();

        harness.Renderer.RenderedElementIds(id).Should().NotContain(SamElementIds.Fab);
        harness.Renderer.RenderedElementIds(id).Should().NotContain(SamElementIds.Panel);
        harness.Renderer.RenderedComponentNames(id).Should().BeEmpty(
            "nothing coach-shaped belongs in the DOM of a shell with nobody signed in");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_signed_in_learner_gets_the_entry_control()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();

        harness.Renderer.RenderedElementIds(id).Should().Contain(SamElementIds.Fab);
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// The transition itself, without the component being rebuilt — the persistent-scope case.
    /// </summary>
    [Fact]
    public async Task Signing_out_removes_the_surface_from_a_host_that_is_already_mounted()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
        harness.Renderer.RenderedElementIds(id).Should().Contain(SamElementIds.Fab);

        harness.Auth.SignOut();
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        harness.Renderer.RenderedElementIds(id).Should().NotContain(SamElementIds.Fab);
        harness.Renderer.RenderedElementIds(id).Should().NotContain(SamElementIds.Panel);
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// And the surface comes back for the next learner, so the gate is a gate and not a kill
    /// switch.
    /// </summary>
    [Fact]
    public async Task Signing_in_as_the_next_learner_restores_the_entry_control()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
        harness.Auth.SignOut();
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        harness.Renderer.RenderedElementIds(id).Should().NotContain(SamElementIds.Fab);

        harness.Auth.SignIn("profile-b", "b@example.test");
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        harness.Renderer.RenderedElementIds(id).Should().Contain(SamElementIds.Fab);
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// Mounting the host does not, on its own, start a conversation. Opening is a learner action,
    /// and a host that resumed on mount would resume for whoever the scope last belonged to.
    /// </summary>
    [Fact]
    public async Task Mounting_the_host_asks_the_server_for_nothing()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        await harness.Renderer.RenderAsync<SamOverlayHost>();

        harness.Client.CreateConversationCalls.Should().Be(0);
        harness.Client.ListConversationCalls.Should().Be(0);
        harness.Client.MessagePageRequests.Should().BeEmpty();
    }
}

/// <summary>A <see cref="NavigationManager"/> a test can construct and point anywhere.</summary>
internal sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/dashboard");

    /// <summary>The last location this was asked to navigate to.</summary>
    public string? LastNavigatedTo { get; private set; }

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        LastNavigatedTo = uri;
        Uri = ToAbsoluteUri(uri).ToString();
    }
}
