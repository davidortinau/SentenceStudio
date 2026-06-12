using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Platform;

/// <summary>
/// Per-circuit cached snapshot of the authenticated user's identifiers. Captured
/// once per circuit and reused across all hub method invocations so we don't
/// re-read <see cref="AuthenticationStateProvider"/> (and potentially re-hit
/// <see cref="UserManager{TUser}"/>) on every UI interaction.
/// </summary>
public sealed record CircuitUserState(string? NameIdentifier, string? UserProfileId)
{
    public static CircuitUserState Empty { get; } = new(null, null);
}

/// <summary>
/// Singleton accessor that surfaces the current <see cref="CircuitUserState"/>
/// to code that runs inside a Blazor circuit but isn't itself circuit-scoped
/// (notably the singleton <see cref="WebPreferencesService"/>).
///
/// Backed by <see cref="AsyncLocal{T}"/>, which propagates through awaits within
/// the same ExecutionContext. <see cref="CircuitUserStateHandler"/> sets the
/// value at the start of every inbound circuit activity (hub method invocation),
/// so any code awaited from that handler sees the correct user — even though
/// the singleton holding this accessor is shared across all circuits.
///
/// Modeled on the Steve Sanderson `CircuitServicesAccessor` pattern documented
/// at https://learn.microsoft.com/aspnet/core/blazor/blazor-server-ef-core (the
/// "Access AuthenticationStateProvider in outgoing request middleware" section).
/// </summary>
public sealed class CircuitUserStateAccessor
{
    private static readonly AsyncLocal<CircuitUserState?> _current = new();

    public CircuitUserState? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>
/// Scoped <see cref="CircuitHandler"/> that captures the authenticated user's
/// <see cref="ClaimsPrincipal"/> at circuit creation and republishes it through
/// <see cref="CircuitUserStateAccessor"/> for every inbound hub method invocation.
///
/// This is how the singleton <see cref="WebPreferencesService"/> resolves the
/// active user during the Blazor Web App InteractiveServer render pass, where
/// <see cref="IHttpContextAccessor.HttpContext"/> is null and per-user state
/// cannot be derived from the HTTP request.
///
/// State is captured lazily on first inbound activity (not in the constructor)
/// because <see cref="AuthenticationStateProvider"/> can take an async path
/// during the initial circuit warmup.
/// </summary>
public sealed class CircuitUserStateHandler : CircuitHandler
{
    private readonly CircuitUserStateAccessor _accessor;
    private readonly AuthenticationStateProvider _authProvider;
    private readonly IServiceProvider _scopedServices;
    private readonly ILogger<CircuitUserStateHandler> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private CircuitUserState? _cachedState;

    public CircuitUserStateHandler(
        CircuitUserStateAccessor accessor,
        AuthenticationStateProvider authProvider,
        IServiceProvider scopedServices,
        ILogger<CircuitUserStateHandler> logger)
    {
        _accessor = accessor;
        _authProvider = authProvider;
        _scopedServices = scopedServices;
        _logger = logger;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async ctx =>
        {
            _accessor.Current = await GetOrInitStateAsync();
            try
            {
                await next(ctx);
            }
            finally
            {
                _accessor.Current = null;
            }
        };
    }

    private async Task<CircuitUserState> GetOrInitStateAsync()
    {
        if (_cachedState is not null)
        {
            return _cachedState;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_cachedState is not null)
            {
                return _cachedState;
            }

            ClaimsPrincipal? principal = null;
            try
            {
                var auth = await _authProvider.GetAuthenticationStateAsync();
                principal = auth?.User;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CircuitUserStateHandler: GetAuthenticationStateAsync failed");
            }

            if (principal?.Identity?.IsAuthenticated != true)
            {
                _cachedState = CircuitUserState.Empty;
                return _cachedState;
            }

            var nameId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var profileId = principal.FindFirst(AuthClaimTypes.UserProfileId)?.Value;

            // Legacy-cookie fallback: cookies issued before AppUserClaimsPrincipalFactory
            // was registered won't carry the user_profile_id claim. Resolve it once via
            // UserManager and cache for the lifetime of the circuit so we don't repeat
            // the DB hit on every inbound activity.
            if (string.IsNullOrEmpty(profileId) && !string.IsNullOrEmpty(nameId))
            {
                try
                {
                    var userManager = _scopedServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await userManager.FindByIdAsync(nameId);
                    profileId = user?.UserProfileId;
                    if (string.IsNullOrEmpty(profileId))
                    {
                        _logger.LogWarning(
                            "CircuitUserStateHandler: legacy-cookie fallback found ApplicationUser '{NameId}' but UserProfileId is null/empty",
                            nameId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "CircuitUserStateHandler: legacy-cookie UserManager fallback failed for NameIdentifier '{NameId}'",
                        nameId);
                }
            }

            _cachedState = new CircuitUserState(nameId, profileId);
            return _cachedState;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
