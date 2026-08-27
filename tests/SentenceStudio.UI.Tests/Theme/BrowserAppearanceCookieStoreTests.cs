using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services;
using SentenceStudio.WebApp.Platform.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The per-browser cookie substrate: what SSR reads, what a circuit reads, and what happens when
/// the value is nonsense.
/// </summary>
/// <remarks>
/// <para>
/// The cookie exists because the appearance has to be known <i>synchronously, during the
/// server-side render</i> — before <c>App.razor</c> writes the <c>&lt;html&gt;</c> element.
/// Anything only reachable from the browser paints the default first and snaps afterwards. These
/// tests pin that read path, the circuit read path that takes over once <c>HttpContext</c> is gone,
/// and the validation that keeps a hand-edited cookie harmless.
/// </para>
/// </remarks>
public class BrowserAppearanceCookieStoreTests
{
    private static BrowserAppearanceCookieStore Build(
        HttpContext? context,
        FakeCookieChannel? channel = null,
        string environment = "Development")
    {
        return new BrowserAppearanceCookieStore(
            new StubHttpContextAccessor { HttpContext = context },
            channel ?? new FakeCookieChannel(),
            new StubHostEnvironment(environment),
            NullLogger<BrowserAppearanceCookieStore>.Instance);
    }

    private static DefaultHttpContext RequestWithCookie(string? token, bool https = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = https ? "https" : "http";
        if (token is not null)
        {
            context.Request.Headers.Cookie = $"{AppearanceCookie.Name}={Uri.EscapeDataString(token)}";
        }

        return context;
    }

