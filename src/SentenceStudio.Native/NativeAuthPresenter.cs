using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Native;

/// <summary>One instance per signed-in native shell, never a process-wide service locator.</summary>
public sealed partial class NativeAuthPresenter(
    IAuthService auth,
    IPreferencesService preferences,
    UserProfileRepository profiles,
    DeviceUserScopeProvider scope,
    IPostLoginRouter postLogin,
    INativeSyncReadiness sync,
    NativePendingWrites pendingWrites,
    NativeResourcePresenter resources,
    SkillProfileRepository skills,
    ILogger<NativeAuthPresenter> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _trustedOwnerId;
    private DeviceUserScopeProvider.ScopeStamp? _transition;
    private DeviceUserScopeProvider.ScopeStamp? _published;

    private NativeAuthState _state =
        new(NativePhase.Idle, NativeDestination.SignIn);

    public NativeAuthState State =>
        _state is { UserId: { } owner }
        && (!string.Equals(_trustedOwnerId, owner, StringComparison.Ordinal)
            || !auth.IsSignedIn
            || preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) != owner
            || (_state.Phase == NativePhase.Ready
                ? _published is not { } published
                  || published.PublishedOwner != owner
                  || !scope.IsCurrent(published)
                  || !scope.TryGetUserProfileId(out var scopedOwner)
                  || scopedOwner != owner
                : _transition is not { } transition
                  || !scope.IsCurrent(transition)))
            ? new(NativePhase.Blocked, NativeDestination.SignIn,
                ErrorKey: NativeText.InvalidCredentials)
            : _state;

    public event Action<NativeAuthState>? StateChanged;

    public async Task<NativeAuthState> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RestoreCoreAsync(cancellationToken);
        }
        finally
        {
            if (State.UserId is null
                || State.Phase is not (NativePhase.Ready or NativePhase.PendingSync))
                ClearOwnedScope();
            _gate.Release();
        }
    }

    private async Task<NativeAuthState> RestoreCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (State is { Phase: NativePhase.Ready, UserId: { } readyOwner })
            {
                using var existingAuthentication =
                    await scope.EnterAuthenticationAsync(cancellationToken);
                if (await HasPublishedTokenOwnerAsync(readyOwner, cancellationToken))
                    return State;
            }

            if (!BeginAuthentication())
                return RefuseChangedOwner();
            using var authentication = await scope.EnterAuthenticationAsync(cancellationToken);
            if (_transition is not { } restoreStamp || !scope.IsCurrent(restoreStamp))
                return RefuseChangedOwner();
            SetState(new(NativePhase.Loading, NativeDestination.SignIn));
            var restored = await auth.SignInAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (restored is not null)
                return await EnterAuthenticatedAsync(restored, cancellationToken);

            // A transient refresh failure must not erase the stored session
            // or pretend that the optimistic Blazor principal is authenticated.
            var hasStoredSession = await auth.HasStoredSessionAsync();
            return SetState(hasStoredSession
                ? new(NativePhase.PendingSync, NativeDestination.SignIn,
                    ErrorKey: NativeText.ConnectionError, HasPendingSync: true)
                : new(NativePhase.Ready, NativeDestination.SignIn));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Native auth restoration cannot reach the server.");
            return SetState(new(NativePhase.PendingSync, NativeDestination.SignIn,
                ErrorKey: NativeText.ConnectionError, HasPendingSync: true));
        }
    }

    public async Task<NativeAuthState> SignInAsync(
        string email, string password, bool rememberMe = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                logger.LogWarning("Native sign-in refused an empty email or password.");
                return SetState(new(NativePhase.Error, NativeDestination.SignIn,
                    ErrorKey: NativeText.InvalidCredentials));
            }

            if (!BeginAuthentication())
                return RefuseChangedOwner();
            using var authentication = await scope.EnterAuthenticationAsync(cancellationToken);
            if (_transition is not { } signInStamp || !scope.IsCurrent(signInStamp))
                return RefuseChangedOwner();
            SetState(new(NativePhase.Loading, NativeDestination.SignIn));
            var result = await auth.SignInAsync(email.Trim(), password);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null)
                return SetState(new(NativePhase.Error, NativeDestination.SignIn,
                    ErrorKey: NativeText.InvalidCredentials));

            var state = await EnterAuthenticatedAsync(result, cancellationToken);
            if (state.Phase == NativePhase.Ready && state.UserId is { } signedInOwner
                && rememberMe && _published is { } published
                && scope.IsCurrent(published)
                && preferences.Get(
                    UserProfileRepository.ActiveProfileIdKey, string.Empty) == signedInOwner)
            {
                preferences.Set("remember_me", true);
                preferences.Set("remember_email", email.Trim());
            }
            return state;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Native sign-in HTTP or transport failure.");
            return SetState(new(NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: ex.StatusCode is null
                    ? NativeText.ConnectionError
                    : NativeText.UnexpectedError));
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "Native sign-in is unavailable.");
            return SetState(new(NativePhase.Blocked, NativeDestination.SignIn,
                ErrorKey: NativeText.SignInUnavailable));
        }
        catch (Exception ex) when (ex is AuthTokenPersistenceException
            or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "Native sign-in could not persist or decode the auth response.");
            return SetState(new(NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: NativeText.UnexpectedError));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Native sign-in failed unexpectedly.");
            return SetState(new(NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: NativeText.UnexpectedError));
        }
        finally
        {
            if (State.UserId is null
                || State.Phase is not (NativePhase.Ready or NativePhase.PendingSync))
                ClearOwnedScope();
            _gate.Release();
        }
    }

    public async Task<NativeAuthState> RetryReadinessAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is { Phase: NativePhase.PendingSync, UserId: null })
                return await RestoreCoreAsync(cancellationToken);
            if (State.Phase == NativePhase.Ready)
            {
                if (State.UserId is null && State.Destination == NativeDestination.SignIn)
                    return State;
                if (State.UserId is { } readyOwner
                    && await HasPublishedTokenOwnerAsync(readyOwner, cancellationToken))
                    return State;
                return RefuseChangedOwner();
            }
            if (State.UserId is not { } userId || !HasAuthenticatedOwner(userId))
                return RefuseChangedOwner();

            using var authentication = await scope.EnterAuthenticationAsync(cancellationToken);
            if (!HasAuthenticatedOwner(userId))
                return RefuseChangedOwner();
            return await ResolveReadinessAsync(userId, cancellationToken);
        }
        finally
        {
            if (State.UserId is null
                || State.Phase is not (NativePhase.Ready or NativePhase.PendingSync))
                ClearOwnedScope();
            _gate.Release();
        }
    }

    public async Task<NativeAuthState> SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var authentication = await scope.EnterAuthenticationAsync(cancellationToken);
            if (_trustedOwnerId is not { } owner)
            {
                if (auth.IsSignedIn || !string.IsNullOrWhiteSpace(
                        preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty)))
                    return RefuseChangedOwner();
                return SetState(new(NativePhase.Ready, NativeDestination.SignIn));
            }
            if (!string.Equals(
                    preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                    owner, StringComparison.Ordinal)
                || _published is { } published && !scope.IsCurrent(published)
                || _transition is { } transition && !scope.IsCurrent(transition))
                return RefuseChangedOwner();
            if (auth.IsSignedIn
                && ParseOwnerClaim(await auth.GetAccessTokenAsync([])) != owner)
                return RefuseChangedOwner();

            await auth.SignOutAsync();
            return SetState(new(NativePhase.Ready, NativeDestination.SignIn));
        }
        finally
        {
            ClearOwnedScope();
            _trustedOwnerId = null;
            _gate.Release();
        }
    }

    private async Task<NativeAuthState> EnterAuthenticatedAsync(
        AuthResult result, CancellationToken cancellationToken)
    {
        var owner = ParseOwnerClaim(result.AccessToken);
        if (string.IsNullOrWhiteSpace(owner)
            || result.ExpiresOn <= DateTimeOffset.UtcNow
            || !auth.IsSignedIn
            || _transition is not { } transition
            || !scope.IsCurrent(transition)
            || !string.Equals(
                owner,
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                StringComparison.Ordinal))
        {
            logger.LogWarning("Native sign-in refused an absent or mismatched token owner.");
            return SetState(new(NativePhase.Blocked, NativeDestination.SignIn,
                ErrorKey: NativeText.InvalidCredentials));
        }

        _trustedOwnerId = owner;
        try
        {
            if (!await HasCurrentTokenOwnerAsync(owner, cancellationToken)
                || _transition is not { } current
                || !scope.IsCurrent(current))
                return RefuseChangedOwner();

            return await ResolveReadinessAsync(owner, cancellationToken);
        }
        catch
        {
            ClearOwnedScope();
            throw;
        }
    }

    private async Task<NativeAuthState> ResolveReadinessAsync(
        string owner, CancellationToken cancellationToken)
    {
        if (!HasAuthenticatedOwner(owner) || _transition is not { } transition)
            return RefuseChangedOwner();

        NativeSyncOutcome outcome;
        var syncRevision = pendingWrites.Revision(owner);
        NativeDestination? destination = null;
        NativeAuthState? deferred = null;
        bool shouldMarkOnboarded = false;
        SetState(new(NativePhase.Loading, NativeDestination.Dashboard, owner));
        using (var lease = scope.TryBeginReadiness(owner, transition))
        {
            if (lease is null
                || !await HasCurrentTokenOwnerAsync(owner, cancellationToken))
                return RefuseChangedOwner();

            if (!lease.IsCurrent || !HasAuthenticatedOwner(owner))
                return RefuseChangedOwner();
            var localProfile = await profiles.GetByIdAsync(owner, cancellationToken);
            if (!lease.IsCurrent || !HasAuthenticatedOwner(owner))
                return RefuseChangedOwner();

            try
            {
                outcome = await sync.SynchronizeAsync(owner, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Post-login sync is offline; local data remains available.");
                outcome = NativeSyncOutcome.Offline;
            }
            if (!lease.IsCurrent || !HasAuthenticatedOwner(owner)
                || !await HasCurrentTokenOwnerAsync(owner, cancellationToken))
                return RefuseChangedOwner();

            if (localProfile is null && outcome == NativeSyncOutcome.Confirmed)
            {
                localProfile = await profiles.GetByIdAsync(owner, cancellationToken);
                if (!lease.IsCurrent || !HasAuthenticatedOwner(owner))
                    return RefuseChangedOwner();
            }

            var hasPopulatedProfile = localProfile is not null
                && !string.IsNullOrWhiteSpace(localProfile.NativeLanguage)
                && !string.IsNullOrWhiteSpace(localProfile.TargetLanguage);
            if (localProfile is null && outcome != NativeSyncOutcome.Confirmed)
            {
                deferred = new(NativePhase.PendingSync, NativeDestination.SignIn,
                    owner, NativeText.ConnectionError, HasPendingSync: true);
            }
            else
            {
                var route = await postLogin.DecideRouteAsync(owner);
                if (!lease.IsCurrent || !HasAuthenticatedOwner(owner)
                    || !await HasCurrentTokenOwnerAsync(owner, cancellationToken))
                    return RefuseChangedOwner();
                if (route.DeferUntilSyncCompletes)
                {
                    deferred = new(NativePhase.PendingSync, NativeDestination.SignIn,
                        owner, NativeText.ConnectionError, HasPendingSync: true);
                }
                else
                {
                    destination = route.Path == "/onboarding"
                        ? NativeDestination.Onboarding
                        : NativeDestination.Dashboard;
                    if (route.Path is not ("/" or "/onboarding")
                        || !hasPopulatedProfile && destination != NativeDestination.Onboarding)
                        throw new InvalidOperationException(
                            "Post-login router returned an unsupported native destination.");

                    if (!lease.IsCurrent || !HasAuthenticatedOwner(owner)
                        || !await HasCurrentTokenOwnerAsync(owner, cancellationToken))
                        return RefuseChangedOwner();
                    shouldMarkOnboarded = route.ShouldMarkOnboarded;
                }
            }
        }

        if (deferred is not null)
            return SetState(deferred);
        if (destination is not { } readyDestination)
            throw new InvalidOperationException("Post-login router did not approve a native destination.");
        if (!await HasCurrentTokenOwnerAsync(owner, cancellationToken)
            || _transition is not { } current
            || !scope.TryPublishAuthenticatedUser(
                owner, current, cancellationToken, out var published))
            return RefuseChangedOwner();

        _transition = null;
        _published = published;
        if (!await HasPublishedTokenOwnerAsync(owner, cancellationToken))
            return RefuseChangedOwner();
        if (shouldMarkOnboarded)
            preferences.Set("is_onboarded", true);
        if (!scope.IsCurrent(published)
            || preferences.Get(
                UserProfileRepository.ActiveProfileIdKey, string.Empty) != owner)
            return RefuseChangedOwner();
        if (outcome == NativeSyncOutcome.Confirmed)
            pendingWrites.MarkConfirmedIfUnchanged(owner, syncRevision);
        return SetState(new(NativePhase.Ready, readyDestination, owner,
            HasPendingSync: outcome != NativeSyncOutcome.Confirmed
                || pendingWrites.IsPending(owner)));
    }

    private bool HasAuthenticatedOwner(string userId) =>
        string.Equals(_trustedOwnerId, userId, StringComparison.Ordinal)
        && ((_transition is { } transition && scope.IsCurrent(transition))
            || (_published is { } published
                && string.Equals(published.PublishedOwner, userId, StringComparison.Ordinal)
                && scope.IsCurrent(published)))
        && auth.IsSignedIn
        && string.Equals(
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            userId,
            StringComparison.Ordinal);

    private async Task<bool> HasCurrentTokenOwnerAsync(
        string userId, CancellationToken cancellationToken)
    {
        if (!HasAuthenticatedOwner(userId))
            return false;
        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        return auth.IsSignedIn
            && ParseOwnerClaim(token) == userId
            && HasAuthenticatedOwner(userId);
    }

    private async Task<bool> HasPublishedTokenOwnerAsync(
        string owner, CancellationToken cancellationToken)
    {
        if (_published is not { } published
            || !string.Equals(_trustedOwnerId, owner, StringComparison.Ordinal)
            || !scope.IsCurrent(published)
            || !scope.TryGetUserProfileId(out var scopedOwner)
            || scopedOwner != owner
            || !auth.IsSignedIn
            || preferences.Get(
                UserProfileRepository.ActiveProfileIdKey, string.Empty) != owner)
            return false;

        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        return auth.IsSignedIn && ParseOwnerClaim(token) == owner
            && scope.IsCurrent(published)
            && preferences.Get(
                UserProfileRepository.ActiveProfileIdKey, string.Empty) == owner;
    }

    private bool BeginAuthentication()
    {
        if (!scope.TryBeginAuthentication(_published, out var transition))
            return false;

        _published = null;
        _transition = transition;
        _trustedOwnerId = null;
        return true;
    }

    private void ClearOwnedScope()
    {
        if (_published is { } published)
            scope.TryClearIfCurrent(published);
        else if (_transition is { } transition)
            scope.TryClearIfCurrent(transition);
        _published = null;
        _transition = null;
        _trustedOwnerId = null;
    }

    private NativeAuthState RefuseChangedOwner()
    {
        logger.LogWarning("Native auth owner changed during readiness; refusing navigation.");
        ClearOwnedScope();
        _trustedOwnerId = null;
        return SetState(new(NativePhase.Blocked, NativeDestination.SignIn,
            ErrorKey: NativeText.InvalidCredentials));
    }

    private NativeAuthState SetState(NativeAuthState value)
    {
        _state = value;
        StateChanged?.Invoke(State);
        return State;
    }

    internal static string? ParseOwnerClaim(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return null;
        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
            var owners = token.Claims
                .Where(claim => claim.Type == AuthClaimTypes.UserProfileId)
                .ToArray();
            return token.ValidTo > DateTime.UtcNow && owners.Length == 1
                && !string.IsNullOrWhiteSpace(owners[0].Value)
                ? owners[0].Value : null;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }
}
