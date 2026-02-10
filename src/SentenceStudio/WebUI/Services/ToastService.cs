using Microsoft.JSInterop;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Toast notification service using Bootstrap toasts.
/// Replaces UXDivers Popups Toast for Blazor pages.
/// </summary>
public class ToastService
{
    public event Action<ToastMessage> OnShow;

    public void ShowSuccess(string message, int durationMs = 3000)
        => OnShow?.Invoke(new ToastMessage(message, ToastType.Success, durationMs));

    public void ShowError(string message, int durationMs = 5000)
        => OnShow?.Invoke(new ToastMessage(message, ToastType.Error, durationMs));

    public void ShowWarning(string message, int durationMs = 4000)
        => OnShow?.Invoke(new ToastMessage(message, ToastType.Warning, durationMs));

    public void ShowInfo(string message, int durationMs = 3000)
        => OnShow?.Invoke(new ToastMessage(message, ToastType.Info, durationMs));
}

public record ToastMessage(string Text, ToastType Type, int DurationMs);

public enum ToastType
{
    Success,
    Error,
    Warning,
    Info
}
