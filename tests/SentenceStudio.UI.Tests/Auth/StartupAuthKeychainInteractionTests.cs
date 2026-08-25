using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;

namespace SentenceStudio.UI.Tests.Auth;

/// <summary>
/// Startup auth behaviour when the platform keystore refuses without user authorisation.
/// </summary>
/// <remarks>
/// <para>
/// The macOS AppKit head shipped a startup deadlock: MAUI's macOS SecureStorage reads the legacy
/// file-based keychain via <c>SecItemCopyMatching</c>, legacy items are ACL-gated on the creating
/// binary's code signature, and Debug builds of that head are ad-hoc signed — so after any rebuild
/// the cdhash no longer matched, macOS raised a modal SecurityAgent prompt, and the native read
/// blocked forever. It never returned and never threw, so <c>Blazor</c>'s
/// <c>AuthorizeRouteView</c> sat in its <c>Authorizing</c> state and the app showed
/// "Checking authentication..." indefinitely.
/// </para>
/// <para>
/// These tests pin the contract that prevents a recurrence: when the store reports
/// <see cref="SecureStorageReadStatus.InteractionRequired"/>, auth resolution must complete
/// promptly, report "no session", clear nothing, and leak nothing into the log.
/// </para>
/// </remarks>
public class StartupAuthKeychainInteractionTests
{
    private const string JwtKey = "auth_jwt";
    private const string RefreshKey = "auth_refresh";
    private const string ExpiresKey = "auth_expires";

    // ------------------------------------------------------- IdentityAuthService

