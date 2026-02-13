namespace SentenceStudio.Services;

/// <summary>
/// Shared communication bridge between native MAUI footer and Blazor Translation component.
/// Registered as a singleton so both layers share the same instance.
/// </summary>
public class TranslationBridge
{
    // ── Native → Blazor ──

    public event Action<string>? OnGradeRequested;
    public event Action? OnNextRequested;
    public event Action? OnPreviousRequested;
    public event Action? OnToggleInputModeRequested;

    public void RequestGrade(string userInput) => OnGradeRequested?.Invoke(userInput);
    public void RequestNext() => OnNextRequested?.Invoke();
    public void RequestPrevious() => OnPreviousRequested?.Invoke();
    public void RequestToggleInputMode() => OnToggleInputModeRequested?.Invoke();

    // ── Blazor → Native ──

    public event Action<int, int>? OnProgressChanged;
    public event Action<bool>? OnGradingStateChanged;
    public event Action<List<string>>? OnVocabBlocksChanged;
    public event Action<string>? OnInputModeChanged;
    public event Action<bool>? OnCanGoPreviousChanged;
    public event Action? OnInputClearRequested;
    public event Action<bool>? OnContentReadyChanged;

    public void NotifyProgress(int current, int total) => OnProgressChanged?.Invoke(current, total);
    public void NotifyGradingState(bool isGrading) => OnGradingStateChanged?.Invoke(isGrading);
    public void NotifyVocabBlocks(List<string> blocks) => OnVocabBlocksChanged?.Invoke(blocks);
    public void NotifyInputMode(string mode) => OnInputModeChanged?.Invoke(mode);
    public void NotifyCanGoPrevious(bool canGo) => OnCanGoPreviousChanged?.Invoke(canGo);
    public void NotifyClearInput() => OnInputClearRequested?.Invoke();
    public void NotifyContentReady(bool ready) => OnContentReadyChanged?.Invoke(ready);

    /// <summary>
    /// Appends a vocab block word to the current input.
    /// Native layer listens and appends to the Entry.
    /// </summary>
    public event Action<string>? OnVocabBlockAppend;
    public void AppendVocabBlock(string word) => OnVocabBlockAppend?.Invoke(word);
}
