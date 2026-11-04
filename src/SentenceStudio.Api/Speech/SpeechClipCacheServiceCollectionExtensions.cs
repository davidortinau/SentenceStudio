using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Security.DataProtection;

namespace SentenceStudio.Api.Speech;

/// <summary>Wires the speech clip cache to the <c>media</c> blob container.</summary>
/// <remarks>
/// Aspire injects <c>ConnectionStrings:media-container</c>: the Azurite connection string locally,
/// and <c>Endpoint=https://...;ContainerName=media</c> in Azure, where the managed identity
/// (DefaultAzureCredential, as for Foundry) authenticates. With no connection string, the cache is
/// off and every request goes to the provider, exactly as before.
/// </remarks>
public static class SpeechClipCacheServiceCollectionExtensions
{
    public const string SectionName = "SpeechCache";
    public const string ConnectionName = "media-container";
    public const string DefaultContainerName = "media";

    private const string EndpointSegment = "Endpoint=";

    public static IServiceCollection AddSpeechClipCache(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var (store, description) = CreateStore(configuration, environment);
        if (store is not null)
        {
            services.TryAddSingleton<ISpeechClipStore>(store);
        }

        // A test host can register its own ISpeechClipStore before or after this; the cache
        // resolves whichever is in the container when it is first used.
        services.TryAddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SpeechClipCache>>();
            var resolved = sp.GetService<ISpeechClipStore>();
            logger.LogInformation(
                "Speech cache {State}. {Configuration}",
                resolved is null ? "disabled" : "enabled",
                resolved is null || ReferenceEquals(resolved, store) ? description : "Store=Substituted");
            return new SpeechClipCache(logger, resolved);
        });

        return services;
    }

    /// <summary>Builds the blob store, or explains why there is none. Never throws: the cache is optional.</summary>
    internal static (ISpeechClipStore? Store, string Description) CreateStore(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!configuration.GetValue($"{SectionName}:Enabled", true))
        {
            return (null, "Reason=TurnedOff");
        }

        // Tests boot the real Program; never let one reach an Azure endpoint with the machine's
        // credential. Same rule as the coach key ring.
        if (environment.IsEnvironment(CoachDataProtectionServiceCollectionExtensions.TestingEnvironmentName))
        {
            return (null, "Reason=TestingEnvironment");
        }

        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return (null, "Reason=NoConnectionString");
        }

        var (target, containerName) = CoachKeyRingPlanner.SplitConnectionString(connectionString, DefaultContainerName);

        try
        {
            var container = target.StartsWith(EndpointSegment, StringComparison.OrdinalIgnoreCase)
                ? new BlobContainerClient(ContainerUri(target, containerName), new DefaultAzureCredential(), ClientOptions())
                : new BlobContainerClient(target, containerName, ClientOptions());

            var mode = target.StartsWith(EndpointSegment, StringComparison.OrdinalIgnoreCase)
                ? "ManagedIdentity"
                : "ConnectionString";
            return (new AzureBlobSpeechClipStore(container), $"Mode={mode} Container={containerName} Prefix={SpeechClipCacheKey.Prefix}");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or UriFormatException)
        {
            // The value is not repeated: a connection string can carry an account key.
            return (null, $"Reason=InvalidConnectionString ExceptionType={ex.GetType().Name}");
        }
    }

    private static Uri ContainerUri(string target, string containerName)
    {
        var endpoint = target[EndpointSegment.Length..].Split(';')[0].Trim().TrimEnd('/');
        return new Uri($"{new Uri(endpoint, UriKind.Absolute).AbsoluteUri.TrimEnd('/')}/{containerName}");
    }

    /// <summary>
    /// Short retries and timeouts: a slow store must fall back to the provider quickly instead of
    /// holding the learner's request. <see cref="SpeechClipCache"/> also bounds each call.
    /// </summary>
    private static BlobClientOptions ClientOptions()
    {
        var options = new BlobClientOptions();
        options.Retry.Mode = RetryMode.Exponential;
        options.Retry.MaxRetries = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(2);
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(20);
        return options;
    }
}
