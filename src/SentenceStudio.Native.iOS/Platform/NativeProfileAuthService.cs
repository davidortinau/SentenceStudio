using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativeProfileAuthService(
    IdentityAuthService identity,
    NativeProfileBootstrap profiles) : IAuthService
{
    public bool IsSignedIn => identity.IsSignedIn;
    public string? UserName => identity.UserName;
    public Task<bool> HasStoredSessionAsync() => identity.HasStoredSessionAsync();
    public Task<string?> GetAccessTokenAsync(string[] scopes) => identity.GetAccessTokenAsync(scopes);
    public Task SignOutAsync() => identity.SignOutAsync();
    public Task<bool> DeleteAccountAsync() => identity.DeleteAccountAsync();
    public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) =>
        identity.ChangePasswordAsync(currentPassword, newPassword);

    public async Task<AuthResult?> SignInAsync()
    {
        var result = await identity.SignInAsync();
        if (result is not null)
            await profiles.EnsureLocalProfileAsync(result.AccessToken);
        return result;
    }

    public async Task<AuthResult?> SignInAsync(string email, string password)
    {
        try
        {
            var result = await identity.SignInAsync(email, password);
            if (result is not null)
                await profiles.EnsureLocalProfileAsync(result.AccessToken);
            return result;
        }
        catch (TaskCanceledException ex)
        {
            throw new HttpRequestException("Native sign-in timed out contacting the API.", ex);
        }
    }

    public async Task<AuthResult?> RegisterAsync(string email, string password, string displayName)
    {
        var result = await identity.RegisterAsync(email, password, displayName);
        if (result is not null)
            await profiles.EnsureLocalProfileAsync(result.AccessToken);
        return result;
    }
}

public sealed class NativeProfileBootstrap(
    IHttpClientFactory clients,
    IServiceScopeFactory scopes,
    ILogger<NativeProfileBootstrap> logger)
{
    public async Task EnsureLocalProfileAsync(string jwt)
    {
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims
            .FirstOrDefault(claim => claim.Type == AuthClaimTypes.UserProfileId)?.Value;
        if (string.IsNullOrWhiteSpace(owner))
            throw new UnauthorizedAccessException("The authenticated token has no profile owner.");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.UserProfiles.AnyAsync(profile => profile.Id == owner))
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/profile/{Uri.EscapeDataString(owner)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient("AuthClient").SendAsync(request);
        }
        catch (TaskCanceledException ex)
        {
            throw new HttpRequestException("Native profile fetch timed out contacting the API.", ex);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("The server refused the profile for this account.");
            response.EnsureSuccessStatusCode();
            var profile = await response.Content.ReadFromJsonAsync<NativeApiProfile>()
                ?? throw new InvalidOperationException("The API returned an empty profile.");
            if (!string.Equals(owner, profile.Id, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The API profile does not match the signed-in owner.");

            db.UserProfiles.Add(new UserProfile
            {
                Id = owner,
                Name = profile.DisplayName,
                Email = profile.Email,
                NativeLanguage = string.IsNullOrWhiteSpace(profile.NativeLanguage)
                    ? string.Empty : profile.NativeLanguage,
                TargetLanguage = string.IsNullOrWhiteSpace(profile.TargetLanguage)
                    ? string.Empty : profile.TargetLanguage,
                DisplayLanguage = profile.DisplayLanguage,
                TargetCEFRLevel = profile.TargetCefrLevel,
                PreferredSessionMinutes = profile.PreferredSessionMinutes,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Stored an authenticated owner-scoped profile in the isolated iOS database.");
        }
    }

    private sealed record NativeApiProfile(
        string Id,
        string DisplayName,
        string Email,
        string? NativeLanguage,
        string? TargetLanguage,
        string DisplayLanguage,
        string TargetCefrLevel,
        int PreferredSessionMinutes);
}
