namespace SentenceStudio.Contracts.Wire;

/// <summary>
/// The revision of the wire contract a build understands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not the app's version.</b> The question a gate has to answer is "does this
/// build know the members of this enum", which is a property of the contract assembly, not of a
/// marketing release. Tying it to the app version would mean comparing an iOS build number, a
/// TestFlight build, a web deploy hash and an APK version code against each other — four different
/// shapes, none of them ordered the way the contract changed.
/// </para>
/// <para>
/// <b>Bump this, and only this, when a wire value is added.</b> The revision goes up by one when a
/// member is appended to an enum that crosses the API/client boundary, or when a new gated value
/// is registered in <see cref="WireValueGateRegistry"/>. It never goes down and it is never reused.
/// </para>
/// </remarks>
public static class WireProtocolVersion
{
    /// <summary>
    /// The revision this build speaks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>1 — the tolerance foundation.</b> Clients read unknown enum values without failing and
    /// render them as unavailable rather than guessing. No enum value is gated at this revision;
    /// the registry is empty on purpose.
    /// </para>
    /// </remarks>
    public const int Current = 1;

    /// <summary>
    /// The revision assumed for a client that sends no version at all.
    /// </summary>
    /// <remarks>
    /// Zero, not <see cref="Current"/>. A request with no version is either a build that predates
    /// this header or something that is not our client, and both must be treated as the oldest
    /// thing that could be on the other end. Assuming the newest is how a gate ships a value to
    /// the exact clients it was built to protect.
    /// </remarks>
    public const int Unknown = 0;
}

/// <summary>Header names the client and the server agree on.</summary>
public static class WireHeaders
{
    /// <summary>
    /// Carries <see cref="WireProtocolVersion.Current"/> on every client request.
    /// </summary>
    /// <remarks>
    /// A header rather than a query parameter or a body field: it applies to every route, it does
    /// not change any URL, and it survives a request whose body is a stream. Servers that do not
    /// read it ignore it, which is what makes shipping the client half first safe.
    /// </remarks>
    public const string ClientProtocolVersion = "X-SentenceStudio-Wire-Version";
}

/// <summary>
/// One wire enum member that must not be sent to a client older than a stated revision.
/// </summary>
/// <param name="EnumType">The enum the member belongs to.</param>
/// <param name="MemberName">The member that is gated.</param>
/// <param name="MinimumClientProtocolVersion">
/// The lowest <see cref="WireProtocolVersion"/> that may receive <paramref name="MemberName"/>.
/// </param>
/// <param name="DowngradeMemberName">
/// What an older client is sent instead. Must be a member that older client already knows, and it
/// must be honest — a downgrade that claims something specific and false is worse than the
/// unavailable rendering the tolerant converter would have produced on its own.
/// </param>
/// <param name="Rationale">Why the value is gated, and why the downgrade is truthful.</param>
public sealed record WireValueGate(
    Type EnumType,
    string MemberName,
    int MinimumClientProtocolVersion,
    string DowngradeMemberName,
    string Rationale);

/// <summary>
/// Every gated wire value. <b>Empty today, and that is the current design.</b>
/// </summary>
/// <remarks>
/// <para>
/// This is the seam, not a behaviour. No enum member is suppressed at
/// <see cref="WireProtocolVersion.Current"/> because no new member has been introduced that an
/// older client could not already read. Registering the first entry is the moment the server
/// starts making per-client decisions, and it is a reviewable change to this one list rather than
/// a condition buried in a projection.
/// </para>
/// <para>
/// <b>The two halves are independent on purpose.</b> The gate stops a value reaching a client that
/// cannot name it; the tolerant converter stops an unnamed value taking the conversation down.
/// Either alone leaves a hole — a gate that is missed sends the value anyway, and tolerance alone
/// means every skew degrades to an unavailable card. Both together mean the common case is correct
/// and the uncommon case is survivable.
/// </para>
/// </remarks>
public static class WireValueGateRegistry
{
    /// <summary>The gated values. Empty at <see cref="WireProtocolVersion.Current"/>.</summary>
    public static IReadOnlyList<WireValueGate> All { get; } = Array.Empty<WireValueGate>();

    /// <summary>
    /// The value a client at <paramref name="clientProtocolVersion"/> may be sent in place of
    /// <paramref name="value"/>.
    /// </summary>
    /// <remarks>
    /// Identity while <see cref="All"/> is empty. Written now, and called from nowhere in
    /// production yet, so the first gated value is a one-line registry entry rather than a new
    /// code path invented under time pressure.
    /// </remarks>
    public static TEnum Project<TEnum>(TEnum value, int clientProtocolVersion)
        where TEnum : struct, Enum
    {
        if (All.Count == 0)
        {
            return value;
        }

        foreach (var gate in All)
        {
            if (gate.EnumType != typeof(TEnum)
                || clientProtocolVersion >= gate.MinimumClientProtocolVersion
                || !string.Equals(Enum.GetName(value), gate.MemberName, StringComparison.Ordinal))
            {
                continue;
            }

            return Enum.Parse<TEnum>(gate.DowngradeMemberName);
        }

        return value;
    }

    /// <summary>
    /// The revision a request declared, or <see cref="WireProtocolVersion.Unknown"/> when the
    /// header is absent or unreadable.
    /// </summary>
    public static int ParseClientProtocolVersion(string? headerValue) =>
        int.TryParse(headerValue, out var version) && version > 0 ? version : WireProtocolVersion.Unknown;
}
