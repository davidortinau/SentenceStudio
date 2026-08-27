using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services;
using SentenceStudio.Services.Theme;
using SentenceStudio.WebApp.Platform.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The bug this workstream exists to fix: on the web, one learner's theme was every learner's theme.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddSentenceStudioCoreServices</c> registered <c>ThemeService</c> as a singleton. On MAUI that
/// is right — the process is the device. On the web it meant one object, one tuple, and one
/// <c>ThemeChanged</c> invocation list for the whole server. <c>MainLayout</c> subscribes to that
/// event for the life of a circuit, so a learner picking Vapor did not merely change their own
/// colours: the singleton's state moved under everyone, and the event ran every other circuit's
/// handler and repainted their browser mid-lesson.
/// </para>
/// <para>
/// These tests assert the two halves of the fix — the registration is scoped, and two scopes are
/// genuinely independent in both state and events.
/// </para>
/// </remarks>
public class WebCircuitThemeIsolationTests
{
    /// <summary>
    /// Builds the web host's real appearance registrations plus the ambient services they need.
    /// </summary>
    // Fully qualified: the app has its own static SentenceStudio.ServiceProvider helper in scope.
    private static Microsoft.Extensions.DependencyInjection.ServiceProvider BuildWebProvider(
        IHttpContextAccessor? accessor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The web host builder registers this; the appearance cookie's Secure policy reads it.
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

        // A stub rather than the framework accessor: HttpContextAccessor is backed by a static
        // AsyncLocal, so two scopes on one thread would share whatever context was assigned last.
        // Null is also the honest value here — these tests model live circuits, where the request
        // that opened them is long finished.
        services.AddSingleton(accessor ?? new StubHttpContextAccessor());

        // A JS runtime that behaves the way Blazor's does before interop is allowed. This keeps the
        // real JsAppearanceCookieChannel in the graph — so the graph under test is the production
        // one — while giving it nothing to talk to.
        services.AddSingleton<IJSRuntime, UnavailableJsRuntime>();

        // The production registration, called directly — not a re-declaration of what it should be.
        services.AddBrowserAppearance();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    [Fact]
    public void ThemeService_is_registered_scoped_on_the_web_never_singleton()
    {
        var services = new ServiceCollection();
        services.AddBrowserAppearance();

        var descriptor = services.Single(d => d.ServiceType == typeof(ThemeService));

        descriptor.Lifetime.Should().Be(
            ServiceLifetime.Scoped,
            "a Blazor circuit is a DI scope; a singleton here is one theme for the whole server");
        descriptor.Lifetime.Should().NotBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_preference_store_and_cookie_channel_are_scoped_too()
    {
        var services = new ServiceCollection();
        services.AddBrowserAppearance();

        // A singleton store would put every browser's cookie value back in one field, which would
        // reintroduce the leak one layer down from the presentation state.
        services.Single(d => d.ServiceType == typeof(IThemePreferenceStore))
            .Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(IAppearanceCookieChannel))
            .Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void The_shared_core_registration_no_longer_registers_theme_state_at_all()
    {
        // The shared extension cannot pick a lifetime that is correct for both hosts, so it no
        // longer picks one. If it starts registering ThemeService again, the web host would resolve
        // whichever registration won and the leak could return silently.
        var services = new ServiceCollection();
        services.AddSentenceStudioCoreServices();

        services.Should().NotContain(
            d => d.ServiceType == typeof(ThemeService),
            "hosts call AddDeviceThemePresentation or AddBrowserAppearance explicitly");
    }

    [Fact]
    public void Two_circuits_get_different_theme_service_instances()
    {
        using var provider = BuildWebProvider();
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();

        var a = circuitA.ServiceProvider.GetRequiredService<ThemeService>();
        var b = circuitB.ServiceProvider.GetRequiredService<ThemeService>();

        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public async Task Two_circuits_hold_different_theme_tuples_at_the_same_time()
    {
        using var provider = BuildWebProvider();
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();

        var a = circuitA.ServiceProvider.GetRequiredService<ThemeService>();
        var b = circuitB.ServiceProvider.GetRequiredService<ThemeService>();

        await a.SetThemeAsync("vapor");
        await a.SetModeAsync(ThemeMode.Light);
        await a.SetFontScaleAsync(1.5);

        await b.SetThemeAsync("forest");

        a.Current.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.5));
        b.Current.Should().Be(
            new AppearanceSelection("forest", ThemeCatalog.DefaultMode, AppearanceSelection.DefaultFontScale),
            "circuit B never asked for Vapor, light mode, or 150% text");
    }

    [Fact]
    public async Task A_theme_change_in_one_circuit_raises_no_event_in_another()
    {
        using var provider = BuildWebProvider();
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();

        var a = circuitA.ServiceProvider.GetRequiredService<ThemeService>();
        var b = circuitB.ServiceProvider.GetRequiredService<ThemeService>();

        // Exactly what MainLayout does on first render.
        var eventsInA = new List<ThemeChangedEventArgs>();
        var eventsInB = new List<ThemeChangedEventArgs>();
        a.ThemeChanged += (_, e) => eventsInA.Add(e);
        b.ThemeChanged += (_, e) => eventsInB.Add(e);

        await a.SetThemeAsync("vapor");
        a.PreviewMode(ThemeMode.Light);

        eventsInA.Should().HaveCount(2);
        eventsInB.Should().BeEmpty("circuit B's browser must not repaint because circuit A changed theme");
    }

    [Fact]
    public async Task A_preview_and_revert_in_one_circuit_does_not_disturb_another()
    {
        using var provider = BuildWebProvider();
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();

        var a = circuitA.ServiceProvider.GetRequiredService<ThemeService>();
        var b = circuitB.ServiceProvider.GetRequiredService<ThemeService>();

        await b.SetThemeAsync("ocean");

        a.PreviewTheme("brite");
        b.CurrentTheme.Should().Be("ocean");

        await a.RevertAsync();
        b.CurrentTheme.Should().Be("ocean");
        b.CanRevert.Should().BeFalse("revert arming belongs to the circuit that previewed");
    }

    /// <summary>A Production host environment, matching how the app is actually deployed.</summary>
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SentenceStudio.WebApp";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// An accessor with no request behind it — the state a Blazor circuit is actually in.
    /// </summary>
    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>
    /// A JS runtime that refuses every call the way Blazor's does during prerendering. The real
    /// channel treats that as "no stored preference", which is what the isolation tests want:
    /// every scope starts from the default and then diverges only because of its own changes.
    /// </summary>
    private sealed class UnavailableJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time.");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time.");
    }
}
