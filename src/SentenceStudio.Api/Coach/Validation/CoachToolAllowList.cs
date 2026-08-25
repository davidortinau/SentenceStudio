using System.Text.Json;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// Checks the tools the coach can call.
/// The list is closed: the coach gets exactly the tools the registry enabled and nothing else.
/// How many that is depends on the feature switches, so no count is stated here — a number in a
/// comment is the first thing to go stale when a tool is added.
/// The check also refuses a tool whose schema names an identity argument or
/// accepts an argument the contract does not name.
/// </summary>
/// <remarks>
/// <c>CoachAgentFactory</c> runs this on both create paths, so the baseline arm and the
/// harness arm are gated by the same check and neither can receive an unapproved tool. A
/// violation throws <see cref="CoachContractViolationException"/>; each arm maps that to a
/// failed turn, so no model call is made.
/// </remarks>
public sealed class CoachToolAllowList
{
    /// <summary>Argument names that no tool may accept.</summary>
    private static readonly string[] BannedArgumentWords =
    [
        "user", "users", "tenant", "profile", "account", "email", "subject",
        "password", "secret", "credential", "apikey", "token"
    ];

    /// <summary>Words that show a tool can change data.</summary>
    private static readonly string[] WriteToolMarkers =
    [
        "write", "update", "delete", "remove", "apply", "save", "create", "set_", "insert", "drop"
    ];

    private readonly ICoachToolRegistry? _registry;

    /// <summary>Creates an allow list backed by the tool registry.</summary>
    public CoachToolAllowList(ICoachToolRegistry registry) => _registry = registry;

    /// <summary>Creates an allow list with no registry (falls back to static <see cref="CoachToolNames.All"/>).</summary>
    public CoachToolAllowList() => _registry = null;

    /// <summary>The allowed tool names, from the registry if available, otherwise the core five.</summary>
    private IReadOnlyList<string> AllowedNames =>
        _registry?.EnabledNames ?? (IReadOnlyList<string>)CoachToolNames.CoreFive;

    /// <summary>Checks the tool set the application is about to give the model.</summary>
    public CoachValidationResult Validate(IEnumerable<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var violations = new List<CoachViolation>();
        var seen = new List<string>();
        var allowed = AllowedNames;

        foreach (var tool in tools)
        {
            seen.Add(tool.Name);

            if (!allowed.Contains(tool.Name))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.ToolAllowList,
                    "unknown_tool",
                    $"The tool '{tool.Name}' is not on the coach allow-list."));
            }

            foreach (var marker in WriteToolMarkers)
            {
                if (!tool.Name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsApprovedProposalTool(tool.Name))
                {
                    continue;
                }

                violations.Add(new CoachViolation(
                    CoachViolationKind.ToolAllowList,
                    "write_tool",
                    $"The tool '{tool.Name}' names a change action. The coach has read-only tools."));
            }

            violations.AddRange(ValidateSchema(tool));
        }

        foreach (var expected in allowed)
        {
            if (!seen.Contains(expected))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.ToolAllowList,
                    "missing_tool",
                    $"The tool set does not hold '{expected}'."));
            }
        }

        var duplicates = seen.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var duplicate in duplicates)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.ToolAllowList,
                "duplicate_tool",
                $"The tool set holds '{duplicate}' more than one time."));
        }

        return CoachValidationResult.From(violations);
    }

    /// <summary>
    /// Whether a write-sounding name belongs to a registered proposal tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two conditions, and both are needed. The <c>propose_</c> prefix alone would let any tool
    /// opt out of the marker check by renaming itself; a write risk class alone would not stop a
    /// bluntly-named <c>delete_vocabulary</c> from being registered as a write and sailing
    /// through. Together they say: this name announces that it proposes rather than acts, and the
    /// registry agrees it is a write tool that goes through the approval ledger.
    /// </para>
    /// <para>
    /// The registry is the authority for the second half, which is why a missing registry refuses.
    /// Reading the class off the tool itself would mean trusting the thing being checked.
    /// </para>
    /// </remarks>
    private bool IsApprovedProposalTool(string toolName)
    {
        if (!toolName.StartsWith(CoachToolNames.ProposePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var registration = _registry?.Find(toolName);
        return registration is not null
            && registration.RiskClass is CoachToolRiskClass.WriteSoft or CoachToolRiskClass.WriteHard;
    }

    /// <summary>Checks one tool schema for an identity argument or an open shape.</summary>
    public IReadOnlyList<CoachViolation> ValidateSchema(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var violations = new List<CoachViolation>();
        var schema = tool.JsonSchema;

        foreach (var name in CollectPropertyNames(schema))
        {
            foreach (var word in CoachEmbargoScanner.SplitIntoLowercaseWords(name))
            {
                if (BannedArgumentWords.Contains(word))
                {
                    violations.Add(new CoachViolation(
                        CoachViolationKind.ToolAllowList,
                        "identity_argument",
                        $"The tool '{tool.Name}' accepts an argument named '{name}'."));
                }
            }

            foreach (var word in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
            {
                if (BannedArgumentWords.Contains(word.ToLowerInvariant()))
                {
                    violations.Add(new CoachViolation(
                        CoachViolationKind.ToolAllowList,
                        "identity_argument",
                        $"The tool '{tool.Name}' accepts an argument named '{name}'."));
                }
            }
        }

        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.True)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.ToolAllowList,
                "open_schema",
                $"The tool '{tool.Name}' accepts an argument its contract does not name."));
        }

        return violations;
    }

    /// <summary>Collects every property name in a schema, at any depth.</summary>
    public static IEnumerable<string> CollectPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in schema.EnumerateObject())
            {
                if (member.NameEquals("properties") && member.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in member.Value.EnumerateObject())
                    {
                        yield return property.Name;
                        foreach (var nested in CollectPropertyNames(property.Value))
                        {
                            yield return nested;
                        }
                    }
                    continue;
                }

                foreach (var nested in CollectPropertyNames(member.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
            {
                foreach (var nested in CollectPropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
