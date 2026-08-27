using System.Reflection;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Proves the memory CRUD contracts are not part of the model and tool output graph.
/// </summary>
/// <remarks>
/// <para>
/// The coach embargo scanner exists to keep terms, glosses, example sentences, and transcripts off
/// the model boundary. Memory CRUD is a different surface: the learner reading back and editing
/// preferences they typed themselves. Running the model-output word list over it produced a false
/// positive on <c>ExampleRegister</c>, which is the product's own name for the setting.
/// </para>
/// <para>
/// The fix is separation, not a rename and not a weaker scanner. These tests are what make the
/// separation real: if somebody later moves a memory DTO into the coach contracts namespace, hands
/// one to the model as a tool result, or lets one reach a serialized session, the build fails here
/// rather than quietly re-creating the collision.
/// </para>
/// </remarks>
public sealed class CoachMemoryContractSeparationTests
{
    private static readonly Assembly ContractsAssembly = typeof(CoachMemoryFactDto).Assembly;

    private static IReadOnlyList<Type> MemoryPublicTypes { get; } = ContractsAssembly
        .GetTypes()
        .Where(t => t.Namespace is not null
                    && t.Namespace.StartsWith("SentenceStudio.Contracts.LearnerMemory", StringComparison.Ordinal))
        .ToList();

    [Fact]
    public void TheMemoryContractsSatisfyTheirOwnBoundedContract()
    {
        CoachMemoryContractValidator.Scan().Should().BeEmpty();
    }

    [Fact]
    public void TheProductConceptKeepsItsName()
    {
        // The setting is called the example register. Renaming it to dodge a scanner that was
        // never meant to cover this surface would leave the product and the code disagreeing.
        typeof(CoachMemoryValueDto)
            .GetProperty(nameof(CoachMemoryValueDto.ExampleRegister))
            .Should().NotBeNull();

        Enum.GetNames<CoachMemoryKind>().Should().Contain(nameof(CoachMemoryKind.ExampleRegister));
    }

    [Fact]
    public void NoMemoryContractSitsInTheModelOutputNamespace()
    {
        MemoryPublicTypes.Should().NotBeEmpty("the discovery predicate must find something");

        MemoryPublicTypes.Should().OnlyContain(
            t => !t.Namespace!.StartsWith(
                CoachMemoryContractValidator.ModelOutputNamespacePrefix, StringComparison.Ordinal),
            "memory contracts must not be discovered by the coach output embargo scanner");
    }

    [Fact]
    public void TheEmbargoScannerDoesNotDiscoverAnyMemoryContract()
    {
        // The scanner discovers by namespace. This asserts the actual discovered sets, so a change
        // to either predicate that widened it to cover memory would fail here. Both scopes are
        // checked: memory contracts belong to neither, and being swept into the bounded public
        // scope would be just as wrong as being swept into the strict model-visible one.
        CoachOutputContract.ModelVisibleTypes
            .Should().NotIntersectWith(MemoryPublicTypes);

        CoachOutputContract.PublicClientContractTypes
            .Should().NotIntersectWith(MemoryPublicTypes);

        CoachOutputContract.ToolResultTypes
            .Should().NotIntersectWith(MemoryPublicTypes);
    }

    [Fact]
    public void NoToolResultTypeReachesAMemoryContract()
    {
        foreach (var toolResult in CoachOutputContract.ToolResultTypes)
        {
            Reachable(toolResult).Should().NotIntersectWith(
                MemoryPublicTypes,
                $"{toolResult.Name} is returned to the model and must not carry a memory contract");
        }
    }

