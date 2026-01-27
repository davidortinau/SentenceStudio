using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services
{
    public class ConversationService
    {
        private readonly IServiceProvider _serviceProvider;
        readonly IConfiguration configuration;
        private AiService _aiService;
        private readonly IChatClient _client;
        private readonly string _openAiApiKey;
        private ISyncService _syncService;
        private readonly ILogger<ConversationService> _logger;

        // Default persona name - could be made configurable
        private const string DefaultPersonaName = "김철수";

        public ConversationService(IServiceProvider serviceProvider, IConfiguration configuration, IChatClient chatClient, ILogger<ConversationService> logger)
        {
            _serviceProvider = serviceProvider;
            _aiService = serviceProvider.GetRequiredService<AiService>();
            _client = chatClient;
            this.configuration = configuration;
            _syncService = serviceProvider.GetService<ISyncService>();
            _logger = logger;

            _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
        }

        /// <summary>
        /// Loads and renders the system prompt template with persona configuration.
        /// </summary>
        private async Task<string> GetSystemPromptAsync(string personaName = DefaultPersonaName)
        {
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Conversation.system.scriban-txt");
            using var reader = new StreamReader(templateStream);
            var template = Template.Parse(await reader.ReadToEndAsync());
            return await template.RenderAsync(new { name = personaName });
        }

        /// <summary>
        /// Loads and renders the scenario-specific system prompt template.
        /// </summary>
        private async Task<string> GetScenarioSystemPromptAsync(ConversationScenario scenario)
        {
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Conversation.scenario.scriban-txt");
            using var reader = new StreamReader(templateStream);
            var template = Template.Parse(await reader.ReadToEndAsync());

            return await template.RenderAsync(new
            {
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

        /// <summary>
        /// Builds a chat message list from conversation history with proper roles.
        /// </summary>
        private List<ChatMessage> BuildChatHistory(IEnumerable<ConversationChunk> chunks)
        {
            var messages = new List<ChatMessage>();

            foreach (var chunk in chunks.OrderBy(c => c.SentTime))
            {
                var role = chunk.Role == ConversationRole.User ? ChatRole.User : ChatRole.Assistant;
                messages.Add(new ChatMessage(role, chunk.Text ?? string.Empty));
            }

            return messages;
        }

        public async Task<Conversation> ResumeConversation()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var mostRecentConversation = await db.Conversations
                .Include(c => c.Chunks)
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync();

            return mostRecentConversation;
        }

        public async Task SaveConversationChunk(ConversationChunk chunk)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Set timestamps
            if (chunk.CreatedAt == default)
                chunk.CreatedAt = DateTime.UtcNow;

            try
            {
                if (chunk.Id != 0)
                {
                    db.ConversationChunks.Update(chunk);
                }
                else
                {
                    db.ConversationChunks.Add(chunk);
                }

                await db.SaveChangesAsync();

                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in SaveConversationChunk");
            }
        }

        public async Task<string> StartConversation(ConversationScenario? scenario = null)
        {
            try
            {
                // Build system prompt based on scenario
                string systemPrompt;
                string userPrompt;

                if (scenario != null)
                {
                    systemPrompt = await GetScenarioSystemPromptAsync(scenario);
                    _logger.LogInformation("Starting conversation with scenario: {Name}", scenario.Name);

                    // Use scenario-specific start conversation template
                    using Stream scenarioTemplateStream = await FileSystem.OpenAppPackageFileAsync("StartConversation.scenario.scriban-txt");
                    using var scenarioReader = new StreamReader(scenarioTemplateStream);
                    var scenarioTemplate = Template.Parse(await scenarioReader.ReadToEndAsync());
                    userPrompt = await scenarioTemplate.RenderAsync(new
                    {
                        scenario = new
                        {
                            persona_name = scenario.PersonaName,
                            persona_description = scenario.PersonaDescription,
                            situation_description = scenario.SituationDescription,
                            conversation_type = scenario.ConversationType.ToString()
                        }
                    });
                }
                else
                {
                    systemPrompt = await GetSystemPromptAsync();

                    // Use default start conversation template
                    using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("StartConversation.scriban-txt");
                    using var reader = new StreamReader(templateStream);
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    userPrompt = await template.RenderAsync();
                }

                // Build chat messages with proper roles
                var messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt)
                };

                var response = await _client.GetResponseAsync<string>(messages);
                return response.Result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in StartConversation");
                return string.Empty;
            }
        }

        public async Task<Reply> ContinueConversation(List<ConversationChunk> chunks, ConversationScenario? scenario = null)
        {
            try
            {
                // Use the single-prompt pattern that works with structured output
                // Build a complete prompt string with persona, history, and instructions
                string prompt;

                if (scenario != null)
                {
                    // Use scenario-specific template
                    using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("ContinueConversation.scenario.scriban-txt");
                    using var reader = new StreamReader(templateStream);
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    prompt = await template.RenderAsync(new
                    {
                        scenario = new
                        {
                            persona_name = scenario.PersonaName,
                            persona_description = scenario.PersonaDescription,
                            situation_description = scenario.SituationDescription,
                            conversation_type = scenario.ConversationType.ToString()
                            // question_bank = scenario.QuestionBank
                        },
                        chunks = chunks  // Include ALL chunks including user's latest message
                    });
                }
                else
                {
                    // Use default template (original working pattern)
                    using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("ContinueConversation.scriban-txt");
                    using var reader = new StreamReader(templateStream);
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    prompt = await template.RenderAsync(new { name = DefaultPersonaName, chunks = chunks });  // Include ALL chunks
                }

                var response = await _client.GetResponseAsync<Reply>(prompt);
                return response.Result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in ContinueConversation");
                return new Reply { Message = string.Empty };
            }
        }

        public async Task<int> SaveConversation(Conversation conversation)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Set timestamps
            if (conversation.CreatedAt == default)
                conversation.CreatedAt = DateTime.UtcNow;

            try
            {
                if (conversation.Id != 0)
                {
                    db.Conversations.Update(conversation);
                }
                else
                {
                    db.Conversations.Add(conversation);
                }

                await db.SaveChangesAsync();

                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in SaveConversation");
            }

            return conversation.Id;
        }

        public async Task DeleteConversation(Conversation conversation)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                // EF Core will handle cascade delete of chunks automatically if configured
                db.Conversations.Remove(conversation);
                await db.SaveChangesAsync();

                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in DeleteConversation");
            }
        }

        public async Task<List<Conversation>> GetAllConversationsAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await db.Conversations
                .Include(c => c.Chunks)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }

        public async Task<Conversation> GetConversationAsync(int id)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await db.Conversations
                .Include(c => c.Chunks)
                .Where(c => c.Id == id)
                .FirstOrDefaultAsync();
        }

        public async Task<List<ConversationChunk>> GetConversationChunksAsync(int conversationId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await db.ConversationChunks
                .Where(cc => cc.ConversationId == conversationId)
                .OrderBy(cc => cc.CreatedAt)
                .ToListAsync();
        }
    }
}
