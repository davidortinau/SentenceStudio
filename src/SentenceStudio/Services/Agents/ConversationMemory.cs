using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services.Agents;

/// <summary>
/// Memory state that persists conversation context across agent invocations.
/// </summary>
public sealed class ConversationMemoryInfo
{
    public List<string> ConversationTopics { get; set; } = new();
    public List<string> DiscussedVocabulary { get; set; } = new();
    public string? UserProficiencyLevel { get; set; }
    public string? ConversationSummary { get; set; }
    public int TurnCount { get; set; }
}

/// <summary>
/// AIContextProvider that maintains conversation memory and provides context to agents.
/// Implements persistence through JSON serialization for SQLite storage.
/// </summary>
public sealed class ConversationMemory : AIContextProvider
{
    private readonly IChatClient? _chatClient;
    private readonly ILogger? _logger;
    
    public ConversationMemoryInfo MemoryInfo { get; private set; }

    /// <summary>
    /// Creates a new ConversationMemory with optional chat client for summarization.
    /// </summary>
    public ConversationMemory(IChatClient? chatClient = null, ConversationMemoryInfo? memoryInfo = null, ILogger? logger = null)
    {
        _chatClient = chatClient;
        _logger = logger;
        MemoryInfo = memoryInfo ?? new ConversationMemoryInfo();
    }

    /// <summary>
    /// Constructor for deserialization from persisted state.
    /// </summary>
    public ConversationMemory(IChatClient chatClient, JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _chatClient = chatClient;
        MemoryInfo = serializedState.ValueKind == JsonValueKind.Object
            ? serializedState.Deserialize<ConversationMemoryInfo>(jsonSerializerOptions) ?? new ConversationMemoryInfo()
            : new ConversationMemoryInfo();
    }

    /// <summary>
    /// Called before the agent invokes the underlying inference service.
    /// Provides conversation context and memory to enhance the agent's responses.
    /// </summary>
    public override ValueTask<AIContext> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var instructions = new StringBuilder();

        // Add conversation context
        if (!string.IsNullOrEmpty(MemoryInfo.ConversationSummary))
        {
            instructions.AppendLine($"Previous conversation context: {MemoryInfo.ConversationSummary}");
        }

        if (MemoryInfo.ConversationTopics.Any())
        {
            instructions.AppendLine($"Topics discussed so far: {string.Join(", ", MemoryInfo.ConversationTopics)}");
        }

        if (MemoryInfo.DiscussedVocabulary.Any())
        {
            instructions.AppendLine($"Vocabulary words used in conversation: {string.Join(", ", MemoryInfo.DiscussedVocabulary.TakeLast(10))}");
        }

        if (!string.IsNullOrEmpty(MemoryInfo.UserProficiencyLevel))
        {
            instructions.AppendLine($"User's detected Korean proficiency level: {MemoryInfo.UserProficiencyLevel}");
        }

        instructions.AppendLine($"This is turn {MemoryInfo.TurnCount + 1} of the conversation.");

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = instructions.ToString()
        });
    }

    /// <summary>
    /// Called after the agent has received a response from the underlying inference service.
    /// Extracts and stores relevant memories from the conversation.
    /// </summary>
    public override async ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        MemoryInfo.TurnCount++;

        // Extract topics and vocabulary from the conversation if we have a chat client
        if (_chatClient != null)
        {
            try
            {
                var userMessages = context.RequestMessages
                    .Where(m => m.Role == ChatRole.User)
                    .ToList();

                if (userMessages.Any())
                {
                    // Use AI to extract topics and vocabulary from user messages
                    var extractionResult = await _chatClient.GetResponseAsync<MemoryExtractionResult>(
                        context.RequestMessages,
                        new ChatOptions
                        {
                            Instructions = @"Extract from the user's messages:
1. Main topics being discussed (1-3 words each)
2. Korean vocabulary words used by the user
3. Estimated Korean proficiency level (beginner/intermediate/advanced)
Return nulls for any field you cannot determine."
                        },
                        cancellationToken: cancellationToken);

                    if (extractionResult?.Result != null)
                    {
                        var extraction = extractionResult.Result;

                        if (extraction.Topics?.Any() == true)
                        {
                            foreach (var topic in extraction.Topics.Where(t => !string.IsNullOrEmpty(t)))
                            {
                                if (!MemoryInfo.ConversationTopics.Contains(topic!))
                                {
                                    MemoryInfo.ConversationTopics.Add(topic!);
                                }
                            }
                        }

                        if (extraction.VocabularyUsed?.Any() == true)
                        {
                            foreach (var word in extraction.VocabularyUsed.Where(w => !string.IsNullOrEmpty(w)))
                            {
                                if (!MemoryInfo.DiscussedVocabulary.Contains(word!))
                                {
                                    MemoryInfo.DiscussedVocabulary.Add(word!);
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(extraction.ProficiencyLevel))
                        {
                            MemoryInfo.UserProficiencyLevel = extraction.ProficiencyLevel;
                        }
                    }
                }

                // Update conversation summary every 5 turns
                if (MemoryInfo.TurnCount % 5 == 0 && context.RequestMessages.Any())
                {
                    var summaryResult = await _chatClient.GetResponseAsync<string>(
                        context.RequestMessages,
                        new ChatOptions
                        {
                            Instructions = "Summarize the key points of this conversation in 2-3 sentences. Focus on what was discussed and any important information shared."
                        },
                        cancellationToken: cancellationToken);

                    if (!string.IsNullOrEmpty(summaryResult?.Result))
                    {
                        MemoryInfo.ConversationSummary = summaryResult.Result;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ConversationMemory: Failed to extract memories from conversation");
            }
        }
    }

    /// <summary>
    /// Serializes the memory state for persistence.
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return JsonSerializer.SerializeToElement(MemoryInfo, jsonSerializerOptions);
    }

    /// <summary>
    /// Gets the serialized state as a string for database storage.
    /// </summary>
    public string GetSerializedStateString()
    {
        return JsonSerializer.Serialize(MemoryInfo);
    }

    /// <summary>
    /// Creates a ConversationMemory from a serialized state string.
    /// </summary>
    public static ConversationMemory FromSerializedString(string serializedState, IChatClient? chatClient = null, ILogger? logger = null)
    {
        var memoryInfo = string.IsNullOrEmpty(serializedState)
            ? new ConversationMemoryInfo()
            : JsonSerializer.Deserialize<ConversationMemoryInfo>(serializedState) ?? new ConversationMemoryInfo();

        return new ConversationMemory(chatClient, memoryInfo, logger);
    }
}

/// <summary>
/// DTO for AI extraction of conversation memories.
/// </summary>
internal sealed class MemoryExtractionResult
{
    public List<string?>? Topics { get; set; }
    public List<string?>? VocabularyUsed { get; set; }
    public string? ProficiencyLevel { get; set; }
}
