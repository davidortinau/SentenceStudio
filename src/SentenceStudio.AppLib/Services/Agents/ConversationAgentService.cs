using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using Scriban;

namespace SentenceStudio.Services.Agents;

/// <summary>
/// Multi-agent service that orchestrates conversation partner and grading agents.
/// Implements parallel agent execution with shared memory context.
/// </summary>
public class ConversationAgentService : IConversationAgentService
{
    private readonly IChatClient _chatClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly UserProfileRepository _userProfileRepository;
    private readonly VocabularyLookupTool _vocabularyTool;
    private readonly ILogger<ConversationAgentService> _logger;

    private ConversationMemory? _currentMemory;
    private AIAgent? _conversationAgent;
    private AIAgent? _gradingAgent;
    private AgentThread? _conversationThread;
    private string _targetLanguage = "Korean";

    // Default persona name
    private const string DefaultPersonaName = "김철수";

    public ConversationAgentService(
        IChatClient chatClient,
        IServiceProvider serviceProvider,
        UserProfileRepository userProfileRepository,
        VocabularyLookupTool vocabularyTool,
        ILogger<ConversationAgentService> logger)
    {
        _chatClient = chatClient;
        _serviceProvider = serviceProvider;
        _userProfileRepository = userProfileRepository;
        _vocabularyTool = vocabularyTool;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> StartConversationAsync(ConversationScenario? scenario = null)
    {
        _logger.LogInformation("Starting conversation with scenario: {Scenario}", scenario?.Name ?? "default");

        // Initialize fresh memory for new conversation
        _currentMemory = new ConversationMemory(_chatClient, logger: _logger);

        // Build the conversation partner agent
        var systemPrompt = await GetConversationPartnerPromptAsync(scenario);
        
        _conversationAgent = _chatClient.AsAIAgent(
            instructions: systemPrompt,
            name: "ConversationPartner",
            tools: [_vocabularyTool.CreateFunction()]);

        // Create grading agent (no vocabulary tool - just evaluates)
        var gradingPrompt = await GetGradingAgentPromptAsync();
        
        _gradingAgent = _chatClient.AsAIAgent(
            instructions: gradingPrompt,
            name: "GradingAgent");

        // Get new thread for conversation
        _conversationThread = await _conversationAgent.GetNewThreadAsync();

        // Generate opening message
        var openingPrompt = await GetOpeningPromptAsync(scenario);
        var response = await _conversationAgent.RunAsync(openingPrompt, _conversationThread);

        return response.ToString();
    }

    /// <inheritdoc/>
    public async Task<Reply> ContinueConversationAsync(
        string userMessage,
        List<ConversationChunk> conversationHistory,
        ConversationScenario? scenario = null)
    {
        _logger.LogInformation("Continuing conversation, history count: {Count}", conversationHistory.Count);

        // Ensure agents are initialized
        if (_conversationAgent == null || _gradingAgent == null)
        {
            _logger.LogWarning("Agents not initialized, starting new conversation");
            await StartConversationAsync(scenario);
        }

        // Build context from conversation history
        var contextMessages = BuildContextMessages(conversationHistory);
        contextMessages.Add(new ChatMessage(ChatRole.User, userMessage));

        // Run both agents in parallel
        var conversationTask = RunConversationAgentAsync(contextMessages);
        var gradingTask = RunGradingAgentAsync(userMessage, conversationHistory);

        await Task.WhenAll(conversationTask, gradingTask);

        var conversationResponse = await conversationTask;
        var gradeResult = await gradingTask;

        // Combine results into Reply
        return new Reply
        {
            Message = conversationResponse,
            Comprehension = gradeResult.ComprehensionScore,
            ComprehensionNotes = gradeResult.ComprehensionNotes,
            GrammarCorrections = gradeResult.GrammarCorrections
        };
    }

    /// <inheritdoc/>
    public async Task LoadMemoryStateAsync(int conversationId)
    {
        _logger.LogDebug("Loading memory state for conversation {ConversationId}", conversationId);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var memoryState = await db.Set<ConversationMemoryState>()
                .FirstOrDefaultAsync(m => m.ConversationId == conversationId);

            if (memoryState != null)
            {
                _currentMemory = ConversationMemory.FromSerializedString(
                    memoryState.SerializedState,
                    _chatClient,
                    _logger);

                _logger.LogDebug("Loaded memory state with {TurnCount} turns", 
                    _currentMemory.MemoryInfo.TurnCount);
            }
            else
            {
                _currentMemory = new ConversationMemory(_chatClient, logger: _logger);
                _logger.LogDebug("Created new memory state for conversation {ConversationId}", conversationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load memory state for conversation {ConversationId}", conversationId);
            _currentMemory = new ConversationMemory(_chatClient, logger: _logger);
        }
    }

    /// <inheritdoc/>
    public async Task SaveMemoryStateAsync(int conversationId)
    {
        if (_currentMemory == null)
        {
            _logger.LogWarning("No memory to save for conversation {ConversationId}", conversationId);
            return;
        }

        _logger.LogDebug("Saving memory state for conversation {ConversationId}", conversationId);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existingState = await db.Set<ConversationMemoryState>()
                .FirstOrDefaultAsync(m => m.ConversationId == conversationId);

            if (existingState != null)
            {
                existingState.SerializedState = _currentMemory.GetSerializedStateString();
                existingState.ConversationSummary = _currentMemory.MemoryInfo.ConversationSummary;
                existingState.DiscussedVocabulary = string.Join(",", _currentMemory.MemoryInfo.DiscussedVocabulary);
                existingState.DetectedProficiencyLevel = _currentMemory.MemoryInfo.UserProficiencyLevel;
                existingState.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                var newState = new ConversationMemoryState
                {
                    ConversationId = conversationId,
                    SerializedState = _currentMemory.GetSerializedStateString(),
                    ConversationSummary = _currentMemory.MemoryInfo.ConversationSummary,
                    DiscussedVocabulary = string.Join(",", _currentMemory.MemoryInfo.DiscussedVocabulary),
                    DetectedProficiencyLevel = _currentMemory.MemoryInfo.UserProficiencyLevel
                };
                db.Set<ConversationMemoryState>().Add(newState);
            }

            await db.SaveChangesAsync();
            _logger.LogDebug("Saved memory state for conversation {ConversationId}", conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save memory state for conversation {ConversationId}", conversationId);
        }
    }

    /// <inheritdoc/>
    public ConversationMemory? GetCurrentMemory() => _currentMemory;

    #region Private Helper Methods

    private async Task<string> RunConversationAgentAsync(List<ChatMessage> messages)
    {
        try
        {
            if (_conversationAgent == null || _conversationThread == null)
            {
                _logger.LogError("Conversation agent or thread not initialized");
                return "죄송합니다. 오류가 발생했습니다.";
            }

            var response = await _conversationAgent.RunAsync(messages, _conversationThread);
            return response.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running conversation agent");
            return "죄송합니다. 오류가 발생했습니다.";
        }
    }

    private async Task<GradeResult> RunGradingAgentAsync(string userMessage, List<ConversationChunk> history)
    {
        try
        {
            if (_gradingAgent == null)
            {
                _logger.LogWarning("Grading agent not initialized");
                return new GradeResult { ComprehensionScore = 0.5 };
            }

            // Build grading context with conversation history
            var gradingPrompt = BuildGradingPrompt(userMessage, history);
            var gradingInstructions = await GetGradingAgentPromptAsync();

            // Use structured output for grading
            var response = await _chatClient.GetResponseAsync<GradeResult>(
                gradingPrompt,
                new ChatOptions
                {
                    Instructions = gradingInstructions
                });

            if (response?.Result != null)
            {
                _logger.LogDebug("Grading complete: Score={Score}, Corrections={Count}", 
                    response.Result.ComprehensionScore, 
                    response.Result.GrammarCorrections?.Count ?? 0);
            }

            return response?.Result ?? new GradeResult { ComprehensionScore = 0.5 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running grading agent");
            return new GradeResult
            {
                ComprehensionScore = 0.5,
                ComprehensionNotes = $"Unable to grade response due to an error: {ex.Message}"
            };
        }
    }

    private List<ChatMessage> BuildContextMessages(List<ConversationChunk> history)
    {
        var messages = new List<ChatMessage>();

        foreach (var chunk in history.OrderBy(c => c.SentTime))
        {
            var role = chunk.Role == ConversationRole.User ? ChatRole.User : ChatRole.Assistant;
            messages.Add(new ChatMessage(role, chunk.Text ?? string.Empty));
        }

        return messages;
    }

    private string BuildGradingPrompt(string userMessage, List<ConversationChunk> history)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("Conversation context:");
        foreach (var chunk in history.TakeLast(6))
        {
            var speaker = chunk.Role == ConversationRole.User ? "User" : "Partner";
            sb.AppendLine($"{speaker}: {chunk.Text}");
        }

        sb.AppendLine();
        sb.AppendLine($"User's latest message to grade: {userMessage}");

        return sb.ToString();
    }

    private async Task<string> GetConversationPartnerPromptAsync(ConversationScenario? scenario)
    {
        string targetLanguage = _targetLanguage;

        if (scenario != null)
        {
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Conversation.scenario.scriban-txt");
            using var reader = new StreamReader(templateStream);
            var template = Template.Parse(await reader.ReadToEndAsync());

            return await template.RenderAsync(new
            {
                target_language = targetLanguage,
                scenario = new
                {
                    persona_name = scenario.PersonaName,
                    persona_description = scenario.PersonaDescription,
                    situation_description = scenario.SituationDescription,
                    conversation_type = scenario.ConversationType.ToString(),
                    question_bank = scenario.QuestionBank
                }
            });
        }

        using Stream defaultStream = await FileSystem.OpenAppPackageFileAsync("Conversation.system.scriban-txt");
        using var defaultReader = new StreamReader(defaultStream);
        var defaultTemplate = Template.Parse(await defaultReader.ReadToEndAsync());
        return await defaultTemplate.RenderAsync(new { name = DefaultPersonaName, target_language = targetLanguage });
    }

    private async Task<string> GetGradingAgentPromptAsync()
    {
        string targetLanguage = _targetLanguage;

        return $@"You are a {targetLanguage} language grading assistant. Your job is to:

1. Evaluate the user's {targetLanguage} message for comprehension (0.0 to 1.0 scale):
   - 1.0: Perfect, native-like communication
   - 0.8-0.9: Minor errors but message is clear
   - 0.6-0.7: Understandable with some effort
   - 0.4-0.5: Partially understandable
   - 0.2-0.3: Difficult to understand
   - 0.0-0.1: Not understandable

2. Provide brief comprehension notes explaining what worked well or what was unclear.

3. Identify grammar corrections:
   - Find grammar mistakes in the user's {targetLanguage}
   - Provide the corrected version
   - Explain the grammar rule simply

Focus on being helpful and encouraging while providing accurate corrections.
Only identify significant errors that affect meaning or are common learner mistakes.
Do not nitpick minor stylistic preferences.";
    }

    private async Task<string> GetOpeningPromptAsync(ConversationScenario? scenario)
    {
        string targetLanguage = _targetLanguage;

        if (scenario != null)
        {
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("StartConversation.scenario.scriban-txt");
            using var reader = new StreamReader(templateStream);
            var template = Template.Parse(await reader.ReadToEndAsync());

            return await template.RenderAsync(new
            {
                target_language = targetLanguage,
                scenario = new
                {
                    persona_name = scenario.PersonaName,
                    persona_description = scenario.PersonaDescription,
                    situation_description = scenario.SituationDescription,
                    conversation_type = scenario.ConversationType.ToString()
                }
            });
        }

        using Stream defaultStream = await FileSystem.OpenAppPackageFileAsync("StartConversation.scriban-txt");
        using var defaultReader = new StreamReader(defaultStream);
        var defaultTemplate = Template.Parse(await defaultReader.ReadToEndAsync());
        return await defaultTemplate.RenderAsync(new { target_language = targetLanguage });
    }

    #endregion

    #region Database Operations

    /// <inheritdoc/>
    public async Task<Conversation> ResumeConversationAsync(string language)
    {
        _targetLanguage = language;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = await db.Conversations
            .Include(c => c.Chunks)
            .Where(c => c.Language == language)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (conversation != null)
        {
            _logger.LogDebug("Resumed {Language} conversation {Id} with {Count} chunks", language, conversation.Id, conversation.Chunks?.Count ?? 0);
        }

        return conversation ?? new Conversation { CreatedAt = DateTime.Now, Language = language };
    }

    /// <inheritdoc/>
    public Task<Conversation> ResumeConversationAsync()
    {
        return ResumeConversationAsync(_targetLanguage);
    }

    /// <inheritdoc/>
    public async Task<int> SaveConversationAsync(Conversation conversation)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (conversation.Id == 0)
        {
            db.Conversations.Add(conversation);
        }
        else
        {
            db.Conversations.Update(conversation);
        }

        await db.SaveChangesAsync();
        
        // Trigger sync if available
        var syncService = scope.ServiceProvider.GetService<ISyncService>();
        if (syncService != null)
        {
            _ = syncService.TriggerSyncAsync();
        }

        return conversation.Id;
    }

    /// <inheritdoc/>
    public async Task SaveConversationChunkAsync(ConversationChunk chunk)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (chunk.Id == 0)
        {
            db.ConversationChunks.Add(chunk);
        }
        else
        {
            db.ConversationChunks.Update(chunk);
        }

        await db.SaveChangesAsync();

        // Trigger sync if available
        var syncService = scope.ServiceProvider.GetService<ISyncService>();
        if (syncService != null)
        {
            _ = syncService.TriggerSyncAsync();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteConversationAsync(Conversation conversation)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Delete all chunks first
        var chunks = await db.ConversationChunks
            .Where(c => c.ConversationId == conversation.Id)
            .ToListAsync();
        
        db.ConversationChunks.RemoveRange(chunks);
        db.Conversations.Remove(conversation);
        
        await db.SaveChangesAsync();

        // Trigger sync if available
        var syncService = scope.ServiceProvider.GetService<ISyncService>();
        if (syncService != null)
        {
            _ = syncService.TriggerSyncAsync();
        }
    }

    /// <inheritdoc/>
    public async Task<List<Conversation>> GetAllConversationsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Conversations
            .Include(c => c.Chunks)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<Conversation?> GetConversationAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Conversations
            .Include(c => c.Chunks)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <inheritdoc/>
    public async Task<List<ConversationChunk>> GetConversationChunksAsync(int conversationId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ConversationChunks
            .Where(c => c.ConversationId == conversationId)
            .OrderBy(c => c.SentTime)
            .ToListAsync();
    }

    #endregion
}
