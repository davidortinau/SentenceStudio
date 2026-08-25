using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Api.Tests.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// Proves the two coach boundaries are actually separate, by structure rather than by naming.
/// </summary>
/// <remarks>
/// <para>
/// The durable-history contracts are judged under bounded rules instead of the strict model-facing
/// embargo. That is only defensible if they genuinely cannot reach the model — otherwise the split
/// is a loophole rather than a boundary, and the strict rules were the only thing holding the line.
/// </para>
/// <para>
/// So these tests do not check names. They walk the real object graphs the model can see — the
/// tool schemas the provider is handed, the typed intent it produces, the request assembled for a
/// run, and the state serialized into an agent session — and assert that no history contract type
/// appears anywhere in any of them. If someone later hangs a conversation DTO off the turn
/// request, the embargo scanner will not catch it, because the type is legitimately allowed to
/// exist. This will.
/// </para>
/// </remarks>
public class CoachHistoryModelIsolationTests
{
    /// <summary>The history contracts, which the model must never be able to reach.</summary>
    private static IReadOnlyList<Type> HistoryTypes { get; } = CoachOutputContract.PublicClientContractTypes
        .Where(t => t.Name.Contains("Conversation", StringComparison.Ordinal)
                    || t.Name.Contains("HistoryMessage", StringComparison.Ordinal)
                    || t.Name.Contains("MessagePage", StringComparison.Ordinal)
                    || t.Name.Contains("TurnOperation", StringComparison.Ordinal))
        .ToList();

    [Fact]
    public void The_history_contracts_exist_and_are_client_facing()
    {
        // Guards the guard: if the discovery filter above silently matched nothing, every other
        // test in this class would pass over an empty set and prove nothing at all.
        HistoryTypes.Should().NotBeEmpty("the isolation tests must have something to isolate");
        HistoryTypes.Should().Contain(typeof(CoachConversationDto));
        HistoryTypes.Should().Contain(typeof(CoachMessagePageDto));
        HistoryTypes.Should().Contain(typeof(CoachTurnOperationDto));

        HistoryTypes.Should().NotIntersectWith(CoachOutputContract.ModelVisibleTypes,
            "a history contract that is also model-visible would defeat the whole split");
    }

    /// <summary>
    /// The two scopes partition the coach shapes: nothing in both, nothing in neither.
    /// </summary>
    /// <remarks>
    /// Discovery is by namespace, so the risk is not a type being classified wrongly but a type
    /// falling into the seam between the two lists and being scanned by neither. This closes it.
    /// </remarks>
    [Fact]
    public void Every_coach_contract_lands_in_exactly_one_scope()
    {
        var model = CoachOutputContract.ModelVisibleTypes.ToHashSet();
        var client = CoachOutputContract.PublicClientContractTypes.ToHashSet();

        model.Should().NotIntersectWith(client, "a type judged by both rule sets has an ambiguous boundary");

        var everyCoachContract = typeof(CoachTurnResponse).Assembly
            .GetTypes()
            .Where(t => t.IsPublic
                        && t is { IsClass: true, IsAbstract: false }
                        && t.Namespace is not null
                        && t.Namespace.StartsWith("SentenceStudio.Contracts.Coach", StringComparison.Ordinal));

        foreach (var type in everyCoachContract)
        {
            (model.Contains(type) || client.Contains(type)).Should().BeTrue(
                "{0} is a coach contract that no scope scans", type.FullName);
        }
    }

    /// <summary>
    /// No history type appears in the tool surface the provider is actually handed.
    /// </summary>
    /// <remarks>
    /// Built from the real DI container rather than from a hand-listed set of tool types, so a
    /// sixth tool added later is covered without anyone remembering to add it here.
    /// </remarks>
    [Fact]
    public void No_history_contract_reaches_the_tool_surface()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(b => b.ClearProviders());
        services.AddCoachToolDataServices();
        services.AddScoped<IUserScopeProvider>(_ => new FakeUserScopeProvider(CoachToolTestFixture.UserA));
        services.AddScoped<IPlanDateContext>(_ => new PlanDateContext(TimeZoneInfo.Utc));
        services.AddScoped<IDeterministicPlanGenerator>(_ => new RecordingPlanGenerator(_ => null));
        services.AddSingleton<ILanguageSegmenter, KoreanLanguageSegmenter>();
        services.Configure<SentenceStudio.Api.Coach.Runtime.CoachOptions>(_ => { });
        services.AddCoachReadOnlyTools();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<ICoachToolFactory>().CreateTools();

        tools.Should().NotBeEmpty("an empty tool set would make this assertion vacuous");

        // The declared schema is what the provider sees, so a history type leaking into a tool
        // argument would show up as its member names in the serialized schema.
        foreach (var tool in tools)
        {
            var schema = tool.JsonSchema.ToString();
            foreach (var historyType in HistoryTypes)
            {
                schema.Should().NotContain(historyType.Name,
                    "tool '{0}' must not describe the history contract {1}", tool.Name, historyType.Name);
            }
        }

