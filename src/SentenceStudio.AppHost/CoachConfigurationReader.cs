using Microsoft.Extensions.Configuration;

namespace SentenceStudio.AppHost;

/// <summary>
/// Result of reading the Coach allowlist. Carries the deduplicated list plus source indices
/// where duplicates were dropped, so the top-level AppHost can log a warning without exposing
/// the profile ID values.
/// </summary>
internal sealed class CoachAllowlistResult
{
    /// <summary>Deduplicated, compacted, trimmed entries in first-occurrence order.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>
    /// Source indices (0-based, from the configuration keys) where a duplicate was detected and
    /// dropped. Empty when no duplicates exist.
    /// </summary>
    public IReadOnlyList<int> DuplicateSourceIndices { get; }

    internal CoachAllowlistResult(IReadOnlyList<string> ids, IReadOnlyList<int> duplicateSourceIndices)
    {
        Ids = ids;
        DuplicateSourceIndices = duplicateSourceIndices;
    }
}

/// <summary>
/// Environment values the AppHost forwards to the API for Coach, plus non-sensitive source
/// diagnostics that must be reported by the AppHost itself.
/// </summary>
internal sealed class CoachApiEnvironmentResult
{
    /// <summary>API environment variable name/value pairs ready for <c>WithEnvironment</c>.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    /// <summary>Source allowlist indices that were dropped because they repeated an earlier ID.</summary>
    public IReadOnlyList<int> DuplicateAllowlistSourceIndices { get; }

    internal CoachApiEnvironmentResult(
        IReadOnlyDictionary<string, string> environmentVariables,
        IReadOnlyList<int> duplicateAllowlistSourceIndices)
    {
        EnvironmentVariables = environmentVariables;
        DuplicateAllowlistSourceIndices = duplicateAllowlistSourceIndices;
    }
}

/// <summary>
/// Pure-function reader for Coach AppHost configuration. Extracted from the top-level AppHost
/// program so the exact same source selection, validation, normalization, and forwarding path is
/// testable without generating a deployment.
/// </summary>
internal static class CoachConfigurationReader
{
    /// <summary>
    /// Maximum indexed entries scanned in <c>Coach:AllowedUserProfileIds:N</c>.
    /// </summary>
    internal const int MaxAllowedEntries = 16;

    private static readonly (string ConfigurationKey, string EnvironmentName)[] OptionalSettings =
    {
        ("Coach:AgentConfigVersion", "Coach__AgentConfigVersion"),
        ("Coach:MaxOutputTokens", "Coach__MaxOutputTokens"),
        ("Coach:ReasoningEffort", "Coach__ReasoningEffort"),
        ("Coach:MaxRunsPerDay", "Coach__MaxRunsPerDay"),
        ("Coach:MaxRunsPerWeek", "Coach__MaxRunsPerWeek"),
        ("Coach:DataProtection:KeyVaultKeyIdentifier", "Coach__DataProtection__KeyVaultKeyIdentifier"),
        ("Coach:DataProtection:ManagedIdentityClientId", "Coach__DataProtection__ManagedIdentityClientId"),
        ("Coach:DurableHistory:Enabled", "Coach__DurableHistory__Enabled"),
        ("Coach:Memory:Enabled", "Coach__Memory__Enabled"),
        ("Coach:SamOverlay:Enabled", "Coach__SamOverlay__Enabled"),
        ("Coach:SamReadTools:Enabled", "Coach__SamReadTools__Enabled"),
        ("Coach:SamWriteTools:Enabled", "Coach__SamWriteTools__Enabled"),
        ("Coach:Opportunities:Enabled", "Coach__Opportunities__Enabled"),
        ("Coach:Opportunities:RetentionDays", "Coach__Opportunities__RetentionDays"),
        ("Coach:Reports:Enabled", "Coach__Reports__Enabled"),
        ("Coach:Reports:RetentionDays", "Coach__Reports__RetentionDays")
    };

