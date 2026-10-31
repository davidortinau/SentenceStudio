using System.Net;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Bounded, credential-free download of a quiz photo URL. Vocabulary photos
/// are third-party URLs (for example picsum.photos) that answer with a
/// redirect; the source web app's <c>&lt;img&gt;</c> follows it, so the native
/// loader follows a small number of HTTPS-only redirects itself instead of
/// letting the handler auto-redirect. No request carries an owner bearer,
/// cookie, or credential: this loader owns its own plain handler and is never
/// built from the owner-bound API/AI client.
/// </summary>
public sealed class NativeRemotePhotoLoader : IDisposable
{
    public const int MaxRedirects = 3;

    private readonly HttpClient _client;
    private readonly Uri? _apiRoot;
    private readonly int _maxBytes;

    public NativeRemotePhotoLoader(int maxBytes, Uri? apiRoot, TimeSpan timeout)
        : this(maxBytes, apiRoot, CreateHandler(), timeout)
    {
    }

    internal NativeRemotePhotoLoader(
        int maxBytes, Uri? apiRoot, HttpMessageHandler handler, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
        _apiRoot = apiRoot;
        _client = new HttpClient(handler) { Timeout = timeout };
    }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null,
        PreAuthenticate = false
    };

    /// <summary>The first request may be HTTPS, or HTTP only under the configured API root.</summary>
    public static bool CanRequest(Uri? uri, Uri? apiRoot) =>
        uri is { IsAbsoluteUri: true }
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Scheme == Uri.UriSchemeHttps
            || uri.Scheme == Uri.UriSchemeHttp && apiRoot is not null
                && apiRoot.IsBaseOf(uri));

    /// <summary>A redirect never downgrades: every hop must be HTTPS without user info.</summary>
    public static bool CanFollowRedirect(Uri? target) =>
        target is { IsAbsoluteUri: true }
        && target.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(target.UserInfo);

    public async Task<byte[]?> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!CanRequest(uri, _apiRoot))
            return null;
        var current = uri;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= MaxRedirects
                    || response.Headers.Location is not { } location)
                    return null;
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!CanFollowRedirect(next))
                    return null;
                current = next;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentLength > _maxBytes
                || !IsImageMediaType(response.Content.Headers.ContentType?.MediaType))
                return null;
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadLimitedAsync(stream, _maxBytes, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    // Same acceptance as the Android loader; callers still verify image bytes.
    private static bool IsImageMediaType(string? mediaType) =>
        mediaType is not null
        && (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/octet-stream",
                StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]?> ReadLimitedAsync(
        Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximum)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public void Dispose() => _client.Dispose();
}
