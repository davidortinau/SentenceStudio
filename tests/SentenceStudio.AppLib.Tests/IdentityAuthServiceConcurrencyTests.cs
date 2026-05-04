using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using Xunit;

namespace SentenceStudio.AppLib.Tests;

/// <summary>
/// Regression tests for IdentityAuthService concurrency bugs.
/// Specifically: concurrent GetAccessTokenAsync calls must NOT race to POST /api/auth/refresh.
/// </summary>
public class IdentityAuthServiceConcurrencyTests
{
    /// <summary>
    /// Regression test for the refresh-token concurrency race (Bug 1 in auth persistence plan).
    /// Two parallel callers racing to GetAccessTokenAsync should trigger exactly ONE POST to /api/auth/refresh.
    /// Both callers should receive the same new AccessToken.
    /// A third call after the refresh completes should use the cached token (no new POST).
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallers_TriggersExactlyOneRefresh()
    {
        // Arrange
        var testRefreshToken = "R1-valid-refresh-token";
        var newAccessToken = "NEW-JWT-TOKEN-FROM-REFRESH";
        var newRefreshToken = "R2-new-refresh-token";
        var expiresAt = DateTime.UtcNow.AddHours(24);

        var secureStorage = new InMemorySecureStorageService();
        await secureStorage.SetAsync("auth_refresh", testRefreshToken);
        await secureStorage.SetAsync("auth_jwt", "EXPIRED-JWT");
        await secureStorage.SetAsync("auth_expires", DateTime.UtcNow.AddMinutes(-10).ToString("O")); // expired 10 min ago

        var mockPreferences = new Mock<IPreferencesService>();
        
        var handler = new TrackingHttpMessageHandler(new AuthResponseDto(
            Token: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: expiresAt,
            UserName: "testuser@example.com",
            UserProfileId: "profile-123"
        ));

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://localhost:5001") });

        var authService = new IdentityAuthService(
            httpClientFactory.Object,
            secureStorage,
            mockPreferences.Object,
            NullLogger<IdentityAuthService>.Instance);

        var scopes = Array.Empty<string>();

        // Act — fire two concurrent GetAccessTokenAsync calls
        var call1Task = authService.GetAccessTokenAsync(scopes);
        var call2Task = authService.GetAccessTokenAsync(scopes);
        var results = await Task.WhenAll(call1Task, call2Task);

        // Assert — both callers got the same token
        results[0].Should().Be(newAccessToken, "first caller should receive the new token");
        results[1].Should().Be(newAccessToken, "second caller should receive the same new token");

        // Assert — exactly one POST /api/auth/refresh was sent
        handler.RefreshRequestCount.Should().Be(1, "concurrent calls should trigger exactly ONE refresh, not two");

        // Act — third call after refresh completes should use cached token (no new POST)
        var call3Result = await authService.GetAccessTokenAsync(scopes);

        // Assert
        call3Result.Should().Be(newAccessToken, "third caller should receive cached token");
        handler.RefreshRequestCount.Should().Be(1, "cached token should NOT trigger another refresh");
    }

    /// <summary>
    /// In-memory stub for ISecureStorageService.
    /// </summary>
    private class InMemorySecureStorageService : ISecureStorageService
    {
        private readonly Dictionary<string, string> _store = new();

        public Task<string?> GetAsync(string key)
        {
            _store.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetAsync(string key, string value)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public bool Remove(string key)
        {
            return _store.Remove(key);
        }

        public void RemoveAll()
        {
            _store.Clear();
        }
    }

    /// <summary>
    /// Custom HttpMessageHandler that records every request and returns a canned AuthResponse for /api/auth/refresh.
    /// </summary>
    private class TrackingHttpMessageHandler : HttpMessageHandler
    {
        private readonly AuthResponseDto _cannedResponse;
        private int _refreshRequestCount;

        public TrackingHttpMessageHandler(AuthResponseDto cannedResponse)
        {
            _cannedResponse = cannedResponse;
        }

        public int RefreshRequestCount => _refreshRequestCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.PathAndQuery.Contains("/api/auth/refresh") == true)
            {
                Interlocked.Increment(ref _refreshRequestCount);

                // Simulate network delay to widen the race window
                await Task.Delay(50, cancellationToken);

                var json = JsonSerializer.Serialize(_cannedResponse, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            // Other requests return 404
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Maps the API's AuthResponse JSON shape.
    /// </summary>
    private sealed record AuthResponseDto(
        string Token,
        string RefreshToken,
        DateTime ExpiresAt,
        string? UserName,
        string? UserProfileId);
}
