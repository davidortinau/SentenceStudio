using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// The one place the coach surfaces are torn down when the account they belong to changes.
/// </summary>
/// <remarks>
/// <para>
/// Every coach service is registered scoped, and scoped reads as "per learner" only where the
/// scope ends when the learner does. It does not here. In Blazor Server a circuit is one visit.
/// In the MAUI BlazorWebView the scope is tied to the <em>document</em>, not to the app and not to
/// the learner: <c>WebViewManager.AttachToPageAsync</c> disposes the current <c>PageContext</c>
/// and calls <c>_provider.CreateAsyncScope()</c> on every page attach, so a soft (client-side)
/// navigation keeps the scope while a full document load replaces it. Signing out is a soft
/// navigation, so the scope — and everything the previous learner's session cached — survives it.
/// That is why a decrypted transcript, a conversation title, and an open confirmation step could
/// all outlive an account switch and appear to the next learner before any owner-scoped request
/// had been made.
/// </para>
/// <para>
/// Do not rely on the scope boundary either way. It is neither "once per app" nor "once per
/// learner", and which one it looks like depends on whether the last navigation forced a document
/// load — which is a rendering detail, not an identity one.
/// </para>
/// <para>
/// So the boundary is watched rather than assumed. This subscribes to the
/// <see cref="AuthenticationStateProvider"/> for the life of the scope, not the life of a
/// component, because the component that would otherwise own the subscription is the layout, and
/// the layout is disposed and rebuilt around exactly the transition being watched for. It reacts
/// to <em>any</em> principal change — the sign-out button, a rejected refresh token, a silent
/// re-login as a different account, another tab — because the logout button is only one of the
/// ways an account ends.
/// </para>
/// <para>
/// It clears rather than reloads. Reloading would leave the previous learner's content on screen
/// until a request came back, and that window is the defect. The next learner's surfaces are
/// rebuilt from the server, from empty.
/// </para>
/// </remarks>
public sealed class CoachAccountBoundary : IDisposable
{
    private readonly CoachWorkspaceState _workspace;
    private readonly CoachConversationDirectory _directory;
    private readonly CoachFeatureFlags _flags;
    private readonly CoachMemoryDirectory? _memory;
    private readonly AuthenticationStateProvider? _authStateProvider;
    private readonly ILogger<CoachAccountBoundary>? _logger;
    private readonly object _gate = new();

    private bool _attached;
    private bool _seeded;
    private bool _disposed;
    private CoachAccountIdentity _identity = CoachAccountIdentity.Anonymous;

    /// <summary>
    /// Identifies this instance in logs. One boundary per DI scope, and the MAUI BlazorWebView
    /// builds a new scope on every page attach, so this is effectively the page-scope id.
    /// </summary>
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Exposed for diagnostics so hosts can correlate their scope with this one.</summary>
    public string InstanceId => _instanceId;

    public CoachAccountBoundary(
        CoachWorkspaceState workspace,
        CoachConversationDirectory directory,
        CoachFeatureFlags flags,
        CoachMemoryDirectory? memory = null,
        AuthenticationStateProvider? authStateProvider = null,
        ILogger<CoachAccountBoundary>? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _flags = flags ?? throw new ArgumentNullException(nameof(flags));
        _memory = memory;
        _authStateProvider = authStateProvider;
        _logger = logger;
    }

    /// <summary>
    /// Raised after the surfaces have been cleared because the account changed.
    /// </summary>
    /// <remarks>
    /// Raised after the clear, never before, so a handler that re-renders can only ever observe
    /// the empty state. The overlay uses it to collapse itself; anything else that holds visual
    /// state derived from a conversation should use it the same way.
    /// </remarks>
    public event Action<CoachAccountIdentity>? Crossed;

    /// <summary>Raised on every observation, so hosts can re-render their authenticated gate.</summary>
    public event Action? Changed;

    /// <summary>The account the coach surfaces currently belong to.</summary>
    public CoachAccountIdentity CurrentIdentity
    {
        get { lock (_gate) { return _identity; } }
    }

    /// <summary>True when the last observed principal was authenticated.</summary>
    /// <remarks>
    /// False until the first observation. A surface that has not yet been told who is signed in
    /// must not draw itself, because the alternative default — assume somebody is — is the
    /// hardcoded <c>true</c> this class was written to remove.
    /// </remarks>
    public bool IsAuthenticated
    {
        get { lock (_gate) { return _seeded && _identity.IsAuthenticated; } }
    }

