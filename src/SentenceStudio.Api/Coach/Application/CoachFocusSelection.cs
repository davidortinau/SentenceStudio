using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The server-only record of one resolved vocabulary focus.
/// </summary>
/// <remarks>
/// <para>
/// This is the artifact that makes a focus immutable. The resolver runs once, against the trusted
/// scope, and its answer is frozen here: the exact owned vocabulary identifiers in rank order,
/// the plan version they were chosen for, and the canonical focus they satisfy. Accepting a
/// pending suggestion replays these identifiers rather than resolving again, so the plan the
/// learner accepts is the plan they previewed — not a fresh selection that drifted because a word
/// came due in between.
/// </para>
/// <para>
/// The model never sees it. It is not projected into agent context, and the public
/// <see cref="CoachVocabularyFocusDto"/> carries none of its identifiers.
/// </para>
/// </remarks>
public sealed record CoachFocusSelection
{
    /// <summary>The canonical focus code this selection satisfies.</summary>
    public required string FocusCode { get; init; }

    /// <summary>
    /// The exact owned vocabulary identifiers, in the resolver's rank order. Order is part of the
    /// artifact: replaying a different order would produce a different plan.
    /// </summary>
    public required IReadOnlyList<string> VocabularyWordIds { get; init; }

    /// <summary>The plan version current when the resolver ran.</summary>
    public required string ResolvedForPlanVersion { get; init; }

    /// <summary>Owned words that matched before the count bound.</summary>
    public required int EligibleCount { get; init; }

    /// <summary>
    /// The words as projected for display. Present on a pending offer so a reload can render the
    /// same set without resolving again; deliberately absent from the revision audit, which stays
    /// free of lexical content.
    /// </summary>
    public IReadOnlyList<CoachVocabularyFocusWordDto>? Words { get; init; }

    /// <summary>The same selection with display words stripped, for audit storage.</summary>
    public CoachFocusSelection WithoutWords() => this with { Words = null };
}

/// <summary>Strips learner-authored text from a delta before it is stored or returned.</summary>
public static class CoachDeltaRedaction
{
    /// <summary>
    /// The same change with the learner's focus wording removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>VocabularyFocusDescription</c> is the learner's own words. It exists for exactly one
    /// step — the controlled registry maps it to a canonical code — and after that step nothing
    /// needs it: the code, the frozen identifiers, and <c>ChangedFields</c> carry every decision
    /// the server made. Keeping it would put raw learner text in an unencrypted column, which the
    /// coach's whole storage design says never happens outside the encrypted conversation.
    /// </para>
    /// <para>
    /// <c>ClearVocabularyFocus</c> and <c>ChangedFields</c> survive, so an apply replayed from
    /// storage still knows the focus was part of the change and whether it was being removed.
    /// </para>
    /// </remarks>
    public static CoachConstraintDeltaDto WithoutRawFocusText(this CoachConstraintDeltaDto delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (delta.VocabularyFocusDescription is null)
        {
            return delta;
        }

        return new CoachConstraintDeltaDto
        {
            AvailableMinutes = delta.AvailableMinutes,
            AudioAllowed = delta.AudioAllowed,
            SpeechAllowed = delta.SpeechAllowed,
            TypingAllowed = delta.TypingAllowed,
            SkillEmphasis = delta.SkillEmphasis,
            ClearSkillEmphasis = delta.ClearSkillEmphasis,
            GoalTag = delta.GoalTag,
            ClearGoalTag = delta.ClearGoalTag,
            GoalHorizonDays = delta.GoalHorizonDays,
            ClearGoalHorizonDays = delta.ClearGoalHorizonDays,
            EnergyLevel = delta.EnergyLevel,
            VocabularyFocusDescription = null,
            ClearVocabularyFocus = delta.ClearVocabularyFocus,
            ChangedFields = delta.ChangedFields
        };
    }
}

/// <summary>
/// What the coach stores for a pending suggestion.
/// </summary>
/// <remarks>
/// <para>
/// Written into the existing <c>PendingSuggestionDeltaJson</c> column, which is untyped JSON, so
/// carrying a focus selection alongside the delta needs no schema change.
/// </para>
/// <para>
/// A row written before this shape existed is a bare <c>CoachConstraintDeltaDto</c>. The reader
/// detects that and reads it as a delta with no selection, so an offer that was pending across the
/// deployment still accepts correctly.
/// </para>
/// </remarks>
public sealed record CoachPendingSuggestionEnvelope
{
    /// <summary>Envelope shape version. Absent in the legacy bare-delta form.</summary>
    public int Version { get; init; } = 1;

    public required CoachConstraintDeltaDto Delta { get; init; }

    /// <summary>The frozen focus selection, when this suggestion sets a focus.</summary>
    public CoachFocusSelection? FocusSelection { get; init; }

    /// <summary>Reads either the envelope or a legacy bare delta.</summary>
    public static CoachPendingSuggestionEnvelope? TryRead(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty(nameof(Delta), out _))
            {
                var legacy = CoachNormalizedJson.Deserialize<CoachConstraintDeltaDto>(json);
                return legacy is null ? null : new CoachPendingSuggestionEnvelope { Delta = legacy };
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return CoachNormalizedJson.Deserialize<CoachPendingSuggestionEnvelope>(json);
    }
}

