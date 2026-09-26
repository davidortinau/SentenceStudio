using System.Reflection;
using System.Collections.Frozen;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.Capabilities;

public sealed class ApplicationContractGraphException(string message) : InvalidOperationException(message);

public static class ApplicationContractGraphValidator
{
    private static readonly HashSet<Type> ScalarTypes =
    [
        typeof(string),
        typeof(bool),
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(char),
        typeof(Guid),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(TimeSpan)
    ];

    private static readonly HashSet<Type> SupportedCollections =
    [
        typeof(IReadOnlyList<>),
        typeof(IReadOnlyCollection<>),
        typeof(IEnumerable<>),
        typeof(List<>),
        typeof(IReadOnlyDictionary<,>),
        typeof(Dictionary<,>)
    ];

    public static void Validate(Type rootType, string role)
    {
        _ = CollectReachableContractNodes(rootType, role);
    }

    public static IReadOnlySet<Type> CollectReachableContractNodes(Type rootType, string role)
    {
        ArgumentNullException.ThrowIfNull(rootType);

        var visited = new HashSet<Type>();
        var contractNodes = new HashSet<Type>();
        ValidateNode(rootType, role, rootType.Name, visited, contractNodes);
        return contractNodes.ToFrozenSet();
    }

    public static void ValidateProjectionSeparation(
        Type clientResultType,
        Type coachObservationType)
    {
        var clientNodes = CollectReachableContractNodes(clientResultType, "Client result");
        var coachNodes = CollectReachableContractNodes(coachObservationType, "Coach observation");
        var overlap = clientNodes
            .Intersect(coachNodes)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        if (overlap.Length > 0)
        {
            throw new ApplicationContractGraphException(
                "Coach observation contract graph contains client-result contract node(s): "
                + string.Join(", ", overlap.Select(type => type.FullName)) + ".");
        }
    }

    private static void ValidateNode(
        Type type,
        string role,
        string path,
        HashSet<Type> visited,
        HashSet<Type> contractNodes)
    {
        if (type.IsByRef || type.IsPointer || type.IsFunctionPointer || type.ContainsGenericParameters)
        {
            throw Invalid(role, path, type, "open, pointer, and by-reference types are not supported");
        }

        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            ValidateNode(nullable, role, path, visited, contractNodes);
            return;
        }

        if (ScalarTypes.Contains(type) || type.IsEnum)
        {
            return;
        }

        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1)
            {
                throw Invalid(role, path, type, "only one-dimensional arrays are supported");
            }

            ValidateNode(type.GetElementType()!, role, path + "[]", visited, contractNodes);
            return;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (!SupportedCollections.Contains(definition))
            {
                throw Invalid(role, path, type, "the generic container is not in the closed contract collection set");
            }

            foreach (var argument in type.GetGenericArguments())
            {
                ValidateNode(argument, role, $"{path}<{argument.Name}>", visited, contractNodes);
            }

            return;
        }

        if (type == typeof(object)
            || type == typeof(Type)
            || typeof(Delegate).IsAssignableFrom(type)
            || type.IsInterface
            || type.IsAbstract)
        {
            throw Invalid(role, path, type, "polymorphic and runtime-shaped contract nodes are not supported");
        }

        if (!string.Equals(type.Namespace, AppOperationWireSurface.Namespace, StringComparison.Ordinal))
        {
            throw Invalid(
                role,
                path,
                type,
                $"contract object types must be declared in {AppOperationWireSurface.Namespace}");
        }

        if (!type.IsClass || !type.IsSealed || !type.IsPublic)
        {
            throw Invalid(role, path, type, "contract object types must be public sealed classes");
        }

        if (!visited.Add(type))
        {
            return;
        }

        contractNodes.Add(type);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length != 0)
            {
                throw Invalid(role, $"{path}.{property.Name}", property.PropertyType, "properties must be readable and non-indexed");
            }

            ValidateNode(property.PropertyType, role, $"{path}.{property.Name}", visited, contractNodes);
        }
    }

    private static ApplicationContractGraphException Invalid(string role, string path, Type type, string reason) =>
        new($"{role} contract graph is invalid at {path} ({type}): {reason}.");
}
