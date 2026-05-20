using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SentenceStudio.Api.Conversation;
using SentenceStudio.Contracts.Conversation;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Stub implementation of <see cref="IServerConversationService"/> used by the
/// conversation endpoint tests so they never reach an LLM. Tests can inspect
/// the last call's arguments and set canned responses.
/// </summary>
public sealed class StubServerConversationService : IServerConversationService
{
    public ConversationScenario? LastScenario { get; private set; }
    public string? LastUserMessage { get; private set; }
    public List<ConversationHistoryItemDto> LastHistory { get; private set; } = new();
    public string? LastTargetLanguageLabel { get; private set; }

    public string OpeningMessage { get; set; } = "안녕하세요. 만나서 반갑습니다.";

    public ConversationContinueResponse ContinueResponse { get; set; } = new()
    {
        AssistantMessage = "네, 그렇군요.",
        ComprehensionScore = 85,
        ComprehensionNotes = "Clear and natural.",
        GrammarCorrections = new(),
        VocabularyAnalysis = new(),
        IsComplete = false,
    };

    public Task<string> StartAsync(
        ConversationScenario? scenario,
        string targetLanguageLabel,
        CancellationToken cancellationToken)
    {
        LastScenario = scenario;
        LastTargetLanguageLabel = targetLanguageLabel;
        return Task.FromResult(OpeningMessage);
    }

    public Task<ConversationContinueResponse> ContinueAsync(
        string userMessage,
        IReadOnlyList<ConversationHistoryItemDto> conversationHistory,
        ConversationScenario? scenario,
        string targetLanguageLabel,
        CancellationToken cancellationToken)
    {
        LastUserMessage = userMessage;
        LastHistory = new List<ConversationHistoryItemDto>(conversationHistory);
        LastScenario = scenario;
        LastTargetLanguageLabel = targetLanguageLabel;
        return Task.FromResult(ContinueResponse);
    }
}
