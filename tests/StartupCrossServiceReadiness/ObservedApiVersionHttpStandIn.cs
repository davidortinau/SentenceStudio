using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Startup;

namespace SentenceStudio.Tests.Startup;

internal sealed class ObservedApiVersionHttpStandIn : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _requests;
    private int _requestsWithCredentials;

    private ObservedApiVersionHttpStandIn(
        WebApplication app, string databaseIdentity, string migrationId)
    {
        _app = app;
        DatabaseIdentity = databaseIdentity;
        MigrationId = migrationId;
    }

    internal Uri Address { get; private set; } = null!;
    internal string DatabaseIdentity { get; }
    internal string MigrationId { get; }
    internal int Requests => Volatile.Read(ref _requests);
    internal bool ReceivedCredentials => Volatile.Read(ref _requestsWithCredentials) != 0;

    internal static async Task<ObservedApiVersionHttpStandIn> StartAsync(
        StartupGateCrossServiceProof proof)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();

        var app = builder.Build();
        var standIn = new ObservedApiVersionHttpStandIn(
            app, proof.ApiIdentity, proof.ExpectedMigrationId);
        app.MapGet("/api/version/info", standIn.RespondAsync);
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault();
            standIn.Address = new Uri(address
                ?? throw new InvalidOperationException(
                    "The controlled HTTP API stand-in did not report its loopback address."));
            if (standIn.Address.Host != "127.0.0.1" ||
                standIn.Address.UserInfo.Length != 0)
            {
                throw new InvalidOperationException(
                    "The controlled HTTP API stand-in must be bound only to loopback.");
            }
            return standIn;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private Task RespondAsync(HttpContext http)
    {
        Interlocked.Increment(ref _requests);
        if (http.Request.Headers.Authorization.Count != 0 ||
            http.Request.Headers.Cookie.Count != 0)
        {
            Interlocked.Exchange(ref _requestsWithCredentials, 1);
        }

        return http.Response.WriteAsJsonAsync(new
        {
            version = "1.0",
            applicationStartupContract = ApiApplicationStartupContract.Current,
            applicationMigrationId = MigrationId,
            databaseIdentity = DatabaseIdentity
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
