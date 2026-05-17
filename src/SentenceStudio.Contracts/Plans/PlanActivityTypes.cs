namespace SentenceStudio.Contracts.Plans;

/// <summary>
/// Canonical wire-format strings for the ActivityType field on PlanItemDto.
/// Must stay byte-identical to the server's PlanActivityType enum so clients
/// can do string-based mapping safely.
/// </summary>
public static class PlanActivityTypes
{
    public const string VocabularyReview  = "VocabularyReview";
    public const string Reading           = "Reading";
    public const string Listening         = "Listening";
    public const string VideoWatching     = "VideoWatching";
    public const string Shadowing         = "Shadowing";
    public const string Cloze             = "Cloze";
    public const string Translation       = "Translation";
    public const string Writing           = "Writing";
    public const string SceneDescription  = "SceneDescription";
    public const string Conversation      = "Conversation";
    public const string VocabularyGame    = "VocabularyGame";
    public const string NumberDrill       = "NumberDrill";
}
