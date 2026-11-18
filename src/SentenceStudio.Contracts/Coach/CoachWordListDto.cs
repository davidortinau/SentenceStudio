using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// Words Sam listed because the learner asked to see them, shown under Sam's reply as rows that
/// each open the word. The server builds every row from what its read returned; the model never
/// writes one.
/// </summary>
public sealed class CoachWordListDto
{
    /// <summary>Which words these are.</summary>
    public required CoachWordListKind Kind { get; init; }

    /// <summary>The order the rows are in.</summary>
    public required CoachWordListOrder Order { get; init; }

    /// <summary>The words, in <see cref="Order"/>.</summary>
    public IReadOnlyList<CoachWordListRowDto> Rows { get; init; } = [];

    /// <summary>The most rows a list carries.</summary>
    public const int MaxRows = 40;
}

/// <summary>One word in a <see cref="CoachWordListDto"/>.</summary>
/// <param name="WordId">The word's server identifier.</param>
/// <param name="TargetTerm">The word in the language being learned.</param>
/// <param name="NativeTerm">Its meaning in the learner's language.</param>
/// <param name="MasteryPercent">Mastery out of 100, or null when never practised.</param>
/// <param name="Destination">Where the row opens: the word's own page. Null when it opens nowhere.</param>
public sealed record CoachWordListRowDto(
    string WordId,
    string TargetTerm,
    string NativeTerm,
    int? MasteryPercent,
    CoachDestinationDto? Destination);

/// <summary>Which words a list holds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value; the client shows the rows under a neutral heading.")]
public enum CoachWordListKind
{
    Unknown = 0,

    /// <summary>The words today's plan works on.</summary>
    PlanVocabulary = 1
}

/// <summary>The order of a word list's rows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value; the client shows the rows as sent without naming an order.")]
public enum CoachWordListOrder
{
    Unknown = 0,

    /// <summary>The plan's own order.</summary>
    PlanOrder = 1,

    /// <summary>Lowest mastery first; words never practised last.</summary>
    WeakestFirst = 2
}
