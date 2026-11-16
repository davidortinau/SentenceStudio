using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Startup;

namespace SentenceStudio.WebApp.Tests.Infrastructure;

/// <summary>
/// A stand-in for the API, running on a real loopback port.
/// </summary>
/// <remarks>
/// <para>
/// It answers the startup readiness route the WebApp waits on, and records every request it
/// receives, so a test can prove what the WebApp did or did not put on the wire (an admin page's
/// first HTML must spend no admin request, for instance). A stub is used rather than the real API
/// because these tests are about the WebApp's side, and one that booted the API too could not
/// tell a WebApp fault from an API one.
/// </para>
/// <para>
/// It records the <c>Authorization</c> header's shape and the bearer token of each request. The
/// recording lives in memory for the lifetime of one test and is never logged. (The name is from
/// the Sam operator page this stub first served; that page is now the admin feedback inbox.)
/// </para>
/// </remarks>
public sealed class StubOperatorApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    private StubOperatorApi(WebApplication app) => _app = app;

    /// <summary>What the public API startup/readiness route answers with.</summary>
    public HttpStatusCode VersionStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>The migration ID advertised by the test API after its fixture has migrated.</summary>
    public string VersionMigrationId { get; set; } = string.Empty;

    /// <summary>The non-secret identity read from the migrated test database.</summary>
    public string VersionDatabaseIdentity { get; set; } = string.Empty;

    /// <summary>Optional redirect target for the readiness client's real HTTP handler test.</summary>
    public string? VersionRedirectTarget { get; set; }

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

        app.MapGet("/api/version/info", (HttpContext http) =>
        {
            if (stub.VersionRedirectTarget is not null)
            {
                http.Response.StatusCode = (int)stub.VersionStatus;
                http.Response.Headers.Location = stub.VersionRedirectTarget;
                return Task.CompletedTask;
            }

            return stub.Answer(http, stub.VersionStatus, stub.VersionBody());
        });
        app.MapGet("/api/version/ready-after-redirect", (HttpContext http) =>
            stub.Answer(http, HttpStatusCode.OK, stub.VersionBody()));

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

    private string VersionBody() => JsonSerializer.Serialize(new
    {
        version = "1.0",
        applicationStartupContract = ApiApplicationStartupContract.Current,
        applicationMigrationId = VersionMigrationId,
        databaseIdentity = VersionDatabaseIdentity
    });

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
