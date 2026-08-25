namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The whole-second rule for an evidence timestamp.
/// </summary>
/// <remarks>
/// <para>
/// A duplicate of the server's own <c>CoachResultScope.NormalizeAsOf</c>, and duplicated for the
/// same reason the scope enums are mirrored: Contracts cannot reference Api. The two are held
/// equal by a test that runs both over the same inputs, which is a stronger guarantee than a
/// shared helper nobody re-reads.
/// </para>
/// <para>
/// <b>Truncated, never rounded.</b> Rounding 12:00:00.7 up to 12:00:01 would place the stated
/// instant after the data it was computed from. "As of" is a claim that the answer was true at
/// that moment, and the only safe direction to move it is backwards.
/// </para>
/// <para>
/// <b>Kind is pinned.</b> A local value serializes with an offset instead of <c>Z</c> — a
/// different claim from the one the member's name makes. Local converts, preserving the instant;
/// unspecified is read as UTC, which is what the name already asserts.
/// </para>
/// </remarks>
public static class CoachEvidenceInstant
{
    /// <summary>Truncates to the whole second and pins the kind to UTC.</summary>
    public static DateTime Normalize(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond),
            DateTimeKind.Utc);
    }
}

/// <summary>
/// One read-only fact behind a coach statement.
/// Evidence is aggregate only. Evidence always states a date range.
/// Evidence never carries a target-language term, a gloss, an example, or diary text.
/// </summary>
/// <remarks>
/// <para>
/// The members below <see cref="Values"/> describe <em>how the fact was obtained</em> rather than
/// what it says. They exist because an aggregate with no stated coverage is the shape a reader
/// over-claims from: twenty resources read as the whole shelf, ten words read as the whole
/// vocabulary, a filtered set read as everything. Every one of them is optional, so a server not
/// yet wired to supply them and a client too old to read them both behave exactly as before.
/// </para>
/// <para>
/// <b>Still aggregate-only.</b> The additions are closed enums, counts, and one timestamp. There is
/// no member here a term, a gloss, an example, or an expected answer could travel in — including
/// <see cref="WithheldReason"/>, which discloses that something was held back and why, never what.
/// </para>
/// </remarks>
public sealed class CoachEvidenceDto
{
    private readonly DateTime? _asOfUtc;

    /// <summary>The kind of evidence.</summary>
    public required CoachEvidenceKind Kind { get; init; }

    /// <summary>
    /// Server prose for the heading. <b>Fallback only.</b>
    /// </summary>
    /// <remarks>
    /// Was documented as "the localized label" and never was: the server writes it in English from
    /// a fixed switch and has no idea what the learner reads. A client that can name
    /// <see cref="Kind"/> localizes the heading itself and ignores this. It stays required and
    /// populated so a client built before that change keeps rendering exactly as it does today.
    /// </remarks>
    public required string Label { get; init; }

    /// <summary>
    /// Server prose for the one-line summary. <b>Fallback only</b>, on the same terms as
    /// <see cref="Label"/>; a client that can name <see cref="DefinitionCode"/> localizes from it.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>The first day of the window, in the user-local calendar.</summary>
    public required DateOnly WindowStartDate { get; init; }

    /// <summary>The last day of the window, in the user-local calendar.</summary>
    public required DateOnly WindowEndDate { get; init; }

    /// <summary>The values behind the summary.</summary>
    public IReadOnlyList<CoachEvidenceValueDto> Values { get; init; } = Array.Empty<CoachEvidenceValueDto>();

    // ── How the fact was obtained. All optional and additive. ────────────────

    /// <summary>
    /// How much of the learner's data this fact was drawn from. Null when the server did not say.
    /// </summary>
    public CoachEvidenceCoverage? Coverage { get; init; }

    /// <summary>The order of the rows behind this fact. Null when the server did not say.</summary>
    public CoachEvidenceOrder? Order { get; init; }

    /// <summary>
    /// Which definition of the population produced this fact. Null when the server did not say.
    /// </summary>
    public CoachDefinitionCode? DefinitionCode { get; init; }

    /// <summary>
    /// Why matching rows were left out, when any were. Null when the server did not say.
    /// </summary>
    public CoachWithheldReason? WithheldReason { get; init; }

    /// <summary>
    /// The instant this fact was true at, truncated to the whole second and always UTC.
    /// </summary>
    /// <remarks>
    /// Normalized on the way in rather than by the caller, so no construction path — including
    /// deserialization of a value a newer server sent — can put sub-second precision on screen.
    /// It was never information here: the underlying reads are computed from calendar days, review
    /// dates, and completion rows. Left in, it would make two identical facts compare unequal and
    /// render as two different timestamps.
    /// </remarks>
    public DateTime? AsOfUtc
    {
        get => _asOfUtc;
        init => _asOfUtc = value is { } instant ? CoachEvidenceInstant.Normalize(instant) : null;
    }

    /// <summary>
    /// How many rows matched before anything was withheld or paged away. Null when the fact is not
    /// a search over a population.
    /// </summary>
    public int? MatchedCount { get; init; }

    /// <summary>How many rows this fact was computed from. Null when the server did not say.</summary>
    public int? ReturnedCount { get; init; }

    /// <summary>
    /// How many matching rows were deliberately left out. Null when the server did not say; zero
    /// when the server looked and none were.
    /// </summary>
    public int? WithheldCount { get; init; }
}

/// <summary>
/// One aggregate value in an evidence item.
/// </summary>
public sealed class CoachEvidenceValueDto
{
    /// <summary>
    /// Server prose for the value's label. <b>Fallback only</b> — see <see cref="Code"/>.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Which value this is, so the client can localize the label. Null when the server did not say.
    /// </summary>
    /// <remarks>
    /// Optional and additive: an old client ignores it and keeps reading <see cref="Label"/>, and a
    /// new client reading a value the server did not code falls back to the same prose.
    /// </remarks>
    public CoachEvidenceValueCode? Code { get; init; }

    /// <summary>The value.</summary>
    public required double Value { get; init; }

    /// <summary>The unit of the value.</summary>
    public required CoachEvidenceUnit Unit { get; init; }
}
