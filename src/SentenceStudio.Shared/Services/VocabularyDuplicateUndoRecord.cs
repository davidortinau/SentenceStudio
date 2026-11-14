using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Services;

/// <summary>
/// What a duplicate decision changed, kept so the learner can undo it (stored as JSON in
/// <see cref="SentenceStudio.Shared.Models.VocabularyDuplicateDecision.Before"/>; contract in
/// docs/sync-all-tables-plan.md, "Duplicate decisions"). <see cref="Words"/> are the copies as
/// they were before: for a merge every copy it took (the keeper and the copies merged into it),
/// for a kept-apart decision the copies whose meaning or language it clarified.
/// <see cref="After"/> are the changed words as the decision left them (the keeper of a merge,
/// the clarified copies): Undo puts a field back only while it still has that value, so an edit
/// the learner made since is kept.
/// </summary>
public sealed record VocabularyDuplicateUndoRecord(IReadOnlyList<VocabularyDuplicateWordSnapshot> Words,
    IReadOnlyList<VocabularyDuplicateWordSnapshot>? After = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The record, or null when <paramref name="json"/> is empty or not one.</summary>
    public static VocabularyDuplicateUndoRecord? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<VocabularyDuplicateUndoRecord>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One word's fields before a duplicate decision changed it, and the learner's resources that
/// held it. <see cref="LexicalUnitType"/> is the enum's name and <see cref="PartOfSpeech"/> the
/// canonical token (<see cref="SentenceStudio.Shared.Models.VocabularyPartOfSpeechTokens"/>).
/// </summary>
public sealed record VocabularyDuplicateWordSnapshot(
    string Id, string? TargetLanguageTerm, string? NativeLanguageTerm, string? Lemma, string? Language,
    string? Tags, string? MnemonicText, string? MnemonicImageUri, string? AudioPronunciationUri,
    string LexicalUnitType, string? PartOfSpeech, DateTime CreatedAt, IReadOnlyList<string> ResourceIds);
