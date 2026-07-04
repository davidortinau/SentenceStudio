using System.Text.Json;

namespace SentenceStudio.Shared.Models;

public sealed record VocabQuizSessionSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? PlanItemId { get; init; }
    public IReadOnlyList<string> FocusVocabularyIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ResourceIds { get; init; } = Array.Empty<string>();
    public bool DueOnly { get; init; }
    public string? SkillId { get; init; }
    public IReadOnlyList<VocabQuizBatchItemSnapshot> BatchPool { get; init; } = Array.Empty<VocabQuizBatchItemSnapshot>();
    public IReadOnlyList<string> RoundWordOrder { get; init; } = Array.Empty<string>();
    public int RoundCursor { get; init; }
    public int CurrentTurnInRound { get; init; }
    public int RoundsCompleted { get; init; }
    public int WordsMastered { get; init; }
    public int TotalTurns { get; init; }
    public int CorrectCount { get; init; }
    public IReadOnlyList<string> SessionItemsWordIds { get; init; } = Array.Empty<string>();
    public bool PromptUsesNativeLanguage { get; init; }
    public string UserMode { get; init; } = string.Empty;

    public static string BuildLaunchContextKey(
        string? planItemId,
        IEnumerable<string>? focusVocabularyIds,
        IEnumerable<string>? resourceIds,
        bool dueOnly,
        string? skillId)
    {
        var focusCsv = NormalizeIds(focusVocabularyIds);
        var resourceCsv = NormalizeIds(resourceIds);
        return $"plan={NormalizeValue(planItemId)}|focus={focusCsv}|res={resourceCsv}|due={dueOnly.ToString().ToLowerInvariant()}|skill={NormalizeValue(skillId)}";
    }

    public string Serialize() => Serialize(this);

    public static string Serialize(VocabQuizSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static VocabQuizSessionSnapshot Deserialize(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            throw new JsonException("Vocab quiz session state JSON is empty.");
        }

        return JsonSerializer.Deserialize<VocabQuizSessionSnapshot>(stateJson, JsonOptions)
            ?? throw new JsonException("Vocab quiz session state JSON did not contain a snapshot.");
    }

    private static string NormalizeIds(IEnumerable<string>? ids)
    {
        return string.Join(
            ',',
            (ids ?? Array.Empty<string>())
                .Select(id => id.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    private static string NormalizeValue(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record VocabQuizBatchItemSnapshot
{
    public string WordId { get; init; } = string.Empty;
    public int SessionCorrectCount { get; init; }
    public int SessionMCCorrect { get; init; }
    public int SessionTextCorrect { get; init; }
    public bool PendingRecognitionCheck { get; init; }
    public bool LostKnownThisSession { get; init; }
    public bool WasCorrectThisSession { get; init; }
    public bool IsDueOnlySession { get; init; }
    public bool RequiresFullSessionDemonstration { get; init; }
    public bool UseKnownWordShortcut { get; init; }
    public int RecognitionDemonstrationsBaseline { get; init; }
    public int ProductionDemonstrationsBaseline { get; init; }
}
