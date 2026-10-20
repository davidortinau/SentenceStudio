namespace SentenceStudio.Native.iOS.Features.Quiz;

internal sealed record QuizViewInputs(
    string Answer,
    bool CorrectionCompleted,
    bool Busy,
    bool CanRetry,
    bool AnswerShouldFocus,
    string SentenceInput);
