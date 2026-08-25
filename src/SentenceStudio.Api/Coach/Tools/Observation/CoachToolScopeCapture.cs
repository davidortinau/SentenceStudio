using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Captures the <see cref="CoachResultScope"/> a tool stated, on its way through the serializer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not simply read off the result.</b> <c>AIFunctionFactory</c> marshals a tool's
/// return value to a <see cref="JsonElement"/> before any wrapper sees it, so by the time
/// <c>CoachObservedFunction</c> has the result the envelope — and the
/// <c>ICoachScopedResult</c> it implemented — is gone. Microsoft.Extensions.AI 10.0.1 exposes no
/// pre-marshal hook.
/// </para>
/// <para>
/// <b>Why the marshalled JSON is not good enough either.</b> The scope's model-facing projection
/// deliberately omits six foundation members, <c>DefinitionCode</c> among them, and W3's evidence
/// shape needs exactly that one. Re-reading the scope out of the JSON would hand the projection a
/// silently incomplete object — every field present and correct, and the one the consumer needed
/// reading <c>Unspecified</c>. That is the class of defect this whole seam exists to prevent, so
/// producing it in the seam itself was not an option.
/// </para>
/// <para>
/// <b>The box, and why it is not a plain AsyncLocal value.</b> An <see cref="AsyncLocal{T}"/> write
/// made deeper in a call stack does not flow back up to the caller. So the caller installs a
/// mutable box before invoking, the converter mutates the box, and the caller reads it afterwards.
/// Each invocation installs its own box, and <see cref="AsyncLocal{T}"/> isolates concurrent flows,
/// so two tool calls in flight cannot see each other's scope.
/// </para>
/// <para>
/// <b>Nothing about the emitted JSON changes.</b> The converter records the instance and then
/// delegates to a serializer built from the same options minus itself, so the bytes the model reads
/// are byte-for-byte what they were — which the pinned-projection tests already hold to the
/// character.
/// </para>
/// </remarks>
public static class CoachToolScopeCapture
{
    private static readonly AsyncLocal<ScopeBox?> Slot = new();

    /// <summary>Installs a fresh box for one invocation and returns it.</summary>
    public static ScopeBox Begin()
    {
        var box = new ScopeBox();
        Slot.Value = box;
        return box;
    }

    /// <summary>Clears the slot once the invocation is done.</summary>
    public static void End() => Slot.Value = null;

    /// <summary>Records a scope, if an invocation is listening.</summary>
    internal static void Record(CoachResultScope scope) => Slot.Value?.Record(scope);

    /// <summary>The scope one invocation stated.</summary>
    public sealed class ScopeBox
    {
        /// <summary>The captured scope, or null when the tool stated none.</summary>
        public CoachResultScope? Scope { get; private set; }

        /// <summary>
        /// Records the first scope only.
        /// </summary>
        /// <remarks>
        /// First rather than last, so the value is deterministic if a result ever nests a second
        /// scope. A read states one scope by contract; "the outermost one" is the answer that stays
        /// true if that ever stops being so.
        /// </remarks>
        internal void Record(CoachResultScope scope) => Scope ??= scope;
    }
}

/// <summary>
/// Serializes a <see cref="CoachResultScope"/> exactly as before, and notes it in passing.
/// </summary>
/// <remarks>
/// Attached to the tool factory's serializer options. It is a read-only observer of the
/// serialization path: it adds a reference assignment and changes no output.
/// </remarks>
public sealed class CoachResultScopeCaptureConverter : JsonConverter<CoachResultScope>
{
    private readonly JsonSerializerOptions _passthrough;

    /// <param name="source">
    /// The options this converter is attached to. A copy without this converter is taken once, and
    /// is what actually writes — calling the ambient options from inside the converter would
    /// re-enter it forever.
    /// </param>
    public CoachResultScopeCaptureConverter(JsonSerializerOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var passthrough = new JsonSerializerOptions(source);
        for (var i = passthrough.Converters.Count - 1; i >= 0; i--)
        {
            if (passthrough.Converters[i] is CoachResultScopeCaptureConverter)
            {
                passthrough.Converters.RemoveAt(i);
            }
        }

        _passthrough = passthrough;
    }

    /// <inheritdoc />
    public override CoachResultScope? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<CoachResultScope>(ref reader, _passthrough);

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, CoachResultScope value, JsonSerializerOptions options)
    {
        // The whole capture. Everything after this line is the serialization that would have
        // happened anyway.
        CoachToolScopeCapture.Record(value);

        JsonSerializer.Serialize(writer, value, _passthrough);
    }
}
