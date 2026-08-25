using System.Text.Json;

namespace SentenceStudio.Contracts.Wire;

/// <summary>
/// The single set of serializer options every client uses to talk to this API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one instance and not one per client.</b> Tolerance that is applied per call site is
/// tolerance that is missing from the call site somebody adds next month, and the failure is
/// invisible until a server ships a new enum member — at which point one screen in the app throws
/// and the rest do not. A single shared options object makes "did this call go through the
/// tolerant path" answerable by reading one line.
/// </para>
/// <para>
/// <b>Why Web defaults.</b> <see cref="System.Net.Http.Json"/>'s parameterless overloads already
/// serialize with <see cref="JsonSerializerDefaults.Web"/> — camelCase names, case-insensitive
/// matching, numbers readable from strings. Starting from the same defaults means installing these
/// options changes exactly one thing, enum tolerance, and leaves property naming and number
/// handling byte-for-byte as they were.
/// </para>
/// <para>
/// <b>What this is not.</b> It is not the server's options. The API keeps its own strict
/// configuration so a malformed or unrecognised value in a learner's request body is still a bad
/// request, and the coach's structured-output parsing stays strict so a model cannot invent an
/// enum member and have it read as something benign.
/// </para>
/// </remarks>
public static class WireJson
{
    /// <summary>
    /// The options every outbound request and inbound response on a client uses.
    /// </summary>
    /// <remarks>
    /// System.Text.Json freezes an options instance the first time it serializes with it, so by
    /// the time any second caller could reach this it is already immutable and a late
    /// <c>Converters.Add</c> throws rather than silently changing how another feature parses.
    /// It is deliberately not frozen eagerly with <c>MakeReadOnly()</c>: that overload demands a
    /// <c>TypeInfoResolver</c> up front, and supplying the reflection-based one here would put a
    /// <c>RequiresUnreferencedCode</c> call on a static initializer that every trimmed mobile head
    /// links in.
    /// </remarks>
    public static JsonSerializerOptions Client { get; } = CreateClientOptions();

    /// <summary>
    /// A fresh copy of the client options, for a caller that must add its own converter.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="Client"/>. This exists for tests and for a future surface that needs an
    /// extra converter without reaching into shared state to get it.
    /// </remarks>
    public static JsonSerializerOptions CreateClientOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new TolerantWireEnumConverterFactory());
        return options;
    }
}
