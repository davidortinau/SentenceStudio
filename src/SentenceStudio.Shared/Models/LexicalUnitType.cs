namespace SentenceStudio.Shared.Models;

/// <summary>
/// Classifies a vocabulary entry by linguistic scope.
/// </summary>
public enum LexicalUnitType
{
    /// <summary>Entry type not yet determined or unclear.</summary>
    Unknown = 0,

    /// <summary>Single word or morpheme (e.g., "책", "읽다").</summary>
    Word = 1,

    /// <summary>Multi-word phrase or collocation that functions as a unit (e.g., "한국 음식", "비가 오다").</summary>
    Phrase = 2,

    /// <summary>Complete grammatical sentence (subject + predicate).</summary>
    Sentence = 3,

    /// <summary>Idiomatic expression whose meaning cannot be deduced from its parts (e.g., "하나부터 열까지", "영혼 있어요?").</summary>
    Idiom = 4
}