    /// <summary>True once the current principal has been read at least once.</summary>
    public bool HasResolved
    {
        get { lock (_gate) { return _seeded; } }
    }

    /// <summary>
    /// Starts watching the authentication state, and applies whatever it currently says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent, and deliberately so: the layout, the overlay host, and the coach route may all
    /// call it, and every one of them can be created more than once in a scope that outlives them.
    /// A second subscription would be a second reset per transition and a leak per rebuild.
    /// </para>
    /// <para>
    /// The first application always clears, even when it finds a signed-in learner. At the moment
    /// this is first called nothing has been loaded yet, so clearing costs nothing — and the case
    /// it covers is the one that has no other defence: a scope where the sign-in happened on a
    /// screen this service was never mounted under, so the only principal it will ever see is
    /// already the new learner's.
    /// </para>
    /// </remarks>
    public async Task AttachAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_attached)
            {
                _attached = true;
                if (_authStateProvider is not null)
                {
                    _authStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
                }
            }
        }

        if (_authStateProvider is null)
        {
            return;
        }

        try
        {
            var state = await _authStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Apply(state.User);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An auth state that cannot be read is not evidence that the previous learner is still
            // there, and the surfaces this guards are the ones that must not be shown to somebody
            // who is not their owner. Unreadable is treated as signed out.
            _logger?.LogWarning(ex, "Coach account boundary could not read the authentication state.");
            Apply(principal: null);
        }
    }

    /// <summary>
    /// Applies a principal, clearing the coach surfaces when it names a different account.
    /// </summary>
    /// <remarks>
    /// Safe to call repeatedly with the same learner: a re-notification for an account that has
    /// not changed only publishes <see cref="Changed"/>, so a token refresh, a claims top-up, or
    /// the MAUI optimistic-principal handoff never costs a conversation.
    /// </remarks>
    public void Apply(ClaimsPrincipal? principal)
    {
        var next = CoachAccountIdentity.From(principal);
        bool crossed;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            crossed = !_seeded || !next.IsSameAccountAs(_identity);
            _identity = next;
            _seeded = true;
        }

        if (crossed)
        {
            ClearSurfaces();
        }

        // Subscriber counts are logged because the failure this class exists to catch looks
        // identical whether the event was never raised or was raised to nobody: on the MAUI
        // BlazorWebView a page attach builds a fresh DI scope, so a host that subscribed to a
        // previous scope's boundary is invisible here, and a host that never subscribed is too.
        _logger?.LogDebug(
            "Coach account boundary applied: authenticated={IsAuthenticated} crossed={Crossed} " +
            "seeded={Seeded} changedSubscribers={ChangedCount} crossedSubscribers={CrossedCount} " +
            "boundary={BoundaryId}",
            next.IsAuthenticated,
            crossed,
            true,
            Changed?.GetInvocationList().Length ?? 0,
            Crossed?.GetInvocationList().Length ?? 0,
            _instanceId);

        Changed?.Invoke();

        if (crossed)
        {
            Crossed?.Invoke(next);
        }
    }

    /// <summary>
    /// Clears every coach surface that holds learner content, in one place.
    /// </summary>
    /// <remarks>
    /// Public so a caller that already knows the account is over — an explicit sign-out handler,
    /// a test — can invoke the same path rather than reimplementing part of it. There is exactly
    /// one list of things to clear and this is it.
    /// </remarks>
    public void ClearSurfaces()
    {
        // The workspace first: it is the only one holding a decrypted transcript and a live
        // confirmation, so it is the one whose staleness is measured in seconds rather than in
        // renders.
        _workspace.ResetForAccountBoundary();

        // The shelf the workspace takes a thread from, including the selection and the cached
        // titles. Reset also drops the availability answer it was given.
        _directory.Reset();

        // Explicitly, not only through the directory. The two can be constructed with different
        // instances, and a flag that says "durable history is on for you" is an answer about a
        // learner who has gone.
        _flags.Reset();

        _memory?.Reset();
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> task) =>
        _ = HandleAuthenticationStateChangedAsync(task);

    private async Task HandleAuthenticationStateChangedAsync(Task<AuthenticationState> task)
    {
        try
        {
            var state = await task.ConfigureAwait(false);
            Apply(state.User);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Coach account boundary could not resolve a state change.");
            Apply(principal: null);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_attached && _authStateProvider is not null)
            {
                _authStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
            }

            _attached = false;
        }

        Crossed = null;
        Changed = null;
    }
}