    /// <summary>
    /// Selects the only configuration source Coach may use for the current AppHost mode.
    /// Publish mode intentionally rebuilds configuration from process environment variables alone:
    /// the AppHost's default configuration also contains appsettings.Development.json and user
    /// secrets, neither of which may become a deployment manifest value. Run mode retains the
    /// complete builder configuration so local development behavior is unchanged.
    /// </summary>
    internal static IConfiguration ForExecutionMode(
        IConfiguration builderConfiguration,
        bool isPublishMode)
    {
        var environmentConfiguration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        return ForExecutionMode(builderConfiguration, isPublishMode, environmentConfiguration);
    }

    /// <summary>Test seam for supplying a deterministic environment-only configuration.</summary>
    internal static IConfiguration ForExecutionMode(
        IConfiguration builderConfiguration,
        bool isPublishMode,
        IConfiguration environmentConfiguration)
    {
        ArgumentNullException.ThrowIfNull(builderConfiguration);
        ArgumentNullException.ThrowIfNull(environmentConfiguration);

        return isPublishMode ? environmentConfiguration : builderConfiguration;
    }

    /// <summary>
    /// Reads, validates, and normalizes every Coach value the AppHost forwards to the API.
    /// Optional values remain verbatim after trimming so the API's startup validators remain the
    /// single authority for their formats, ranges, and feature dependencies.
    /// </summary>
    internal static CoachApiEnvironmentResult ReadApiEnvironment(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Coach__Enabled"] = ReadEnabled(configuration) ? "true" : "false",
            ["Coach__Implementation"] = ReadImplementation(configuration)
        };

        var allowlist = ReadAllowedUserProfileIdsWithDiagnostics(configuration);
        for (var i = 0; i < allowlist.Ids.Count; i++)
        {
            environmentVariables[$"Coach__AllowedUserProfileIds__{i}"] = allowlist.Ids[i];
        }

        foreach (var (configurationKey, environmentName) in OptionalSettings)
        {
            var value = configuration[configurationKey]?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                environmentVariables[environmentName] = value;
            }
        }

        return new CoachApiEnvironmentResult(
            environmentVariables,
            allowlist.DuplicateSourceIndices);
    }

    /// <summary>
    /// Reads all nonblank <c>Coach:AllowedUserProfileIds:N</c> entries (N = 0..15) from
    /// configuration, preserving index order but compacting gaps. Returns an empty list when
    /// no entries are set, keeping the cohort fail-closed.
    /// </summary>
    internal static IReadOnlyList<string> ReadAllowedUserProfileIds(IConfiguration configuration)
    {
        return ReadAllowedUserProfileIdsWithDiagnostics(configuration).Ids;
    }

    /// <summary>
    /// Extended version that also reports which source indices were dropped as duplicates.
    /// The AppHost uses this to log a warning without exposing profile ID values.
    /// Comparison semantics: ordinal (case-sensitive), matching the API's
    /// <c>CoachOptionsValidator</c> which uses <c>HashSet&lt;string&gt;(StringComparer.Ordinal)</c>.
    /// </summary>
    internal static CoachAllowlistResult ReadAllowedUserProfileIdsWithDiagnostics(IConfiguration configuration)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateIndices = new List<int>();

        for (var i = 0; i < MaxAllowedEntries; i++)
        {
            var raw = configuration[$"Coach:AllowedUserProfileIds:{i}"]?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!seen.Add(raw))
            {
                duplicateIndices.Add(i);
                continue;
            }

            result.Add(raw);
        }

        return new CoachAllowlistResult(result, duplicateIndices);
    }

    private static bool ReadEnabled(IConfiguration configuration)
    {
        var raw = configuration["Coach:Enabled"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!bool.TryParse(raw.Trim(), out var enabled))
        {
            throw new InvalidOperationException(
                "Coach:Enabled must be 'true' or 'false'. Set Coach__Enabled on the AppHost, or leave it unset to keep the coach off.");
        }

        return enabled;
    }

    private static string ReadImplementation(IConfiguration configuration)
    {
        var raw = configuration["Coach:Implementation"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return "baseline";
        }

        var implementation = raw.Trim().ToLowerInvariant();

        if (implementation is not ("baseline" or "harness"))
        {
            throw new InvalidOperationException(
                "Coach:Implementation must be 'baseline' or 'harness'. Leave Coach__Implementation unset to keep the plain baseline agent arm.");
        }

        return implementation;
    }
}
