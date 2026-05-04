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
    List<string> AcceptableAlternates
);
