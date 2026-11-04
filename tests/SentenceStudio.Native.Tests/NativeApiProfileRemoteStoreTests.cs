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
    [InlineData("not json", "application/json")]
    [InlineData("{\"id\": 5}", "application/json")]
    [InlineData("<html>gateway</html>", "text/html")]
    public async Task MalformedProfileReadIsAnHonestRejectionWithoutAWrite(
        string body, string mediaType)
    {
        var handler = new Handler(body, mediaType);
        var store = new NativeApiProfileRemoteStore(
            new Factory(handler), new Auth(TokenFor("owner-a")),
            NullLogger<NativeApiProfileRemoteStore>.Instance);

        var outcome = await store.SaveOnboardingAsync(
            "owner-a", new NativeOnboardingDraft("Name", "English", "Korean"),
            CancellationToken.None);

        Assert.Equal(NativeProfileRemoteOutcome.Rejected, outcome);
        Assert.Equal(1, handler.Calls);
    }

    private static readonly NativeProfileFields Fields =
        new("Saved Name", "Japanese", "French", "Korean", 45, "C1");

    [Fact]
    public async Task ProfileSaveSendsOnlyEditableFieldsToMe_AndReturnsTheServerValues()
    {
        var handler = new Recorder(HttpStatusCode.OK, """
            {"id":"owner-a","displayName":"Server Name","email":"a@example.invalid",
             "nativeLanguage":"Japanese","targetLanguage":"French","displayLanguage":"Korean",
             "targetCefrLevel":"C1","preferredSessionMinutes":45,"openAiApiKey":"sk-secret"}
            """);
        var store = new NativeApiProfileRemoteStore(new Factory(handler),
            new Auth(TokenFor("owner-a")), NullLogger<NativeApiProfileRemoteStore>.Instance);

        var result = await store.SaveProfileAsync("owner-a", Fields, CancellationToken.None);

        Assert.Equal(NativeProfileRemoteOutcome.Saved, result.Outcome);
        Assert.Equal(Fields with { Name = "Server Name" }, result.Saved);
        Assert.Equal("a@example.invalid", result.Email);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/profile/me", request.Path);
        Assert.Equal("Bearer", request.Scheme);
        using var body = System.Text.Json.JsonDocument.Parse(request.Body!);
        var names = body.RootElement.EnumerateObject()
            .Select(p => p.Name.ToLowerInvariant()).Order().ToArray();
        Assert.Equal(new[] { "displaylanguage", "displayname", "nativelanguage",
            "preferredsessionminutes", "targetcefrlevel", "targetlanguage" }, names);
        Assert.DoesNotContain("owner-a", request.Body);
    }

    [Fact]
    public async Task ProfileSaveRefusesAMismatchedTokenOwnerWithoutARequest()
    {
        var handler = new Recorder(HttpStatusCode.OK, "{}");
        var store = new NativeApiProfileRemoteStore(new Factory(handler),
            new Auth(TokenFor("owner-b")), NullLogger<NativeApiProfileRemoteStore>.Instance);

        var result = await store.SaveProfileAsync("owner-a", Fields, CancellationToken.None);
        var empty = await store.SaveProfileAsync("", Fields, CancellationToken.None);

        Assert.Equal(NativeProfileRemoteOutcome.Rejected, result.Outcome);
        Assert.Equal(NativeProfileRemoteOutcome.Rejected, empty.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}", NativeProfileRemoteOutcome.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, "", NativeProfileRemoteOutcome.Rejected)]
    [InlineData(HttpStatusCode.NotFound, "", NativeProfileRemoteOutcome.Rejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", NativeProfileRemoteOutcome.TransportFailure)]
    [InlineData(HttpStatusCode.OK, "not json", NativeProfileRemoteOutcome.Rejected)]
    [InlineData(HttpStatusCode.OK, """
        {"id":"owner-b","displayName":"B","nativeLanguage":"English","targetLanguage":"Korean",
         "displayLanguage":"English","targetCefrLevel":"A1","preferredSessionMinutes":15}
        """, NativeProfileRemoteOutcome.Rejected)]
    public async Task ProfileSaveFailuresAreHonest(
        HttpStatusCode status, string body, NativeProfileRemoteOutcome expected)
    {
        var handler = new Recorder(status, body);
        var store = new NativeApiProfileRemoteStore(new Factory(handler),
            new Auth(TokenFor("owner-a")), NullLogger<NativeApiProfileRemoteStore>.Instance);

        var result = await store.SaveProfileAsync("owner-a", Fields, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Saved);
    }

    [Fact]
    public async Task ProfileSaveTransportErrorIsATransportFailure()
    {
        var handler = new Recorder(HttpStatusCode.OK, "") { Throw = true };
        var store = new NativeApiProfileRemoteStore(new Factory(handler),
            new Auth(TokenFor("owner-a")), NullLogger<NativeApiProfileRemoteStore>.Instance);

        var result = await store.SaveProfileAsync("owner-a", Fields, CancellationToken.None);

        Assert.Equal(NativeProfileRemoteOutcome.TransportFailure, result.Outcome);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Scheme, string? Body);

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal bool Throw { get; init; }
        internal List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Scheme,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Throw)
                throw new HttpRequestException("offline");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static string TokenFor(string owner) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(AuthClaimTypes.UserProfileId, owner)],
            expires: DateTime.UtcNow.AddHours(1)));

    private sealed class Handler(string body, string mediaType) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
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
