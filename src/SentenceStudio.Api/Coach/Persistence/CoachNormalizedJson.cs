using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// Deterministic serialization helpers for the normalized JSON columns.
/// </summary>
/// <remarks>
/// Determinism matters because the revision audit stores a SHA-256 hash of each plan
/// snapshot. Two logically identical snapshots must produce byte-identical JSON, so
/// options are fixed here and never taken from ambient defaults.
/// </remarks>
public static class CoachNormalizedJson
{
    /// <summary>The single serializer configuration used for every normalized column.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The configuration used to read a payload that came from outside the server.
    /// </summary>
    /// <remarks>
    /// Identical to <see cref="Options"/> except that an unmapped member is an error rather than
    /// something to drop silently. Model-supplied tool arguments are the case this exists for: a
    /// payload carrying a member the contract does not declare is not a payload with a harmless
    /// extra field, it is a payload written against a different contract, and accepting the
    /// members that happened to match would mean approving a preview built from a request nobody
    /// fully read. Stored server-authored payloads keep using <see cref="Options"/>, where a
    /// member added by a later schema must stay readable.
    /// </remarks>
    public static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Serializes a normalized value for storage.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes a stored normalized value.</summary>
    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deserializes an externally-supplied value, refusing any undeclared member.</summary>
    public static T? DeserializeStrict<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, StrictOptions);

    /// <summary>Lower-case hex SHA-256 of the supplied normalized JSON.</summary>
    public static string Hash(string normalizedJson)
    {
        ArgumentNullException.ThrowIfNull(normalizedJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedJson));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// The ISO-8601 week key (<c>yyyy-Www</c>) for a learner-local date. Weekly coach
    /// limits are scoped by this key.
    /// </summary>
    public static string WeekKey(DateOnly localDate)
    {
        var date = localDate.ToDateTime(TimeOnly.MinValue);
        var week = ISOWeek.GetWeekOfYear(date);
        var year = ISOWeek.GetYear(date);
        return string.Create(CultureInfo.InvariantCulture, $"{year:D4}-W{week:D2}");
    }
}
