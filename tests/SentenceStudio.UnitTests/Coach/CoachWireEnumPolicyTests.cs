using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// The wire-tolerance policy, enforced by reflection rather than by review.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists to prevent is specific and quiet: the server appends a member to an
/// enum, the change looks entirely additive, and every client already in the field throws
/// <see cref="JsonException"/> the first time it reads a payload carrying that member. Nothing in
/// a normal review catches it, because the diff that broke the client contains only a new enum
/// member and the client contains no code at all.
/// </para>
/// <para>
/// So the rule is enforced where the enum lives: any enum a client can reach from a wire DTO has
/// to say, on the type, what an unrecognised value degrades to. Adding a member to an annotated
/// enum stays a one-line change; adding a whole new enum to the wire surface stops the build's
/// test run until somebody decides what an old client should do with it.
/// </para>
/// </remarks>
public class CoachWireEnumPolicyTests
{
    /// <summary>
    /// Enums that are exempt from the policy, with the reason each one is safe without it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Empty, and meant to stay that way.</b> It exists so an exception is a reviewable entry in
    /// a list rather than a missing annotation nobody notices. An entry here is a claim that a
    /// client can read an unknown value of that enum and still behave correctly — which is almost
    /// never true, because the failure mode is an exception thrown inside the deserializer before
    /// any client code runs.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<Type, string> DocumentedExceptions =
        new Dictionary<Type, string>();

    /// <summary>
    /// Every public shape a client parses: the wire namespaces, minus the model-facing ones.
    /// </summary>
    private static IReadOnlyList<Type> ClientWireShapes { get; } = WireAssembly
        .GetTypes()
        .Where(t => t.IsPublic
            && !t.IsEnum
            && (t.IsClass || (t.IsValueType && !t.IsPrimitive))
            && !t.IsAbstract
            && WireContractNamespaces.IsClientWire(t.Namespace))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>Every enum reachable from a client wire shape, by walking property types.</summary>
    private static IReadOnlyList<Type> ReachableEnums { get; } = WalkForEnums(ClientWireShapes);

    private static Assembly WireAssembly => typeof(CoachTurnResponse).Assembly;

    [Fact]
    public void The_wire_surface_is_not_empty()
    {
        // A reachability walk that silently finds nothing would make every other test in this class
        // vacuously true, which is the one way this file could pass while enforcing nothing.
        ClientWireShapes.Should().NotBeEmpty();
        ReachableEnums.Should().NotBeEmpty();
        ReachableEnums.Should().Contain(typeof(CoachMessageKind));
        ReachableEnums.Should().Contain(typeof(CoachWriteStatus));
    }

