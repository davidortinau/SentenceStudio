using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Tests;

public sealed class NativeApiProfileRemoteStoreTests
{
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\": 5}")]
    public async Task MalformedProfileReadIsAnHonestRejectionWithoutAWrite(string body)
    {
        var handler = new Handler(body);
        var store = new NativeApiProfileRemoteStore(
            new Factory(handler), new Auth(TokenFor("owner-a")),
            NullLogger<NativeApiProfileRemoteStore>.Instance);

        var outcome = await store.SaveOnboardingAsync(
            "owner-a", new NativeOnboardingDraft("Name", "English", "Korean"),
            CancellationToken.None);

        Assert.Equal(NativeProfileRemoteOutcome.Rejected, outcome);
        Assert.Equal(1, handler.Calls);
    }

    private static string TokenFor(string owner) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(AuthClaimTypes.UserProfileId, owner)],
            expires: DateTime.UtcNow.AddHours(1)));

    private sealed class Handler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class Auth(string token) : IAuthService
    {
        public bool IsSignedIn => true;
        public string? UserName => null;
        public Task<string?> GetAccessTokenAsync(string[] scopes) => Task.FromResult<string?>(token);
        public Task<bool> HasStoredSessionAsync() => Task.FromResult(true);
        public Task<AuthResult?> SignInAsync() => throw new NotSupportedException();
        public Task<AuthResult?> SignInAsync(string email, string password) =>
            throw new NotSupportedException();
        public Task<AuthResult?> RegisterAsync(string email, string password, string displayName) =>
            throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
        public Task<bool> DeleteAccountAsync() => throw new NotSupportedException();
        public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) =>
            throw new NotSupportedException();
    }
}
