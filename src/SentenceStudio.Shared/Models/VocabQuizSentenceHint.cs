namespace SentenceStudio.Shared.Models;

/// <summary>
/// Target-language-only sentence content safe to expose as a Vocab Quiz hint.
/// </summary>
public sealed record VocabQuizSentenceHint(
    int ExampleSentenceId,
    string VocabularyWordId,
    string TargetSentence);
