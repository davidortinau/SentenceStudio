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
        try
        {
            var scopes = _configuration.GetSection("DownstreamApi:Scopes").Get<string[]>()
                         ?? ["api://8c051bcf-bd3a-4051-9cd3-0556ba5df2d8/.default"];

            var token = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire token for downstream API call");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
