using System.Net.Http.Json;
using SentenceStudio.Contracts.Wire;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebApp.Coach;

/// <summary>Preserves the coach's typed HTTP failures before the AG-UI client starts reading SSE.</summary>
public sealed class CoachAGUIProblemHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        using (response)
        {
            var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                ? await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(
                    WireJson.Client, cancellationToken)
                : null;
            throw new CoachApiException(response.StatusCode, problem?.Type, problem?.Title, problem?.Detail);
        }
    }
}
