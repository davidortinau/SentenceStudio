using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SentenceStudio.Api.Feedback;

/// <summary>Why a GitHub token could not be obtained. Carries no secret.</summary>
public enum GitHubTokenFailure
{
    None,

    /// <summary>Neither a GitHub App nor a personal token is configured.</summary>
    Unconfigured,

    /// <summary>GitHub refused the app's credentials, or the app is not installed on the repository.</summary>
    Unauthorized,

    /// <summary>GitHub could not be reached or answered unexpectedly.</summary>
    Unavailable
}

/// <summary>A bearer token for the GitHub REST API, or the reason there is none.</summary>
public sealed record GitHubToken(string? Value, GitHubTokenFailure Failure)
{
    public static GitHubToken Fail(GitHubTokenFailure failure) => new(null, failure);
}

/// <summary>Supplies the credential used to file feedback issues.</summary>
public interface IGitHubTokenProvider
{
    Task<GitHubToken> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>Drops a cached token GitHub has just rejected, so the next call fetches a new one.</summary>
    void Invalidate(string token);
}

/// <summary>
/// GitHub App installation tokens when the app is configured, otherwise the personal token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Production uses the GitHub App</b> (<c>GitHub:AppClientId</c> and <c>GitHub:AppPrivateKey</c>):
/// issues are filed as the app's bot, the credential is limited to issues on one repository, and
/// nothing expires on a calendar. A personal token (<c>GitHub:Pat</c>) remains for local development.
/// </para>
/// <para>
/// The app signs a ten-minute JWT with its private key, finds its installation on the repository,
/// and exchanges the JWT for a one-hour installation token, which is cached until five minutes
/// before it expires. Neither the key, the JWT, nor the token is ever logged.
/// </para>
/// </remarks>
public sealed class GitHubTokenProvider : IGitHubTokenProvider, IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubTokenProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedUntil;
    private long? _installationId;

    public GitHubTokenProvider(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        TimeProvider time,
        ILogger<GitHubTokenProvider> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GitHubToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        var clientId = _configuration["GitHub:AppClientId"];
        var privateKey = _configuration["GitHub:AppPrivateKey"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(privateKey))
        {
            var pat = _configuration["GitHub:Pat"];
            return string.IsNullOrWhiteSpace(pat)
                ? GitHubToken.Fail(GitHubTokenFailure.Unconfigured)
                : new GitHubToken(pat, GitHubTokenFailure.None);
        }

        // Most calls are cache hits; they never wait behind a refresh in progress.
        if (Volatile.Read(ref _cachedToken) is { } cached && _time.GetUtcNow() < _cachedUntil - RefreshMargin)
        {
            return new GitHubToken(cached, GitHubTokenFailure.None);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && _time.GetUtcNow() < _cachedUntil - RefreshMargin)
            {
                return new GitHubToken(_cachedToken, GitHubTokenFailure.None);
            }

            string jwt;
            try
            {
                jwt = CreateAppJwt(clientId.Trim(), privateKey, _time.GetUtcNow());
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                _logger.LogError("The GitHub App private key could not be read. Exception={ExceptionType}", ex.GetType().Name);
                return GitHubToken.Fail(GitHubTokenFailure.Unconfigured);
            }

            var client = _httpClientFactory.CreateClient("GitHub");

            if (_installationId is null)
            {
                var (id, failure) = await FindInstallationAsync(client, jwt, cancellationToken).ConfigureAwait(false);
                if (id is null)
                {
                    return GitHubToken.Fail(failure);
                }

                _installationId = id;
            }

            var installationId = _installationId.Value;

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/app/installations/{installationId}/access_tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("GitHub refused an installation token. Status={StatusCode}", (int)response.StatusCode);
                if ((int)response.StatusCode is 401 or 403 or 404)
                {
                    // The installation may have been removed; look it up again next time.
                    _installationId = null;
                    return GitHubToken.Fail(GitHubTokenFailure.Unauthorized);
                }

                return GitHubToken.Fail(GitHubTokenFailure.Unavailable);
            }

            using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var token = doc.RootElement.GetProperty("token").GetString();
            var expiresAt = doc.RootElement.TryGetProperty("expires_at", out var expires)
                && expires.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : _time.GetUtcNow().AddMinutes(55);

            if (string.IsNullOrEmpty(token))
            {
                return GitHubToken.Fail(GitHubTokenFailure.Unavailable);
            }

            _cachedUntil = expiresAt;
            Volatile.Write(ref _cachedToken, token);
            return new GitHubToken(token, GitHubTokenFailure.None);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException
                                       or OverflowException)
        {
            _logger.LogError("A GitHub App token could not be obtained. Exception={ExceptionType}", ex.GetType().Name);
            return GitHubToken.Fail(GitHubTokenFailure.Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate(string token)
    {
        if (string.Equals(Volatile.Read(ref _cachedToken), token, StringComparison.Ordinal))
        {
            Volatile.Write(ref _cachedToken, null);
            // The installation may have been removed or replaced; look it up again.
            _installationId = null;
        }
    }

    private async Task<(long? Id, GitHubTokenFailure Failure)> FindInstallationAsync(
        HttpClient client, string jwt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/repos/{FeedbackIssueFiler.GitHubRepo}/installation");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // 401: the key or client id is wrong. 404: the app is not installed on the repository.
            _logger.LogError(
                "The GitHub App installation for the feedback repository was not found. Status={StatusCode}",
                (int)response.StatusCode);
            // Only a refusal of the credentials or a missing installation is a credential problem;
            // an outage or rate limit is transient.
            return (null, (int)response.StatusCode is 401 or 403 or 404
                ? GitHubTokenFailure.Unauthorized
                : GitHubTokenFailure.Unavailable);
        }

        using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (doc.RootElement.GetProperty("id").GetInt64(), GitHubTokenFailure.None);
    }

    /// <summary>
    /// Fails startup when an app key is configured but cannot sign, so a malformed key in Key Vault
    /// stops the rollout instead of failing every learner's report later.
    /// </summary>
    public static void ValidateConfiguration(IConfiguration configuration)
    {
        var clientId = configuration["GitHub:AppClientId"];
        var privateKey = configuration["GitHub:AppPrivateKey"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(privateKey))
        {
            return;
        }

        try
        {
            _ = CreateAppJwt(clientId.Trim(), privateKey, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                "GitHub:AppPrivateKey is set but is not a readable PEM private key. Feedback issues "
                + "could not be filed. Store the GitHub App's .pem file contents unchanged.", ex);
        }
    }

    /// <summary>
    /// The app's own JWT: RS256, issued a minute in the past for clock drift, valid nine minutes
    /// (GitHub allows at most ten), with the app's client id as issuer.
    /// </summary>
    public static string CreateAppJwt(string clientId, string privateKeyPem, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        // Secrets stores often flatten the PEM's line breaks into "\n" text.
        rsa.ImportFromPem(privateKeyPem.Replace("\\n", "\n").Trim());

        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = clientId
        }));

        var signingInput = $"{header}.{payload}";
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _gate.Dispose();
}
