using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// The bounded contract check for the learner-memory CRUD surface.
/// </summary>
/// <remarks>
/// <para>
/// The coach embargo scanner guards a different graph: what the model may emit and what a tool may
/// return. It bans a fixed word list because a coach turn response must never carry a term, a
/// gloss, an example sentence, or a transcript across the boundary. Memory CRUD is not that graph.
/// It is the learner reading back and editing preferences they typed themselves, and its field
/// names are the product's own vocabulary — <c>ExampleRegister</c> is the name of the setting.
/// </para>
/// <para>
/// Applying the model-output word list here forced a choice between renaming a product concept and
/// weakening a security control. Both are wrong: the rename hides the mismatch instead of fixing
/// it, and the weakening removes a rule that other code depends on. So the memory contracts live
/// outside <c>SentenceStudio.Contracts.Coach</c>, are excluded from the model-output graph by
/// construction, and are validated here against rules that fit what they actually are.
/// </para>
/// <para>
/// What this validator still enforces, because these are the properties that matter on any public
/// boundary regardless of which graph it belongs to:
/// <list type="bullet">
/// <item>The type set is a closed allow-list. A new public type in the namespace fails until it is
/// listed here deliberately.</item>
/// <item>No open-ended member. <see cref="object"/>, <see cref="JsonElement"/>,
/// <see cref="JsonNode"/>, and <see cref="JsonDocument"/> are refused, so nothing can smuggle an
/// arbitrary payload through a typed field.</item>
/// <item>No database entity, no persistence type, and no MAUI or UI type reachable from a
/// member.</item>
/// <item>No member name that reads as an instruction, a credential, or a raw transcript.</item>
/// </list>
/// </para>
/// </remarks>
public static class CoachMemoryContractValidator
{
    /// <summary>The name used in violation messages and test output.</summary>
    public const string ContractName = "coach_memory_public_contract";

    /// <summary>
    /// The namespace that holds the memory CRUD contracts. Deliberately not under
    /// <c>SentenceStudio.Contracts.Coach</c>, which is the model and tool output graph.
    /// </summary>
    public const string ContractNamespace = "SentenceStudio.Contracts.LearnerMemory";

    /// <summary>
    /// The namespace prefix that the coach output embargo scanner discovers. Memory contracts must
    /// stay out of it.
    /// </summary>
    public const string ModelOutputNamespacePrefix = "SentenceStudio.Contracts.Coach";

    /// <summary>
    /// Every public type allowed on the memory CRUD boundary. Closed on purpose: adding a type is
    /// a decision, not a side effect of creating a file.
    /// </summary>
    public static ImmutableArray<Type> AllowedTypes { get; } =
    [
        typeof(CoachMemoryValueDto),
        typeof(CoachMemoryFactDto),
        typeof(CoachMemoryPageDto),
        typeof(CoachMemoryApproveRequest),
        typeof(CoachMemoryRejectRequest),
        typeof(CoachMemoryEditRequest),
        typeof(CoachMemoryForgetAllResponse),
        typeof(CoachMemoryProblemTypes)
    ];

    /// <summary>
    /// Member-name words refused on this surface. Much shorter than the model-output list, because
    /// the risk here is different: a learner-facing settings DTO cannot leak a due term or a
    /// transcript, but it must never look like a place to put an instruction or a credential.
    /// </summary>
    public static ImmutableHashSet<string> BannedMemberWords { get; } =
    [
        "password", "secret", "secrets", "credential", "credentials",
        "apikey", "token", "tokens", "sql", "command", "commands",
        "script", "scripts", "prompt", "prompts", "instruction", "instructions",
        "transcript", "transcripts", "diary", "journal"
    ];

    private static readonly ImmutableArray<Type> OpenEndedTypes =
    [
        typeof(object), typeof(JsonElement), typeof(JsonNode), typeof(JsonDocument)
    ];

    private static readonly ImmutableArray<string> ForbiddenMemberNamespacePrefixes =
    [
        "SentenceStudio.Api.Coach.Persistence",
        "SentenceStudio.Api.Coach.Memory",
        "SentenceStudio.Shared.Models",
        "SentenceStudio.Shared.Data",
        "Microsoft.Maui",
        "MauiReactor"
    ];

    /// <summary>Every public type actually declared in the memory contract namespace.</summary>
    public static IReadOnlyList<Type> DiscoveredTypes { get; } = typeof(CoachMemoryFactDto).Assembly
        .GetTypes()
        .Where(t => t.IsPublic
                    && t is { IsClass: true, IsAbstract: false }
                    && string.Equals(t.Namespace, ContractNamespace, StringComparison.Ordinal))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>Runs the contract and returns every violation found.</summary>
    public static IReadOnlyList<string> Scan()
    {
        var violations = new List<string>();
        var allowed = AllowedTypes.ToImmutableHashSet();

        foreach (var discovered in DiscoveredTypes.Where(t => !allowed.Contains(t)))
        {
            violations.Add(
                $"[allow_list] {discovered.Name} is public in {ContractNamespace} but is not in AllowedTypes.");
        }

        foreach (var type in AllowedTypes)
        {
            if (type.Namespace?.StartsWith(ModelOutputNamespacePrefix, StringComparison.Ordinal) == true)
            {
                violations.Add(
                    $"[graph] {type.Name} sits under {ModelOutputNamespacePrefix}, which is the model and tool " +
                    "output graph. Memory CRUD contracts must not be discovered by the coach output embargo.");
            }

            foreach (var member in PublicMembers(type))
            {
                var memberType = MemberType(member);
                var effective = Nullable.GetUnderlyingType(memberType) ?? memberType;
                var element = ElementType(effective);

                if (OpenEndedTypes.Contains(effective) || OpenEndedTypes.Contains(element))
                {
                    violations.Add(
                        $"[open_ended] {type.Name}.{member.Name} is {effective.Name}, which can carry any payload.");
                }

                foreach (var candidate in new[] { effective, element })
                {
                    var ns = candidate.Namespace;
                    if (ns is null)
                    {
                        continue;
                    }

                    var forbidden = ForbiddenMemberNamespacePrefixes
                        .FirstOrDefault(p => ns.StartsWith(p, StringComparison.Ordinal));

                    if (forbidden is not null)
                    {
                        violations.Add(
                            $"[leak] {type.Name}.{member.Name} exposes {candidate.Name} from {forbidden}.");
                    }
                }

                var offending = SplitWords(member.Name).FirstOrDefault(BannedMemberWords.Contains);
                if (offending is not null)
                {
                    violations.Add($"[member_name] {type.Name}.{member.Name} names '{offending}'.");
                }
            }
        }

        return violations;
    }

    /// <summary>Throws when the contract is violated. Intended for a startup guard.</summary>
    public static void EnsureValid()
    {
        var violations = Scan();
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"{ContractName} failed:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
        }
    }

    private static IEnumerable<MemberInfo> PublicMembers(Type type) => type
        .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Field)
        .Where(m => !m.Name.StartsWith("EqualityContract", StringComparison.Ordinal));

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => typeof(object)
    };

    private static Type ElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType() ?? type;
        }

        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            if (args.Length == 1)
            {
                return args[0];
            }
        }

        return type;
    }

    /// <summary>Splits a Pascal-case member name into lowercase words.</summary>
    private static IEnumerable<string> SplitWords(string name)
    {
        var start = 0;
        for (var i = 1; i <= name.Length; i++)
        {
            if (i == name.Length || char.IsUpper(name[i]))
            {
                if (i > start)
                {
                    yield return name[start..i].ToLowerInvariant();
                }

                start = i;
            }
        }
    }
}
