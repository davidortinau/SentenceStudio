using System.Text.Json;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Presentation-only envelope stored by the existing ActivitySessionService.
/// It extends the source snapshot with the pending attempt and a stable turn
/// identity. A graded turn is saved before feedback is acknowledged; Next
/// looks up that identity before ever calling the shared attempt writer.
/// </summary>
internal sealed record NativeQuizStoredState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Version { get; init; } = 1;
    public required string UserId { get; init; }
    public required DateOnly PlanDate { get; init; }
    public required string LaunchContextKey { get; init; }
    public string? PlanItemRowId { get; init; }
    public required string DisplayDirection { get; init; }
    public required string TurnId { get; init; }
    public required VocabQuizSessionSnapshot Snapshot { get; init; }
    public VocabularyAttempt? PendingAttempt { get; init; }
    public bool RequiresCorrection { get; init; }
    public bool SummaryVisible { get; init; }
    public long ElapsedTicks { get; init; }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static NativeQuizStoredState Deserialize(string json)
    {
        var state = JsonSerializer.Deserialize<NativeQuizStoredState>(json, JsonOptions)
            ?? throw new JsonException("Native quiz snapshot is empty.");
        if (state.Version != 1 || state.Snapshot is null
            || string.IsNullOrWhiteSpace(state.UserId)
            || string.IsNullOrWhiteSpace(state.LaunchContextKey)
            || string.IsNullOrWhiteSpace(state.TurnId)
            || state.ElapsedTicks < 0)
            throw new JsonException("Native quiz snapshot cannot be resumed safely.");
        return state;
    }
}
