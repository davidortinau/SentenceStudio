using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Agents;

/// <summary>
/// Service interface for multi-agent conversation handling.
/// Orchestrates conversation partner and grading agents with parallel execution.
/// </summary>
public interface IConversationAgentService
{
    /// <summary>
    /// Starts a new conversation with the given scenario.
    /// </summary>
    /// <param name="scenario">Optional scenario for the conversation.</param>
    /// <returns>The opening message from the conversation partner.</returns>
    Task<string> StartConversationAsync(ConversationScenario? scenario = null);

    /// <summary>
    /// Continues the conversation with the user's message.
    /// Runs conversation partner and grading agents in parallel.
    /// </summary>
    /// <param name="userMessage">The user's Korean input.</param>
    /// <param name="conversationHistory">Previous conversation chunks for context.</param>
    /// <param name="scenario">Active conversation scenario.</param>
    /// <returns>Combined reply with message, comprehension score, and grammar corrections.</returns>
    Task<Reply> ContinueConversationAsync(
        string userMessage,
        List<ConversationChunk> conversationHistory,
        ConversationScenario? scenario = null);

    /// <summary>
    /// Loads memory state for a conversation from persistence.
    /// </summary>
    /// <param name="conversationId">The conversation ID to load memory for.</param>
    Task LoadMemoryStateAsync(int conversationId);

    /// <summary>
    /// Saves the current memory state for a conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID to save memory for.</param>
    Task SaveMemoryStateAsync(int conversationId);

    /// <summary>
    /// Gets the current conversation memory info (for debugging/display).
    /// </summary>
    ConversationMemory? GetCurrentMemory();
}
