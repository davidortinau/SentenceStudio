using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.WebApp.Tests.Infrastructure;

/// <summary>
/// A stand-in for the Development-only coach operator API, running on a real loopback port.
/// </summary>
/// <remarks>
/// <para>
/// The WebApp reaches the operator surface over HTTP through <c>SamOpportunityOperatorClient</c>,
/// and the thing under test is what that client puts on the wire and what the WebApp does with the
/// answer. A stub is used rather than the real API because these tests need to control the answer
/// — the same request has to be able to return rows, a 404, and a 401 — and because a test that
/// booted the API too could not tell a WebApp token-forwarding bug apart from an API cohort bug.
/// </para>
/// <para>
/// It records the <c>Authorization</c> header of every request, which is how the token-forwarding
/// assertions are made. It records the header's <em>shape</em> and the bearer token, because the
/// tests must be able to prove the token names the signed-in learner; the recording lives in
/// memory for the lifetime of one test and is never logged.
/// </para>
/// </remarks>
public sealed class StubOperatorApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    private StubOperatorApi(WebApplication app) => _app = app;

    /// <summary>What the operator routes answer with. Mutable between requests.</summary>
    public HttpStatusCode RollupStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>The JSON body the rollup route returns when it answers 200.</summary>
    public string RollupBody { get; set; } = "[]";

    /// <summary>What the list route answers with.</summary>
    public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>The JSON body the list route returns when it answers 200.</summary>
    public string ListBody { get; set; } = """{"items":[],"total":0,"skip":0,"take":50}""";

    /// <summary>The base address the WebApp should be pointed at.</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>Every request this stub received, oldest first.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>One request, reduced to what the assertions need.</summary>
    /// <param name="Path">The request path, including query.</param>
    /// <param name="AuthorizationScheme">The auth scheme, or null when no header was sent.</param>
    /// <param name="BearerToken">The bearer token, or null when the scheme was not Bearer.</param>
    public sealed record RecordedRequest(string Path, string? AuthorizationScheme, string? BearerToken);

    /// <summary>Starts the stub on an arbitrary free loopback port.</summary>
    public static async Task<StubOperatorApi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();

        var app = builder.Build();
        var stub = new StubOperatorApi(app);

        app.Use(async (context, next) =>
        {
            stub.Record(context);
            await next();
        });

        const string prefix = "/api/v1/coach/operator/opportunities";

        app.MapGet($"{prefix}/rollup", (HttpContext http) =>
            stub.Answer(http, stub.RollupStatus, stub.RollupBody));

        app.MapGet(prefix, (HttpContext http) =>
            stub.Answer(http, stub.ListStatus, stub.ListBody));

        app.MapGet($"{prefix}/", (HttpContext http) =>
            stub.Answer(http, stub.ListStatus, stub.ListBody));

        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses.FirstOrDefault();

        stub.BaseAddress = address
            ?? app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("The stub operator API did not report an address.");

        return stub;
    }

    private void Record(HttpContext context)
    {
        string? scheme = null;
        string? token = null;

        var header = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(header))
        {
            var space = header.IndexOf(' ');
            if (space > 0)
            {
                scheme = header[..space];
                if (string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    token = header[(space + 1)..].Trim();
                }
            }
            else
            {
                scheme = header;
            }
        }

        _requests.Enqueue(new RecordedRequest(
            context.Request.Path + context.Request.QueryString, scheme, token));
    }

    private async Task Answer(HttpContext http, HttpStatusCode status, string body)
    {
        http.Response.StatusCode = (int)status;
        if (status != HttpStatusCode.OK)
        {
            return;
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
