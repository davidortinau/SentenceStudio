using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.WebApp.Coach;

/// <summary>Projects the existing owned ledger; the UI library must not become a second history writer.</summary>
public sealed class CoachConversationThread(CoachWorkspaceState workspace) : IConversationThread
{
    public string ThreadId => workspace.EntryId ?? "new";
    public bool IsStateful => true;
    public string? ConversationId => workspace.ConversationId ?? workspace.SessionId;

    public void AppendUserMessage(ChatMessage message) { }
    public void AppendUpdate(ChatResponseUpdate update) { }
    public void CompleteTurn() { }

    public IReadOnlyList<ChatResponseUpdate> GetUpdates() =>
        workspace.Timeline.Select(ToUpdate).ToArray();

    internal static ChatResponseUpdate ToUpdate(CoachTimelineEntry entry) => new()
    {
        Role = entry.Message?.Role == SentenceStudio.Contracts.Coach.CoachMessageRole.Learner
            || entry.Kind == CoachTimelineKind.LearnerMessage ? ChatRole.User : ChatRole.Assistant,
        MessageId = EntryId(entry),
        Contents = [new CoachTimelineContent(entry)]
    };

    internal static string EntryId(CoachTimelineEntry entry) =>
        entry.MessageId ?? entry.Message?.MessageId ?? $"artifact:{entry.Kind}:{entry.TurnSequence}:{entry.Sequence}";
}