    // -------------------------------------------------------------------------------------------
    // SSR read path
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Ssr_reads_the_browsers_cookie_synchronously_off_the_request()
    {
        var store = Build(RequestWithCookie("v1.vapor.light.125"));

        store.TryLoad(out var selection).Should().BeTrue();
        selection.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.25));
    }

    [Fact]
    public void Each_request_reads_its_own_browsers_cookie()
    {
        // Two learners, two browsers, one server. The scoped store means each request's HttpContext
        // is the only cookie its store can see.
        var learnerA = Build(RequestWithCookie("v1.vapor.light.125"));
        var learnerB = Build(RequestWithCookie("v1.forest.dark.100"));

        learnerA.TryLoad(out var a).Should().BeTrue();
        learnerB.TryLoad(out var b).Should().BeTrue();

        a.ThemeId.Should().Be("vapor");
        b.ThemeId.Should().Be("forest");
    }

    [Fact]
    public void The_theme_service_seeded_from_a_request_renders_that_browsers_choice()
    {
        // This is the App.razor path end to end: cookie on the request, attributes on <html>.
        var service = new ThemeService(Build(RequestWithCookie("v1.slate.light.90")));

        service.CurrentTheme.Should().Be("slate");
        service.CurrentMode.Should().Be("light");
        service.FontScale.Should().Be(0.9);
    }

    [Fact]
    public void A_request_with_no_cookie_yields_the_default()
    {
        var service = new ThemeService(Build(RequestWithCookie(null)));

        service.Current.Should().Be(AppearanceSelection.Default);
    }

    // -------------------------------------------------------------------------------------------
    // Invalid cookies
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("garbage")]
    [InlineData("v1.not-a-theme.dark.100")]
    [InlineData("v1.seoul-pop.sepia.100")]
    [InlineData("v1.seoul-pop.dark.9000")]
    [InlineData("v2.seoul-pop.dark.100")]
    [InlineData("")]
    public void An_invalid_cookie_falls_back_to_the_default_instead_of_throwing(string token)
    {
        var store = Build(RequestWithCookie(token));

        store.TryLoad(out _).Should().BeFalse();

        // And the service that consumes it renders something valid rather than failing the page.
        new ThemeService(store).Current.Should().Be(AppearanceSelection.Default);
    }

    [Fact]
    public void An_absurdly_long_cookie_is_rejected_without_being_parsed()
    {
        var store = Build(RequestWithCookie(new string('x', 4096)));

        store.TryLoad(out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_invalid_cookie_read_from_the_browser_also_falls_back()
    {
        var channel = new FakeCookieChannel { Value = "v1.not-a-theme.dark.100" };
        var store = Build(context: null, channel);

        (await store.LoadAsync()).Should().BeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Circuit read path
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inside_a_circuit_the_cookie_is_read_from_the_browser()
    {
        // HttpContext is null once the circuit is running: the request that carried the cookie
        // finished, and a WebSocket has no headers. JS interop is the only route left.
        var channel = new FakeCookieChannel { Value = "v1.forest.light.115" };
        var store = Build(context: null, channel);

        store.TryLoad(out _).Should().BeFalse("there is no request to read");

        var loaded = await store.LoadAsync();

        loaded.Should().Be(new AppearanceSelection("forest", ThemeMode.Light, 1.15));
        channel.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task A_circuit_read_is_cached_for_the_life_of_the_scope()
    {
        var channel = new FakeCookieChannel { Value = "v1.forest.light.115" };
        var store = Build(context: null, channel);

        await store.LoadAsync();
        await store.LoadAsync();
        store.TryLoad(out var cached).Should().BeTrue();

        channel.ReadCount.Should().Be(1, "one interop round trip per circuit is enough");
        cached.ThemeId.Should().Be("forest");
    }

    [Fact]
    public async Task An_ssr_miss_does_not_fall_through_to_interop()
    {
        // When an HttpContext exists, its cookies are authoritative. Asking the browser as well
        // would be a slower way to learn the same thing — and during prerender there is no JS
        // runtime to ask.
        var channel = new FakeCookieChannel { Value = "v1.forest.light.115" };
        var store = Build(RequestWithCookie(null), channel);

        (await store.LoadAsync()).Should().BeNull();
        channel.ReadCount.Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------
    // Writes
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_with_a_live_response_sets_a_cookie_header()
    {
        var context = RequestWithCookie(null);
        var store = Build(context);

        await store.SaveAsync(new AppearanceSelection("ocean", ThemeMode.Light, 1.05));

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{AppearanceCookie.Name}=v1.ocean.light.105");
        setCookie.Should().Contain("path=/", Exactly.Once());
        setCookie.Should().Contain("samesite=lax", "the appearance cookie is never sent cross-site");
        setCookie.Should().Contain("secure", "the request was HTTPS");
        setCookie.Should().NotContain("httponly",
            "the circuit has no response to write through, so the browser must be able to write it");
    }

    [Fact]
    public async Task A_write_over_plain_http_omits_secure_so_local_development_still_works()
    {
        var context = RequestWithCookie(null, https: false);
        var store = Build(context, environment: Environments.Development);

        await store.SaveAsync(AppearanceSelection.Default);

        context.Response.Headers.SetCookie.ToString().Should().NotContain("secure");
    }

    // -------------------------------------------------------------------------------------------
    // Secure policy
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task Outside_development_the_cookie_is_always_secure_even_when_the_app_sees_plain_http(
        string environment)
    {
        // Azure Container Apps terminates TLS at ingress and forwards over plain HTTP, so
        // Request.IsHttps is only true here if forwarded-header processing is configured AND
        // working. A Secure flag that quietly disappears when a proxy header goes missing is the
        // wrong shape for a security attribute, so outside Development it does not depend on one.
        var context = RequestWithCookie(null, https: false);
        var store = Build(context, environment: environment);

        await store.SaveAsync(AppearanceSelection.Default);

        context.Response.Headers.SetCookie.ToString().Should().Contain(
            "secure",
            $"a {environment} response is reaching the browser over TLS regardless of what the app sees");
    }

    [Fact]
    public async Task Outside_development_the_cookie_is_secure_over_https_too()
    {
        var context = RequestWithCookie(null, https: true);
        var store = Build(context, environment: Environments.Production);

        await store.SaveAsync(AppearanceSelection.Default);

        context.Response.Headers.SetCookie.ToString().Should().Contain("secure");
    }

    [Fact]
    public async Task Development_over_https_is_still_secure()
    {
        var context = RequestWithCookie(null, https: true);
        var store = Build(context, environment: Environments.Development);

        await store.SaveAsync(AppearanceSelection.Default);

        context.Response.Headers.SetCookie.ToString().Should().Contain("secure");
    }

    [Theory]
    [InlineData("Development", false, false)]
    [InlineData("Development", true, true)]
    [InlineData("Production", false, true)]
    [InlineData("Production", true, true)]
    public void The_secure_policy_is_environment_first_scheme_second(
        string environment,
        bool requestIsHttps,
        bool expectedSecure)
    {
        var options = AppearanceCookie.BuildOptions(
            RequestWithCookie(null, https: requestIsHttps),
            new StubHostEnvironment(environment));

        options.Secure.Should().Be(expectedSecure);
    }

    [Fact]
    public void The_browser_side_writer_decides_secure_from_the_scheme_the_browser_actually_used()
    {
        // The other half of the same policy. A circuit write goes through JS, where
        // window.location.protocol is the real scheme and cannot be lost to a missing proxy header.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.UI")))
        {
            directory = directory.Parent;
        }

        var appJs = File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "SentenceStudio.UI", "wwwroot", "js", "app.js"));

        appJs.Should().Contain("window.location.protocol === 'https:'");
        appJs.Should().Contain("'; Secure'");
    }

    [Fact]
    public async Task A_write_inside_a_circuit_goes_through_the_browser()
    {
        var channel = new FakeCookieChannel();
        var store = Build(context: null, channel);

        await store.SaveAsync(new AppearanceSelection("brite", ThemeMode.Dark, 1.5));

        channel.Written.Should().ContainSingle().Which.Should().Be("v1.brite.dark.150");
    }

    [Fact]
    public async Task A_write_after_the_response_started_goes_through_the_browser_too()
    {
        var context = RequestWithCookie(null);
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Response.HasStarted.Should().BeTrue();

        var channel = new FakeCookieChannel();
        var store = Build(context, channel);

        await store.SaveAsync(new AppearanceSelection("brite", ThemeMode.Dark, 1.5));

        channel.Written.Should().ContainSingle().Which.Should().Be("v1.brite.dark.150");
    }

    [Fact]
    public async Task A_written_value_is_readable_again_without_another_round_trip()
    {
        var channel = new FakeCookieChannel();
        var store = Build(context: null, channel);

        var selection = new AppearanceSelection("sunset", ThemeMode.Light, 1.2);
        await store.SaveAsync(selection);

        store.TryLoad(out var reread).Should().BeTrue();
        reread.Should().Be(selection);
        channel.ReadCount.Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------
    // The cookie itself
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_cookie_carries_no_identity_and_no_secret()
    {
        // Its entire contents are a catalogue theme id, a mode from a closed pair, and an integer
        // percentage — which is what makes it acceptable for it to be script-readable.
        var token = new AppearanceSelection("ocean", ThemeMode.Dark, 1.25).ToToken();

        token.Should().Be("v1.ocean.dark.125");
        token.Length.Should().BeLessThan(AppearanceSelection.MaxTokenLength);
    }

    [Fact]
    public void The_cookie_is_marked_essential_so_consent_gating_cannot_silently_drop_it()
    {
        var options = AppearanceCookie.BuildOptions(
            RequestWithCookie(null),
            new StubHostEnvironment(Environments.Development));

        options.IsEssential.Should().BeTrue();
        options.SameSite.Should().Be(SameSiteMode.Lax);
        options.Path.Should().Be("/");
        options.HttpOnly.Should().BeFalse();
        options.Expires.Should().NotBeNull();
    }

    /// <summary>A host environment with a settable name, for exercising the Secure policy.</summary>
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "SentenceStudio.WebApp";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// An accessor that holds its context in an ordinary field.
    /// </summary>
    /// <remarks>
    /// The framework's <see cref="HttpContextAccessor"/> is backed by a <b>static</b>
    /// <see cref="AsyncLocal{T}"/>, which is right in a server — each request runs on its own
    /// execution context — but wrong in a test that needs two requests side by side on one thread:
    /// assigning the second context silently overwrites the first for both instances. Modelling
    /// "each request sees its own context" with a field is the accurate stand-in.
    /// </remarks>
    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>An <see cref="IAppearanceCookieChannel"/> with an in-memory browser behind it.</summary>
    private sealed class FakeCookieChannel : IAppearanceCookieChannel
    {
        public string? Value { get; set; }
        public int ReadCount { get; private set; }
        public List<string> Written { get; } = [];

        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return new ValueTask<string?>(Value);
        }

        public ValueTask WriteAsync(string token, int lifetimeDays, CancellationToken cancellationToken = default)
        {
            Written.Add(token);
            Value = token;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A response whose headers are already on the wire, so <c>Set-Cookie</c> is no longer an
    /// option. Set as a feature rather than driven through <c>StartAsync</c> because
    /// <see cref="DefaultHttpContext"/>'s default body feature does not flip <c>HasStarted</c>.
    /// </summary>
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state) { }

        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
