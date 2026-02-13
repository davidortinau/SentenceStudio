namespace SentenceStudio.Services;

/// <summary>
/// Shared communication bridge between native MAUI footer and Blazor Writing component.
/// Registered as a singleton so both layers share the same instance.
/// Similar to TranslationBridge but adapted for Writing activity workflow.
/// </summary>
public class WritingBridge
{
    // ── Native → Blazor ──

    public event Action<string>? OnGradeRequested;
    public event Action? OnTranslateRequested;

    public void RequestGrade(string userInput) => OnGradeRequested?.Invoke(userInput);
    public void RequestTranslate() => OnTranslateRequested?.Invoke();

    // ── Blazor → Native ──

    public event Action<bool>? OnGradingStateChanged;
    public event Action<List<string>>? OnVocabBlocksChanged;
    public event Action? OnInputClearRequested;
    public event Action<bool>? OnContentReadyChanged;

    public void NotifyGradingState(bool isGrading) => OnGradingStateChanged?.Invoke(isGrading);
    public void NotifyVocabBlocks(List<string> blocks) => OnVocabBlocksChanged?.Invoke(blocks);
    public void NotifyClearInput() => OnInputClearRequested?.Invoke();
    public void NotifyContentReady(bool ready) => OnContentReadyChanged?.Invoke(ready);

    /// <summary>
    /// Appends a vocab block word to the current input.
    /// Native layer listens and appends to the Entry.
    /// </summary>
    public event Action<string>? OnVocabBlockAppend;
    public void AppendVocabBlock(string word) => OnVocabBlockAppend?.Invoke(word);
}
