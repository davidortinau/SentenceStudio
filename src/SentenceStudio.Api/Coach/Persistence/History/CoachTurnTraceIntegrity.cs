using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// Whether a deserialized turn trace is one this build can be trusted to have read correctly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a post-parse census rather than a converter.</b> The six enums a trace persists do not
/// fail the same way, and that asymmetry is the trap. Three of them —
/// <see cref="CoachScopeCoverage"/>, <see cref="CoachScopeDefinition"/> and
/// <see cref="CoachScopeWithheldReason"/> — carry <c>JsonStringEnumConverter</c>, so a member this
/// build has never heard of arrives as an unknown <em>name</em> and throws. The other three —
/// <see cref="CoachToolCallOutcome"/>, <see cref="CoachToolFailureKind"/> and
/// <see cref="CoachToolArgumentMask"/> — are written as numbers, and System.Text.Json will happily
/// materialise any integer into an enum. So the same forward-compatibility problem shows up as a
/// loud exception on one half of the record and as a silently undefined value on the other.
/// </para>
/// <para>
/// A converter would only fix the loud half. Checking every member against its own declaration
/// after the parse covers all six with one rule, and does it whatever the wire form is — including
/// if somebody later adds a string converter to one of the numeric three, or removes one.
/// </para>
/// <para>
/// <b>The verdict is all-or-nothing for the trace section, and never for the answer.</b> A trace is
/// a diagnostic; the answer is the turn. Section-scoped tolerance means an unreadable trace costs
/// the diagnostic and nothing else — see <c>CoachConversationService.ReadOutcome</c>, which is the
/// only caller that matters.
/// </para>
/// </remarks>
internal static class CoachTurnTraceIntegrity
{
    /// <summary>
    /// Every bit any declared <see cref="CoachToolArgumentMask"/> member sets.
    /// </summary>
    /// <remarks>
    /// Derived from the enum rather than written out, so a member added later widens this without
    /// anybody remembering to. A stored mask carrying a bit outside this union was written by a
    /// build that knew an argument kind this one does not, and the presence set it describes is
    /// therefore not one this build can report.
    /// </remarks>
    private static readonly CoachToolArgumentMask KnownMaskBits =
        Enum.GetValues<CoachToolArgumentMask>()
            .Aggregate(CoachToolArgumentMask.None, (all, bit) => all | bit);

    /// <summary>
    /// The persisted trace enum types this census covers. Six, and the count is asserted.
    /// </summary>
    /// <remarks>
    /// Exposed so the contract test can prove the census is complete against
    /// <see cref="CoachTurnTraceEntry"/>'s own declaration rather than against a list somebody
    /// maintains by hand. A seventh enum on the entry with no arm here is the exact gap this
    /// property makes visible.
    /// </remarks>
    internal static IReadOnlyList<Type> CoveredEnumTypes { get; } =
    [
        typeof(CoachToolCallOutcome),
        typeof(CoachToolFailureKind),
        typeof(CoachToolArgumentMask),
        typeof(CoachScopeCoverage),
        typeof(CoachScopeDefinition),
        typeof(CoachScopeWithheldReason)
    ];

    /// <summary>True when every entry in <paramref name="trace"/> is fully readable.</summary>
    /// <remarks>
    /// One unreadable entry condemns the section. The alternative — dropping the entry and keeping
    /// the rest — would renumber nothing but would silently shorten the record of what a turn did,
    /// and a trace that is quietly incomplete is worse than one that is visibly absent.
    /// </remarks>
    internal static bool IsReadable(CoachTurnTraceSummary trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        foreach (var call in trace.Calls)
        {
            if (call is null || !IsReadable(call))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when all six of this entry's enum members name something this build declares.</summary>
    internal static bool IsReadable(CoachTurnTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Enum.IsDefined(entry.Outcome)
            && (entry.FailureKind is not { } failure || Enum.IsDefined(failure))
            && IsKnownMask(entry.ArgumentMask)
            && Enum.IsDefined(entry.Coverage)
            && Enum.IsDefined(entry.DefinitionCode)
            && Enum.IsDefined(entry.WithheldReason);
    }

    /// <summary>
    /// True when <paramref name="mask"/> sets no bit outside the declared members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Enum.IsDefined</c> is the wrong test for a <c>[Flags]</c> enum — it refuses every legal
    /// combination of two members — so the union of declared bits is what a stored mask is judged
    /// against.
    /// </para>
    /// <para>
    /// <b>The unknown bits are not masked off.</b> Keeping the known ones and dropping the rest
    /// would be a guess dressed as a repair: this build cannot know whether the unknown bit changes
    /// what the known ones mean, and a presence set that is quietly narrower than the one recorded
    /// is a false statement about what the call carried. Simon's "only if provably correct" is not
    /// satisfiable here, so the whole section stands down.
    /// </para>
    /// </remarks>
    private static bool IsKnownMask(CoachToolArgumentMask mask) => (mask & ~KnownMaskBits) == 0;
}
