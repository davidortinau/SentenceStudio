using System.Text.Json;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// The one saved Vocabulary Quiz state every client reads and writes (web, MAUI, the native
/// apps, and later the Flutter port): the contract in docs/activity-session-format.md, stored in
/// <see cref="ActivitySession.StateJson"/>. Every client's tests read the same sample files
/// (tests/fixtures/activity-session). Unknown fields are ignored, so a newer client may add an
/// optional field without breaking an older one; changing a field's meaning needs a new
/// <see cref="Version"/>.
/// </summary>
public sealed record VocabQuizSavedState
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Version { get; init; } = CurrentVersion;
    public required string UserId { get; init; }

    /// <summary>The learner's local day the run belongs to.</summary>
    public required DateOnly PlanDate { get; init; }

    public required string LaunchContextKey { get; init; }

    /// <summary>The run's DailyPlanCompletion row, when the saving client knows it.</summary>
    public string? PlanItemRowId { get; init; }

    /// <summary>The ad-hoc plan item of a quiz started outside the plan, when the saving client made one.</summary>
    public string? AdHocPlanItemId { get; init; }

    /// <summary>Re-practice of an already completed plan card; null when the saving client doesn't track it.</summary>
    public bool? IsRepractice { get; init; }

    /// <summary>The display direction setting when saved ("" when unknown).</summary>
    public string DisplayDirection { get; init; } = string.Empty;

    /// <summary>The current question's identity; a new one for every question.</summary>
    public required string TurnId { get; init; }

    public required VocabQuizSessionSnapshot Snapshot { get; init; }

    /// <summary>
    /// An answer graded but not yet scored. The snapshot's counters already include it. Only
    /// the client that saved it (<see cref="SavedBy"/>) may score it; any other client moves
    /// past that question without scoring it.
    /// </summary>
    public VocabularyAttempt? PendingAttempt { get; init; }

    public bool RequiresCorrection { get; init; }

    /// <summary>The round summary is showing.</summary>
    public bool SummaryVisible { get; init; }

    public long ElapsedTicks { get; init; }

    /// <summary>A sentence-writing turn in progress; same rule as <see cref="PendingAttempt"/>.</summary>
    public string? SentenceSubmissionId { get; init; }

    public string? SentenceSourceInput { get; init; }
    public IReadOnlyList<VocabQuizSentenceGrade> SentenceResults { get; init; } = [];

    /// <summary>The sentence turn's credit is included in the snapshot's counters.</summary>
    public bool SentenceCreditApplied { get; init; }

    /// <summary>
    /// The current question (<see cref="TurnId"/>) was answered and scored on another client;
    /// the saving client moved past it without scoring it, so no receipt of its own exists.
    /// </summary>
    public bool TurnScoredElsewhere { get; init; }

    /// <summary>
    /// The client install that saved this state, e.g. "native:&lt;id&gt;". Null for clients that
    /// never save a pending answer (web, MAUI) and for states saved before this field existed.
    /// </summary>
    public string? SavedBy { get; init; }

    /// <summary>True when <paramref name="installId"/> saved this state.</summary>
    public bool IsSavedBy(string? installId) =>
        !string.IsNullOrEmpty(installId)
        && string.Equals(SavedBy, installId, StringComparison.Ordinal);

    /// <summary>True when the state holds a graded answer or sentence turn.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasUnfinishedTurn => PendingAttempt is not null || SentenceSubmissionId is not null;

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a saved state; throws <see cref="JsonException"/> when it is not a valid one.</summary>
    public static VocabQuizSavedState Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new JsonException("Saved quiz state is empty.");
        var state = JsonSerializer.Deserialize<VocabQuizSavedState>(json, JsonOptions)
            ?? throw new JsonException("Saved quiz state is empty.");
        if (state.Version != CurrentVersion || state.Snapshot is null
            || string.IsNullOrWhiteSpace(state.UserId)
            || string.IsNullOrWhiteSpace(state.LaunchContextKey)
            || string.IsNullOrWhiteSpace(state.TurnId)
            || state.ElapsedTicks < 0
            || state.DisplayDirection is null
            || state.SentenceResults is null
            || (state.SentenceSubmissionId is not null
                && (!Guid.TryParseExact(state.SentenceSubmissionId, "N", out _)
                    || string.IsNullOrWhiteSpace(state.SentenceSourceInput)))
            || (state.SentenceCreditApplied && state.SentenceResults.Count == 0))
            throw new JsonException("Saved quiz state cannot be resumed safely.");
        return state;
    }

    public static bool TryDeserialize(string json, out VocabQuizSavedState? state)
    {
        try
        {
            state = Deserialize(json);
            return true;
        }
        catch (JsonException)
        {
            state = null;
            return false;
        }
    }
}

/// <summary>One graded sentence of a quiz sentence-writing turn.</summary>
public sealed record VocabQuizSentenceGrade(
    string Sentence, bool UsageCorrect, string UsageExplanation,
    double? Accuracy, double? Fluency, string AccuracyExplanation,
    string FluencyExplanation, string RecommendedSentence, string GrammarNotes,
    bool GradeUnavailable = false)
{
    public string? TargetUsedForm { get; init; }
    public bool HasVerifiedTargetUse { get; init; }
}
