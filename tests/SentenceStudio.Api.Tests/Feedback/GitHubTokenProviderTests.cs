using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Api.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// The credential behind feedback filing: GitHub App installation tokens in production, the
/// personal token as the local fallback.
/// </summary>
public sealed class GitHubTokenProviderTests
{
    private const string ClientId = "Iv23ctariyo54wCJKYR2";

    private static string NewPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static (GitHubTokenProvider Provider, FakeGitHub GitHub) Create(
        Dictionary<string, string?> settings, TestTimeProvider? time = null)
    {
        var github = new FakeGitHub();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return (new GitHubTokenProvider(configuration, github, time ?? new TestTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<GitHubTokenProvider>.Instance), github);
    }

    [Fact]
    public void The_app_jwt_is_rs256_signed_with_the_client_id_as_issuer()
    {
        using var rsa = RSA.Create(2048);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var jwt = GitHubTokenProvider.CreateAppJwt(ClientId, rsa.ExportRSAPrivateKeyPem(), now);

        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);
        var payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
        payload.GetProperty("iss").GetString().Should().Be(ClientId);
        payload.GetProperty("iat").GetInt64().Should().Be(now.ToUnixTimeSeconds() - 60);
        (payload.GetProperty("exp").GetInt64() - now.ToUnixTimeSeconds()).Should().BeLessThanOrEqualTo(600,
            "GitHub refuses app JWTs that live longer than ten minutes");
        JsonDocument.Parse(FromBase64Url(parts[0])).RootElement.GetProperty("alg").GetString().Should().Be("RS256");
        rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Fact]
    public void A_key_stored_with_escaped_line_breaks_still_signs()
    {
        var flattened = NewPem().Replace("\n", "\\n");

        var act = () => GitHubTokenProvider.CreateAppJwt(ClientId, flattened, DateTimeOffset.UtcNow);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Without_an_app_the_personal_token_is_used()
    {
        var (provider, github) = Create(new() { ["GitHub:Pat"] = "pat-value" });

        var token = await provider.GetTokenAsync(CancellationToken.None);

        token.Value.Should().Be("pat-value");
        github.Requests.Should().BeEmpty("a personal token needs no exchange");
    }

    [Fact]
    public async Task With_nothing_configured_filing_is_unconfigured()
    {
        var (provider, _) = Create(new());

        (await provider.GetTokenAsync(CancellationToken.None)).Failure.Should().Be(GitHubTokenFailure.Unconfigured);
    }

    [Fact]
    public async Task The_app_finds_its_installation_exchanges_a_token_and_caches_it()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (provider, github) = Create(new()
        {
            ["GitHub:AppClientId"] = ClientId,
            ["GitHub:AppPrivateKey"] = NewPem(),
            ["GitHub:Pat"] = "pat-is-ignored-when-the-app-is-configured"
        }, time);
        github.ExpiresAt = time.GetUtcNow().AddHours(1);

        var first = await provider.GetTokenAsync(CancellationToken.None);
        var second = await provider.GetTokenAsync(CancellationToken.None);

        first.Value.Should().Be("ghs_installation_token_1");
        second.Value.Should().Be(first.Value, "the one-hour token is reused");
        github.Requests.Should().Equal(
            "GET /repos/davidortinau/SentenceStudio/installation",
            "POST /app/installations/4242/access_tokens");
        github.Authorizations.Should().OnlyContain(a => a.StartsWith("Bearer ey"), "both calls use the app JWT");

        time.Advance(TimeSpan.FromMinutes(56));
        (await provider.GetTokenAsync(CancellationToken.None)).Value.Should().Be("ghs_installation_token_2",
            "a token within five minutes of expiry is replaced");
    }

    [Fact]
    public async Task An_app_not_installed_on_the_repository_is_unauthorized()
    {
        var (provider, github) = Create(new()
        {
            ["GitHub:AppClientId"] = ClientId,
            ["GitHub:AppPrivateKey"] = NewPem()
        });
        github.InstallationStatus = HttpStatusCode.NotFound;

        (await provider.GetTokenAsync(CancellationToken.None)).Failure.Should().Be(GitHubTokenFailure.Unauthorized);
    }

    [Fact]
    public async Task A_github_outage_during_lookup_is_unavailable_not_unauthorized()
    {
        var (provider, github) = Create(new()
        {
            ["GitHub:AppClientId"] = ClientId,
            ["GitHub:AppPrivateKey"] = NewPem()
        });
        github.InstallationStatus = HttpStatusCode.ServiceUnavailable;

        (await provider.GetTokenAsync(CancellationToken.None)).Failure.Should().Be(GitHubTokenFailure.Unavailable);
    }

    [Fact]
    public async Task A_rejected_token_is_dropped_and_the_next_call_fetches_a_new_one()
    {
        var (provider, github) = Create(new()
        {
            ["GitHub:AppClientId"] = ClientId,
            ["GitHub:AppPrivateKey"] = NewPem()
        });

        var first = await provider.GetTokenAsync(CancellationToken.None);
        provider.Invalidate(first.Value!);
        var second = await provider.GetTokenAsync(CancellationToken.None);

        second.Value.Should().NotBe(first.Value);
        github.Requests.Count(r => r.EndsWith("/installation")).Should().Be(2, "the installation is looked up again");
    }

    [Fact]
    public void A_configured_key_that_cannot_sign_stops_startup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitHub:AppClientId"] = ClientId,
            ["GitHub:AppPrivateKey"] = "-----BEGIN RSA PRIVATE KEY-----"
        }).Build();

        var act = () => GitHubTokenProvider.ValidateConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a readable PEM*");
    }

    [Fact]
    public async Task An_unreadable_key_is_unconfigured_not_a_crash()
    {
        var (provider, github) = Create(new()
        {
            ["GitHub:AppClientId"] = ClientId,
            ["GitHub:AppPrivateKey"] = "not a pem"
        });

        (await provider.GetTokenAsync(CancellationToken.None)).Failure.Should().Be(GitHubTokenFailure.Unconfigured);
        github.Requests.Should().BeEmpty();
    }

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    private sealed class FakeGitHub : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public List<string> Authorizations { get; } = [];
        public HttpStatusCode InstallationStatus { get; set; } = HttpStatusCode.OK;
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
        private int _issued;

        public HttpClient CreateClient(string name) =>
            new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);

            if (request.RequestUri.AbsolutePath.EndsWith("/installation", StringComparison.Ordinal))
            {
                return Task.FromResult(InstallationStatus == HttpStatusCode.OK
                    ? Json("""{"id":4242}""")
                    : new HttpResponseMessage(InstallationStatus));
            }

            _issued++;
            ExpiresAt = _issued == 1 ? ExpiresAt : ExpiresAt.AddHours(1);
            return Task.FromResult(Json(
                $$"""{"token":"ghs_installation_token_{{_issued}}","expires_at":"{{ExpiresAt:O}}"}"""));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
