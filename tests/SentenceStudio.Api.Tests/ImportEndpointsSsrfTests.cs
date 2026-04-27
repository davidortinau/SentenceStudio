using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Verifies the SSRF allowlist guard on POST /api/imports.
/// Any URL that is not a valid https://youtube.com (or youtu.be) URL must be rejected
/// with 400 Bad Request before the server makes any outbound HTTP request.
/// </summary>
public class ImportEndpointsSsrfTests : IClassFixture<JwtBearerApiFactory>
{
    private const string ImportsPath = "/api/imports";
    private const string TestUserProfileId = "ssrf-test-profile-001";

    private readonly HttpClient _client;

    public ImportEndpointsSsrfTests(JwtBearerApiFactory factory)
    {
        _client = factory.CreateClient();

        var token = TestJwtGenerator.GenerateToken(userProfileId: TestUserProfileId);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    [Theory]
    [InlineData("http://169.254.169.254/metadata/instance")]        // Azure IMDS
    [InlineData("http://169.254.169.254/")]                         // IMDS root
    [InlineData("http://10.0.0.1/internal")]                        // RFC-1918 internal
    [InlineData("http://192.168.1.1/admin")]                        // RFC-1918 internal
    [InlineData("http://localhost/evil")]                            // loopback
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]     // valid YouTube, non-https
    [InlineData("https://evil.com/watch?v=dQw4w9WgXcQ")]           // non-YouTube HTTPS host
    [InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]      // valid host, wrong scheme
    [InlineData("not-a-url-at-all")]                                // unparseable
    [InlineData("")]                                                  // empty
    public async Task StartImport_RejectsSsrfAndInvalidUrls(string badUrl)
    {
        var response = await _client.PostAsJsonAsync(ImportsPath, new
        {
            VideoUrl = badUrl,
            Language = "Korean"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"URL '{badUrl}' should be rejected before any outbound request is made");
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    public async Task StartImport_AcceptsValidYouTubeUrls(string validUrl)
    {
        var response = await _client.PostAsJsonAsync(ImportsPath, new
        {
            VideoUrl = validUrl,
            Language = "Korean"
        });

        // 202 Accepted means the guard passed; actual pipeline failure is expected in tests
        // (no real YouTube API / AI service available). We only care that it is not 400.
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest,
            $"'{validUrl}' is a valid YouTube URL and must pass the SSRF guard");
    }
}
