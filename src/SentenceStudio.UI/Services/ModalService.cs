namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Modal dialog service using Bootstrap modals.
/// Replaces UXDivers Popups for Blazor pages.
/// </summary>
public class ModalService
{
    public event Func<ModalRequest, Task<ModalResult>> OnShowConfirm;
    public event Func<ModalRequest, Task<string>> OnShowPrompt;
    public event Func<ActionSheetRequest, Task<string>> OnShowActionSheet;

    public async Task<ModalResult> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel")
    {
        if (OnShowConfirm != null)
            return await OnShowConfirm.Invoke(new ModalRequest(title, message, confirmText, cancelText));
        return ModalResult.Cancelled;
    }

    public async Task<string> PromptAsync(string title, string message, string placeholder = "", string initialValue = "")
    {
        if (OnShowPrompt != null)
            return await OnShowPrompt.Invoke(new ModalRequest(title, message, placeholder, initialValue));
        return null;
    }

    public async Task<string> ShowActionSheetAsync(string title, string[] options, string cancelText = "Cancel")
    {
        if (OnShowActionSheet != null)
            return await OnShowActionSheet.Invoke(new ActionSheetRequest(title, options, cancelText));
        return null;
    }
}

public record ModalRequest(string Title, string Message, string PrimaryAction, string SecondaryAction);
public record ActionSheetRequest(string Title, string[] Options, string CancelText);

public enum ModalResult
{
    Confirmed,
    Cancelled
}
