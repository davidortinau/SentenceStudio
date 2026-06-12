using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebApp.Platform;

/// <summary>
/// Webapp implementation of <see cref="IPreferencesService"/>.
///
/// IMPORTANT — multi-tenant safety:
/// Per-user keys (<c>active_profile_id</c>) MUST be derived from the current
/// authenticated request or circuit, not from a process-wide dictionary. This
/// service is registered as a singleton (because <see cref="WebSecureStorageService"/>
/// and other consumers depend on it as a singleton) so any per-user state stored
/// in the in-memory dict would leak across concurrent users AND would be wiped
/// on every container revision restart.
///
/// To handle this correctly:
/// * <c>active_profile_id</c> reads resolve from
///   <see cref="HttpContext.User"/> claims (HTTP requests + Blazor SSR prerender) OR
///   <see cref="CircuitUserStateAccessor.Current"/> (Blazor InteractiveServer circuit
///   pass, where HttpContext is null). The UserManager legacy-cookie fallback runs in
///   both contexts.
/// * <c>active_profile_id</c> and <c>is_onboarded</c> writes are no-ops — the
///   claim on the auth cookie is the source of truth; <c>is_onboarded</c> has no
///   readers anywhere in the codebase.
/// * Other keys continue to use the in-memory dict / persistent JSON file
///   (these are app-wide, not per-user).
/// </summary>
public sealed class WebPreferencesService : IPreferencesService
{
    // Keys that MUST be derived from the current authenticated request, not
    // from a shared process-wide dict. Writes are no-ops; reads are routed
    // through the auth cookie claims.
    private const string ActiveProfileIdKey = "active_profile_id";
    private const string IsOnboardedKey = "is_onboarded";
    private const string ResolvedUserProfileIdItem = "__resolved_user_profile_id";

