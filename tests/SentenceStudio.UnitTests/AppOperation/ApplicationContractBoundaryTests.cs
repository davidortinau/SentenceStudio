using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.AppOperation;

public sealed class ApplicationContractBoundaryTests
{
    private static readonly string[] ModelAuthoredForbiddenFragments =
    [
        "User", "Owner", "Identity", "Tenant", "Profile", "Email", "Credential", "Password", "Secret",
        "Confirmation", "Route", "Url", "Uri", "Sql", "Json", "Argument", "DomainVersion",
        "SynchronizationVersion", "StateVersion", "ExecutionAuthority", "Authority"
    ];

    private static readonly string[] SafeProjectionForbiddenFragments =
    [
        "Exception", "StackTrace", "Target", "Route", "Url", "Uri", "Sql", "User", "Owner",
        "Identity", "Tenant", "ProfileId", "Email", "Credential", "Password", "Secret"
    ];

    private static Assembly ContractAssembly => typeof(AppOperationWireSurface).Assembly;

    private static IReadOnlyList<Type> PublicContracts { get; } = ContractAssembly
        .GetTypes()
        .Where(type => type.IsPublic
            && type.Namespace == AppOperationWireSurface.Namespace
            && !type.IsEnum
            && type != typeof(AppOperationWireSurface))
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void Production_contract_graph_contains_no_open_or_arbitrary_payload_types()
    {
        var offenders = FindForbiddenPayloadTypes(PublicContracts);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Recursive_inspector_reports_full_paths_for_a_nested_bad_graph()
    {
        var roots = new[] { typeof(SyntheticBadRoot) };
        var nameOffenders = FindForbiddenNames(roots, ModelAuthoredForbiddenFragments);
        var typeOffenders = FindForbiddenPayloadTypes(roots);

        nameOffenders.Should().Contain(
        [
            "SyntheticBadRoot.Nodes[].Value.UserIdentity",
            "SyntheticBadRoot.Nodes[].Value.TenantId",
            "SyntheticBadRoot.Nodes[].Value.ProfileId",
            "SyntheticBadRoot.Nodes[].Value.Email",
            "SyntheticBadRoot.Nodes[].Value.Credential",
            "SyntheticBadRoot.Nodes[].Value.Secret",
            "SyntheticBadRoot.Nodes[].Value.Route",
            "SyntheticBadRoot.Nodes[].Value.Url",
            "SyntheticBadRoot.Nodes[].Value.Sql",
            "SyntheticBadRoot.Nodes[].Value.ExecutionAuthority",
            "SyntheticBadRoot.Nodes[].Value.DomainVersion",
            "SyntheticBadRoot.Nodes[].Value.Arguments",
            "SyntheticBadRoot.Nodes[].Value.OpenJson"
        ]);

        typeOffenders.Should().Contain(offender =>
            offender.StartsWith("SyntheticBadRoot.Nodes[].Value.Arguments: System.Object", StringComparison.Ordinal));
        typeOffenders.Should().Contain(offender =>
            offender.StartsWith("SyntheticBadRoot.Nodes[].Value.OpenJson: System.Nullable", StringComparison.Ordinal)
            && offender.EndsWith("System.Text.Json.JsonElement", StringComparison.Ordinal));
        typeOffenders.Should().Contain(offender =>
            offender.StartsWith("SyntheticBadRoot.Nodes[].Value.Secrets:", StringComparison.Ordinal)
            && offender.Contains("IReadOnlyDictionary", StringComparison.Ordinal));
        typeOffenders.Should().Contain(offender =>
            offender.StartsWith("SyntheticBadRoot.Nodes[].Value.PolymorphicPayload:", StringComparison.Ordinal)
            && offender.EndsWith(nameof(ISyntheticPolymorphicPayload), StringComparison.Ordinal));
    }

    [Fact]
    public void Model_authored_request_contracts_have_no_identity_secret_route_or_state_authority_fields()
    {
        Type[] requestTypes =
        [
            typeof(ApplicationCapabilityQueryRequest),
            typeof(ApplicationCapabilityCommandRequest)
        ];

        var offenders = FindForbiddenNames(requestTypes, ModelAuthoredForbiddenFragments);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Proposal_decision_and_confirmation_contracts_cannot_carry_command_arguments()
    {
        Type[] protectedTypes =
        [
            typeof(ApplicationOperationProposal),
            typeof(ApplicationOperationDecisionReference),
            typeof(ApplicationOperationConfirmationReference)
        ];

        string[] forbiddenFragments = ["Argument", "Command", "Payload", "Json"];

        var offenders = FindForbiddenNames(protectedTypes, forbiddenFragments)
            .Concat(FindForbiddenPayloadTypes(protectedTypes))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Limitation_continuation_and_observation_contracts_expose_no_raw_failure_or_target_field()
    {
        Type[] safeProjectionTypes =
        [
            typeof(ApplicationLimitation),
            typeof(ApplicationContinuation),
            typeof(CoachObservationEnvelope),
            typeof(CoachObservationReference)
        ];

        FindForbiddenNames(safeProjectionTypes, SafeProjectionForbiddenFragments)
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_semantic_contract_property_has_a_description()
    {
        PublicContracts
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.GetCustomAttribute<DescriptionAttribute>() is null)
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .Should().BeEmpty();
    }

    [Fact]
    public void Technical_identifiers_do_not_use_the_localized_product_name()
    {
        const string productName = "Sam";

        var identifiers = ContractAssembly
            .GetTypes()
            .Where(type => type.Namespace == AppOperationWireSurface.Namespace)
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Select(member => member.Name)))
            .Where(identifier => identifier.Contains(productName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        identifiers.Should().BeEmpty();
    }

    private static IReadOnlyList<string> FindForbiddenNames(
        IEnumerable<Type> roots,
        IReadOnlyList<string> forbiddenFragments) =>
        InspectReachableProperties(roots)
            .Where(entry => forbiddenFragments.Any(fragment =>
                entry.Property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> FindForbiddenPayloadTypes(IEnumerable<Type> roots) =>
        InspectReachableProperties(roots)
            .SelectMany(entry => TypeGraph(entry.Property.PropertyType)
                .Where(IsForbiddenPayloadType)
                .Select(candidate => $"{entry.Path}: {entry.Property.PropertyType} -> {candidate}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<ReachableProperty> InspectReachableProperties(IEnumerable<Type> roots) =>
        roots
            .SelectMany(root => TraverseProperties(root, root.Name, []))
            .ToArray();

    private static IEnumerable<ReachableProperty> TraverseProperties(
        Type type,
        string path,
        HashSet<Type> activePath)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        if (unwrapped.IsArray)
        {
            foreach (var nested in TraverseProperties(
                unwrapped.GetElementType()!,
                $"{path}[]",
                activePath))
            {
                yield return nested;
            }

            yield break;
        }

        var collectionElements = GetCollectionElementTypes(unwrapped);
        if (collectionElements.Count > 0)
        {
            foreach (var element in collectionElements)
            {
                foreach (var nested in TraverseProperties(element, $"{path}[]", activePath))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (IsTerminal(unwrapped)
            || unwrapped.ContainsGenericParameters
            || !activePath.Add(unwrapped))
        {
            yield break;
        }

        try
        {
            foreach (var property in unwrapped.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyPath = $"{path}.{property.Name}";
                yield return new ReachableProperty(propertyPath, property);

                foreach (var nested in TraverseProperties(property.PropertyType, propertyPath, activePath))
                {
                    yield return nested;
                }
            }
        }
        finally
        {
            activePath.Remove(unwrapped);
        }
    }

    private static IReadOnlyList<Type> GetCollectionElementTypes(Type type)
    {
        if (type == typeof(string))
        {
            return [];
        }

        return type
            .GetInterfaces()
            .Append(type)
            .Where(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0])
            .Distinct()
            .ToArray();
    }

    private static IEnumerable<Type> TypeGraph(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        yield return unwrapped;

        if (unwrapped.IsArray)
        {
            foreach (var nested in TypeGraph(unwrapped.GetElementType()!))
            {
                yield return nested;
            }
        }

        if (!unwrapped.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in unwrapped.GetGenericArguments())
        {
            foreach (var nested in TypeGraph(argument))
            {
                yield return nested;
            }
        }
    }

    private static bool IsForbiddenPayloadType(Type type) =>
        type == typeof(object)
        || type == typeof(JsonElement)
        || type == typeof(JsonDocument)
        || type.ContainsGenericParameters
        || type.IsGenericTypeDefinition
        || IsDictionary(type)
        || IsOpenPolymorphicType(type);

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type)
        || type
            .GetInterfaces()
            .Append(type)
            .Any(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IDictionary<,>)
                    || definition == typeof(IReadOnlyDictionary<,>)
                    || definition == typeof(Dictionary<,>)));

    private static bool IsOpenPolymorphicType(Type type) =>
        (type.IsInterface || type.IsAbstract)
        && GetCollectionElementTypes(type).Count == 0;

    private static bool IsTerminal(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Guid);

    private sealed record ReachableProperty(string Path, PropertyInfo Property);

    private sealed class SyntheticBadRoot
    {
        public IReadOnlyList<SyntheticEnvelope<SyntheticBadNode?>> Nodes { get; init; } = [];
    }

    private sealed class SyntheticEnvelope<T>
    {
        public T? Value { get; init; }
    }

    private sealed class SyntheticBadNode
    {
        public SyntheticBadNode? Next { get; init; }
        public string? UserIdentity { get; init; }
        public string? TenantId { get; init; }
        public string? ProfileId { get; init; }
        public string? Email { get; init; }
        public string? Credential { get; init; }
        public string? Secret { get; init; }
        public string? Route { get; init; }
        public string? Url { get; init; }
        public string? Sql { get; init; }
        public string? ExecutionAuthority { get; init; }
        public long? DomainVersion { get; init; }
        public object? Arguments { get; init; }
        public JsonElement? OpenJson { get; init; }
        public IReadOnlyDictionary<string, string>? Secrets { get; init; }
        public ISyntheticPolymorphicPayload? PolymorphicPayload { get; init; }
    }

    private interface ISyntheticPolymorphicPayload
    {
    }
}
