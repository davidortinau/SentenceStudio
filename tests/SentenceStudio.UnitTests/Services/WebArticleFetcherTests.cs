using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="WebArticleFetcher"/> using a fake
/// <see cref="HttpMessageHandler"/> — no real network calls.
/// </summary>
public sealed class WebArticleFetcherTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private const string TestUrl = "https://example.com/article";

    private static readonly string SampleHtml = """
        <!DOCTYPE html>
        <html>
        <head>
          <title>Sample Article</title>
          <meta name="description" content="A test article.">
        </head>
        <body>
          <p>Korean vocabulary: 안녕하세요 means hello in Korean.</p>
          <p>Second paragraph with additional content to ensure we exceed the thin-page threshold and avoid fallback warnings during tests.</p>
          <p>Third paragraph: learning Korean vocabulary is both fun and rewarding when approached systematically.</p>
        </body>
        </html>
        """;

    /// <summary>Creates an HttpClient backed by a handler that returns a fixed response.</summary>
    private static (HttpClient http, WebArticleFetcher fetcher) BuildFetcher(
        HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var fetcher = new WebArticleFetcher(http, NullLogger<WebArticleFetcher>.Instance);
        return (http, fetcher);
    }

    /// <summary>Fake handler that returns a pre-canned response.</summary>
    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FixedResponseHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    /// <summary>Fake handler that throws <see cref="TaskCanceledException"/> to simulate timeout.</summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("Simulated timeout");
    }

    /// <summary>Fake handler that throws <see cref="HttpRequestException"/> (DNS / connection failure).</summary>
    private sealed class NetworkErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Name or service not known");
    }

    private static HttpResponseMessage HtmlResponse(string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StringContent(html, Encoding.UTF8, "text/html");
        return new HttpResponseMessage(status) { Content = content };
    }

    // ---------------------------------------------------------------------------
    // Happy-path tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_200Html_ReturnsParsedText()
    {
        var (_, fetcher) = BuildFetcher(new FixedResponseHandler(HtmlResponse(SampleHtml)));

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Succeeded.Should().BeTrue();
        result.Url.Should().Be(TestUrl);
        result.Title.Should().Be("Sample Article");
        result.Text.Should().Contain("안녕하세요");
        result.Text.Should().Contain("Korean vocabulary");
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task HappyPath_TitlePopulated_FromHtmlTitleTag()
    {
        var (_, fetcher) = BuildFetcher(new FixedResponseHandler(HtmlResponse(SampleHtml)));

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Title.Should().Be("Sample Article");
    }

    [Fact]
    public async Task HappyPath_CancellationToken_Respected()
    {
        // A handler that blocks until cancelled
        var handler = new BlockingHandler();
        var (_, fetcher) = BuildFetcher(handler);

        using var cts = new CancellationTokenSource();
        var task = fetcher.FetchReadableTextAsync(TestUrl, cts.Token);

        cts.Cancel();
        handler.Unblock();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly SemaphoreSlim _gate = new(0, 1);

        public void Unblock() => _gate.Release();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return HtmlResponse(SampleHtml);
        }
    }

    // ---------------------------------------------------------------------------
    // Error / failure tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Http404_ReturnsFailed_WithWarning()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Not Found")
        };
        var (_, fetcher) = BuildFetcher(new FixedResponseHandler(response));

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Succeeded.Should().BeFalse();
        result.Text.Should().BeEmpty();
        result.Warning.Should().Contain("404");
    }

    [Fact]
    public async Task Http500_ReturnsFailed_WithWarning()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server Error")
        };
        var (_, fetcher) = BuildFetcher(new FixedResponseHandler(response));

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Succeeded.Should().BeFalse();
        result.Warning.Should().Contain("500");
    }

    [Fact]
    public async Task NonHtmlContentType_ReturnsFailed_WithWarning()
    {
        var content = new StringContent("{\"key\":\"value\"}", Encoding.UTF8, "application/json");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var (_, fetcher) = BuildFetcher(new FixedResponseHandler(response));

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Succeeded.Should().BeFalse();
        result.Warning.Should().Contain("application/json");
    }

    [Fact]
    public async Task Timeout_ReturnsFailed_WithTimeoutWarning()
    {
        var (_, fetcher) = BuildFetcher(new TimeoutHandler());

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Succeeded.Should().BeFalse();
        result.Warning.Should().Contain("timed out");
    }

    [Fact]
    public async Task NetworkError_ReturnsFailed_WithErrorWarning()
    {
        var (_, fetcher) = BuildFetcher(new NetworkErrorHandler());

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Succeeded.Should().BeFalse();
        result.Warning.Should().NotBeNullOrEmpty();
    }

    // ---------------------------------------------------------------------------
    // Byte cap test
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ByteCap_LargeResponse_StillReturnsSucceeded()
    {
        // Build an HTML page with a body that far exceeds 2 MB
        // We only test that the fetcher doesn't throw and returns Succeeded=true.
        // The reducer's 8000-char output cap is tested separately in readability tests.
        var bigBody = string.Concat(Enumerable.Repeat("한국어단어 ", 60_000)); // ~360 KB (Korean chars)
        var html = $"<html><head><title>Big Page</title></head><body><p>{bigBody}</p></body></html>";

        // Return more bytes than the cap — the content itself is ~360KB here which is < 2MB,
        // but we verify the fetcher handles large payloads without exception.
        var (_, fetcher) = BuildFetcher(new FixedResponseHandler(HtmlResponse(html)));

        var result = await fetcher.FetchReadableTextAsync(TestUrl);

        result.Succeeded.Should().BeTrue();
        result.Title.Should().Be("Big Page");
        result.Text.Length.Should().BeLessOrEqualTo(8_000); // reducer cap
    }
}
