using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

// ---------------------------------------------------------------------------
// Result type
// ---------------------------------------------------------------------------

/// <summary>
/// The result of fetching and reducing a web article to plain readable text.
/// </summary>
/// <param name="Url">The original URL that was fetched.</param>
/// <param name="Title">The HTML &lt;title&gt; of the page, if found.</param>
/// <param name="Text">The readable text body (possibly empty).</param>
/// <param name="Succeeded">True when the fetch completed without a transport error.</param>
/// <param name="Warning">Optional diagnostic note (thin page, truncation, HTTP error, etc.).</param>
public sealed record WebArticleText(
    string Url,
    string? Title,
    string Text,
    bool Succeeded,
    string? Warning);

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

/// <summary>
/// Fetches a URL and returns its content reduced to human-readable plain text,
/// suitable for downstream vocabulary extraction.
/// </summary>
public interface IWebArticleFetcher
{
    /// <summary>
    /// Fetches <paramref name="url"/>, strips HTML boilerplate, and returns the
    /// readable body text.  Never throws on transport or parse failure —
    /// always returns a result with <see cref="WebArticleText.Succeeded"/> set
    /// appropriately.
    /// </summary>
    Task<WebArticleText> FetchReadableTextAsync(string url, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Production implementation of <see cref="IWebArticleFetcher"/>.
/// Accepts an <see cref="HttpClient"/> so a fake <see cref="HttpMessageHandler"/>
/// can be supplied in unit tests without hitting the network.
/// Register via <c>services.AddHttpClient&lt;WebArticleFetcher&gt;()</c> (or
/// a named/typed factory) in DI; the <c>HttpClient</c> receives a sensible
/// timeout and User-Agent in the constructor.
/// </summary>
public sealed class WebArticleFetcher : IWebArticleFetcher
{
    private const int MaxDownloadBytes = 2 * 1024 * 1024; // 2 MB

    private readonly HttpClient _http;
    private readonly ILogger<WebArticleFetcher> _logger;

    public WebArticleFetcher(HttpClient http, ILogger<WebArticleFetcher> logger)
    {
        _http = http;
        _logger = logger;

        // Set a desktop-ish User-Agent so sites don't block us as a bot
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }

        // Cap total request time; a site that never responds should not block the queue drain
        if (_http.Timeout == Timeout.InfiniteTimeSpan)
            _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <inheritdoc/>
    public async Task<WebArticleText> FetchReadableTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.9");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "WebArticleFetcher: {Url} returned HTTP {Status}",
                    url, (int)response.StatusCode);
                return new WebArticleText(url, null, "", false,
                    $"HTTP {(int)response.StatusCode}");
            }

            // Only attempt to parse HTML content
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "WebArticleFetcher: {Url} returned non-HTML content-type {CT}", url, contentType);
                return new WebArticleText(url, null, "", false,
                    $"Non-HTML content-type: {contentType}");
            }

            // Read body with a hard byte cap to avoid allocating huge strings
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxDownloadBytes];
            int totalRead = 0;
            int bytesRead;
            while (totalRead < MaxDownloadBytes &&
                   (bytesRead = await stream.ReadAsync(
                       buffer.AsMemory(totalRead, MaxDownloadBytes - totalRead), ct)) > 0)
            {
                totalRead += bytesRead;
            }

            // Detect charset from Content-Type header; fall back to UTF-8
            var charSet = response.Content.Headers.ContentType?.CharSet;
            Encoding encoding;
            try
            {
                encoding = string.IsNullOrEmpty(charSet)
                    ? Encoding.UTF8
                    : Encoding.GetEncoding(charSet);
            }
            catch
            {
                encoding = Encoding.UTF8;
            }

            var html = encoding.GetString(buffer, 0, totalRead);
            var text = HtmlReadability.Reduce(html, out var title, out var warning);

            return new WebArticleText(url, title, text, true, warning);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout fired — not a user cancellation
            _logger.LogWarning(ex, "WebArticleFetcher: timeout fetching {Url}", url);
            return new WebArticleText(url, null, "", false, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "WebArticleFetcher: HTTP error fetching {Url}", url);
            return new WebArticleText(url, null, "", false, $"HTTP error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WebArticleFetcher: unexpected error fetching {Url}", url);
            return new WebArticleText(url, null, "", false, $"Error: {ex.Message}");
        }
    }
}

// ---------------------------------------------------------------------------
// Pure HTML reducer — no IO, fully unit-testable
// ---------------------------------------------------------------------------

/// <summary>
/// Stateless, IO-free HTML-to-readable-text reducer.
/// Uses compiled regex and <see cref="WebUtility.HtmlDecode"/> — no external
/// packages required.  Accuracy is "good enough" for downstream AI vocab
/// extraction; it is not a full readability parser.
/// </summary>
internal static partial class HtmlReadability
{
    private const int MaxOutputLength = 8_000;

