using System.Collections;
using System.Reflection;
using SentenceStudio.Contracts.Wire;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The wire-tolerance policy checked against the <em>shipped client surface</em> rather than
/// against a namespace.
/// </summary>
/// <remarks>
/// <para>
/// The policy test in <c>SentenceStudio.UnitTests</c> walks the contract namespaces, because that
/// project targets net10.0 and cannot reference <c>SentenceStudio.AppLib</c>. That leaves one gap:
/// a type the client genuinely parses but that lives outside those namespaces would be invisible
/// to it. This test closes the gap from the other end — it starts at
/// <see cref="ICoachApiClient"/>'s own signatures, which is the definition of "what a client
/// parses" that cannot drift from reality.
/// </para>
/// <para>
/// Two tests rather than one because they fail for different reasons and the difference is the
/// useful part: the namespace test fails when somebody adds an enum, this one fails when somebody
/// adds a method.
/// </para>
/// </remarks>
public class CoachClientWireSurfaceTests
{
    private static IReadOnlyList<Type> ReachableEnums { get; } = WalkForEnums();

    [Fact]
    public void The_client_surface_walk_finds_something()
    {
        // A walk that silently found nothing would make the assertion below vacuous.
        ReachableEnums.Should().NotBeEmpty();
        ReachableEnums.Should().Contain(typeof(SentenceStudio.Contracts.Coach.CoachMessageKind));
        ReachableEnums.Should().Contain(typeof(SentenceStudio.Contracts.LearnerMemory.CoachMemoryKind));
    }

    [Fact]
    public void Every_enum_on_the_shipped_client_surface_declares_a_fallback()
    {
        var offenders = ReachableEnums
            .Where(t => !WireEnumFallback.IsAnnotated(t))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "these are the exact types CoachApiClient deserializes; an unannotated one throws on a "
            + "value a newer server sends and loses the whole response");
    }

    [Fact]
    public void Every_enum_on_the_shipped_client_surface_is_covered_by_the_client_options()
    {
        var factory = new TolerantWireEnumConverterFactory();

        ReachableEnums
            .Where(t => !factory.CanConvert(t))
            .Select(t => t.FullName)
            .Should().BeEmpty("the annotation only matters if WireJson.Client actually routes the type");
    }

    /// <summary>
    /// Every enum reachable from the parameters and return types of <see cref="ICoachApiClient"/>.
    /// </summary>
    private static IReadOnlyList<Type> WalkForEnums()
    {
        var contractAssembly = typeof(SentenceStudio.Contracts.Coach.CoachTurnResponse).Assembly;
        var seen = new HashSet<Type>();
        var found = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var queue = new Queue<Type>();

        foreach (var method in typeof(ICoachApiClient).GetMethods())
        {
            Enqueue(method.ReturnType);

            foreach (var parameter in method.GetParameters())
            {
                Enqueue(parameter.ParameterType);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Enqueue(property.PropertyType);
            }
        }

        return found.Values.ToList();

        void Enqueue(Type type)
        {
            foreach (var candidate in Unwrap(type))
            {
                if (candidate.IsEnum)
                {
                    found.TryAdd(candidate.FullName!, candidate);
                }
                else if (candidate.Assembly == contractAssembly && !seen.Contains(candidate))
                {
                    queue.Enqueue(candidate);
                }
            }
        }
    }

    /// <summary>Peels tasks, nullables, arrays and generic collection arguments off a type.</summary>
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

        // Task<T> and every generic collection are peeled the same way: the interesting type is
        // always the argument, never the wrapper.
        if (underlying.IsGenericType
            && (typeof(IEnumerable).IsAssignableFrom(underlying)
                || typeof(Task).IsAssignableFrom(underlying)))
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
