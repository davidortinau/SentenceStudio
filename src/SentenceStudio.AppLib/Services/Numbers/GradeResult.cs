namespace SentenceStudio.Services.Numbers;

public record GradeResult(
    bool IsCorrect,
    string Verdict,
    string? ErrorClass,
    string CanonicalAnswer,
    string? UserAnswer,
    string? Tip
);
