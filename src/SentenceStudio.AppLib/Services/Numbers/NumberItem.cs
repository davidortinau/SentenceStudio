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
