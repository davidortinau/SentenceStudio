using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SentenceStudio.Application.Capabilities;

namespace SentenceStudio.Application.Capabilities.Discovery;

public sealed class ApplicationCapabilityDiscoveryIndexBuilder
{
    private readonly List<ApplicationCapabilityDiscoveryDocument> _documents = [];
    private readonly Dictionary<CapabilityKey, ApplicationCapabilityDiscoveryDocument> _identities = [];
    private readonly Dictionary<string, string> _familiesByCode = new(StringComparer.Ordinal);
    private bool _isFrozen;

    public bool IsFrozen => _isFrozen;

    public ApplicationCapabilityIndexAdmission TryAdd(
        IApplicationCapabilityDescriptor descriptor,
        ApplicationCapabilitySchemaFingerprints schemaFingerprints)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schemaFingerprints);
        EnsureMutable();
        var snapshot = ApplicationCapabilityDiscoveryDescriptorSnapshot.Create(descriptor);
        ApplicationCapabilityDescriptorValidator.Validate(snapshot);

        if (!ApplicationCapabilityStaticValidation.HasCompleteDeclaredPolicy(snapshot))
        {
            return new(
                ApplicationCapabilityIndexAdmissionState.DefaultDenied,
                snapshot.Code,
                snapshot.MajorVersion);
        }

        if (_familiesByCode.TryGetValue(snapshot.Code, out var existingFamily)
            && !string.Equals(existingFamily, snapshot.Family, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Capability '{snapshot.Code}' changes family from '{existingFamily}' "
                + $"to '{snapshot.Family}' across versions.");
        }

        var names = new[] { snapshot.Code }.Concat(snapshot.Aliases);
        foreach (var name in names)
        {
            var key = new CapabilityKey(name, snapshot.MajorVersion);
            if (_identities.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"Capability name or alias '{name}@{snapshot.MajorVersion}' collides with "
                    + $"'{existing.Code}@{existing.MajorVersion}'.");
            }
        }

        var document = new ApplicationCapabilityDiscoveryDocument(snapshot, schemaFingerprints);
        _documents.Add(document);
        _familiesByCode[snapshot.Code] = snapshot.Family;
        foreach (var name in names)
        {
            _identities.Add(new CapabilityKey(name, snapshot.MajorVersion), document);
        }

        return new(
            ApplicationCapabilityIndexAdmissionState.Added,
            snapshot.Code,
            snapshot.MajorVersion);
    }

    public string ComputeDescriptorSetHash()
    {
        EnsureMutable();
        return ComputeHash(_documents);
    }

    public ApplicationCapabilityDiscoveryIndex Freeze(
        ApplicationCapabilityIndexGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        EnsureMutable();

        if (!string.Equals(
                generation.IndexSchemaVersion,
                ApplicationCapabilityIndexGeneration.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Index generation schema version is stale or unsupported.");
        }

        var actualHash = ComputeHash(_documents);
        if (!string.Equals(
                actualHash,
                generation.DescriptorSetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Index generation descriptor set hash is stale or does not match the frozen documents.");
        }

        var canonicalGeneration = new ApplicationCapabilityIndexGeneration(
            ApplicationCapabilityIndexGeneration.CurrentSchemaVersion,
            actualHash,
            generation.EmbeddingProvider,
            generation.EmbeddingModel,
            generation.EmbeddingGeneration,
            generation.EmbeddingDimensions,
            generation.CreatedAtUtc,
            generation.PromotionState);
        _isFrozen = true;
        var ordered = _documents
            .OrderBy(document => document.Code, StringComparer.Ordinal)
            .ThenBy(document => document.MajorVersion)
            .ToArray();
        var families = ordered
            .GroupBy(document => document.Family, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<ApplicationCapabilityDiscoveryDocument>)
                    new ReadOnlyCollection<ApplicationCapabilityDiscoveryDocument>(
                        group.OrderBy(document => document.Code, StringComparer.Ordinal)
                            .ThenBy(document => document.MajorVersion)
                            .ToArray()),
                StringComparer.Ordinal);

        return new(
            canonicalGeneration,
            new ReadOnlyCollection<ApplicationCapabilityDiscoveryDocument>(ordered),
            families);
    }

    private static string ComputeHash(
        IEnumerable<ApplicationCapabilityDiscoveryDocument> documents)
    {
        var canonical = new StringBuilder();
        Append(canonical, ApplicationCapabilityDiscoveryFingerprint.CanonicalRepresentationVersion);
        foreach (var document in documents
                     .OrderBy(item => item.Code, StringComparer.Ordinal)
                     .ThenBy(item => item.MajorVersion))
        {
            Append(canonical, document.Code);
            Append(canonical, document.MajorVersion.ToString(CultureInfo.InvariantCulture));
            Append(canonical, document.DiscoveryFingerprint);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');

    private void EnsureMutable()
    {
        if (_isFrozen)
        {
            throw new InvalidOperationException(
                "The application capability discovery index builder is frozen.");
        }
    }

    private readonly record struct CapabilityKey(string Code, int MajorVersion);
}

internal static class ApplicationCapabilityDiscoveryFingerprint
{
    internal const string CanonicalRepresentationVersion =
        "application-capability-discovery-descriptor/v2";

    internal static string Compute(
        IApplicationCapabilityDescriptor descriptor,
        ApplicationCapabilitySchemaFingerprints schemas)
    {
        var canonical = new StringBuilder();
        Append(canonical, "representation-version", CanonicalRepresentationVersion);
        Append(canonical, "code", descriptor.Code);
        Append(canonical, "family", descriptor.Family);
        Append(canonical, "major-version", descriptor.MajorVersion);
        Append(canonical, "selection-description", descriptor.SelectionDescription);
        AppendSet(canonical, "positive-examples", descriptor.PositiveExamples);
        AppendSet(canonical, "negative-examples", descriptor.NegativeExamples);
        AppendSet(canonical, "aliases", descriptor.Aliases);
        AppendSet(
            canonical,
            "surfaces",
            descriptor.Surfaces.Select(surface => Convert.ToInt32(surface, CultureInfo.InvariantCulture)));
        Append(canonical, "execution-authority", descriptor.ExecutionAuthority);
        Append(canonical, "effect", descriptor.Effect);
        Append(canonical, "sensitivity", descriptor.Sensitivity);
        Append(canonical, "confirmation", descriptor.Confirmation);
        Append(canonical, "idempotency", descriptor.Idempotency);
        Append(canonical, "synchronization", descriptor.Synchronization);
        Append(canonical, "continuation", descriptor.Continuation);
        Append(canonical, "policy-state", descriptor.PolicyState);
        Append(canonical, "coach-exposure-policy", descriptor.CoachExposurePolicy);
        Append(canonical, "rollout", descriptor.Rollout);
        Append(canonical, "availability", descriptor.Availability);
        Append(canonical, "dependencies", descriptor.Dependencies);
        Append(canonical, "release-validation-status", descriptor.ReleaseValidationStatus);
        Append(canonical, "request-contract-type", TypeIdentity(descriptor.RequestType));
        Append(canonical, "client-result-contract-type", TypeIdentity(descriptor.ClientResultType));
        Append(canonical, "coach-observation-contract-type", TypeIdentity(descriptor.CoachObservationType));
        Append(canonical, "descriptor-fingerprint", descriptor.DescriptorFingerprint);

        AppendBudgets(canonical, descriptor.Budgets);
        AppendImplementationIdentity(canonical, "handler", descriptor.Handler);
        AppendImplementationIdentity(canonical, "coach-projector", descriptor.CoachProjector);
        AppendQualification(canonical, descriptor.QualificationCandidate);

        Append(canonical, "request-schema-fingerprint", schemas.Request);
        Append(canonical, "client-result-schema-fingerprint", schemas.ClientResult);
        Append(canonical, "coach-observation-schema-fingerprint", schemas.CoachObservation);
        Append(canonical, "result-member-cost", 1);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void AppendBudgets(
        StringBuilder builder,
        ApplicationCapabilityBudgets? budgets)
    {
        Append(builder, "budgets-present", budgets is not null);
        if (budgets is null)
        {
            return;
        }

        Append(builder, "budget-max-request-bytes", budgets.MaxRequestBytes);
        Append(builder, "budget-max-client-result-bytes", budgets.MaxClientResultBytes);
        Append(builder, "budget-max-coach-observation-bytes", budgets.MaxCoachObservationBytes);
        Append(builder, "budget-schema-token-cost", budgets.SchemaTokenCost);
        Append(builder, "budget-risk-cost", budgets.RiskCost);
        Append(builder, "budget-exposure-cost", budgets.ExposureCost);
        Append(builder, "budget-max-execution-milliseconds", budgets.MaxExecutionMilliseconds);
        Append(builder, "budget-max-continuation-depth", budgets.MaxContinuationDepth);
    }

    private static void AppendImplementationIdentity(
        StringBuilder builder,
        string name,
        ApplicationCapabilityImplementationIdentity? identity)
    {
        Append(builder, $"{name}-present", identity is not null);
        if (identity is null)
        {
            return;
        }

        Append(
            builder,
            $"{name}-contract-type",
            identity.ContractType is null ? null : TypeIdentity(identity.ContractType));
        Append(builder, $"{name}-deferred-identity", identity.DeferredIdentity);
    }

    private static void AppendQualification(
        StringBuilder builder,
        ApplicationCapabilityQualificationCandidate? candidate)
    {
        Append(builder, "qualification-present", candidate is not null);
        if (candidate is null)
        {
            return;
        }

        Append(
            builder,
            "qualification-descriptor-fingerprint",
            candidate.DescriptorFingerprint);
        Append(
            builder,
            "qualification-handler-contract-type",
            TypeIdentity(candidate.HandlerContractType));
        Append(
            builder,
            "qualification-coach-projector-contract-type",
            TypeIdentity(candidate.CoachProjectorContractType));
        Append(
            builder,
            "qualification-report-fingerprint",
            candidate.QualificationReportFingerprint);
    }

    private static string TypeIdentity(Type type) =>
        $"{type.Assembly.GetName().Name}:{type.FullName ?? type.Name}";

    private static void AppendSet<T>(
        StringBuilder builder,
        string name,
        IEnumerable<T> values)
    {
        var ordered = values
            .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Append(builder, $"{name}-count", ordered.Length);
        foreach (var value in ordered)
        {
            Append(builder, $"{name}-item", value);
        }
    }

    private static void Append<T>(StringBuilder builder, string name, T value)
        where T : struct =>
        Append(builder, name, Convert.ToString(value, CultureInfo.InvariantCulture));

    private static void Append(
        StringBuilder builder,
        string name,
        string? value)
    {
        AppendValue(builder, name);
        AppendValue(builder, value is null ? "null" : "value");
        if (value is not null)
        {
            AppendValue(builder, value);
        }
    }

    private static void AppendValue(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');
}

public sealed class ApplicationCapabilityDiscoveryIndex
{
    internal ApplicationCapabilityDiscoveryIndex(
        ApplicationCapabilityIndexGeneration generation,
        IReadOnlyList<ApplicationCapabilityDiscoveryDocument> documents,
        FrozenDictionary<string, IReadOnlyList<ApplicationCapabilityDiscoveryDocument>> families)
    {
        Generation = generation;
        Documents = documents;
        Families = families;
    }

    public ApplicationCapabilityIndexGeneration Generation { get; }

    public IReadOnlyList<ApplicationCapabilityDiscoveryDocument> Documents { get; }

    internal FrozenDictionary<string, IReadOnlyList<ApplicationCapabilityDiscoveryDocument>> Families { get; }
}