    /// <summary>
    /// If the stripped body is shorter than this threshold (chars), use
    /// title/description as the text instead.
    /// </summary>
    private const int ThinPageThreshold = 100;

    // -----------------------------------------------------------------------
    // Compiled regex patterns (source-generated for zero-allocation dispatch)
    // -----------------------------------------------------------------------

    // Whole-block removals (content included)
    [GeneratedRegex(@"<head\b[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadBlockRegex();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlockRegex();

    [GeneratedRegex(@"<noscript\b[^>]*>.*?</noscript>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NoscriptBlockRegex();

    [GeneratedRegex(@"<nav\b[^>]*>.*?</nav>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NavBlockRegex();

    [GeneratedRegex(@"<header\b[^>]*>.*?</header>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeaderBlockRegex();

    [GeneratedRegex(@"<footer\b[^>]*>.*?</footer>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FooterBlockRegex();

    // Extractors (run before head/block stripping so they can find tags)
    [GeneratedRegex(@"<title\b[^>]*>(?<content>[^<]*)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTagRegex();

    // <meta name="description" content="...">
    [GeneratedRegex(
        @"<meta\b[^>]*\bname\s*=\s*[""']description[""'][^>]*\bcontent\s*=\s*[""'](?<content>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaDescNameFirstRegex();

    // <meta content="..." name="description">
    [GeneratedRegex(
        @"<meta\b[^>]*\bcontent\s*=\s*[""'](?<content>[^""']*)[""'][^>]*\bname\s*=\s*[""']description[""']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaDescContentFirstRegex();

    // Strip any remaining tag
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    // Whitespace normalisation
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesRegex();

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reduces <paramref name="html"/> to plain readable text.
    /// </summary>
    /// <param name="html">Raw HTML input.</param>
    /// <param name="title">Populated with the page &lt;title&gt;, if present.</param>
    /// <param name="warning">
    /// Populated with a diagnostic note when output was truncated, the page
    /// was thin (title/description used as fallback), etc.  <c>null</c> on a
    /// clean extraction.
    /// </param>
    /// <returns>Readable plain text, at most <c>8000</c> characters.</returns>
    public static string Reduce(string html, out string? title, out string? warning)
    {
        title = null;
        warning = null;

        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // --- Extract metadata BEFORE stripping <head> ---
        var titleMatch = TitleTagRegex().Match(html);
        if (titleMatch.Success)
            title = WebUtility.HtmlDecode(titleMatch.Groups["content"].Value.Trim());

        string? metaDesc = null;
        var metaMatch = MetaDescNameFirstRegex().Match(html);
        if (!metaMatch.Success)
            metaMatch = MetaDescContentFirstRegex().Match(html);
        if (metaMatch.Success)
            metaDesc = WebUtility.HtmlDecode(metaMatch.Groups["content"].Value.Trim());

        // --- Strip whole blocks (content and tags) ---
        html = HeadBlockRegex().Replace(html, " ");
        html = ScriptBlockRegex().Replace(html, " ");
        html = StyleBlockRegex().Replace(html, " ");
        html = NoscriptBlockRegex().Replace(html, " ");
        html = NavBlockRegex().Replace(html, " ");
        html = HeaderBlockRegex().Replace(html, " ");
        html = FooterBlockRegex().Replace(html, " ");

        // --- Strip remaining tags ---
        html = AnyTagRegex().Replace(html, " ");

        // --- Decode HTML entities ---
        html = WebUtility.HtmlDecode(html);

        // --- Normalise whitespace ---
        html = HorizontalWhitespaceRegex().Replace(html, " ");
        // Convert carriage returns so newline handling is consistent
        html = html.Replace("\r\n", "\n").Replace('\r', '\n');
        html = ExcessiveNewlinesRegex().Replace(html, "\n\n");
        html = html.Trim();

        // --- Thin-page fallback: use title / meta-description when body is sparse ---
        if (html.Length < ThinPageThreshold)
        {
            var fallback = BuildFallbackText(title, metaDesc);
            if (!string.IsNullOrEmpty(fallback))
            {
                warning = "thin page; used title/description";
                html = string.IsNullOrEmpty(html)
                    ? fallback
                    : $"{fallback}\n\n{html}";
            }
        }

        // --- Cap output length ---
        if (html.Length > MaxOutputLength)
        {
            html = html[..MaxOutputLength];
            warning = warning is null
                ? "content truncated at 8000 chars"
                : $"{warning}; content truncated";
        }

        return html;
    }

    private static string? BuildFallbackText(string? title, string? description)
    {
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description))
            return null;
        if (string.IsNullOrEmpty(description))
            return title;
        if (string.IsNullOrEmpty(title))
            return description;
        return $"{title}. {description}";
    }
}
