namespace SentenceStudio.Api.Security.DataProtection;

/// <summary>How the key ring is stored.</summary>
public enum CoachKeyRingMode
{
    /// <summary>
    /// The framework default: a local file system or registry ring. Per-machine and, in a
    /// container, lost on restart. Only acceptable when no durable coach content exists.
    /// </summary>
    HostDefault,

    /// <summary>Azure Blob Storage reached with a connection string. The Azurite path locally.</summary>
    AzureBlobConnectionString,

    /// <summary>Azure Blob Storage reached by URI with a token credential. The deployed path.</summary>
    AzureBlobUri
}

/// <summary>
/// The resolved key-ring configuration. Produced by <see cref="CoachKeyRingPlanner"/> before any
/// Azure client is constructed, so the decision is testable without a network or an emulator.
/// </summary>
/// <remarks>
/// <see cref="ConnectionString"/>, <see cref="ContainerUri"/>, and
/// <see cref="KeyVaultKeyIdentifier"/> are secrets or secret-adjacent and must never reach a log.
/// <see cref="Describe"/> exists so callers have an obvious safe alternative.
/// </remarks>
public sealed record CoachKeyRingPlan
{
    /// <summary>How the ring is stored.</summary>
    public required CoachKeyRingMode Mode { get; init; }

    /// <summary>The stable Data Protection application name.</summary>
    public required string ApplicationName { get; init; }

    /// <summary>Blob connection string. Never log. Null outside <see cref="CoachKeyRingMode.AzureBlobConnectionString"/>.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Container URI. Never log. Null outside <see cref="CoachKeyRingMode.AzureBlobUri"/>.</summary>
    public Uri? ContainerUri { get; init; }

    /// <summary>The container name. Not a secret.</summary>
    public string? ContainerName { get; init; }

    /// <summary>The key-ring blob name. Not a secret.</summary>
    public string? BlobName { get; init; }

    /// <summary>The wrapping key. Never log — it names the key that decrypts every payload.</summary>
    public Uri? KeyVaultKeyIdentifier { get; init; }

    /// <summary>The user-assigned identity to authenticate with, when set.</summary>
    public string? ManagedIdentityClientId { get; init; }

    /// <summary>Whether the ring is wrapped by a Key Vault key.</summary>
    public bool IsKeyVaultProtected => KeyVaultKeyIdentifier is not null;

    /// <summary>Whether the ring survives a restart.</summary>
    public bool IsDurable => Mode is not CoachKeyRingMode.HostDefault;

    /// <summary>
    /// A content-free, secret-free description for startup logs: mode, container and blob names,
    /// and whether Key Vault wrapping is on. Deliberately the only string a caller should log.
    /// </summary>
    public string Describe() =>
        $"Mode={Mode} Container={ContainerName ?? "(none)"} Blob={BlobName ?? "(none)"} " +
        $"KeyVaultProtected={IsKeyVaultProtected}";
}

/// <summary>
/// Raised when the host cannot be configured with a key ring that is safe for the content it is
/// about to store. Always fatal at startup: a process that boots without a durable ring writes
/// payloads nothing will ever read again.
/// </summary>
public sealed class CoachDataProtectionConfigurationException : Exception
{
    /// <inheritdoc />
    public CoachDataProtectionConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Decides where the key ring lives. Pure: no Azure client, no network, no environment probing
/// beyond the values handed in, so every branch — including the Production failure branches — is
/// unit-testable without touching a real account.
/// </summary>
public static class CoachKeyRingPlanner
{
    /// <summary>The connection-string segment Aspire appends for a container-scoped resource.</summary>
    private const string ContainerNameSegment = "ContainerName=";

    /// <summary>The connection-string segment Aspire uses for the token-credential form.</summary>
    private const string EndpointSegment = "Endpoint=";

    /// <summary>
    /// Resolves the plan, or throws when durable content would be written under a key ring that
    /// cannot survive a restart.
    /// </summary>
    /// <param name="options">Bound <c>Coach:DataProtection</c> settings.</param>
    /// <param name="connectionString">
    /// The value of <c>ConnectionStrings:{ConnectionName}</c>, or null when the host has no
    /// storage resource wired up.
    /// </param>
    /// <param name="durableContentEnabled">
    /// Whether durable coach history or memory is on. This is what raises a missing key ring from
    /// a nuisance to a release blocker.
    /// </param>
    /// <param name="isProduction">
    /// Whether the host is Production. Only Production fails closed: a developer must still be
    /// able to boot the API with nothing configured.
    /// </param>
    /// <exception cref="CoachDataProtectionConfigurationException">
    /// Production, durable content on, and no durable ring or no Key Vault wrapping.
    /// </exception>
    public static CoachKeyRingPlan Resolve(
        CoachDataProtectionOptions options,
        string? connectionString,
        bool durableContentEnabled,
        bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);