    private readonly string _storagePath;
    private readonly object _syncRoot = new();
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly CircuitUserStateAccessor _circuitUserState;
    private readonly ILogger<WebPreferencesService> _logger;
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public WebPreferencesService(
        string storagePath,
        IHttpContextAccessor httpContextAccessor,
        CircuitUserStateAccessor circuitUserState,
        ILogger<WebPreferencesService> logger)
    {
        _storagePath = storagePath;
        _httpContextAccessor = httpContextAccessor;
        _circuitUserState = circuitUserState;
        _logger = logger;
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_storagePath))
        {
            var json = File.ReadAllText(_storagePath);
            _values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
        }
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (key == ActiveProfileIdKey)
        {
            // Per-user: resolve from the current request's authenticated principal.
            // NEVER read from the shared dict — would leak the most-recent-login's id
            // to every other user on this server instance.
            if (typeof(T) == typeof(string))
            {
                var resolved = ResolveActiveUserProfileId();
                return (T)(object)(resolved ?? string.Empty);
            }
            return defaultValue;
        }

        lock (_syncRoot)
        {
            if (!_values.TryGetValue(key, out var serialized))
            {
                return defaultValue;
            }

            return JsonSerializer.Deserialize<T>(serialized) ?? defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        if (key == ActiveProfileIdKey || key == IsOnboardedKey)
        {
            // No-op: active_profile_id is derived from the auth cookie's
            // user_profile_id claim. is_onboarded is dead — no readers in the
            // codebase (verified via grep across SentenceStudio.UI/Shared/WebApp).
            // Logging at Debug because legacy callers (AccountEndpoints, MainLayout)
            // still invoke this on every login.
            _logger.LogDebug(
                "WebPreferencesService.Set('{Key}') ignored — per-user state is derived from auth cookie claims, not preferences.",
                key);
            return;
        }

        lock (_syncRoot)
        {
            _values[key] = JsonSerializer.Serialize(value);
            Persist();
        }
    }

    public void Remove(string key)
    {
        if (key == ActiveProfileIdKey || key == IsOnboardedKey)
        {
            // Same reasoning as Set: per-user state lives on the auth cookie.
            // SignOutAsync invalidates the cookie which is the real "remove".
            _logger.LogDebug(
                "WebPreferencesService.Remove('{Key}') ignored — per-user state is cleared by SignOutAsync.",
                key);
            return;
        }

        lock (_syncRoot)
        {
            if (_values.Remove(key))
            {
                Persist();
            }
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _values.Clear();
            Persist();
        }
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(_values);
        File.WriteAllText(_storagePath, json);
    }

    /// <summary>
    /// Resolves the active UserProfileId for the current request or circuit.
    /// Falls through three tiers in order:
    ///   1. <see cref="HttpContext.User"/> claims (HTTP requests + SSR prerender pass)
    ///   2. <see cref="CircuitUserStateAccessor.Current"/> (Blazor InteractiveServer circuit pass)
    ///   3. <see cref="UserManager{TUser}.FindByIdAsync"/> on the HTTP request scope as a
    ///      legacy-cookie fallback for tier 1 — tier 2 already handles its own legacy fallback
    ///      at circuit-open time (see <see cref="CircuitUserStateHandler"/>).
    ///
    /// Tier 1 results are cached on <see cref="HttpContext.Items"/> for the rest of the request.
    /// </summary>
    private string? ResolveActiveUserProfileId()
    {
        var http = _httpContextAccessor.HttpContext;
        if (http?.User?.Identity?.IsAuthenticated == true)
        {
            return ResolveFromHttpContext(http);
        }

        // Blazor circuit path: HttpContext is null, so consult the per-circuit
        // snapshot captured by CircuitUserStateHandler at the start of each
        // inbound activity.
        var circuitState = _circuitUserState.Current;
        if (circuitState is { UserProfileId: var pid } && !string.IsNullOrEmpty(pid))
        {
            return pid;
        }

        return null;
    }

    private string? ResolveFromHttpContext(HttpContext http)
    {
        // Cache lookup — repositories often call Get("active_profile_id") many
        // times per request; one lookup per request is enough.
        if (http.Items.TryGetValue(ResolvedUserProfileIdItem, out var cached) && cached is string cachedId)
        {
            return string.IsNullOrEmpty(cachedId) ? null : cachedId;
        }

        // Fast path: the claim is on the cookie.
        var fromClaim = http.User.FindFirst(AuthClaimTypes.UserProfileId)?.Value;
        if (!string.IsNullOrEmpty(fromClaim))
        {
            http.Items[ResolvedUserProfileIdItem] = fromClaim;
            return fromClaim;
        }

        // Slow path: legacy cookie without the user_profile_id claim. Look up
        // the ApplicationUser by NameIdentifier (always present on Identity
        // cookies) and read UserProfileId. Cache the result so this runs once
        // per request, not once per repository call.
        var userId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            http.Items[ResolvedUserProfileIdItem] = string.Empty;
            return null;
        }

        try
        {
            // Use the request's existing DI scope rather than creating our own.
            var userManager = http.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            // We must call sync-blocking here because IPreferencesService.Get is sync.
            // The repositories that call this hit it inside their own async methods,
            // so the calling stack already deals with sync-over-async at a higher level.
            var user = userManager.FindByIdAsync(userId).GetAwaiter().GetResult();
            var profileId = user?.UserProfileId ?? string.Empty;
            http.Items[ResolvedUserProfileIdItem] = profileId;
            if (string.IsNullOrEmpty(profileId))
            {
                _logger.LogWarning(
                    "ResolveActiveUserProfileId: ApplicationUser '{UserId}' has no UserProfileId.",
                    userId);
                return null;
            }
            return profileId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ResolveActiveUserProfileId: failed to resolve UserProfileId for '{UserId}' via UserManager.",
                userId);
            http.Items[ResolvedUserProfileIdItem] = string.Empty;
            return null;
        }
    }
}