    [Fact]
    public async Task HasStoredSessionAsync_WhenKeystoreNeedsUser_ReportsNoSession()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, "a-real-refresh-token");
        store.RequireInteractionFor(RefreshKey);

        var sut = CreateAuthService(store, out _);

        (await sut.HasStoredSessionAsync()).Should().BeFalse(
            "an unreadable keychain must present as signed out, not as a hang");
    }

    [Fact]
    public async Task HasStoredSessionAsync_WhenKeystoreNeedsUser_CompletesPromptly()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, "a-real-refresh-token");
        store.RequireInteractionFor(RefreshKey);

        var sut = CreateAuthService(store, out _);

        var call = sut.HasStoredSessionAsync();
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(call, "the startup probe must never wait on a UI prompt");
    }

    /// <summary>
    /// Non-destructive: the refresh token is still in the keystore afterwards. This is the rule
    /// that stops a "fix" from resolving the hang by wiping credentials.
    /// </summary>
    [Fact]
    public async Task HasStoredSessionAsync_WhenKeystoreNeedsUser_LeavesStoredTokensIntact()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, "a-real-refresh-token");
        store.RequireInteractionFor(RefreshKey);

        var sut = CreateAuthService(store, out _);
        await sut.HasStoredSessionAsync();

        store.Contains(RefreshKey).Should().BeTrue();
        store.Removals.Should().BeEmpty();
        store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task HasStoredSessionAsync_UsesNonInteractiveAccess()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, "token");

        var sut = CreateAuthService(store, out _);
        await sut.HasStoredSessionAsync();

        store.Accesses.Should().NotBeEmpty();
        store.Accesses.Should().OnlyContain(a => a == SecureStorageAccess.NoInteraction,
            "automatic startup reads must never be allowed to prompt");
    }

    [Fact]
    public async Task SignInAsync_Silent_WhenKeystoreNeedsUser_ReturnsNullWithoutNetworkCall()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(JwtKey, "jwt");
        store.Seed(RefreshKey, "refresh");
        store.RequireInteractionFor(JwtKey);

        var sut = CreateAuthService(store, out var http);

        (await sut.SignInAsync()).Should().BeNull();
        http.RequestCount.Should().Be(0, "there is no usable token to refresh with");
        store.Removals.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenKeystoreNeedsUser_ReturnsNullWithoutNetworkCall()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, "refresh");
        store.RequireInteractionFor(RefreshKey);

        var sut = CreateAuthService(store, out var http);

        (await sut.GetAccessTokenAsync(Array.Empty<string>())).Should().BeNull();
        http.RequestCount.Should().Be(0);
        store.Removals.Should().BeEmpty();
    }

    /// <summary>The refusal must not put the token, or its length, in the log.</summary>
    [Fact]
    public async Task KeychainRefusal_IsLoggedWithoutTokenOrPii()
    {
        const string secret = "eyJhbGciOiJIUzI1NiJ9.a-real-refresh-token.sig";

        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, secret);
        store.RequireInteractionFor(RefreshKey);

        var sut = CreateAuthService(store, out _, out var logs);
        await sut.HasStoredSessionAsync();
        await sut.SignInAsync();

        var text = logs.AllText;
        text.Should().NotContain(secret);
        text.Should().NotContain("a-real-refresh-token");
        text.Should().NotContain("eyJ");
        text.Should().NotContain(secret.Length.ToString());
    }

    // ------------------------------------------- MauiAuthenticationStateProvider

    /// <summary>
    /// End of the chain: the Blazor router asks for auth state, and must get an unauthenticated
    /// principal back rather than sitting in <c>Authorizing</c> forever.
    /// </summary>
    [Fact]
    public async Task AuthenticationStateProvider_WhenKeystoreNeedsUser_ResolvesToSignedOut()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, "refresh");
        store.RequireInteractionFor(RefreshKey);

        var authService = CreateAuthService(store, out _);
        var provider = new MauiAuthenticationStateProvider(
            authService, new NullLogger<MauiAuthenticationStateProvider>());

        var stateTask = provider.GetAuthenticationStateAsync();
        var finished = await Task.WhenAny(stateTask, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(stateTask, "startup must not block on a keychain prompt");

        var state = await stateTask;
        state.User.Identity?.IsAuthenticated.Should().NotBe(true);
    }

    [Fact]
    public async Task AuthenticationStateProvider_WithReadableSession_StaysOptimisticallySignedIn()
    {
        var store = new ScriptedSecureStorage();
        store.Seed(RefreshKey, "refresh");

        var authService = CreateAuthService(store, out _);
        var provider = new MauiAuthenticationStateProvider(
            authService, new NullLogger<MauiAuthenticationStateProvider>());

        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity?.IsAuthenticated.Should().BeTrue(
            "a readable refresh token still means the user is signed in");
    }

    // ------------------------------------------------------------------ helpers

    private static IdentityAuthService CreateAuthService(
        ScriptedSecureStorage store,
        out CountingHttpMessageHandler handler)
        => CreateAuthService(store, out handler, out _);

    private static IdentityAuthService CreateAuthService(
        ScriptedSecureStorage store,
        out CountingHttpMessageHandler handler,
        out ListLogger<IdentityAuthService> logger)
    {
        handler = new CountingHttpMessageHandler();
        logger = new ListLogger<IdentityAuthService>();

        return new IdentityAuthService(
            new SingleClientFactory(handler),
            store,
            new InMemoryPreferences(),
            logger);
    }

    private sealed class ScriptedSecureStorage : ISecureStorageService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _needInteraction = new(StringComparer.Ordinal);

        public List<SecureStorageAccess> Accesses { get; } = new();
        public List<string> Removals { get; } = new();
        public List<string> Writes { get; } = new();

        public void Seed(string key, string value) => _values[key] = value;

        public void RequireInteractionFor(string key) => _needInteraction.Add(key);

        public bool Contains(string key) => _values.ContainsKey(key);

        public Task<string?> GetAsync(string key)
        {
            // A caller that ignores TryGetAsync would deadlock on the real platform. Model that
            // as "no value" rather than hanging the test suite; the assertions on Accesses are
            // what prove the non-interactive path is used.
            if (_needInteraction.Contains(key))
                return Task.FromResult<string?>(null);

            _values.TryGetValue(key, out var v);
            return Task.FromResult(v);
        }

        public Task<SecureStorageReadResult> TryGetAsync(
            string key,
            SecureStorageAccess access,
            CancellationToken cancellationToken = default)
        {
            Accesses.Add(access);

            if (_needInteraction.Contains(key) && access == SecureStorageAccess.NoInteraction)
                return Task.FromResult(SecureStorageReadResult.NeedsInteraction);

            return Task.FromResult(_values.TryGetValue(key, out var v)
                ? SecureStorageReadResult.FromValue(v)
                : SecureStorageReadResult.Missing);
        }

        public Task SetAsync(string key, string value)
        {
            Writes.Add(key);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public bool Remove(string key)
        {
            Removals.Add(key);
            return _values.Remove(key);
        }

        public void RemoveAll()
        {
            Removals.Add("*");
            _values.Clear();
        }
    }

    private sealed class InMemoryPreferences : IPreferencesService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var v) && v is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public void Clear() => _values.Clear();
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://localhost:5001") };
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private int _count;

        public int RequestCount => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = new();

        public string AllText
        {
            get { lock (_lines) return string.Join("\n", _lines); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var p in pairs)
                        _lines.Add($"{p.Key}={p.Value}");
            }
        }
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