        var applicationName = string.IsNullOrWhiteSpace(options.ApplicationName)
            ? CoachDataProtectionOptions.DefaultApplicationName
            : options.ApplicationName.Trim();

        var keyVaultKeyIdentifier = ParseKeyVaultKeyIdentifier(options.KeyVaultKeyIdentifier);

        if (!options.Enabled || string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail closed, but only where it is a real risk. A missing ring in Production with
            // durable content on is unrecoverable data loss the moment the process restarts, so
            // the host must not start. Anywhere else, the framework default is acceptable and a
            // developer keeps a working local loop.
            if (isProduction && durableContentEnabled)
            {
                throw new CoachDataProtectionConfigurationException(
                    "Durable coach content is enabled in Production but no Data Protection key ring is " +
                    $"configured. Set 'ConnectionStrings:{options.ConnectionName}' to the key-ring blob " +
                    $"container and '{CoachDataProtectionOptions.SectionName}:KeyVaultKeyIdentifier' to the " +
                    "wrapping key. Starting without a durable key ring would make every protected coach " +
                    "row unreadable after the next restart.");
            }

            return new CoachKeyRingPlan
            {
                Mode = CoachKeyRingMode.HostDefault,
                ApplicationName = applicationName
            };
        }

        var (target, containerName) = SplitConnectionString(connectionString, options.ContainerName);

        // Production must also be wrapped. A key ring blob that is not protected by a Key Vault
        // key is plaintext key material: anyone who can read the storage account can decrypt
        // every learner conversation without ever touching the database.
        if (isProduction && durableContentEnabled && keyVaultKeyIdentifier is null)
        {
            throw new CoachDataProtectionConfigurationException(
                "Durable coach content is enabled in Production but the Data Protection key ring is not " +
                $"protected by a Key Vault key. Set '{CoachDataProtectionOptions.SectionName}:" +
                "KeyVaultKeyIdentifier' to a versionless key identifier. An unwrapped key ring in blob " +
                "storage is plaintext key material.");
        }

        if (target.StartsWith(EndpointSegment, StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = target[EndpointSegment.Length..].Trim().TrimEnd('/');

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new CoachDataProtectionConfigurationException(
                    $"'ConnectionStrings:{options.ConnectionName}' carries an Endpoint that is not an " +
                    "absolute URI. The value is not repeated here because it may carry a credential.");
            }

            return new CoachKeyRingPlan
            {
                Mode = CoachKeyRingMode.AzureBlobUri,
                ApplicationName = applicationName,
                ContainerUri = new Uri($"{endpointUri.AbsoluteUri.TrimEnd('/')}/{containerName}"),
                ContainerName = containerName,
                BlobName = options.BlobName,
                KeyVaultKeyIdentifier = keyVaultKeyIdentifier,
                ManagedIdentityClientId = NormalizeOrNull(options.ManagedIdentityClientId)
            };
        }

        return new CoachKeyRingPlan
        {
            Mode = CoachKeyRingMode.AzureBlobConnectionString,
            ApplicationName = applicationName,
            ConnectionString = target,
            ContainerName = containerName,
            BlobName = options.BlobName,
            KeyVaultKeyIdentifier = keyVaultKeyIdentifier,
            ManagedIdentityClientId = NormalizeOrNull(options.ManagedIdentityClientId)
        };
    }

    /// <summary>
    /// Splits Aspire's container-scoped connection string into the storage target and the
    /// container name.
    /// </summary>
    /// <remarks>
    /// Aspire appends <c>;ContainerName=&lt;name&gt;</c> to both forms it emits — the Azurite
    /// shared-key string and the <c>Endpoint=</c> string. The Azure SDK does not understand that
    /// segment, so it has to come off before the value reaches a client.
    /// </remarks>
    private static (string Target, string ContainerName) SplitConnectionString(
        string connectionString,
        string fallbackContainerName)
    {
        var segments = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(segments.Length);
        var containerName = fallbackContainerName;

        foreach (var segment in segments)
        {
            if (segment.StartsWith(ContainerNameSegment, StringComparison.OrdinalIgnoreCase))
            {
                var value = segment[ContainerNameSegment.Length..].Trim();
                if (value.Length > 0)
                {
                    containerName = value;
                }

                continue;
            }

            kept.Add(segment);
        }

        return (string.Join(';', kept), containerName);
    }

    /// <summary>
    /// Validates the wrapping key identifier without echoing it. An https absolute URI is
    /// required; anything else is a configuration mistake that would otherwise surface as an
    /// authentication failure at first write, long after startup.
    /// </summary>
    private static Uri? ParseKeyVaultKeyIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new CoachDataProtectionConfigurationException(
                $"'{CoachDataProtectionOptions.SectionName}:KeyVaultKeyIdentifier' must be an absolute " +
                "https key identifier. The configured value is not repeated here.");
        }

        return uri;
    }

    private static string? NormalizeOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