/// <summary>
/// What the coach stores as a session's active state.
/// </summary>
/// <remarks>
/// <para>
/// Written into the existing <c>ActiveConstraintsJson</c> column. The public
/// <see cref="CoachConstraintSetDto"/> deliberately carries no vocabulary identifiers, so the
/// frozen selection rides alongside it here rather than inside it. That keeps identifiers off the
/// wire while letting an unrelated later change — "make it 20 minutes" — preserve the exact focus
/// set instead of re-resolving it and quietly swapping the words.
/// </para>
/// <para>
/// A row written before this shape existed holds a bare <see cref="CoachConstraintSetDto"/>.
/// <see cref="TryRead"/> detects that and reads it as constraints with no selection.
/// </para>
/// </remarks>
public sealed record CoachActiveStateEnvelope
{
    public int Version { get; init; } = 1;

    public required CoachConstraintSetDto Constraints { get; init; }

    /// <summary>The frozen focus selection behind <c>Constraints.VocabularyFocus</c>.</summary>
    public CoachFocusSelection? FocusSelection { get; init; }

    /// <summary>
    /// What this checkpoint covers of the durable ledger, and the configuration it was built
    /// under. Null on a session created before durable history, or by the compatibility routes.
    /// </summary>
    public CoachCheckpointCoverage? Checkpoint { get; init; }

    /// <summary>How many past selections a session remembers well enough to restore.</summary>
    public const int MaxRemembered = 5;

    /// <summary>
    /// The selections this session has resolved, with their display words.
    /// </summary>
    /// <remarks>
    /// The revision audit is permanent and deliberately holds no vocabulary term, so an Undo can
    /// recover which words a past plan used but not what they say. This session-scoped list closes
    /// that gap without putting lexical content in the audit: the session row already holds the
    /// current selection's words and expires with the session.
    /// </remarks>
    public IReadOnlyList<CoachFocusSelection> RememberedFocus { get; init; } =
        Array.Empty<CoachFocusSelection>();

    /// <summary>The remembered words for a selection restored from an audit, if this session has them.</summary>
    public CoachFocusSelection? Rehydrate(CoachFocusSelection? selection)
    {
        if (selection is null || selection.Words is { Count: > 0 })
        {
            return selection;
        }

        var match = RememberedFocus.FirstOrDefault(r =>
            string.Equals(r.FocusCode, selection.FocusCode, StringComparison.Ordinal)
            && r.VocabularyWordIds.SequenceEqual(selection.VocabularyWordIds, StringComparer.Ordinal));

        return match is null ? selection : selection with { Words = match.Words };
    }

    /// <summary>This session's memory with <paramref name="selection"/> added, newest first.</summary>
    public IReadOnlyList<CoachFocusSelection> Remembering(CoachFocusSelection? selection)
    {
        if (selection is null || selection.Words is not { Count: > 0 })
        {
            return RememberedFocus;
        }

        return RememberedFocus
            .Where(r => !string.Equals(r.FocusCode, selection.FocusCode, StringComparison.Ordinal)
                        || !r.VocabularyWordIds.SequenceEqual(selection.VocabularyWordIds, StringComparer.Ordinal))
            .Prepend(selection)
            .Take(MaxRemembered)
            .ToList();
    }

    /// <summary>
    /// Reads either shape. Returns null only when there is nothing stored at all.
    /// </summary>
    public static CoachActiveStateEnvelope? TryRead(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // Discriminate on structure rather than on a failed deserialize: the legacy shape has no
        // "Constraints" member, and probing is cheaper and clearer than catching an exception.
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty(nameof(Constraints), out _))
            {
                var legacy = CoachNormalizedJson.Deserialize<CoachConstraintSetDto>(json);
                return legacy is null ? null : new CoachActiveStateEnvelope { Constraints = legacy };
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return CoachNormalizedJson.Deserialize<CoachActiveStateEnvelope>(json);
    }
}

/// <summary>
/// What a 24-hour checkpoint covers of the permanent ledger, and the configuration that produced
/// it.
/// </summary>
/// <remarks>
/// <para>
/// This rides in the session's existing protected active-state column rather than in new columns,
/// so durable history needs no schema change on the session table. The values are server-only:
/// none of them reaches the wire.
/// </para>
/// <para>
/// <see cref="CoveredSequence"/> is what makes a stale checkpoint detectable. A checkpoint whose
/// coverage trails the ledger — because another replica appended, or because a crash landed
/// between the ledger append and the checkpoint update — must be rebuilt from the ledger rather
/// than trusted, or the agent would answer without having seen its own last turn.
/// </para>
/// </remarks>
public sealed record CoachCheckpointCoverage
{
    /// <summary>The conversation this checkpoint belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The highest ledger sequence the stored agent session has seen.</summary>
    public long CoveredSequence { get; init; }

    /// <summary>The agent configuration version the session was built under.</summary>
    public string? AgentConfigVersion { get; init; }

    /// <summary>The prompt policy version the session was built under.</summary>
    public string? PromptVersion { get; init; }

    /// <summary>The tool policy version the session was built under.</summary>
    public string? ToolPolicyVersion { get; init; }

    /// <summary>The model policy version the session was built under.</summary>
    public string? ModelPolicyVersion { get; init; }

    /// <summary>
    /// True when this coverage was produced by the same configuration the caller is running now.
    /// A mismatch is a rebuild signal, never a reason to hide history.
    /// </summary>
    public bool Matches(CoachCheckpointCoverage current) =>
        current is not null
        && string.Equals(ConversationId, current.ConversationId, StringComparison.Ordinal)
        && string.Equals(AgentConfigVersion, current.AgentConfigVersion, StringComparison.Ordinal)
        && string.Equals(PromptVersion, current.PromptVersion, StringComparison.Ordinal)
        && string.Equals(ToolPolicyVersion, current.ToolPolicyVersion, StringComparison.Ordinal)
        && string.Equals(ModelPolicyVersion, current.ModelPolicyVersion, StringComparison.Ordinal);
}
