using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Services;

/// <summary>
/// What a duplicate decision changed, kept so the learner can undo it (stored as JSON in
/// <see cref="SentenceStudio.Shared.Models.VocabularyDuplicateDecision.Before"/>; contract in
/// docs/sync-all-tables-plan.md, "Duplicate decisions"). A merge holds every copy it took (the
/// keeper and the copies merged into it) as they were before; a kept-apart decision holds the
/// copies whose meaning or language it clarified.
/// </summary>
public sealed record VocabularyDuplicateUndoRecord(IReadOnlyList<VocabularyDuplicateWordSnapshot> Words)
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
