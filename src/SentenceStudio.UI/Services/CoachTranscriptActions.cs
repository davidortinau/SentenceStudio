namespace SentenceStudio.WebUI.Services;

public sealed record CoachTranscriptActions(
    Func<Task> BeginViewportChange,
    Func<Task> EndViewportChange,
    Func<string, Task> ReturnFocus,
    Func<Task> ViewPlan);
