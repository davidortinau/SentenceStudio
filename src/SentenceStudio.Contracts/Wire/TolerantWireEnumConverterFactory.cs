using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Contracts.Wire;

/// <summary>
/// Reads wire enums without throwing on a value this build has never heard of, and writes them
/// only ever as canonical names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where this is installed matters more than what it does.</b> It goes in the
/// <see cref="JsonSerializerOptions.Converters"/> collection of a <em>client</em>'s options — see
/// <see cref="WireJson.Client"/>. System.Text.Json resolves converters in this order: a
/// <c>[JsonConverter]</c> on a property, then the options' <c>Converters</c> collection, then a
/// <c>[JsonConverter]</c> on the type. Because the options collection wins over the type
/// attribute, installing this factory makes one client tolerant while leaving every other
/// serializer in the solution exactly as strict as it was.
/// </para>
/// <para>
/// That is deliberate and load-bearing. The same enums are parsed in three other places that must
/// <b>not</b> become tolerant:
/// </para>
/// <list type="bullet">
///   <item>
///     the server parsing structured model output, where a model inventing
///     <c>"DeletePlan"</c> must be refused rather than quietly read as "no change";
///   </item>
///   <item>
///     the API binding a learner's request body, where an unrecognised value is a bad request;
///   </item>
///   <item>
///     Entity Framework, which stores several of these enums as ordinals and never goes through
///     System.Text.Json at all.
///   </item>
/// </list>
/// <para>
/// <b>Unknown value versus malformed document.</b> A string or a number this build cannot name is
/// tolerated: it collapses to the member declared by <see cref="WireEnumFallbackAttribute"/>. An
/// object, an array or a boolean in an enum position is not tolerated — that is a shape error, not
/// a version skew, and swallowing it would hide a genuinely broken response behind a plausible
/// default.
/// </para>
/// </remarks>
public sealed class TolerantWireEnumConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        var underlying = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        return underlying.IsEnum && WireEnumFallback.IsAnnotated(underlying);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var underlying = Nullable.GetUnderlyingType(typeToConvert);

        var converterType = underlying is null
            ? typeof(TolerantWireEnumConverter<>).MakeGenericType(typeToConvert)
            : typeof(NullableTolerantWireEnumConverter<>).MakeGenericType(underlying);

        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// The tolerant converter for one enum. Never throws on an unrecognised value; always writes a
/// canonical name.
/// </summary>
internal sealed class TolerantWireEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>
    /// Canonical name to value. Case-insensitive on read because a client that receives
    /// <c>"completed"</c> from a proxy that lower-cased it should still read it as
    /// <c>Completed</c> rather than degrade — the value is known, only its casing is not.
    /// </summary>
    private static readonly FrozenDictionary<string, TEnum> ByName =
        Enum.GetNames<TEnum>()
            .ToFrozenDictionary(name => name, Enum.Parse<TEnum>, StringComparer.OrdinalIgnoreCase);

    private static readonly TEnum Fallback = (TEnum)WireEnumFallback.Describe(typeof(TEnum)).Value;

    private static readonly string FallbackName = WireEnumFallback.Describe(typeof(TEnum)).MemberName;

    /// <summary>
    /// An explicit <c>null</c> in an enum position is version skew, not a broken document, so it
    /// is handled here rather than left to throw before the converter runs.
    /// </summary>
    public override bool HandleNull => true;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader);

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(NameOf(value));

    public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } name && ByName.TryGetValue(name, out var parsed) ? parsed : Fallback;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WritePropertyName(NameOf(value));

    /// <summary>
    /// The canonical name, or the fallback's name for a value that is not a declared member.
    /// </summary>
    /// <remarks>
    /// An undefined value can only get here by a cast in client code. Writing the number would put
    /// an integer on a wire the whole contract says carries names, and every reader of that
    /// payload — the server, a log, a support engineer — would then have to guess.
    /// </remarks>
    private static string NameOf(TEnum value) => Enum.GetName(value) ?? FallbackName;

    internal static TEnum ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString() is { } name && ByName.TryGetValue(name, out var parsed)
            ? parsed
            : Fallback,

        // A number is accepted so a server that ever ships a numeric enum — or a proxy that
        // rewrites one — cannot take the conversation down. A number that names no member lands on
        // the fallback exactly like an unknown string.
        JsonTokenType.Number => ReadNumber(ref reader),

        JsonTokenType.Null => Fallback,

        // Shape error, not version skew. Let it fail.
        _ => throw new JsonException(
            $"Expected a string or a number for {typeof(TEnum).Name} but found {reader.TokenType}.")
    };

    private static TEnum ReadNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var signed))
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), signed);
            return Enum.IsDefined(candidate) ? candidate : Fallback;
        }

        if (reader.TryGetUInt64(out var unsigned))
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), unsigned);
            return Enum.IsDefined(candidate) ? candidate : Fallback;
        }

        // A fractional or out-of-range number names no member. Degrade rather than fail: the
        // surrounding message is still worth showing.
        return Fallback;
    }
}

/// <summary>
/// The nullable projection. <c>null</c> stays <c>null</c> — an absent value and an unreadable one
/// are different facts, and a DTO that bothered to make the property nullable is asking to keep
/// them apart.
/// </summary>
internal sealed class NullableTolerantWireEnumConverter<TEnum> : JsonConverter<TEnum?>
    where TEnum : struct, Enum
{
    public override bool HandleNull => true;

    public override TEnum? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null
            ? null
            : TolerantWireEnumConverter<TEnum>.ReadValue(ref reader);

    public override void Write(Utf8JsonWriter writer, TEnum? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(Enum.GetName(value.Value)
            ?? WireEnumFallback.Describe(typeof(TEnum)).MemberName);
    }
}
