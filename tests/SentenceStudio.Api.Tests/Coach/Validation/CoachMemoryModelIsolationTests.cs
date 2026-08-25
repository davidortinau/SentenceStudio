using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// Proves the memory seam by structure: what the model may propose, and what it may never touch.
/// </summary>
/// <remarks>
/// <para>
/// Memory has an asymmetric boundary, and the asymmetry is the whole design. The model is allowed
/// to <em>propose</em> — so a small, bounded proposal shape is deliberately part of the typed
/// intent. The model is not allowed to <em>read</em> stored memory as a structure, call a tool
/// against it, or receive a persisted fact with its identifiers and version. Selected memory
/// reaches a turn as one formatted string in an untrusted block and nothing else.
/// </para>
/// <para>
/// These tests walk the real graphs rather than checking names, for the same reason the history
/// isolation tests do: the embargo scanner cannot catch a legitimately-named type being hung off
/// the wrong root, and that is exactly the mistake a future change is most likely to make.
/// </para>
/// </remarks>
public class CoachMemoryModelIsolationTests
{
    /// <summary>The persisted-memory contracts, which the model must never receive.</summary>
    private static IReadOnlyList<Type> StoredMemoryTypes { get; } =
    [
        typeof(CoachMemoryFactDto),
        typeof(CoachMemoryValueDto),
        typeof(CoachMemoryPageDto),
        typeof(CoachMemoryApproveRequest),
        typeof(CoachMemoryEditRequest),
        typeof(CoachMemoryRejectRequest)
    ];

    [Fact]
    public void The_model_may_propose_a_memory_but_only_through_the_bounded_intent()
    {
        var reachable = ReachableFrom([typeof(CoachTurnIntent)]);

        // The proposal shape is in, by design.
        reachable.Should().Contain(typeof(CoachMemoryProposalIntent),
            "the model has to be able to propose, or explicit memory could never be created");

        // The stored shapes are out. A model that could emit a fact id and a version could
        // approve its own proposal, and the learner's approval step would become decorative.
        AssertNoStoredMemoryTypeIn(reachable, "the coach turn intent");
    }

    [Fact]
    public void The_proposal_intent_carries_no_identifier_status_or_version()
    {
        var members = typeof(CoachMemoryProposalIntent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        members.Should().NotBeEmpty();

        // No Id, no Version, no Status, no owner. The model describes what the learner said; the
        // server decides what row that becomes and who it belongs to.
        members.Should().NotContain(n => n.Contains("Id", StringComparison.Ordinal));
        members.Should().NotContain(n => n.Contains("Version", StringComparison.Ordinal));
        members.Should().NotContain(n => n.Contains("Status", StringComparison.Ordinal));
        members.Should().NotContain(n => n.Contains("Owner", StringComparison.Ordinal));
    }

    [Fact]
    public void No_stored_memory_contract_reaches_the_agent_turn_request()
    {
        var reachable = ReachableFrom([typeof(CoachAgentTurnRequest)]);

        AssertNoStoredMemoryTypeIn(reachable, "the agent turn request");

        // Selected memory travels as text, not as a structure. A typed fact on the request would
        // invite the framework to treat it as anything other than untrusted data.
        typeof(CoachAgentTurnRequest)
            .GetProperty(nameof(CoachAgentTurnRequest.MemoryBlock))!
            .PropertyType.Should().Be(typeof(string),
                "a nullable string: present or absent, never a structure");
    }

    [Fact]
    public void No_stored_memory_contract_reaches_the_agent_turn_result()
    {
        AssertNoStoredMemoryTypeIn(ReachableFrom([typeof(CoachAgentTurnResult)]), "the agent turn result");
    }

    [Fact]
    public void No_memory_contract_reaches_any_tool_the_provider_is_handed()
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

        // There is no memory tool, and there must not be one. A model that could query memory
        // could enumerate what it is not currently allowed to see.
        foreach (var tool in tools)
        {
            tool.Name.Should().NotContain("emor", "no tool may read or write learner memory");

            var schema = tool.JsonSchema.ToString();
            foreach (var type in StoredMemoryTypes.Append(typeof(CoachMemoryProposalIntent)))
            {
                schema.Should().NotContain(type.Name,
                    "tool '{0}' must not describe {1}", tool.Name, type.Name);
            }
        }

        var reachable = ReachableFrom(tools
            .Select(t => t.UnderlyingMethod)
            .Where(m => m is not null)
            .SelectMany(m => m!.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

        AssertNoStoredMemoryTypeIn(reachable, "the tool signature graph");
        reachable.Should().NotContain(typeof(CoachMemoryProposalIntent),
            "proposing is an intent the model emits, never an action it invokes");
    }

    /// <summary>
    /// A serialized turn request carries no fact identifier or version, checked as text.
    /// </summary>
    /// <remarks>
    /// Type-graph walking stops at <c>string</c>, which is precisely where the memory block lives.
    /// Serializing a realistic request and reading the bytes covers the gap: the block must carry
    /// the learner's own words and nothing that identifies the row they came from.
    /// </remarks>
    [Fact]
    public void A_serialized_turn_request_carries_no_memory_identifier_or_version()
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
            LearnerText = "What should I study today?",
            MemoryBlock =
                """
                UNTRUSTED SAVED LEARNING PREFERENCES
                {"kind":"PersistentStudyGoal","value":"Prepare for a work trip to Seoul"}
                """
        };

        var json = JsonSerializer.Serialize(request);

        foreach (var forbidden in new[] { "factId", "\"id\"", "version", "status", "ciphertext", "userProfileId" })
        {
            json.ToLowerInvariant().Should().NotContain(forbidden.ToLowerInvariant());
        }

        json.Should().Contain("UNTRUSTED", "the block must stay labelled all the way to the wire");
    }

    private static void AssertNoStoredMemoryTypeIn(IReadOnlyCollection<Type> reachable, string what)
    {
        foreach (var type in StoredMemoryTypes)
        {
            reachable.Should().NotContain(type,
                "{0} must not be able to reach the persisted memory contract {1}", what, type.Name);
        }
    }

    /// <summary>Every type reachable from these roots through public members, bounded.</summary>
    private static IReadOnlyCollection<Type> ReachableFrom(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(roots);

        while (pending.Count > 0)
        {
            var type = Unwrap(pending.Pop());

            if (type is null || !seen.Add(type))
            {
                continue;
            }

            if (type.IsPrimitive || type == typeof(string) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                pending.Push(property.PropertyType);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                pending.Push(field.FieldType);
            }
        }

        return seen;
    }

    private static Type? Unwrap(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (argument.Namespace?.StartsWith("SentenceStudio", StringComparison.Ordinal) == true)
                {
                    return argument;
                }
            }
        }

        return type;
    }
}
