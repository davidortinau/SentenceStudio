using System.Reflection;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// The union of the closed failure vocabularies a ledger row may cite.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> a new vocabulary. Reusing <see cref="CoachWriteFailureCodes"/> and
/// <see cref="CoachToolException.Code"/> verbatim is what keeps this table joinable to the write
/// audit and to the turn telemetry: a reviewer asking "how often did
/// <c>preference_setting_session_minutes</c> fail with <c>invalid_arguments</c>" is asking about
/// the same string the write ledger already writes.
/// </para>
/// <para>
/// The set is discovered by reflection over the existing constant classes rather than copied,
/// so a new refusal code cannot exist in the write ledger and be unrepresentable here.
/// <c>CoachOpportunityTriggerMappingTests</c> proves the discovery covers every constant.
/// </para>
/// </remarks>
public static class CoachOpportunityFailureCodes
{
    /// <summary>Every code the ledger will accept, sorted ordinally.</summary>
    public static IReadOnlyList<string> All { get; } = Build();

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    /// <summary>True when <paramref name="code"/> belongs to one of the reused closed sets.</summary>
    public static bool IsKnown(string? code) =>
        !string.IsNullOrEmpty(code) && Known.Contains(code);

    /// <summary>
    /// The stable code for a tool failure kind, taken from the exception type's own mapping so
    /// the ledger and the wire answer can never disagree about what a failure is called.
    /// </summary>
    public static string ForToolFailure(CoachToolFailureKind kind) =>
        new CoachToolException(kind, "unused", "unused").Code;

    private static string[] Build()
    {
        var codes = new List<string>();

        foreach (var field in typeof(CoachWriteFailureCodes)
                     .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string)
                && field.GetRawConstantValue() is string value
                && !string.IsNullOrWhiteSpace(value))
            {
                codes.Add(value);
            }
        }

        foreach (var kind in Enum.GetValues<CoachToolFailureKind>())
        {
            codes.Add(ForToolFailure(kind));
        }

        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
        distinct.Sort(StringComparer.Ordinal);
        return [.. distinct];
    }
}
