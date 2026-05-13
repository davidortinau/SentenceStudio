using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

public record NumberItem(
    Guid Id,
    string ContextCode,
    string SubModeCode,
    string? CounterId,
    string? CounterText,
    NumberSystem System,
    string Bucket,
    long DigitValue,
    string CanonicalAnswer,
    string DisplayPrompt,
    string AudioCue,
    List<string> Hints,
    List<string> AcceptableAlternates,
    Dictionary<string, string>? ErrorClassHints = null,
    // Always-populated digit-form representation for reinforcement display
    // (e.g., "47 잔", "3:45", "23살", "15,000원", "10월 5일", "3째").
    // Independent of SubMode: useful in the answer screen to show learners
    // both the digit form and the Korean word form side-by-side.
    string DigitDisplay = "",
    // TapTheCounter-specific properties
    string? NounCue = null,
    List<string>? CounterChoices = null,
    // Disambiguate-specific properties (paired prompts)
    string? PromptA = null,
    string? PromptB = null,
    string? CorrectAnswerA = null,
    string? CorrectAnswerB = null,
    List<string>? ChoicesA = null,
    List<string>? ChoicesB = null,
    string? HintA = null,
    string? HintB = null,
    string? AudioCueA = null,
    string? AudioCueB = null
);
