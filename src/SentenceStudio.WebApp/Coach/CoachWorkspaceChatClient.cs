using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.WebApp.Coach;

/// <summary>
/// Adds the application's turn lifecycle to the AG-UI transport used by the workspace. Only the
/// latest user message is submitted; UI history never replaces the server's trusted context.
/// </summary>
public sealed class CoachWorkspaceChatClient(CoachWorkspaceState workspace) : IChatClient
{
    private ChatMessage? _lastMessage;
    private string? _lastEntryId;
    private bool _retryPending;
    private bool TurnFailed => workspace.State is CoachUiState.Failed or CoachUiState.Offline or CoachUiState.Limited
        or CoachUiState.InputTooLong or CoachUiState.Expired or CoachUiState.Incomplete;
    internal bool HasRecoveredTurn => _retryPending && _lastEntryId == workspace.EntryId && !TurnFailed;
    public bool IsSending { get; private set; }
    public event Action? HistoryRestored;

    internal void NotifyHistoryRestored() => HistoryRestored?.Invoke();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var message = messages.LastOrDefault();
        if (message is null || message.Role != ChatRole.User || string.IsNullOrWhiteSpace(message.Text))
        {
            throw new ArgumentException("A coach run requires a user text message.", nameof(messages));
        }
        if (_retryPending && ReferenceEquals(message, _lastMessage)
            && (_lastEntryId != workspace.EntryId || !TurnFailed))
        {
            throw new InvalidOperationException("The failed coach turn is no longer current. Use the current conversation's recovery controls.");
        }
        if (!workspace.CanSubmit)
        {
            throw new InvalidOperationException("The coach cannot accept a turn in its current state.");
        }
        if (message.Text.Length > workspace.MaxDraftLength)
        {
            throw new ArgumentException("The coach message exceeds the input limit.", nameof(messages));
        }

        var prior = workspace.Timeline.Select(CoachConversationThread.EntryId).ToHashSet(StringComparer.Ordinal);
        var entryId = workspace.EntryId;
        IsSending = true;
        try
        {
            if (_retryPending && _lastEntryId == entryId && ReferenceEquals(message, _lastMessage))
            {
                await workspace.RetryLastAsync(cancellationToken);
            }
            else
            {
                _lastMessage = message;
                _lastEntryId = entryId;
                _retryPending = true;
                workspace.Draft = message.Text;
                await workspace.SendDraftAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (entryId != workspace.EntryId || TurnFailed)
            {
                throw new InvalidOperationException("The coach turn did not complete. Use the recovery controls.");
            }
            _retryPending = false;
            foreach (var entry in workspace.Timeline)
            {
                if (entry.Kind != CoachTimelineKind.LearnerMessage
                    && !prior.Contains(CoachConversationThread.EntryId(entry)))
                {
                    yield return CoachConversationThread.ToUpdate(entry);
                }
            }
        }
        finally
        {
            IsSending = false;
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken: cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
