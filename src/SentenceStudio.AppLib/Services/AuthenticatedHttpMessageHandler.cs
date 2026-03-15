using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Delegating handler that attaches a Bearer token to outgoing requests.
/// For Entra ID, uses configured scopes. For Identity auth (no scopes configured),
/// requests the token with an empty scope array — IdentityAuthService returns the
/// stored JWT regardless of scopes.
/// If token acquisition returns null, the request proceeds unauthenticated so the
/// server's DevAuthHandler can handle it during development.
/// </summary>
public class AuthenticatedHttpMessageHandler : DelegatingHandler
{
    private readonly string[] _defaultScopes;
    private readonly IAuthService _authService;
    private readonly ILogger<AuthenticatedHttpMessageHandler> _logger;

    public AuthenticatedHttpMessageHandler(
        IAuthService authService,
        IConfiguration configuration,
        ILogger<AuthenticatedHttpMessageHandler> logger)
    {
        _authService = authService;
        _logger = logger;

        _defaultScopes = configuration.GetSection("AzureAd:Scopes").Get<string[]>()
            ?? Array.Empty<string>();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var token = await _authService.GetAccessTokenAsync(_defaultScopes);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to attach Bearer token; proceeding without auth");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
