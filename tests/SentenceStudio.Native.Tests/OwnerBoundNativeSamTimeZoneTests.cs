using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Regression for 2026-10-31: Sam said "no plan for today yet" while the native Dashboard showed
/// one. The native Sam client sent no X-Timezone, so after UTC midnight the API computed the next
/// day and found no plan for it.
/// </summary>
public sealed class OwnerBoundNativeSamTimeZoneTests
{
    private static readonly Uri ApiRoot = new("https://api.example.invalid/");

    [Fact]
    public async Task EverySamRequestCarriesTheDeviceTimeZone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var capture = new CapturingHandler();
        using var provider = Build(zone, capture);

        using var client = new HttpClient(CreateHandler(provider), disposeHandler: false) { BaseAddress = ApiRoot };
        (await client.GetAsync("/api/v1/coach/availability")).Dispose();

        Assert.Equal(zone.Id, Assert.Single(capture.TimeZones));
    }

    [Fact]
    public async Task TheZoneIsReadPerRequestNotFrozenIntoThePooledChain()
    {
        var current = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var capture = new CapturingHandler();
        var services = Services(capture);
        services.AddTransient<IPlanDateContext>(_ => new PlanDateContext(current));
        using var provider = services.BuildServiceProvider();
        var handler = CreateHandler(provider);

        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = ApiRoot };
        (await client.GetAsync("/api/v1/coach/availability")).Dispose();
        current = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        (await client.GetAsync("/api/v1/coach/availability")).Dispose();

        Assert.Equal(["America/Chicago", "Asia/Seoul"], capture.TimeZones);
    }

    [Fact]
    public async Task AnExplicitTimeZoneIsNotOverwritten()
    {
        var capture = new CapturingHandler();
        using var provider = Build(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"), capture);

        using var client = new HttpClient(CreateHandler(provider), disposeHandler: false) { BaseAddress = ApiRoot };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/coach/availability");
        request.Headers.TryAddWithoutValidation(PlanDateHeaders.TimeZone, "Europe/Paris");
        (await client.SendAsync(request)).Dispose();

        Assert.Equal("Europe/Paris", Assert.Single(capture.TimeZones));
    }

    private static ServiceProvider Build(TimeZoneInfo zone, CapturingHandler capture)
    {
        var services = Services(capture);
        services.AddTransient<IPlanDateContext>(_ => new PlanDateContext(zone));
        return services.BuildServiceProvider();
    }

    private static ServiceCollection Services(CapturingHandler capture)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuthService>(_ => throw new InvalidOperationException("not used"));
        services.AddSingleton<IPreferencesService>(new NativeSliceFixture.ScratchPreferences());
        services.AddSingleton<DeviceUserScopeProvider>();
        services.AddOwnerBoundNativeSam(ApiRoot);
        // Replace only the transport so the registered handler chain is what is exercised.
        services.AddHttpClient(OwnerBoundNativeSamClient.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => capture);
        return services;
    }

    private static HttpMessageHandler CreateHandler(ServiceProvider provider) =>
        provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(OwnerBoundNativeSamClient.ClientName);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> TimeZones { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues(PlanDateHeaders.TimeZone, out var values))
                TimeZones.AddRange(values);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