    [Fact]
    public void Every_enum_a_client_can_reach_declares_an_unknown_value_fallback()
    {
        var offenders = ReachableEnums
            .Where(t => !WireEnumFallback.IsAnnotated(t) && !DocumentedExceptions.ContainsKey(t))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "an enum a client parses must say what an unreadable value becomes; without it the "
            + "deserializer throws and the whole response is lost, not just the field");
    }

    [Fact]
    public void Every_declared_fallback_names_a_real_member_and_says_why()
    {
        foreach (var enumType in ReachableEnums.Where(WireEnumFallback.IsAnnotated))
        {
            var descriptor = WireEnumFallback.Describe(enumType);

            Enum.IsDefined(enumType, descriptor.Value)
                .Should().BeTrue($"{enumType.Name} must fall back to one of its own members");

            // Long enough that "safe" or "n/a" does not pass for a reason.
            descriptor.Rationale.Trim().Length
                .Should().BeGreaterThan(40, $"{enumType.Name} must explain why its fallback is safe");
        }
    }

    [Fact]
    public void The_tolerant_converter_covers_every_reachable_enum()
    {
        var factory = new TolerantWireEnumConverterFactory();

        var uncovered = ReachableEnums
            .Where(t => !DocumentedExceptions.ContainsKey(t))
            .Where(t => !factory.CanConvert(t) || !factory.CanConvert(typeof(Nullable<>).MakeGenericType(t)))
            .Select(t => t.FullName)
            .ToList();

        uncovered.Should().BeEmpty(
            "the annotation is only worth anything if the client's options actually route the type "
            + "through the tolerant converter, in both its plain and nullable forms");
    }

    [Fact]
    public void An_appended_sentinel_really_is_appended()
    {
        foreach (var enumType in ReachableEnums.Where(WireEnumFallback.IsAnnotated))
        {
            var descriptor = WireEnumFallback.Describe(enumType);
            if (descriptor.Kind != WireEnumFallbackKind.AppendedSentinel)
            {
                continue;
            }

            var values = Enum.GetValues(enumType).Cast<object>().ToList();

            // Several of these enums are stored as ordinals, so a sentinel inserted anywhere but
            // the end silently re-labels rows that were written before it existed.
            Enum.GetName(enumType, values[^1])
                .Should().Be(descriptor.MemberName,
                    $"{enumType.Name}'s sentinel must be the last member, never inserted among the real ones");
        }
    }

    [Fact]
    public void The_policy_covers_the_app_operation_namespace_before_it_has_any_types()
    {
        WireContractNamespaces.ClientWireRoots
            .Should().Contain(WireContractNamespaces.AppOperation,
                "the first app-operation enum must arrive under the tolerance rules, not be "
                + "retrofitted into them afterwards");

        WireContractNamespaces.IsClientWire("SentenceStudio.Contracts.AppOperation.Something")
            .Should().BeTrue("a nested namespace is covered without anybody having to list it");
    }

    [Fact]
    public void The_model_facing_intent_namespace_is_excluded_and_unreachable()
    {
        WireContractNamespaces.IsClientWire(WireContractNamespaces.CoachIntent)
            .Should().BeFalse("intent shapes are parsed from model output and must stay strict");

        // The exclusion is only meaningful while no client DTO drags an intent enum into the wire
        // graph. If one ever does, the enum becomes tolerant for the model too, and a model that
        // invents a value stops being refused.
        ReachableEnums
            .Where(t => t.Namespace == typeof(CoachIntentKind).Namespace)
            .Select(t => t.FullName)
            .Should().BeEmpty("a client shape must not reference a model-facing enum");
    }

    [Fact]
    public void Client_tolerance_does_not_loosen_the_strict_parsers()
    {
        // The tolerant behaviour lives in the client's options collection, which System.Text.Json
        // resolves ahead of the type's own [JsonConverter]. Nothing else in the solution picks it
        // up: the server binding a request body, the coach parsing model output, and Entity
        // Framework reading an ordinal are all unaffected.
        var strict = () => JsonSerializer.Deserialize<CoachIntentKind>("\"DeletePlan\"");
        strict.Should().Throw<JsonException>("the intent set is closed and stays closed");

        var strictWireEnum = () => JsonSerializer.Deserialize<CoachMessageKind>("\"HolographicPoem\"");
        strictWireEnum.Should().Throw<JsonException>(
            "default options must stay strict; only a client that opts in gets tolerance");

        JsonSerializer.Deserialize<CoachMessageKind>("\"HolographicPoem\"", WireJson.Client)
            .Should().Be(CoachMessageKind.Unrecognized, "the opted-in client is the one that degrades");
    }

    [Fact]
    public void Every_reachable_enum_still_writes_a_canonical_name()
    {
        foreach (var enumType in ReachableEnums.Where(WireEnumFallback.IsAnnotated))
        {
            foreach (var value in Enum.GetValues(enumType))
            {
                var json = JsonSerializer.Serialize(value, enumType, WireJson.Client);

                json.Should().Be(
                    $"\"{Enum.GetName(enumType, value)}\"",
                    $"{enumType.Name} must round-trip by name; a number on the wire is unreadable "
                    + "in a log and ambiguous to every other reader");
            }
        }
    }

    [Fact]
    public void Wire_enums_keep_the_string_converter_attribute_for_every_other_serializer()
    {
        // The tolerant factory only applies where it is installed. Everywhere else — the API's own
        // options, a test using JsonSerializer directly — the type attribute is what keeps these
        // values names rather than integers.
        var offenders = ReachableEnums
            .Where(t => t.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType
                != typeof(JsonStringEnumConverter))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void No_reachable_enum_has_two_members_that_differ_only_in_case()
    {
        // The tolerant converter reads names case-insensitively, so a proxy that lower-cased a
        // value still resolves to the member it names. That lookup is built once in a static
        // initializer: two members differing only in case would make it throw there, which is the
        // worst place for it — a TypeInitializationException on first parse, from a change that
        // looked like adding an enum member.
        foreach (var enumType in ReachableEnums)
        {
            Enum.GetNames(enumType)
                .Select(name => name.ToUpperInvariant())
                .Should().OnlyHaveUniqueItems(enumType.Name);
        }
    }

    /// <summary>
    /// Every enum reachable from <paramref name="roots"/> through public instance properties,
    /// following collections, arrays and nullables.
    /// </summary>
    private static IReadOnlyList<Type> WalkForEnums(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var enums = new SortedSet<string>(StringComparer.Ordinal);
        var found = new Dictionary<string, Type>(StringComparer.Ordinal);
        var queue = new Queue<Type>(roots);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var candidate in Unwrap(property.PropertyType))
                {
                    if (candidate.IsEnum)
                    {
                        if (enums.Add(candidate.FullName!))
                        {
                            found[candidate.FullName!] = candidate;
                        }

                        continue;
                    }

                    // Only walk into our own contract types; framework types cannot carry one of
                    // our enums without going through one of ours first.
                    if (candidate.Assembly == WireAssembly && !seen.Contains(candidate))
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }
        }

        return enums.Select(name => found[name]).ToList();
    }

    /// <summary>Peels nullables, arrays and generic collection arguments off a property type.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsArray)
        {
            foreach (var inner in Unwrap(underlying.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        if (underlying.IsGenericType && typeof(IEnumerable).IsAssignableFrom(underlying))
        {
            foreach (var argument in underlying.GetGenericArguments())
            {
                foreach (var inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        if (underlying.IsPrimitive || underlying == typeof(string) || underlying == typeof(object))
        {
            yield break;
        }

        yield return underlying;
    }
}
