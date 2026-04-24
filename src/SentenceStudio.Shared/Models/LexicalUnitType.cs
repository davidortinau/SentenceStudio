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

    /// <summary>Multi-word phrase or idiom that functions as a unit (e.g., "어떻게 지내세요?").</summary>
    Phrase = 2,

    /// <summary>Complete grammatical sentence (subject + predicate).</summary>
    Sentence = 3
}