        // And the CLR signatures behind them, which is where a return type would hide.
        var reachable = ReachableFrom(tools
            .Select(t => t.UnderlyingMethod)
            .Where(m => m is not null)
            .SelectMany(m => m!.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

        AssertNoHistoryTypeIn(reachable, "the tool signature graph");
    }

    [Fact]
    public void No_history_contract_reaches_the_typed_intent()
    {
        AssertNoHistoryTypeIn(ReachableFrom([typeof(CoachTurnIntent)]), "the coach turn intent");
    }

    /// <summary>
    /// No history type reaches the request assembled for a model run.
    /// </summary>
    /// <remarks>
    /// This is the one that matters most in practice. Durable history is rebuilt into bounded prior
    /// messages before a turn, and the tempting shortcut is to hand the turn request the history
    /// DTOs directly. The prior-message shape is deliberately a plain role-and-text pair, and this
    /// keeps it that way.
    /// </remarks>
    [Fact]
    public void No_history_contract_reaches_the_agent_turn_request()
    {
        AssertNoHistoryTypeIn(ReachableFrom([typeof(CoachAgentTurnRequest)]), "the agent turn request");
    }

    [Fact]
    public void No_history_contract_reaches_the_agent_turn_result()
    {
        AssertNoHistoryTypeIn(ReachableFrom([typeof(CoachAgentTurnResult)]), "the agent turn result");
    }

    /// <summary>
    /// A serialized agent session carries no history contract, checked as text.
    /// </summary>
    /// <remarks>
    /// Type-graph walking cannot see through the session, because it is persisted as opaque JSON.
    /// Serializing a realistic request and reading the bytes back covers the gap the reflection
    /// tests structurally cannot.
    /// </remarks>
    [Fact]
    public void No_history_contract_name_survives_into_serialized_agent_state()
    {
        var request = new CoachAgentTurnRequest
        {
            SessionId = "session-1",
            ActiveConstraints = new CoachConstraintSetDto
            {
                AvailableMinutes = 15,
                EnergyLevel = CoachEnergyLevel.Normal,
                AudioAllowed = true,
                SpeechAllowed = true,
                TypingAllowed = true
            },
            ClarificationsRemaining = 1,
            UserLocalDate = new DateOnly(2026, 8, 17),
            LearnerText = "shorter please",
            PriorMessages =
            [
                new CoachPriorMessage(CoachMessageRole.Learner, "shorter please"),
                new CoachPriorMessage(CoachMessageRole.Coach, "I shortened it to ten minutes.")
            ]
        };

        var json = JsonSerializer.Serialize(request);

        foreach (var historyType in HistoryTypes)
        {
            json.Should().NotContain(historyType.Name,
                "no history contract may be serialized into agent state");
        }

        // The prior messages carry the conversation forward as plain text, which is the point:
        // the model reads what was said without ever being handed the record that stores it.
        json.Should().Contain("shorter please");
    }

    private static void AssertNoHistoryTypeIn(IReadOnlyCollection<Type> reachable, string surface)
    {
        reachable.Should().NotBeEmpty("the walk must actually reach something for {0}", surface);

        var offenders = reachable.Intersect(HistoryTypes).Select(t => t.Name).ToList();

        offenders.Should().BeEmpty(
            "{0} must not be able to reach a durable-history contract, but reaches: {1}",
            surface,
            string.Join(", ", offenders));
    }

    /// <summary>Every type reachable from the given roots through public members.</summary>
    private static IReadOnlyCollection<Type> ReachableFrom(IEnumerable<Type> roots)
    {
        var visited = new HashSet<Type>();
        var queue = new Queue<Type>(roots);

        while (queue.Count > 0)
        {
            var type = Unwrap(queue.Dequeue());

            if (type is null || !visited.Add(type))
            {
                continue;
            }

            if (type.Namespace is null
                || !type.Namespace.StartsWith("SentenceStudio", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                queue.Enqueue(property.PropertyType);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                queue.Enqueue(field.FieldType);
            }
        }

        return visited;
    }

    /// <summary>Strips nullables, tasks, arrays, and collections down to the carried type.</summary>
    private static Type? Unwrap(Type type)
    {
        while (true)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null)
            {
                type = underlying;
                continue;
            }

            if (type.IsArray)
            {
                var element = type.GetElementType();
                if (element is null)
                {
                    return null;
                }

                type = element;
                continue;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
                {
                    type = type.GetGenericArguments()[0];
                    continue;
                }

                if (typeof(IEnumerable).IsAssignableFrom(type))
                {
                    // Last argument, so a dictionary resolves to its value type rather than its key.
                    type = type.GetGenericArguments()[^1];
                    continue;
                }
            }

            return type;
        }
    }
}
