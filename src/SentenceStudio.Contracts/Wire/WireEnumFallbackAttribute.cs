namespace SentenceStudio.Contracts.Wire;

/// <summary>
/// How a wire enum's unknown-value fallback was chosen. The category is recorded on the type so a
/// reviewer can tell a deliberate decision from an unexamined default.
/// </summary>
public enum WireEnumFallbackKind
{
    /// <summary>
    /// The enum's zero member is already the documented fail-closed value — <c>Unknown</c>,
    /// <c>None</c>, <c>Disabled</c>, <c>Expired</c>, <c>Failed</c>, <c>NoChange</c> and friends.
    /// An unreadable value lands where an unset value already lands, so nothing new is claimed.
    /// </summary>
    SafeZero = 0,

    /// <summary>
    /// The zero member carries real meaning, and some other existing member is the honest neutral
    /// landing spot — <c>Other</c>, <c>Unreadable</c>, and the like. Chosen over the zero member
    /// precisely so an unreadable value is not silently relabelled as a real one.
    /// </summary>
    NeutralMember = 1,

    /// <summary>
    /// The zero member carries real meaning and no other member is honestly neutral, but collapsing
    /// onto a member is still safe because the value drives no control, no write, and no
    /// learner-visible label of its own. The rationale must say why.
    /// </summary>
    /// <remarks>
    /// This is the category that requires the most scrutiny, and it is only defensible alongside
    /// the client-version gate in <see cref="WireValueGate"/>: the gate is what stops a newer
    /// server sending a value an older client cannot name, and this fallback is the fail-safe for
    /// when the gate is absent or wrong.
    /// </remarks>
    DeliberateNeutral = 2,

    /// <summary>
    /// The zero member carries real meaning, no member is neutral, and the client must be able to
    /// <em>tell</em> the value is unknown in order to render honestly. A sentinel was
    /// <b>appended</b> — never inserted, never renumbered — so stored ordinals keep their meaning.
    /// </summary>
    AppendedSentinel = 3
}

/// <summary>
/// Declares the member a wire enum collapses to when a client reads a value it does not know.
/// </summary>
/// <remarks>
/// <para>
/// <b>This attribute changes nothing on its own.</b> It is inert metadata: it does not register a
/// converter, it does not alter the enum's ordinals, and it is invisible to Entity Framework, to
/// the model-facing serializer, and to the server's own strict parsing. It is read by exactly one
/// thing — <see cref="TolerantWireEnumConverterFactory"/>, which a client installs in its own
/// <see cref="System.Text.Json.JsonSerializerOptions"/>.
/// </para>
/// <para>
/// It exists because the alternative — a lookup table of fallbacks living in the client — drifts.
/// The person appending a member to an enum is the person who knows what an unreadable value
/// should degrade to, and they are looking at this file when they do it.
/// </para>
/// <para>
/// <b>Why not simply default to zero.</b> Several coach enums have a meaningful zero:
/// <c>CoachMessageKind.Text</c>, <c>CoachAnswerTopic.Vocabulary</c>,
/// <c>CoachEvidenceUnit.Minutes</c>. Collapsing an unknown value onto those would have the client
/// state something specific and false about content it could not read, which is worse than the
/// exception it replaces. Naming the member explicitly forces that decision to be made and
/// reviewed per enum rather than inherited from declaration order.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class WireEnumFallbackAttribute : Attribute
{
    /// <param name="memberName">
    /// The name of the member an unknown value collapses to. Must be a member of the annotated
    /// enum; the architecture test and the converter both refuse a name that is not.
    /// </param>
    /// <param name="kind">How the member was chosen.</param>
    /// <param name="rationale">
    /// Why this member is safe. Required, and required to be substantive: an unknown value that
    /// reaches a learner's screen is a product decision, not a serialization detail.
    /// </param>
    public WireEnumFallbackAttribute(string memberName, WireEnumFallbackKind kind, string rationale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);

        MemberName = memberName;
        Kind = kind;
        Rationale = rationale;
    }

    /// <summary>The member an unknown value collapses to.</summary>
    public string MemberName { get; }

    /// <summary>How the member was chosen.</summary>
    public WireEnumFallbackKind Kind { get; }

    /// <summary>Why this member is safe.</summary>
    public string Rationale { get; }
}
