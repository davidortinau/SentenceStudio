using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Delegating handler that attaches a Bearer token to outgoing requests.
/// If the user is not signed in or token acquisition fails, the request
/// proceeds without an Authorization header — the server's DevAuthHandler
/// will handle unauthenticated requests during development.
/// </summary>
public class AuthenticatedHttpMessageHandler : DelegatingHandler
{
    private static string[] DefaultScopes => AuthConstants.DefaultScopes;

    private readonly IAuthService _authService;
    private readonly ILogger<AuthenticatedHttpMessageHandler> _logger;

    public AuthenticatedHttpMessageHandler(
        IAuthService authService,
        ILogger<AuthenticatedHttpMessageHandler> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_authService.IsSignedIn)
        {
            try
            {
                var token = await _authService.GetAccessTokenAsync(DefaultScopes);
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
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
