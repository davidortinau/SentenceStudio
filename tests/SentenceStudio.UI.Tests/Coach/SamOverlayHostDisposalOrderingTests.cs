using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
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
/// Account-switch event ordering around component disposal.
/// </summary>
/// <remarks>
/// <para>
/// The coach services are scoped, and on the MAUI BlazorWebView a scope is tied to the
/// <em>document</em>: <c>WebViewManager.AttachToPageAsync</c> disposes the current
/// <c>PageContext</c> — destroying the <c>WebViewRenderer</c> — and calls
/// <c>_provider.CreateAsyncScope()</c> for the next document. A soft navigation keeps the scope;
/// a forced document load replaces it. So a boundary event can be raised while the component that
/// subscribed to it is being torn down, and the component must not act on it.
/// </para>
/// <para>
/// Unsubscribing in <c>DisposeAsync</c> is not sufficient on its own. It closes the window for
/// <em>future</em> notifications, not for one already executing, and the resulting
/// <c>StateHasChanged</c> lands on a component the renderer has already removed. These tests
/// deliver events at and after disposal and pin that nothing is mutated and nothing is scheduled.
/// </para>
/// </remarks>
public class SamOverlayHostDisposalOrderingTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICoachApiClient>(new FakeCoachApiClient
            {
                DurableHistoryAvailable = true,
                Availability = new CoachAvailabilityResponse
                {
                    IsAvailable = true,
                    State = CoachAvailabilityState.Available,
                    CanEditPlan = true,
                    IsDurableHistoryAvailable = true,
                    IsSamOverlayAvailable = true
                }
            });
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
            Workspace = _provider.GetRequiredService<CoachWorkspaceState>();
            Renderer = new InteractiveTestRenderer(
                _provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public TestAuthenticationStateProvider Auth { get; }
        public CoachAccountBoundary Boundary { get; }
        public CoachWorkspaceState Workspace { get; }
        public InteractiveTestRenderer Renderer { get; }

        public async ValueTask DisposeAsync()
        {
            Renderer.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private static ClaimsPrincipal Learner(string profileId, string email) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(SentenceStudio.Contracts.AuthClaimTypes.UserProfileId, profileId),
                new Claim(ClaimTypes.Email, email),
            },
            authenticationType: "test"));

    private static bool ReadIsAuthenticated(IComponent host) =>
        (bool)host.GetType()
            .GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private static bool ReadHasUnread(IComponent host) =>
        (bool)host.GetType()
            .GetField("_hasUnread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private static object? ReadVisualState(IComponent host) =>
        host.GetType()
            .GetField("_visualState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host);

    // ------------------------------------------------------------------ live

    /// <summary>Control: while mounted, the host must still react. Otherwise the guard is a mute.</summary>
    [Fact]
    public async Task A_mounted_host_still_reacts_to_an_account_boundary_crossing()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
        var host = harness.Renderer.LastRootComponent!;
        ReadIsAuthenticated(host).Should().BeTrue();

        harness.Boundary.Apply(principal: null);

        ReadIsAuthenticated(host).Should().BeFalse(
            "a mounted host must observe the boundary, or the disposal guard is hiding a real bug");
        harness.Renderer.Unhandled.Should().BeEmpty();
        _ = id;
    }

    // -------------------------------------------------------------- disposed

    /// <summary>
    /// The regression. Under the previous code the handlers ran unconditionally, mutated the
    /// disposed host's fields and called <c>InvokeAsync(StateHasChanged)</c> on a component the
    /// renderer had already removed — the failure that surfaced later, from the finalizer thread,
    /// as an unobserved-task crit with no useful stack.
    /// </summary>
    [Fact]
    public async Task A_disposed_host_ignores_an_account_boundary_crossing()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
        var host = harness.Renderer.LastRootComponent!;
        ReadIsAuthenticated(host).Should().BeTrue();

        // A crossing notification that was already in flight when the document went away.
        var inFlight = CaptureHandlers(harness.Boundary, "Crossed");

        await harness.Renderer.DisposeRootComponentAsync(id);

        // Anonymous, deliberately: an unguarded handler writes identity.IsAuthenticated straight
        // onto the disposed host, so delivering a signed-in identity would pass either way.
        foreach (var handler in inFlight)
        {
            handler!.DynamicInvoke(CoachAccountIdentity.Anonymous);
        }

        ReadIsAuthenticated(host).Should().BeTrue(
            "a disposed host must not process boundary events at all — not even to agree with them");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disposed_host_ignores_a_boundary_change_that_is_not_a_crossing()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
        var host = harness.Renderer.LastRootComponent!;

        var inFlight = CaptureHandlers(harness.Boundary, "Changed");

        await harness.Renderer.DisposeRootComponentAsync(id);

        // Same account re-notified: raises Changed but not Crossed. The boundary itself is now
        // signed out, so an unguarded handler would read false off it and write that to the host.
        harness.Boundary.Apply(principal: null);
        foreach (var handler in inFlight)
        {
            handler!.DynamicInvoke();
        }

        ReadIsAuthenticated(host).Should().BeTrue();
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// The workspace outlives the host too, and its notifications are the ones that fire most
    /// often during a transition.
    /// </summary>
    [Fact]
    public async Task A_disposed_host_ignores_workspace_notifications()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
        var host = harness.Renderer.LastRootComponent!;
        ReadHasUnread(host).Should().BeFalse();

        // Coach.IsOpen is the condition under which the host's workspace handler sets its unread
        // badge — the field an unguarded handler would write. Opening it for real needs an async
        // conversation round-trip, so it is set directly: the subject here is the disposal guard,
        // not the open path.
        typeof(CoachWorkspaceState)
            .GetProperty(nameof(CoachWorkspaceState.IsOpen))!
            .SetValue(harness.Workspace, true);
        var inFlight = CaptureHandlers(harness.Workspace, "Changed", typeof(CoachWorkspaceState));

        await harness.Renderer.DisposeRootComponentAsync(id);

        foreach (var handler in inFlight)
        {
            handler!.DynamicInvoke();
        }

        ReadHasUnread(host).Should().BeFalse(
            "a disposed host must not accumulate unread state for a learner it will never show");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// A full A -> B -> A cycle delivered entirely after disposal. This is the shape of the native
    /// account switch, and none of it may touch a torn-down host.
    /// </summary>
    [Fact]
    public async Task A_disposed_host_survives_a_whole_account_switch_sequence()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
        var host = harness.Renderer.LastRootComponent!;

        var crossed = CaptureHandlers(harness.Boundary, "Crossed");
        var changed = CaptureHandlers(harness.Boundary, "Changed");

        await harness.Renderer.DisposeRootComponentAsync(id);

        foreach (var identity in new[]
                 {
                     CoachAccountIdentity.Anonymous,                                       // sign out
                     CoachAccountIdentity.From(Learner("profile-b", "b@example.test")),    // in as B
                     CoachAccountIdentity.From(Learner("profile-a", "a@example.test")),    // back to A
                     CoachAccountIdentity.Anonymous,                                       // sign out
                 })
        {
            harness.Boundary.Apply(principal: null);
            foreach (var handler in changed) handler!.DynamicInvoke();
            foreach (var handler in crossed) handler!.DynamicInvoke(identity);
        }

        harness.Workspace.ResetForAccountBoundary();

        ReadIsAuthenticated(host).Should().BeTrue("nothing after disposal may write to this host");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// Disposal must also unsubscribe, so a rebuilt host subscribes exactly once rather than the
    /// scope accumulating one handler per document.
    /// </summary>
    /// <remarks>
    /// Measured as a delta rather than an absolute count. The boundary has a second, deliberate
    /// subscriber that is not a component: <see cref="CoachPersona"/> watches Crossed for the life
    /// of the DI scope, because the coach's name belongs to the signed-in learner rather than to
    /// whichever surface happens to be mounted. Its one handler is correct and permanent; what this
    /// test exists to catch is a handler count that grows with the number of times the host has
    /// been built, which a delta measures and an absolute count would confuse with it.
    /// </remarks>
    [Fact]
    public async Task Rebuilding_the_host_in_one_scope_does_not_accumulate_subscriptions()
    {
        await using var harness = new Harness();
        harness.Auth.SignIn("profile-a", "a@example.test");

        // Taken after one full mount/dispose cycle so any scope-lived subscriber the host resolves
        // on the way past is already counted in the baseline.
        var warmUpId = await harness.Renderer.RenderAsync<SamOverlayHost>();
        await harness.Renderer.DisposeRootComponentAsync(warmUpId);

        var changedBaseline = SubscriberCount(harness.Boundary, "Changed");
        var crossedBaseline = SubscriberCount(harness.Boundary, "Crossed");

        for (var i = 0; i < 4; i++)
        {
            var id = await harness.Renderer.RenderAsync<SamOverlayHost>();
            await harness.Renderer.DisposeRootComponentAsync(id);
        }

        SubscriberCount(harness.Boundary, "Changed").Should().Be(
            changedBaseline, "a disposed host leaves nothing of itself behind");
        SubscriberCount(harness.Boundary, "Crossed").Should().Be(
            crossedBaseline, "a disposed host leaves nothing of itself behind");

        var liveId = await harness.Renderer.RenderAsync<SamOverlayHost>();
        SubscriberCount(harness.Boundary, "Changed").Should().Be(changedBaseline + 1);
        SubscriberCount(harness.Boundary, "Crossed").Should().Be(crossedBaseline + 1);

        await harness.Renderer.DisposeRootComponentAsync(liveId);
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    private static int SubscriberCount(CoachAccountBoundary boundary, string eventName)
    {
        var field = typeof(CoachAccountBoundary)
            .GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (Delegate?)field?.GetValue(boundary);
        return handler?.GetInvocationList().Length ?? 0;
    }

    /// <summary>
    /// Captures the host's currently-registered handlers so a test can deliver a notification that
    /// was <em>already in flight</em> when disposal began.
    /// </summary>
    /// <remarks>
    /// Disposing first and then raising the event through the boundary proves nothing: the host
    /// unsubscribes during disposal, so no handler runs and the assertion passes with or without
    /// the guard. The race being pinned is the other one — the boundary has already read its
    /// invocation list (or another thread is mid-callback) when the renderer removes the
    /// component. Capturing the delegate reproduces exactly that, and it is the only shape in
    /// which the guard is load-bearing.
    /// </remarks>
    private static Delegate?[] CaptureHandlers(object source, string eventName, Type? declaringType = null)
    {
        var field = (declaringType ?? source.GetType())
            .GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (Delegate?)field?.GetValue(source);
        return handler?.GetInvocationList() ?? Array.Empty<Delegate?>();
    }
}
