using Microsoft.Identity.Web;

namespace SentenceStudio.WebApp.Auth;

/// <summary>
/// Attaches a Bearer token to outgoing API calls when Entra ID auth is active.
/// </summary>
public sealed class AuthenticatedApiDelegatingHandler : DelegatingHandler
{
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthenticatedApiDelegatingHandler> _logger;

    public AuthenticatedApiDelegatingHandler(
        ITokenAcquisition tokenAcquisition,
        IConfiguration configuration,
        ILogger<AuthenticatedApiDelegatingHandler> logger)
    {
        _tokenAcquisition = tokenAcquisition;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var scopes = _configuration.GetSection("DownstreamApi:Scopes").Get<string[]>()
                     ?? throw new InvalidOperationException(
                         "DownstreamApi:Scopes must be configured when Entra ID auth is active.");

        var token = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes, cancellationToken: cancellationToken);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
