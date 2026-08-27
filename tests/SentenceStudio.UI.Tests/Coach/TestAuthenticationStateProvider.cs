using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SentenceStudio.Contracts;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// An <see cref="AuthenticationStateProvider"/> a test can sign in, sign out, and expire.
/// </summary>
/// <remarks>
/// <para>
/// Notifications carry an already-completed task, exactly as
/// <c>MauiAuthenticationStateProvider</c> does, so a subscriber that awaits it continues
/// synchronously and the test needs no polling to observe the effect of a sign-out.
/// </para>
/// <para>
/// It can also publish the MAUI "optimistic" principal — the one built from a remembered email
/// while a refresh token is being exchanged, carrying no profile id and no subject. That shape is
/// the reason account identity is compared as a set of recognisable tokens rather than by
/// equality, so a double that could not produce it would not be testing the interesting case.
/// </para>
/// </remarks>
internal sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal _user = new(new ClaimsIdentity());

    /// <summary>How many times the current state was read.</summary>
    public int GetStateCalls { get; private set; }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        GetStateCalls++;
        return Task.FromResult(new AuthenticationState(_user));
    }

    /// <summary>Signs in a learner with a full token principal.</summary>
    /// <param name="displayName">
    /// Optional display name. Distinct from the email on purpose: a display name is learner-chosen
    /// text and must never be treated as an identifier, so a test needs to be able to set it to
    /// something misleading.
    /// </param>
    public void SignIn(string profileId, string email, string? subject = null, string? displayName = null)
    {
        _user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.UserProfileId, profileId),
                new Claim("sub", subject ?? profileId),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, displayName ?? email)
            ],
            authenticationType: "jwt"));

        Publish();
    }

    /// <summary>
    /// Publishes the optimistic principal MAUI shows while a stored session is being refreshed.
    /// </summary>
    public void SignInOptimistically(string email)
    {
        _user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.AuthenticationMethod, "refresh_token_pending"),
                new Claim(ClaimTypes.Name, email),
                new Claim(ClaimTypes.Email, email)
            ],
            authenticationType: "optimistic"));

        Publish();
    }

    /// <summary>Signs out, or models a refresh token the server rejected.</summary>
    public void SignOut()
    {
        _user = new ClaimsPrincipal(new ClaimsIdentity());
        Publish();
    }

    /// <summary>Re-publishes the same principal, as a harmless state notification would.</summary>
    public void Renotify() => Publish();

    private void Publish() =>
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_user)));
}
