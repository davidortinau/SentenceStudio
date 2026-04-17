using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Plugin.Maui.HelpKit.Localization;

namespace Plugin.Maui.HelpKit.Ui;

/// <summary>
/// View-model for <see cref="HelpKitPage"/>. Orchestrates stream
/// consumption and exposes commands bound to the chat chrome.
/// </summary>
internal sealed class HelpKitPageViewModel : INotifyPropertyChanged
{
    private readonly IHelpKit _helpKit;
    private readonly HelpKitLocalizer _loc;
    private readonly ILogger<HelpKitPageViewModel>? _logger;

    private string _draft = string.Empty;
    private bool _isStreaming;
    private string? _conversationId;
    private CancellationTokenSource? _streamCts;

    public HelpKitPageViewModel(
        IHelpKit helpKit,
        HelpKitLocalizer loc,
        ILogger<HelpKitPageViewModel>? logger = null)
    {
        _helpKit = helpKit ?? throw new ArgumentNullException(nameof(helpKit));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _logger = logger;

        SendCommand = new Command(
            execute: async () => await SendAsync().ConfigureAwait(false),
            canExecute: () => !IsStreaming && !string.IsNullOrWhiteSpace(Draft));

        ClearCommand = new Command(
            execute: async () => await ClearAsync().ConfigureAwait(false),
            canExecute: () => !IsStreaming);

        CloseCommand = new Command(
            execute: async () => await CloseAsync().ConfigureAwait(false));
    }

    public ObservableCollection<HelpKitMessageViewModel> Messages { get; } = new();

    public string Draft
    {
        get => _draft;
        set
        {
            if (_draft == value) return;
            _draft = value ?? string.Empty;
            OnPropertyChanged();
            RaiseCanExecuteChanged();
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (_isStreaming == value) return;
            _isStreaming = value;
            OnPropertyChanged();
            RaiseCanExecuteChanged();
        }
    }

    public ICommand SendCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CloseCommand { get; }

    /// <summary>
    /// Raised when a new assistant message is appended and the view should
    /// scroll to it. The message passed is the one to scroll to.
    /// </summary>
    public event EventHandler<HelpKitMessageViewModel>? MessageAdded;

    public async Task SendAsync()
    {
        var text = Draft?.Trim();
        if (string.IsNullOrEmpty(text) || IsStreaming)
            return;

        Draft = string.Empty;

        var userMsg = new HelpKitMessageViewModel(HelpKitMessageViewModel.Kind.User, text);
        Messages.Add(userMsg);
        MessageAdded?.Invoke(this, userMsg);

        var assistantMsg = new HelpKitMessageViewModel(
            HelpKitMessageViewModel.Kind.Assistant,
            _loc.Get("HelpKit.Streaming"));
        Messages.Add(assistantMsg);
        MessageAdded?.Invoke(this, assistantMsg);

        IsStreaming = true;
        _streamCts = new CancellationTokenSource();

        try
        {
            var first = true;
            await foreach (var snapshot in _helpKit
                .StreamAskAsync(text, _conversationId, _streamCts.Token)
                .ConfigureAwait(true))
            {
                if (first)
                {
                    assistantMsg.Content = snapshot.Content ?? string.Empty;
                    first = false;
                }
                else
                {
                    assistantMsg.Content = snapshot.Content ?? assistantMsg.Content;
                }

                if (snapshot.Citations is { Count: > 0 })
                    assistantMsg.Citations = snapshot.Citations;
            }

            if (first)
            {
                assistantMsg.Content = _loc.Get("HelpKit.NoDocumentation");
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("HelpKit stream cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "HelpKit stream failed.");
            Messages.Remove(assistantMsg);
            var err = new HelpKitMessageViewModel(
                HelpKitMessageViewModel.Kind.Error,
                BuildErrorCopy(ex));
            Messages.Add(err);
            MessageAdded?.Invoke(this, err);
        }
        finally
        {
            IsStreaming = false;
            _streamCts?.Dispose();
            _streamCts = null;
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await _helpKit.ClearHistoryAsync().ConfigureAwait(true);
        }
        catch (NotImplementedException)
        {
            // History persistence is wired in a later wave; still clear the
            // in-memory visible transcript so the button feels responsive.
            _logger?.LogDebug("ClearHistoryAsync not implemented yet; clearing view-model only.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ClearHistoryAsync failed.");
        }

        Messages.Clear();
        _conversationId = null;
    }

    public Task CloseAsync() => _helpKit.HideAsync();

    public void CancelStreaming() => _streamCts?.Cancel();

    private string BuildErrorCopy(Exception ex)
    {
        // Loosely detect the Wave-2 RateLimit exception by name without a
        // hard type dependency (Wash owns the type).
        var name = ex.GetType().Name;
        if (name.Contains("RateLimit", StringComparison.OrdinalIgnoreCase))
            return _loc.Get("HelpKit.RateLimitExceeded");

        return _loc.Get("HelpKit.ErrorGeneric");
    }

    private void RaiseCanExecuteChanged()
    {
        (SendCommand as Command)?.ChangeCanExecute();
        (ClearCommand as Command)?.ChangeCanExecute();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