    [Fact]
    public void NoChatToolExposesAMemoryContract()
    {
        // The model's whole tool surface is a closed list of six read-only tools. None of them
        // may name memory, and the allow-list is what ChatOptions.Tools is built from.
        CoachToolNames.All.Should().HaveCount(6);

        CoachToolNames.All.Should().NotContain(
            n => n.Contains("memor", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("remember", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("preference", StringComparison.OrdinalIgnoreCase));

        // No AIFunction is produced from a memory type either.
        var toolTypes = typeof(CoachToolFactory).Assembly
            .GetTypes()
            .Where(t => typeof(CoachToolBase).IsAssignableFrom(t) && t is { IsAbstract: false })
            .ToList();

        foreach (var tool in toolTypes)
        {
            Reachable(tool).Should().NotIntersectWith(
                MemoryPublicTypes, $"{tool.Name} is offered to the model as an {nameof(AIFunction)}");
        }
    }

    [Fact]
    public void TheTurnIntentCannotCarryAMemoryContract()
    {
        // CoachTurnIntent is what the model fills in. A memory value reachable from it would let a
        // prompt-injected model write a preference by emitting one.
        Reachable(typeof(CoachTurnIntent)).Should().NotIntersectWith(MemoryPublicTypes);
    }

    [Fact]
    public void NothingReachableFromASerializedSessionCarriesAMemoryContract()
    {
        // The checkpoint is a serialized AgentSession blob stored on CoachSession, plus the two
        // payloads that cross into and out of the agent. Those are the shapes whose contents can
        // outlive a forget, so they are what must not embed a memory value.
        //
        // Deliberately not a name-pattern sweep over "*Session*" types: that also catches stores
        // and services, which hold a CoachDbContext and therefore reach every entity in the
        // context by construction. Such a match says nothing about what gets serialized.
        var payloadTypes = new[]
        {
            typeof(CoachSession),
            typeof(CoachAgentTurnRequest),
            typeof(CoachAgentTurnResult)
        };

        foreach (var payload in payloadTypes)
        {
            Reachable(payload).Should().NotIntersectWith(
                MemoryPublicTypes,
                $"{payload.Name} is serialized or handed to the agent and can outlive a forget");
        }
    }

    [Fact]
    public void TheSerializedSessionHoldsOpaqueTextRatherThanATypedMemoryField()
    {
        // Belt and braces for the test above: the checkpoint column is a protected string. There
        // is no typed memory member on it that a future change could quietly start populating.
        var members = typeof(CoachSession).GetProperties().Select(p => p.Name).ToList();

        members.Should().Contain("ProtectedAgentSession");
        members.Should().NotContain(n => n.Contains("Memory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheOnlyModelFacingMemoryShapeIsTheInternalContextItem()
    {
        // The model never sees a CRUD DTO. It sees text rendered from CoachMemoryContextItem,
        // which is internal to the API, carries closed enums plus one screened string, and has no
        // version, no status, and no edit affordance.
        var item = typeof(CoachMemoryContextItem);

        item.Assembly.Should().BeSameAs(typeof(CoachMemoryStore).Assembly,
            "the model-facing shape lives in the API, not on the public contract boundary");

        item.Namespace.Should().NotStartWith("SentenceStudio.Contracts");

        var names = item.GetProperties().Select(p => p.Name).ToList();
        names.Should().NotContain("Version");
        names.Should().NotContain("Status");
        names.Should().NotContain("ExpectedVersion");

        // Exactly three strings and nothing else free-form: which fact it came from, the BCP-47
        // language tag the application wrote, and the screened value itself. Everything else on
        // the item is a closed enum or an int.
        item.GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Should().BeEquivalentTo(
            [
                nameof(CoachMemoryContextItem.FactId),
                nameof(CoachMemoryContextItem.TargetLanguageCode),
                nameof(CoachMemoryContextItem.Value)
            ]);

        item.GetProperties()
            .Where(p => p.PropertyType != typeof(string))
            .Should().OnlyContain(p => p.PropertyType.IsEnum || p.PropertyType == typeof(int),
                "a non-string member must be a closed enum or a bounded count");
    }

    /// <summary>Every type reachable from a member of <paramref name="root"/>, transitively.</summary>
    private static HashSet<Type> Reachable(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            var members = current
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Field);

            foreach (var member in members)
            {
                var type = member switch
                {
                    PropertyInfo p => p.PropertyType,
                    FieldInfo f => f.FieldType,
                    _ => null
                };

                foreach (var candidate in Unwrap(type))
                {
                    if (candidate.Assembly == ContractsAssembly
                        || candidate.Assembly == typeof(CoachMemoryStore).Assembly)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }
        }

        seen.Remove(root);
        return seen;
    }

    private static IEnumerable<Type> Unwrap(Type? type)
    {
        if (type is null || type.IsPrimitive || type == typeof(string))
        {
            yield break;
        }

        var effective = Nullable.GetUnderlyingType(type) ?? type;
        yield return effective;

        if (effective.IsArray && effective.GetElementType() is { } element)
        {
            yield return element;
        }

        if (effective.IsGenericType)
        {
            foreach (var argument in effective.GetGenericArguments())
            {
                yield return argument;
            }
        }
    }
}
