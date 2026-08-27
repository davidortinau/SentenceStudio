using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Api.Coach.Opportunities.Digest;

/// <summary>
/// Serializes the digest for a machine reader.
/// </summary>
/// <remarks>
/// <para>
/// camelCase, indented, and with nulls kept. The property names match the operator rollup's own
/// JSON so a consumer does not have to know which producer it read from, and nulls are kept
/// because "this problem named no tool" and "this producer omitted the field" are different facts
/// and a reviewer's script should not have to guess which one it is looking at.
/// </para>
/// <para>
/// Enums are already rendered as strings by the reader before they reach this shape, so there is
/// no converter here and no way for an ordinal to leak into an artifact whose whole value is
/// being readable a year later.
/// </para>
/// </remarks>
public static class CoachOpportunityDigestJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Serializes the digest as indented camelCase JSON.</summary>
    public static string Serialize(CoachOpportunityDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);

        return JsonSerializer.Serialize(digest, Options);
    }
}
