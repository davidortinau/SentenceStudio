using System.ComponentModel;

namespace SentenceStudio.Services.Vocabulary;

/// <summary>
/// One classification returned by the model.
/// </summary>
/// <remarks>
/// The <see cref="Id"/> is the opaque vocabulary word id the request supplied. It is echoed back
/// so a batch can be mapped without relying on array order, and every returned id is validated
/// against the requested set before anything is written.
/// </remarks>
public sealed class VocabularyPartOfSpeechClassification
{
    [Description("The exact opaque word id from the request. Echo it back unchanged.")]
    public string Id { get; set; } = string.Empty;

    [Description("One of: noun, verb, adjective, adverb, expression, counter, particle, unknown. Use 'unknown' when you cannot decide.")]
    public string PartOfSpeech { get; set; } = string.Empty;
}

/// <summary>
/// The structured-output envelope for one classification batch.
/// </summary>
public sealed class VocabularyPartOfSpeechClassificationResponse
{
    [Description("Exactly one entry for every word id in the request. No extra ids, no duplicates, no omissions.")]
    public List<VocabularyPartOfSpeechClassification> Classifications { get; set; } = new();
}

/// <summary>
/// The minimal per-word payload sent to the classifier.
/// </summary>
/// <remarks>
/// This is the whole privacy surface of the feature, so it is a named type rather than an
/// anonymous object: adding a field is a visible, reviewable change. It deliberately excludes the
/// native-language gloss, mnemonics, example sentences, transcripts, resource text, tags, and any
/// user or tenant identifier. A classifier needs the target term and its language, nothing more.
/// </remarks>
public sealed record VocabularyPartOfSpeechRequestItem(
    string Id,
    string? Term,
    string? Lemma,
    string? Language,
    string LexicalUnitType);
