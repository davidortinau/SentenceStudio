using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SentenceStudio.Api.Security.DataProtection;

/// <summary>
/// Configures the Data Protection key ring that every protected coach payload depends on.
/// </summary>
/// <remarks>
/// <para>
/// The coach stores learner conversation state as ciphertext produced by
/// <see cref="IDataProtectionProvider"/>. Out of the box the key ring is written to the local
/// file system and, in a container with no mounted volume, is regenerated on every start — so
/// yesterday's rows silently stop decrypting. That is tolerable while the only protected row is
/// a resumable session checkpoint, and it is data loss the moment durable history or memory is
/// switched on.
/// </para>
/// <para>
/// This puts the ring in blob storage — Azurite locally through Aspire, Azure Blob Storage when
/// deployed — and wraps it with a Key Vault key so possession of the storage account is not
/// possession of the keys.
/// </para>
/// </remarks>
public static class CoachDataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// The environment name that must never reach an Azure endpoint. Integration tests boot the
    /// real <c>Program</c>, so without this a test run would authenticate against whatever
    /// credential the machine happens to have.
    /// </summary>
    public const string TestingEnvironmentName = "Testing";

    /// <summary>Adds the coach Data Protection key ring and its durable-content gate.</summary>
    public static IServiceCollection AddCoachDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger? startupLogger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new CoachDataProtectionOptions();
        configuration.GetSection(CoachDataProtectionOptions.SectionName).Bind(options);

        var durableContent = CoachDurableContentOptions.FromConfiguration(configuration);
        services.TryAddSingleton<ICoachDurableContentGate>(durableContent);

        // Tests boot the real host. Resolving a plan that names an Azure endpoint would make the
        // suite depend on a credential and, worse, could point a test at a real account. The
        // framework default ring is correct here: a test process has no content to outlive it.
        if (environment.IsEnvironment(TestingEnvironmentName))
        {
            services.AddDataProtection().SetApplicationName(ResolveApplicationName(options));
            startupLogger?.LogInformation(
                "[Coach] Data Protection uses the host default key ring in the {Environment} environment.",
                environment.EnvironmentName);
            return services;
        }

        var connectionString = configuration.GetConnectionString(options.ConnectionName);

        // Throws in Production when durable content is on and the ring is missing or unwrapped.
        // Deliberately not caught: a host that cannot protect content must not accept traffic.
        var plan = CoachKeyRingPlanner.Resolve(
            options,
            connectionString,
            durableContent.IsDurableContentEnabled,
            environment.IsProduction());

        services.TryAddSingleton(plan);

        var builder = services.AddDataProtection().SetApplicationName(plan.ApplicationName);

        if (plan.Mode is CoachKeyRingMode.HostDefault)
        {
            // Safe to log: it names no account, container, or key.
            startupLogger?.LogWarning(
                "[Coach] Data Protection uses the host default key ring. Protected coach rows written " +
                "now may not be readable after a restart. {KeyRing}",
                plan.Describe());

            return services;
        }

        var container = CreateContainerClient(plan);

        if (plan.CreateContainerIfMissingAllowed(options, environment))
        {
            // Local only. The emulator starts empty and nothing else provisions the container;
            // in a deployed environment infrastructure owns creation and the app should not hold
            // container-create rights.
            container.CreateIfNotExists();
        }

        // The BlobClient overload is used rather than the connection-string or URI overloads so
        // both modes converge on one code path: the client above already carries the right
        // credential, and the alternatives would re-parse configuration that has already been
        // validated.
        builder.PersistKeysToAzureBlobStorage(container.GetBlobClient(plan.BlobName!));

        if (plan.KeyVaultKeyIdentifier is not null)
        {
            // Order matters: Key Vault protection turns off the automatic storage-location
            // defaults, so persistence has to be configured explicitly first (above) or the ring
            // silently falls back to the local file system.
            builder.ProtectKeysWithAzureKeyVault(plan.KeyVaultKeyIdentifier, CreateCredential(plan));
        }

        // Describe() is the only safe projection: it carries the mode, the container and blob
        // names, and a boolean. No URI, no connection string, no key identifier.
        startupLogger?.LogInformation("[Coach] Data Protection key ring configured. {KeyRing}", plan.Describe());

        return services;
    }

    private static BlobContainerClient CreateContainerClient(CoachKeyRingPlan plan) =>
        plan.Mode switch
        {
            CoachKeyRingMode.AzureBlobConnectionString =>
                new BlobContainerClient(plan.ConnectionString, plan.ContainerName),

            CoachKeyRingMode.AzureBlobUri =>
                new BlobContainerClient(plan.ContainerUri, CreateCredential(plan)),

            _ => throw new CoachDataProtectionConfigurationException(
                $"No blob client exists for key ring mode {plan.Mode}.")
        };

    /// <summary>
    /// Builds the credential for blob and Key Vault access. A user-assigned identity is named
    /// explicitly when configured, because a host with several identities otherwise picks one by
    /// chance and fails intermittently.
    /// </summary>
    private static TokenCredential CreateCredential(CoachKeyRingPlan plan) =>
        plan.ManagedIdentityClientId is null
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = plan.ManagedIdentityClientId
            });

    private static string ResolveApplicationName(CoachDataProtectionOptions options) =>
        string.IsNullOrWhiteSpace(options.ApplicationName)
            ? CoachDataProtectionOptions.DefaultApplicationName
            : options.ApplicationName.Trim();
}

/// <summary>Small helpers kept off the plan record so it stays a pure value.</summary>
internal static class CoachKeyRingPlanExtensions
{
    /// <summary>
    /// Container creation is allowed only for the emulator-shaped connection-string mode outside
    /// Production, and only when the operator has not turned it off.
    /// </summary>
    public static bool CreateContainerIfMissingAllowed(
        this CoachKeyRingPlan plan,
        CoachDataProtectionOptions options,
        IHostEnvironment environment) =>
        options.CreateContainerIfMissing
        && plan.Mode is CoachKeyRingMode.AzureBlobConnectionString
        && !environment.IsProduction();
}
